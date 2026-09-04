using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.SaveSystem;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace BLTAdoptAHero.Behaviors
{
    /// <summary>
    /// Chat-funded bounties on enemy lords. Viewers pool gold (from their adopted hero's own purse)
    /// onto a target lord; whichever adopted hero lands the killing blow collects the whole pool.
    /// Plain gold economy on top of the native HeroKilledEvent - no new combat mechanics.
    /// </summary>
    public class BLTBountyBehavior : CampaignBehaviorBase
    {
        public static BLTBountyBehavior Current => Campaign.Current?.GetCampaignBehavior<BLTBountyBehavior>();

        public class BountyPool
        {
            public string TargetHeroId;
            public string TargetName;
            public int TotalGold;
            // contributor hero StringId -> amount contributed (so it can be refunded if the target
            // dies of natural/unrelated causes, or just tracked for bragging rights)
            public Dictionary<string, int> Contributions = new();
        }

        private Dictionary<string, BountyPool> bounties = new();

        public override void RegisterEvents()
        {
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
        }

        public override void SyncData(IDataStore dataStore)
        {
            using var scopedJsonSync = new ScopedJsonSync(dataStore, nameof(BLTBountyBehavior));
            scopedJsonSync.SyncDataAsJson("BountyData", ref bounties);
            bounties ??= new();
        }

        private void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            if (victim == null || !bounties.TryGetValue(victim.StringId, out var pool))
                return;

            bounties.Remove(victim.StringId);

            if (killer != null && killer.IsAdopted() && pool.TotalGold > 0)
            {
                BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(killer, pool.TotalGold);
                Log.LogFeedResponse(killer.FirstName?.ToString() ?? killer.Name?.ToString(),
                    $"Collected the {pool.TotalGold} gold bounty on {pool.TargetName}!");
            }
        }

        public (bool success, string status) PlaceBounty(Hero placer, Hero target, int amount)
        {
            if (target == null)
                return (false, "Couldn't find that lord.");
            if (!target.IsAlive)
                return (false, $"{target.Name} is already dead.");
            if (amount <= 0)
                return (false, "Bounty amount must be positive.");

            int gold = BLTAdoptAHeroCampaignBehavior.Current.GetHeroGold(placer);
            if (gold < amount)
                return (false, $"Not enough gold ({gold}/{amount}).");

            BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(placer, -amount, isSpending: true);

            if (!bounties.TryGetValue(target.StringId, out var pool))
            {
                pool = new BountyPool { TargetHeroId = target.StringId, TargetName = target.Name?.ToString() ?? target.StringId };
                bounties[target.StringId] = pool;
            }
            pool.TotalGold += amount;
            pool.Contributions.TryGetValue(placer.StringId, out int existing);
            pool.Contributions[placer.StringId] = existing + amount;

            return (true, $"Placed {amount} gold bounty on {pool.TargetName} (pool now {pool.TotalGold}).");
        }

        public IEnumerable<BountyPool> GetTopBounties(int count)
            => bounties.Values.OrderByDescending(b => b.TotalGold).Take(count);
    }
}
