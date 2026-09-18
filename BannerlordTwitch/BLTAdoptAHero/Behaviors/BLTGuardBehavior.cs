using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
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

        // Archers and throwers engage from much further out than melee. With the melee range they
        // were walked back to the hero every half second and never got a shot off.
        private const float RangedEngageRange = 60f;

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            Current = this;
        }

        public override void OnRemoveBehavior()
        {
            ReleaseAll();
            base.OnRemoveBehavior();
            if (Current == this) Current = null;
        }



        protected override void OnEndMission()
        {
            ReleaseAll();
        }

        public void ActivateGuard(Hero hero)
        {
            if (hero == null) return;
            _activeGuards.Add(hero);
        }

        /// <summary>
        /// Stops guarding and hands the retinue back to the normal AI. Just forgetting the hero
        /// was the old behaviour, and it left every retinue member with the last scripted move
        /// still active - they kept walking to that spot instead of fighting, archers included.
        /// </summary>
        public void DeactivateGuard(Hero hero)
        {
            if (hero == null) return;
            _activeGuards.Remove(hero);
            Release(hero);
        }

        public bool IsGuarding(Hero hero) => hero != null && _activeGuards.Contains(hero);

        private void ReleaseAll()
        {
            foreach (var hero in _activeGuards.ToList()) Release(hero);
            _activeGuards.Clear();
        }

        private static void Release(Hero hero)
        {
            var state = BLTSummonBehavior.Current?.GetHeroSummonState(hero);
            if (state == null) return;

            foreach (var r in state.Retinue.Concat(state.Retinue2))
            {
                var agent = r?.Agent;
                if (agent == null || !agent.IsActive()) continue;
                try
                {
                    agent.DisableScriptedMovement();
                    agent.SetAutomaticTargetSelection(true);
                }
                catch { }
            }
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission.Current == null || _activeGuards.Count == 0) return;

            // The battle is decided: nobody should still be pinned to their hero, whatever
            // happens in this mission afterwards.
            if (Mission.Current.MissionResult != null || Mission.Current.MissionEnded)
            {
                ReleaseAll();
                return;
            }

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

                foreach (var r in state.Retinue.Concat(state.Retinue2))
                {
                    if (r?.Agent == null || !r.Agent.IsActive()) continue;

                    if (IsRanged(r.Agent) && FollowCombat.HasEnemyNear(r.Agent, RangedEngageRange))
                    {
                        r.Agent.SetAutomaticTargetSelection(true);
                        r.Agent.DisableScriptedMovement();
                        continue;
                    }

                    float dist = (r.Agent.Position - heroAgent.Position).Length;
                    FollowCombat.EngageOrFollow(r.Agent, ref heroPos, dist, GuardRadius);
                }
            }

            // The hero fell or left: release their retinue too, not just forget them.
            foreach (var h in toRemove) DeactivateGuard(h);
        }

        private static bool IsRanged(Agent agent)
        {
            try
            {
                for (var i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                {
                    var w = agent.Equipment[i];
                    if (!w.IsEmpty && w.CurrentUsageItem != null && w.CurrentUsageItem.IsRangedWeapon)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
