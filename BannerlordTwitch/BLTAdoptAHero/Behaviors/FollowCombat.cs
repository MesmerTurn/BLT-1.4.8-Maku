using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    internal static class FollowCombat
    {
        public const float EngageRange = 9f;

        public static bool HasEnemyNear(Agent agent, float range)
        {
            if (agent?.Team == null || Mission.Current?.Agents == null) return false;
            float r2 = range * range;
            foreach (var other in Mission.Current.Agents)
            {
                if (other == null || !other.IsActive() || other.IsMount) continue;
                if (!other.IsEnemyOf(agent)) continue;
                if ((other.Position - agent.Position).LengthSquared <= r2) return true;
            }
            return false;
        }

        public static void EngageOrFollow(Agent agent, ref WorldPosition leaderPos, float distToLeader, float followDist)
        {
            if (agent == null || !agent.IsActive()) return;
            try
            {
                if (HasEnemyNear(agent, EngageRange))
                {
                    agent.SetAutomaticTargetSelection(true);
                    agent.DisableScriptedMovement();
                    return;
                }
                if (distToLeader > followDist)
                    agent.SetScriptedPosition(ref leaderPos, false, Agent.AIScriptedFrameFlags.None);
            }
            catch { }
        }
    }
}