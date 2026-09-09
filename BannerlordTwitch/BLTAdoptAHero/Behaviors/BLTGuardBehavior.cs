using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    internal class BLTGuardBehavior : MissionBehavior
    {
        public static BLTGuardBehavior Current { get; private set; }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private readonly HashSet<Hero> _activeGuards = new();
        private float _lastTickTime;

        private const float TickInterval = 0.5f;
        private const float GuardRadius = 3f;

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

        public void ActivateGuard(Hero hero)
        {
            if (hero == null) return;
            _activeGuards.Add(hero);
        }

        public void DeactivateGuard(Hero hero)
        {
            if (hero == null) return;
            _activeGuards.Remove(hero);
        }

        public bool IsGuarding(Hero hero) => hero != null && _activeGuards.Contains(hero);

        public override void OnMissionTick(float dt)
        {
            if (Mission.Current == null || _activeGuards.Count == 0) return;

            var now = Mission.Current.CurrentTime;
            if (now - _lastTickTime < TickInterval) return;
            _lastTickTime = now;

            var toRemove = new List<Hero>();

            foreach (var hero in _activeGuards)
            {
                var state = BLTSummonBehavior.Current?.GetHeroSummonState(hero);
                var heroAgent = state?.CurrentAgent;
                if (heroAgent == null || !heroAgent.IsActive()) { toRemove.Add(hero); continue; }

                var heroPos = heroAgent.GetWorldPosition();

                foreach (var r in state.Retinue)
                {
                    if (r.Agent == null || !r.Agent.IsActive()) continue;
                    float dist = (r.Agent.Position - heroAgent.Position).Length;
                    FollowCombat.EngageOrFollow(r.Agent, ref heroPos, dist, GuardRadius);
                }
                foreach (var r in state.Retinue2)
                {
                    if (r.Agent == null || !r.Agent.IsActive()) continue;
                    float dist = (r.Agent.Position - heroAgent.Position).Length;
                    FollowCombat.EngageOrFollow(r.Agent, ref heroPos, dist, GuardRadius);
                }
            }

            foreach (var h in toRemove) _activeGuards.Remove(h);
        }
    }
}