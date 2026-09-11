using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Util;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Gets summoned heroes moving in a siege instead of standing where they arrived.
    ///
    /// Reported by Maku: BLT heroes do not go to the walls. The reason is in how a siege assault
    /// is organised - the attacking formations hand out the ladders, the towers and the ram when
    /// the assault begins, and every unit is given its part then. A hero summoned in the middle
    /// of that fight joins a formation whose work has already been divided up, gets no share of
    /// it, and simply stands at the spawn with nothing to do.
    ///
    /// Rather than trying to rewrite the siege AI, this uses the detachment orders the mod
    /// already has: a hero who has not moved for a while is detached and sent at the walls, or
    /// the gate when the walls cannot be reached. Those two routines are the same ones a viewer
    /// gets from "!formation walls", and they are proven.
    ///
    /// Deliberately narrow: only sieges, only the attacking side, only adopted heroes, and only
    /// those who have genuinely stopped. A hero who is fighting, climbing or advancing is left
    /// entirely alone - the point is to rescue the ones the assault forgot, not to take control
    /// away from people who are already in the battle.
    /// </summary>
    public class BLTSiegeAssaultBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private class Watch
        {
            public Vec3 LastPosition;
            public float StillFor;
            public bool Ordered;
        }

        private readonly Dictionary<Agent, Watch> watched = new();
        private float sinceLastCheck;

        private const float CheckInterval = 2f;

        // Below this much movement between checks, a hero counts as not going anywhere. Generous
        // on purpose: a man shuffling inside a crowd is still part of the assault.
        private const float MovementThreshold = 1.5f;

        public override void OnMissionTick(float dt)
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg?.SiegeSendHeroesToWalls != true) return;

            if (Mission.Current?.IsSiegeBattle != true) return;
            if (!Mission.Current.IsDeploymentFinished) return;

            sinceLastCheck += dt;
            if (sinceLastCheck < CheckInterval) return;
            float elapsed = sinceLastCheck;
            sinceLastCheck = 0f;

            try
            {
                var detachments = BLTHeroDetachmentBehavior.Current;
                if (detachments == null) return;

                float idleNeeded = Math.Max(2f, cfg.SiegeIdleSecondsBeforeOrder);

                foreach (var agent in Mission.Current.Agents.ToList())
                {
                    if (!IsCandidate(agent, detachments))
                    {
                        watched.Remove(agent);
                        continue;
                    }

                    if (!watched.TryGetValue(agent, out var watch))
                    {
                        watched[agent] = new Watch { LastPosition = agent.Position };
                        continue;
                    }

                    float moved = agent.Position.Distance(watch.LastPosition);
                    watch.LastPosition = agent.Position;

                    if (moved > MovementThreshold)
                    {
                        // Moving under their own steam - nothing to fix, and an order now would
                        // only interrupt them.
                        watch.StillFor = 0f;
                        continue;
                    }

                    watch.StillFor += elapsed;
                    if (watch.StillFor < idleNeeded || watch.Ordered) continue;

                    watch.Ordered = true;
                    SendAtTheWalls(agent, detachments);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTSiegeAssaultBehavior)}.{nameof(OnMissionTick)}", ex);
            }
        }

        private static bool IsCandidate(Agent agent, BLTHeroDetachmentBehavior detachments)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman) return false;
            if (agent.IsMount) return false;

            // Only the side doing the assaulting: a defender standing still on a wall is doing
            // exactly what a defender should.
            if (agent.Team?.IsAttacker != true) return false;

            // Only adopted heroes. Ordinary troops are the game's business, and a boss has its
            // own behaviour.
            if (agent.GetAdoptedHero() == null) return false;

            // Someone who has already been given their own orders - by themselves, or by us on a
            // previous pass - is not to be overridden.
            if (detachments.IsDetached(agent)) return false;

            // In a fight already. Whatever they are doing, it is not standing idle.
            return agent.GetTargetAgent() == null;
        }

        private static void SendAtTheWalls(Agent agent, BLTHeroDetachmentBehavior detachments)
        {
            try
            {
                string error = detachments.Detach(agent);
                if (error != null)
                {
                    Log.Trace($"[SiegeAssault] Could not detach {agent.Name}: {error}");
                    return;
                }

                // Walls first, gate second. A ladder or a breach is where an assault is decided;
                // the gate is the fallback for when there is no way up to be found.
                string wallsError = detachments.Walls(agent);
                if (wallsError == null)
                {
                    Log.Trace($"[SiegeAssault] Sent {agent.Name} at the walls.");
                    return;
                }

                string gateError = detachments.TargetDoor(agent);
                if (gateError == null)
                {
                    Log.Trace($"[SiegeAssault] Sent {agent.Name} at the gate.");
                    return;
                }

                // Neither route could be found, so leave them free to fight rather than standing
                // detached and orderless, which is worse than where they started.
                detachments.Charge(agent);
                Log.Trace($"[SiegeAssault] No route for {agent.Name} ({wallsError} / {gateError}); charging instead.");
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTSiegeAssaultBehavior)}.{nameof(SendAtTheWalls)}", ex);
            }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (affectedAgent != null) watched.Remove(affectedAgent);
        }
    }
}
