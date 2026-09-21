using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.UI;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    /// <summary>
    /// The battle events Maku picked out: a killing spree that sends a hero into a frenzy, a war
    /// horn a viewer can blow, a bounty put on an enemy's head, and a payout at the end of the
    /// battle for whoever killed the most.
    ///
    /// They live together because they all hang off the same two things - who killed whom, and the
    /// end of the mission - and splitting them across four behaviours would mean four copies of
    /// that plumbing.
    /// </summary>
    public class BLTBattleEventsBehavior : MissionBehavior
    {
        public static BLTBattleEventsBehavior Current { get; private set; }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        // Kills in a row, per hero, since they were last killed or went into a frenzy.
        private readonly Dictionary<Hero, int> killStreak = new();
        // Kills this battle, per hero, for the blood money payout.
        private readonly Dictionary<Hero, int> battleKills = new();

        private Agent markedAgent;
        private Hero markedBy;
        private float markExpiresAt;
        private float lastMarkPfx;

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            Current = this;
        }

        public override void OnRemoveBehavior()
        {
            base.OnRemoveBehavior();
            if (Current == this) Current = null;
        }

        #region Berserker frenzy

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent,
            AgentState agentState, KillingBlow blow)
        {
            try
            {
                if (affectedAgent == null || !affectedAgent.IsHuman) return;

                // Being put down ends a spree, however it happened.
                var victimHero = affectedAgent.GetAdoptedHero();
                if (victimHero != null) killStreak.Remove(victimHero);

                if (markedAgent != null && affectedAgent == markedAgent)
                    PayBounty(affectorAgent);

                var killerHero = affectorAgent?.GetAdoptedHero();
                if (killerHero == null || killerHero == victimHero) return;

                battleKills[killerHero] = battleKills.TryGetValue(killerHero, out int total) ? total + 1 : 1;

                var cfg = BLTAdoptAHeroModule.CommonConfig;
                if (cfg?.FrenzyEnabled != true) return;

                int streak = killStreak.TryGetValue(killerHero, out int s) ? s + 1 : 1;
                killStreak[killerHero] = streak;

                if (streak < Math.Max(2, cfg.FrenzyKillsRequired)) return;

                killStreak[killerHero] = 0;
                EnterFrenzy(killerHero, affectorAgent, cfg);
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBattleEventsBehavior)}.{nameof(OnAgentRemoved)}", ex);
            }
        }

        /// <summary>
        /// A hero who has cut down several men without going down themselves fights like it: more
        /// damage, faster swings and life drained from every hit - but they take more damage in
        /// return, because a man in a frenzy is not guarding himself.
        /// </summary>
        private void EnterFrenzy(Hero hero, Agent agent, GlobalCommonConfig cfg)
        {
            var rally = BLTRallyBehavior.Current;
            if (rally == null || agent == null || !agent.IsActive()) return;

            rally.Rally(agent, cfg.FrenzyDurationSeconds, cfg.FrenzyDamageDealtPercent,
                cfg.FrenzyDamageTakenPercent, cfg.FrenzyLifestealPercent, 100f,
                cfg.FrenzySwingSpeedPercent);

            cfg.FrenzyEffect.Trigger(agent);

            Log.LogFeedEvent("{=}{Name} is in a frenzy!".Translate(("Name", hero.FirstName.ToString())));
        }

        #endregion

        #region War horn

        /// <summary>
        /// Everyone on the hero's side within earshot fights harder for a while, and enemies who
        /// hear it lose heart.
        /// </summary>
        public int BlowWarHorn(Agent hornBlower, float radius, float durationSeconds,
            float damageDealtPercent, float damageTakenPercent, float enemyMoraleLoss,
            OneShotEffect effect)
        {
            var rally = BLTRallyBehavior.Current;
            if (rally == null || hornBlower?.Team == null || Mission.Current?.Agents == null) return 0;

            int affected = 0;
            float radiusSq = radius * radius;

            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                if ((agent.Position - hornBlower.Position).LengthSquared > radiusSq) continue;

                if (agent.Team != null && agent.Team.IsFriendOf(hornBlower.Team))
                {
                    rally.Rally(agent, durationSeconds, damageDealtPercent, damageTakenPercent, 0f, 100f);
                    affected++;
                }
                else if (enemyMoraleLoss > 0 && agent.IsEnemyOf(hornBlower))
                {
                    try { agent.SetMorale(Math.Max(0f, agent.GetMorale() - enemyMoraleLoss)); }
                    catch { }
                }
            }

            effect.Trigger(hornBlower);
            return affected;
        }

        #endregion

        #region Bounty mark

        public bool HasMark => markedAgent != null && markedAgent.IsActive();

        public string MarkedName => markedAgent?.Name ?? "";

        /// <summary>
        /// Puts a price on one enemy's head. Only one mark exists at a time: a field full of
        /// marked men is not a bounty, it is a shopping list.
        /// </summary>
        public (bool marked, string message) Mark(Hero hero, Agent hunter, float durationSeconds,
            OneShotEffect effect)
        {
            if (HasMark)
                return (false, "{=}{Name} is already marked".Translate(("Name", MarkedName)));

            var target = FindMarkTarget(hunter);
            if (target == null) return (false, "{=}There is no enemy commander in reach to mark".Translate());

            markedAgent = target;
            markedBy = hero;
            markExpiresAt = Mission.Current.CurrentTime + durationSeconds;
            effect.Trigger(target);

            return (true, "{=}{Name} is marked! Whoever kills them takes the bounty"
                .Translate(("Name", target.Name)));
        }

        /// <summary>
        /// The nearest enemy who is somebody: a lord, a hero, or failing that the enemy's own
        /// commander. Marking a random spearman would be no reward to hunt down.
        /// </summary>
        private static Agent FindMarkTarget(Agent hunter)
        {
            if (hunter?.Team == null || Mission.Current?.Agents == null) return null;

            Agent best = null;
            float bestDistance = float.MaxValue;

            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                if (!agent.IsEnemyOf(hunter)) continue;

                bool isSomebody = (agent.Character as CharacterObject)?.HeroObject != null
                                  || agent.Team?.GeneralAgent == agent;
                if (!isSomebody) continue;

                float d = (agent.Position - hunter.Position).LengthSquared;
                if (d < bestDistance) { bestDistance = d; best = agent; }
            }

            return best;
        }

        private void PayBounty(Agent killerAgent)
        {
            try
            {
                var cfg = BLTAdoptAHeroModule.CommonConfig;
                string name = MarkedName;
                var claimer = killerAgent?.GetAdoptedHero();

                markedAgent = null;

                if (claimer == null)
                {
                    Log.LogFeedEvent("{=}{Name} is down, but nobody claimed the bounty"
                        .Translate(("Name", name)));
                    markedBy = null;
                    return;
                }

                int reward = Math.Max(0, cfg?.BountyMarkReward ?? 0);
                if (reward > 0)
                    BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(claimer, reward);

                Log.LogFeedEvent("{=}{Killer} claimed the bounty on {Name}! +{Gold}{GoldIcon}"
                    .Translate(("Killer", claimer.FirstName.ToString()), ("Name", name),
                        ("Gold", reward), ("GoldIcon", Naming.Gold)));

                markedBy = null;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBattleEventsBehavior)}.{nameof(PayBounty)}", ex);
            }
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission.Current == null || markedAgent == null) return;

            float now = Mission.Current.CurrentTime;
            if (!markedAgent.IsActive() || now > markExpiresAt)
            {
                markedAgent = null;
                markedBy = null;
                return;
            }

            // A steady reminder of where the bounty is, rather than one effect at the start that
            // nobody watching the stream would ever see again.
            if (now - lastMarkPfx < 2f) return;
            lastMarkPfx = now;
            BLTAdoptAHeroModule.CommonConfig?.BountyMarkEffect.Trigger(markedAgent);
        }

        #endregion

        #region Blood money

        protected override void OnEndMission()
        {
            try
            {
                var cfg = BLTAdoptAHeroModule.CommonConfig;
                if (cfg?.BloodMoneyEnabled != true || battleKills.Count == 0) return;

                var best = battleKills.OrderByDescending(kv => kv.Value).First();
                if (best.Value < Math.Max(1, cfg.BloodMoneyMinimumKills)) return;

                int reward = best.Value * Math.Max(0, cfg.BloodMoneyGoldPerKill);
                if (reward > 0)
                    BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(best.Key, reward);

                Log.LogFeedEvent("{=}Blood money: {Name} took {Kills} lives and collects {Gold}{GoldIcon}"
                    .Translate(("Name", best.Key.FirstName.ToString()), ("Kills", best.Value),
                        ("Gold", reward), ("GoldIcon", Naming.Gold)));
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBattleEventsBehavior)}.{nameof(OnEndMission)}", ex);
            }
            finally
            {
                battleKills.Clear();
                killStreak.Clear();
            }
        }

        #endregion
    }
}
