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

                if (HasDuel && (affectedAgent == championAlly || affectedAgent == championEnemy))
                    ResolveDuel(affectedAgent);

                if (affectedAgent == bannerAgent) DropBanner(BLTAdoptAHeroModule.CommonConfig);

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
            if (Mission.Current == null) return;

            SlowTick(dt);

            if (markedAgent == null) return;

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

        #region Slow tick

        private float slowTickAccumulated;
        private const float SlowTickInterval = 1f;

        /// <summary>
        /// The things that have to be watched rather than triggered: a side being ground down, a
        /// banner still standing, and men who are on fire.
        /// </summary>
        private void SlowTick(float dt)
        {
            slowTickAccumulated += dt;
            if (slowTickAccumulated < SlowTickInterval) return;
            float elapsed = slowTickAccumulated;
            slowTickAccumulated = 0f;

            try
            {
                TickBurning(elapsed);
                TickBanner();
                TickLastStand();
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBattleEventsBehavior)}.{nameof(SlowTick)}", ex);
            }
        }

        #endregion

        #region Last stand

        // Heroes who have already had their last stand this battle. Once each: it is a last stand,
        // not a second wind on tap.
        private readonly HashSet<Hero> lastStandUsed = new();

        private void TickLastStand()
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg?.LastStandEnabled != true || Mission.Current?.Agents == null) return;

            var rally = BLTRallyBehavior.Current;
            if (rally == null) return;

            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;

                var hero = agent.GetAdoptedHero();
                if (hero == null || lastStandUsed.Contains(hero)) continue;
                if (!IsSideNearlyLost(agent, cfg.LastStandRemainingPercent)) continue;

                lastStandUsed.Add(hero);

                agent.Health = Math.Min(agent.HealthLimit,
                    agent.Health + agent.HealthLimit * cfg.LastStandHealPercent / 100f);

                rally.Rally(agent, cfg.LastStandDurationSeconds, cfg.LastStandDamageDealtPercent,
                    cfg.LastStandDamageTakenPercent, 0f, 100f);

                SlowEnemiesAround(agent, cfg.LastStandSlowRadius, cfg.LastStandEnemySpeedPercent,
                    cfg.LastStandDurationSeconds);

                cfg.LastStandEffect.Trigger(agent);

                Log.LogFeedEvent("{=}{Name} makes a last stand!"
                    .Translate(("Name", hero.FirstName.ToString())));
            }
        }

        /// <summary>
        /// Whether this agent's side has been cut down to a small fraction of the enemy's numbers.
        /// Counted live rather than from the starting rosters, so reinforcements arriving pull a
        /// side back out of it.
        /// </summary>
        private static bool IsSideNearlyLost(Agent agent, float remainingPercent)
        {
            if (agent.Team == null) return false;

            int friends = 0, enemies = 0;
            foreach (var other in Mission.Current.Agents)
            {
                if (other == null || !other.IsActive() || !other.IsHuman || other.Team == null) continue;
                if (other.Team.IsFriendOf(agent.Team)) friends++;
                else if (other.IsEnemyOf(agent)) enemies++;
            }

            if (enemies < 5) return false;
            return friends * 100f / enemies <= remainingPercent;
        }

        private void SlowEnemiesAround(Agent centre, float radius, float speedPercent, float duration)
        {
            if (speedPercent >= 100f) return;

            float radiusSq = radius * radius;
            var rally = BLTRallyBehavior.Current;

            foreach (var other in Mission.Current.Agents.ToList())
            {
                if (other == null || !other.IsActive() || !other.IsEnemyOf(centre)) continue;
                if ((other.Position - centre.Position).LengthSquared > radiusSq) continue;

                // Reuse the rally timer to carry the slow: it already applies a modifier for a
                // fixed time and takes it off again cleanly when the time runs out.
                rally?.Rally(other, duration, 100f, 100f, 0f, 100f, 100f, speedPercent);
            }
        }

        #endregion

        #region Burning arrows

        private class Burning
        {
            public float ExpiresAt;
            public float DamagePerSecond;
            public Hero Owner;
        }

        // Archers whose arrows set men alight, and the men currently alight.
        private readonly Dictionary<Agent, float> burningArchersUntil = new();
        private readonly Dictionary<Agent, Burning> burningAgents = new();

        public int LightArrows(Hero hero, float durationSeconds, OneShotEffect effect)
        {
            int count = 0;
            float until = Mission.Current.CurrentTime + durationSeconds;

            foreach (var agent in BLTRallyBehavior.RallyGroup(hero).ToList())
            {
                if (agent == null || !agent.IsActive()) continue;
                burningArchersUntil[agent] = until;
                effect.Trigger(agent);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Called for every blow. A burning archer's hit sets the victim alight for a few seconds
        /// instead of setting the ground on fire - fire that sticks to men is something the engine
        /// will do, a burning patch of field is not.
        /// </summary>
        public void OnBlowLanded(Agent attacker, Agent victim, bool ranged)
        {
            try
            {
                var cfg = BLTAdoptAHeroModule.CommonConfig;
                if (cfg == null || !ranged || attacker == null || victim == null) return;
                if (!burningArchersUntil.TryGetValue(attacker, out float until)) return;
                if (Mission.Current.CurrentTime > until) { burningArchersUntil.Remove(attacker); return; }

                burningAgents[victim] = new Burning
                {
                    ExpiresAt = Mission.Current.CurrentTime + cfg.BurningArrowsBurnSeconds,
                    DamagePerSecond = cfg.BurningArrowsDamagePerSecond,
                    Owner = attacker.GetAdoptedHero(),
                };
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBattleEventsBehavior)}.{nameof(OnBlowLanded)}", ex);
            }
        }

        private void TickBurning(float elapsed)
        {
            if (burningAgents.Count == 0) return;

            float now = Mission.Current.CurrentTime;
            var effect = BLTAdoptAHeroModule.CommonConfig?.BurningArrowsBurnEffect ?? default;

            foreach (var pair in burningAgents.ToList())
            {
                var agent = pair.Key;
                if (agent == null || !agent.IsActive() || now > pair.Value.ExpiresAt)
                {
                    burningAgents.Remove(agent);
                    continue;
                }

                float damage = pair.Value.DamagePerSecond * elapsed;
                agent.Health = Math.Max(1f, agent.Health - damage);
                effect.Trigger(agent);
            }
        }

        #endregion

        #region Banner bearer

        private Agent bannerAgent;
        private Hero bannerHero;
        private float lastBannerPfx;

        public bool HasBanner => bannerAgent != null && bannerAgent.IsActive();
        public string BannerHolderName => bannerHero?.FirstName?.ToString() ?? "";

        public (bool taken, string message) TakeBanner(Hero hero, Agent agent)
        {
            if (HasBanner && bannerAgent != agent)
                return (false, "{=}{Name} is already carrying the banner"
                    .Translate(("Name", BannerHolderName)));

            bannerAgent = agent;
            bannerHero = hero;
            BLTAdoptAHeroModule.CommonConfig?.BannerEffect.Trigger(agent);

            return (true, "{=}{Name} raises the banner!".Translate(("Name", hero.FirstName.ToString())));
        }

        /// <summary>
        /// While the banner stands, everyone near it fights harder. When the bearer goes down, the
        /// men around them lose heart - which is the whole point of carrying it.
        /// </summary>
        private void TickBanner()
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg == null || bannerAgent == null) return;

            if (!bannerAgent.IsActive())
            {
                DropBanner(cfg);
                return;
            }

            float radiusSq = cfg.BannerRadius * cfg.BannerRadius;
            var rally = BLTRallyBehavior.Current;

            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                if (agent.Team == null || !agent.Team.IsFriendOf(bannerAgent.Team)) continue;
                if ((agent.Position - bannerAgent.Position).LengthSquared > radiusSq) continue;

                // Refreshed every second, so stepping out of its shadow ends the bonus shortly
                // afterwards rather than carrying it across the field.
                rally?.Rally(agent, 2f, cfg.BannerDamageDealtPercent, cfg.BannerDamageTakenPercent,
                    0f, 100f);
            }

            float now = Mission.Current.CurrentTime;
            if (now - lastBannerPfx >= 3f)
            {
                lastBannerPfx = now;
                cfg.BannerEffect.Trigger(bannerAgent);
            }
        }

        private void DropBanner(GlobalCommonConfig cfg)
        {
            var fallen = bannerAgent;
            string name = BannerHolderName;
            bannerAgent = null;
            bannerHero = null;

            if (fallen == null || cfg.BannerFallMoraleLoss <= 0) return;

            float radiusSq = cfg.BannerRadius * cfg.BannerRadius;
            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive() || agent.Team == null) continue;
                if (!agent.Team.IsFriendOf(fallen.Team)) continue;
                if ((agent.Position - fallen.Position).LengthSquared > radiusSq) continue;

                try { agent.SetMorale(Math.Max(0f, agent.GetMorale() - cfg.BannerFallMoraleLoss)); }
                catch { }
            }

            Log.LogFeedEvent("{=}The banner falls with {Name}!".Translate(("Name", name)));
        }

        #endregion

        #region Champion duel

        private Agent championAlly;
        private Agent championEnemy;

        public bool HasDuel => championAlly != null && championEnemy != null;

        /// <summary>
        /// A champion from each side, named before the lines meet. The armies are deliberately NOT
        /// stopped: holding two AI armies still and handing them back afterwards is exactly how a
        /// battle ends up unplayable. What is at stake is morale - whichever champion kills the
        /// other lifts their own side and breaks the other's.
        /// </summary>
        public (bool started, string message) StartDuel(Hero hero, Agent challenger)
        {
            if (HasDuel)
                return (false, "{=}A duel has already been called this battle".Translate());

            var opponent = FindMarkTarget(challenger);
            if (opponent == null)
                return (false, "{=}There is no enemy champion to challenge".Translate());

            championAlly = challenger;
            championEnemy = opponent;

            var cfg = BLTAdoptAHeroModule.CommonConfig;
            cfg?.DuelEffect.Trigger(challenger);
            cfg?.DuelEffect.Trigger(opponent);

            return (true, "{=}{Name} challenges {Opponent}! Whoever falls, their side loses heart"
                .Translate(("Name", hero.FirstName.ToString()), ("Opponent", opponent.Name)));
        }

        private void ResolveDuel(Agent fallen)
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg == null) return;

            var loser = fallen;
            var winner = fallen == championAlly ? championEnemy : championAlly;

            championAlly = null;
            championEnemy = null;

            if (winner == null || loser?.Team == null) return;

            foreach (var agent in Mission.Current.Agents.ToList())
            {
                if (agent == null || !agent.IsActive() || agent.Team == null) continue;

                try
                {
                    if (winner.Team != null && agent.Team.IsFriendOf(winner.Team))
                        agent.SetMorale(Math.Min(100f, agent.GetMorale() + cfg.DuelMoraleSwing));
                    else if (agent.Team.IsFriendOf(loser.Team))
                        agent.SetMorale(Math.Max(0f, agent.GetMorale() - cfg.DuelMoraleSwing));
                }
                catch { }
            }

            Log.LogFeedEvent("{=}{Winner} wins the duel of champions!"
                .Translate(("Winner", winner.Name)));
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
