using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Util;
using HarmonyLib;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Requested by Maku: !rally. The hero is healed to full, their retinue and companions are
    /// patched up, and for a while the whole group fights harder - more damage dealt, less taken,
    /// life drained from every hit, and archers shooting faster.
    ///
    /// Kept as one self-contained behaviour rather than a hero power: it is a single command with
    /// one set of numbers, it applies to a group rather than to one agent, and it has to survive
    /// agents dying and being replaced mid-battle.
    /// </summary>
    public class BLTRallyBehavior : MissionBehavior
    {
        public static BLTRallyBehavior Current { get; private set; }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public class RallyState
        {
            public float ExpiresAt;
            public float DamageDealtMultiplier;
            public float DamageTakenMultiplier;
            public float LifestealPercent;
            public AgentModifierConfig Modifier;
        }

        private readonly Dictionary<Agent, RallyState> rallied = new();

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            Current = this;
        }

        public override void OnRemoveBehavior()
        {
            foreach (var agent in rallied.Keys.ToList()) EndRally(agent);
            base.OnRemoveBehavior();
            if (Current == this) Current = null;
        }

        public bool IsRallied(Agent agent) => agent != null && rallied.ContainsKey(agent);

        /// <summary>
        /// Rallies one agent. The archer bonus is only given to agents actually carrying a ranged
        /// weapon, so a shield wall does not quietly get a stat it cannot use.
        /// </summary>
        public void Rally(Agent agent, float durationSeconds, float damageDealtPercent,
            float damageTakenPercent, float lifestealPercent, float rangedFireRatePercent,
            float swingSpeedPercent = 100f)
        {
            if (agent == null || !agent.IsActive()) return;

            // Re-rallying refreshes rather than stacks: two viewers calling it should not double
            // the numbers.
            if (rallied.TryGetValue(agent, out var existing)) RemoveModifier(agent, existing);

            var state = new RallyState
            {
                ExpiresAt = Mission.Current.CurrentTime + durationSeconds,
                DamageDealtMultiplier = damageDealtPercent / 100f,
                DamageTakenMultiplier = damageTakenPercent / 100f,
                LifestealPercent = lifestealPercent,
            };

            if (rangedFireRatePercent != 100f && IsRanged(agent))
            {
                state.Modifier ??= new AgentModifierConfig();
                state.Modifier.Properties.Add(new PropertyModifierDef
                {
                    Name = DrivenProperty.ReloadSpeed,
                    ModifierPercent = rangedFireRatePercent,
                });
            }

            if (swingSpeedPercent != 100f)
            {
                state.Modifier ??= new AgentModifierConfig();
                state.Modifier.Properties.Add(new PropertyModifierDef
                {
                    Name = DrivenProperty.SwingSpeedMultiplier,
                    ModifierPercent = swingSpeedPercent,
                });
            }

            if (state.Modifier != null) BLTAgentModifierBehavior.Current?.Add(agent, state.Modifier);

            rallied[agent] = state;
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission.Current == null || rallied.Count == 0) return;

            float now = Mission.Current.CurrentTime;
            foreach (var pair in rallied.ToList())
            {
                if (pair.Key == null || !pair.Key.IsActive() || pair.Value.ExpiresAt <= now)
                    EndRally(pair.Key);
            }
        }

        public override void OnAgentDeleted(Agent affectedAgent) => EndRally(affectedAgent);

        private void EndRally(Agent agent)
        {
            if (agent == null) { return; }
            if (rallied.TryGetValue(agent, out var state)) RemoveModifier(agent, state);
            rallied.Remove(agent);
        }

        private static void RemoveModifier(Agent agent, RallyState state)
        {
            if (state?.Modifier == null) return;
            try { BLTAgentModifierBehavior.Current?.Remove(agent, state.Modifier); }
            catch (Exception ex) { Log.Trace($"[Rally] Could not remove modifier: {ex.Message}"); }
        }

        private static bool IsRanged(Agent agent)
        {
            try
            {
                for (var i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                {
                    var w = agent.Equipment[i];
                    if (!w.IsEmpty && w.CurrentUsageItem?.IsRangedWeapon == true) return true;
                }
            }
            catch { }
            return false;
        }

        private void ApplyToBlow(Agent attacker, Agent victim, ref Blow b)
        {
            try
            {
                if (attacker != null && rallied.TryGetValue(attacker, out var attackerState))
                {
                    b.InflictedDamage = (int)Math.Round(b.InflictedDamage * attackerState.DamageDealtMultiplier);

                    if (attackerState.LifestealPercent > 0 && attacker.IsActive() && victim != attacker)
                    {
                        float heal = b.InflictedDamage * attackerState.LifestealPercent / 100f;
                        attacker.Health = Math.Min(attacker.HealthLimit, attacker.Health + heal);
                    }
                }

                if (victim != null && rallied.TryGetValue(victim, out var victimState))
                {
                    b.InflictedDamage = (int)Math.Round(b.InflictedDamage * victimState.DamageTakenMultiplier);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTRallyBehavior)}.{nameof(ApplyToBlow)}", ex);
            }
        }

        [UsedImplicitly, HarmonyPrefix, HarmonyPatch(typeof(Mission), "RegisterBlow")]
        private static void RallyRegisterBlowPrefix(Agent attacker, Agent victim, ref Blow b)
        {
            Current?.ApplyToBlow(attacker, victim, ref b);
        }

        /// <summary>
        /// Everyone a rally covers: the hero, their retinue, and any companions of theirs that are
        /// in this battle. Companions are found by who they are rather than from a stored list,
        /// because they can arrive in the mission by paths this behaviour never sees.
        /// </summary>
        public static IEnumerable<Agent> RallyGroup(Hero hero)
        {
            var state = BLTSummonBehavior.Current?.GetHeroSummonState(hero);

            if (state?.CurrentAgent != null && state.CurrentAgent.IsActive())
                yield return state.CurrentAgent;

            if (state != null)
            {
                foreach (var r in state.Retinue.Concat(state.Retinue2))
                {
                    if (r?.Agent != null && r.Agent.IsActive()) yield return r.Agent;
                }
            }

            var clan = hero?.Clan;
            if (clan == null || Mission.Current?.Agents == null) yield break;

            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive()) continue;
                var agentHero = (agent.Character as CharacterObject)?.HeroObject;
                if (agentHero != null && agentHero != hero && agentHero.CompanionOf == clan)
                    yield return agent;
            }
        }
    }
}
