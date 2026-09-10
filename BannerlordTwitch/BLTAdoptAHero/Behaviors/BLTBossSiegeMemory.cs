using System;
using System.Collections.Generic;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Remembers which sieges have already had their bosses, so beating them once is enough.
    ///
    /// A siege is not one battle. An assault can be fought in waves, it can be reloaded, and the
    /// besieger can come back at the same walls the next day - and each of those starts a fresh
    /// mission, which rolls fresh bosses. Someone who has already fought and won the boss fight
    /// then has to do it again to make the same progress, which is a punishment for succeeding.
    ///
    /// The memory is keyed by the settlement together with the siege's start time, so it is the
    /// specific siege that is remembered, not the town: a later siege of the same place gets its
    /// own bosses. Entries are kept in the save, because reloading is one of the ways the fight
    /// used to come back.
    /// </summary>
    public class BLTBossSiegeMemory : CampaignBehaviorBase
    {
        public static BLTBossSiegeMemory Current { get; private set; }

        public BLTBossSiegeMemory() { Current = this; }

        private List<string> spentSieges = new();

        public override void RegisterEvents() { }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("BLTBossSpentSieges", ref spentSieges);
            spentSieges ??= new List<string>();
        }

        /// <summary>
        /// Identifies the siege the current mission belongs to, or null when the mission is not
        /// part of one. A siege with no start time to read cannot be told apart from the next
        /// siege of the same settlement, so it is treated as unidentifiable rather than guessed at.
        /// </summary>
        public static string CurrentSiegeKey()
        {
            try
            {
                var settlement = PlayerSiege.PlayerSiegeEvent?.BesiegedSettlement
                                 ?? MobileParty.MainParty?.BesiegedSettlement
                                 ?? Settlement.CurrentSettlement;

                var siege = settlement?.SiegeEvent;
                if (settlement == null || siege == null) return null;

                // The absolute start time, not how long ago it was: an elapsed value moves with
                // the campaign clock and would name a different siege every hour.
                return $"{settlement.StringId}@{siege.SiegeStartTime.ToHours:F0}";
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBossSiegeMemory)}.{nameof(CurrentSiegeKey)}", ex);
                return null;
            }
        }

        public bool HasSpawnedFor(string siegeKey)
            => siegeKey != null && (spentSieges ??= new List<string>()).Contains(siegeKey);

        public void MarkSpawnedFor(string siegeKey)
        {
            if (siegeKey == null) return;

            spentSieges ??= new List<string>();
            if (!spentSieges.Contains(siegeKey)) spentSieges.Add(siegeKey);

            // A campaign that runs long enough would otherwise carry every siege it ever fought.
            // The only entries that matter are recent ones - a siege that ended is never asked
            // about again - so the oldest are dropped once the list grows past any plausible
            // number of sieges running at once.
            const int keep = 200;
            if (spentSieges.Count > keep)
                spentSieges.RemoveRange(0, spentSieges.Count - keep);
        }
    }
}
