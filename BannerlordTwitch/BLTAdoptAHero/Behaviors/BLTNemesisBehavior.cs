using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.SaveSystem;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero.Behaviors
{
    /// <summary>
    /// Tracks rivalries between adopted heroes and the enemy lords who have fought them, using only
    /// vanilla Bannerlord concepts (defeats, kills, renown) rather than invented mechanics. A lord who
    /// repeatedly beats (or is beaten by) the same adopted hero becomes a tracked "nemesis" - surfaced
    /// via the !nemesis command.
    /// </summary>
    public class BLTNemesisBehavior : CampaignBehaviorBase
    {
        public static BLTNemesisBehavior Current => Campaign.Current?.GetCampaignBehavior<BLTNemesisBehavior>();

        public class NemesisRecord
        {
            public string EnemyHeroId;
            public string EnemyName;
            public int TimesDefeatedYou;
            public int TimesYouDefeated;
            public float LastEncounterDays;

            // How "dangerous" this nemesis currently is - grows when they beat you, shrinks (and
            // eventually clears) when you beat them, mirroring a Nemesis-System-style rank without
            // inventing new stats: this is purely a running tally of encounter outcomes.
            public int Rank => Math.Max(0, TimesDefeatedYou - TimesYouDefeated);
        }

        // adopted hero StringId -> list of nemesis records against that hero
        private Dictionary<string, List<NemesisRecord>> nemesisData = new();

        public override void RegisterEvents()
        {
        }

        public override void SyncData(IDataStore dataStore)
        {
            using var scopedJsonSync = new ScopedJsonSync(dataStore, nameof(BLTNemesisBehavior));
            scopedJsonSync.SyncDataAsJson("NemesisData", ref nemesisData);
            nemesisData ??= new();
        }

        private List<NemesisRecord> GetOrCreateList(Hero hero)
        {
            if (!nemesisData.TryGetValue(hero.StringId, out var list))
            {
                list = new List<NemesisRecord>();
                nemesisData[hero.StringId] = list;
            }
            return list;
        }

        private static bool IsEligibleEnemyHero(Hero enemyHero, Hero adoptedHero)
        {
            // Only real named lords count as nemeses - not the player's own adopted heroes, and not
            // heroless troops (regular soldiers have no Hero attached at all so are excluded naturally).
            return enemyHero != null
                   && enemyHero != adoptedHero
                   && enemyHero.IsLord;
        }

        /// <summary>Call when an enemy lord's agent has beaten (killed/knocked out) an adopted hero's agent.</summary>
        public void RecordDefeat(Hero adoptedHero, Hero enemyHero)
        {
            if (adoptedHero == null || !IsEligibleEnemyHero(enemyHero, adoptedHero))
                return;

            var list = GetOrCreateList(adoptedHero);
            var record = list.FirstOrDefault(r => r.EnemyHeroId == enemyHero.StringId);
            if (record == null)
            {
                record = new NemesisRecord { EnemyHeroId = enemyHero.StringId, EnemyName = enemyHero.Name?.ToString() ?? enemyHero.StringId };
                list.Add(record);
            }
            record.EnemyName = enemyHero.Name?.ToString() ?? record.EnemyName;
            record.TimesDefeatedYou++;
            record.LastEncounterDays = (float)CampaignTime.Now.ToDays;
        }

        /// <summary>Call when an adopted hero's agent has killed an enemy lord's agent.</summary>
        public void RecordVictory(Hero adoptedHero, Hero enemyHero)
        {
            if (adoptedHero == null || !IsEligibleEnemyHero(enemyHero, adoptedHero))
                return;

            var list = GetOrCreateList(adoptedHero);
            var record = list.FirstOrDefault(r => r.EnemyHeroId == enemyHero.StringId);
            if (record == null)
            {
                record = new NemesisRecord { EnemyHeroId = enemyHero.StringId, EnemyName = enemyHero.Name?.ToString() ?? enemyHero.StringId };
                list.Add(record);
            }
            record.EnemyName = enemyHero.Name?.ToString() ?? record.EnemyName;
            record.TimesYouDefeated++;
            record.LastEncounterDays = (float)CampaignTime.Now.ToDays;

            // A nemesis who has finally been beaten more than they've beaten you fades from the list -
            // keeps the board focused on genuine unresolved rivalries.
            if (record.Rank <= 0 && record.TimesYouDefeated > 0)
            {
                list.Remove(record);
            }
        }

        public IEnumerable<NemesisRecord> GetNemeses(Hero adoptedHero)
            => nemesisData.TryGetValue(adoptedHero.StringId, out var list)
                ? list.OrderByDescending(r => r.Rank).ThenByDescending(r => r.LastEncounterDays)
                : Enumerable.Empty<NemesisRecord>();
    }
}
