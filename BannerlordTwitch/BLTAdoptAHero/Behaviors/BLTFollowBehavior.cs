using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    internal class BLTFollowBehavior : MissionBehavior
    {
        public static BLTFollowBehavior Current { get; private set; }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private readonly Dictionary<Hero, float> _active = new();
        private readonly Dictionary<Hero, float> _lastUpdate = new();
        private readonly Dictionary<Hero, (Hero target, float dist)> _followTargets = new();

        private const float UpdateInterval = 0.5f;
        private const float DefaultFollowDist = 4f;

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

        private static Agent GetHeroAgent(Hero hero)
            => BLTSummonBehavior.Current?.GetHeroSummonState(hero)?.CurrentAgent;

        public void Activate(Hero hero)
        {
            if (hero == null) return;
            _followTargets.Remove(hero);
            _active[hero] = Mission.Current?.CurrentTime ?? 0f;
        }

        public void ActivateFollowHero(Hero hero, Hero target, float followDist = DefaultFollowDist)
        {
            if (hero == null || target == null) return;
            _followTargets[hero] = (target, followDist);
            _active[hero] = Mission.Current?.CurrentTime ?? 0f;
        }

        public void Deactivate(Hero hero)
        {
            if (hero == null) return;
            _active.Remove(hero);
            _lastUpdate.Remove(hero);
            _followTargets.Remove(hero);

            var heroAgent = GetHeroAgent(hero);
            if (heroAgent != null && heroAgent.IsActive())
                ResetAgentToNormalAI(heroAgent);
        }

        private static void ResetAgentToNormalAI(Agent agent)
        {
            try
            {
                if (agent.Formation != null)
                {
                    agent.Formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                    return;
                }
                var cur = agent.GetWorldPosition();
                agent.SetScriptedPosition(ref cur, false, Agent.AIScriptedFrameFlags.None);
            }
            catch { }
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission.Current == null || _active.Count == 0) return;

            var streamerAgent = Mission.Current.MainAgent;
            var now = Mission.Current.CurrentTime;
            var toRemove = new List<Hero>();

            foreach (var kvp in _active.ToList())
            {
                var hero = kvp.Key;
                if (_lastUpdate.TryGetValue(hero, out var lastTime) && now - lastTime < UpdateInterval)
                    continue;
                _lastUpdate[hero] = now;

                var heroAgent = GetHeroAgent(hero);
                if (heroAgent == null || !heroAgent.IsActive()) { toRemove.Add(hero); continue; }

                Agent targetAgent;
                float followDist;

                if (_followTargets.TryGetValue(hero, out var followInfo))
                {
                    targetAgent = GetHeroAgent(followInfo.target);
                    followDist = followInfo.dist;
                    if (targetAgent == null || !targetAgent.IsActive())
                    {
                        _followTargets.Remove(hero);
                        toRemove.Add(hero);
                        continue;
                    }
                }
                else
                {
                    if (streamerAgent == null || !streamerAgent.IsActive()) { toRemove.Add(hero); continue; }
                    targetAgent = streamerAgent;
                    followDist = DefaultFollowDist;
                }

                var targetPos = targetAgent.GetWorldPosition();
                float heroDist = (heroAgent.Position - targetAgent.Position).Length;
                FollowCombat.EngageOrFollow(heroAgent, ref targetPos, heroDist, followDist);
            }

            foreach (var h in toRemove)
            {
                _active.Remove(h);
                _lastUpdate.Remove(h);
                _followTargets.Remove(h);
                var ha = GetHeroAgent(h);
                if (ha != null && ha.IsActive()) ResetAgentToNormalAI(ha);
            }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (_active.Count == 0) return;

            if (affectedAgent.IsMainAgent)
            {
                _active.Clear();
                _lastUpdate.Clear();
                _followTargets.Clear();
                return;
            }

            var dead = _active.Keys.Where(h => GetHeroAgent(h) == affectedAgent).ToList();
            foreach (var h in dead) { _active.Remove(h); _lastUpdate.Remove(h); _followTargets.Remove(h); }

            var orphaned = _followTargets
                .Where(kv => GetHeroAgent(kv.Value.target) == affectedAgent)
                .Select(kv => kv.Key).ToList();
            foreach (var h in orphaned) _followTargets.Remove(h);
        }
    }
}