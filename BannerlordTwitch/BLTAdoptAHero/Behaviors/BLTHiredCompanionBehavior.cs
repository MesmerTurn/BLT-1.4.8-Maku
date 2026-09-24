using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Progression for hired companions, modelled on GeneralEddy's (used with his permission,
    /// 2026-09-24).
    ///
    /// A companion climbs on the skills their own class fights with - the weapon skills of the kit
    /// they carry, plus riding or athletics. When the average of those passes the threshold for
    /// the next tier, they move up and are re-equipped at it. Checked once a day rather than
    /// continuously: this is a campaign-scale thing, and a promotion mid-battle would swap a man's
    /// weapons out from under him.
    ///
    /// The point of doing it on their own skills is that a companion levels by fighting in their
    /// own kit, without the viewer having to spend anything. Buying focus points only speeds it up.
    /// </summary>
    public class BLTHiredCompanionBehavior : CampaignBehaviorBase
    {
        public static BLTHiredCompanionBehavior Current { get; private set; }

        public BLTHiredCompanionBehavior() { Current = this; }

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickHeroEvent.AddNonSerializedListener(this, OnDailyTickHero);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnDailyTickHero(Hero hero)
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg?.CompanionTiersEnabled != true) return;

            try
            {
                var campaign = BLTAdoptAHeroCampaignBehavior.Current;
                if (campaign == null || hero == null || hero.IsDead) return;
                if (!campaign.IsHiredCompanion(hero)) return;

                var classDef = campaign.GetClass(hero);
                if (classDef == null) return;

                int tier = campaign.GetEquipmentTier(hero);
                var thresholds = Thresholds(cfg.CompanionTierThresholds);
                if (thresholds.Count == 0) return;

                // tier is 0 based (0 = tier 1 in chat), so the threshold to LEAVE tier 0 is the
                // first in the list.
                if (tier < 0) tier = 0;
                if (tier >= thresholds.Count) return;   // already at the top of the ladder

                float average = ClassSkillAverage(hero, classDef);
                if (average < thresholds[tier]) return;

                int newTier = tier + 1;
                campaign.SetEquipmentTier(hero, newTier);

                // replaceSameTier false: only what is genuinely worse than the new tier gets
                // swapped, so a good piece they were given is not thrown away on promotion.
                EquipHero.UpgradeEquipment(hero, newTier, classDef, replaceSameTier: false);

                var owner = campaign.GetCompanionOwner(hero);
                Log.LogFeedEvent("{=}{Name} has reached tier {Tier} as a {Class}{Owner}"
                    .Translate(("Name", hero.FirstName.ToString()),
                        ("Tier", newTier + 1),
                        ("Class", classDef.Name.ToString()),
                        ("Owner", owner != null ? $" ({owner.FirstName})" : "")));
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTHiredCompanionBehavior)}.{nameof(OnDailyTickHero)}", ex);
            }
        }

        /// <summary>
        /// The average of the skills this class actually fights with. Riding counts only for a
        /// mounted class and athletics only for one on foot - otherwise every companion would be
        /// dragged towards the same number by skills their kit never trains.
        /// </summary>
        public static float ClassSkillAverage(Hero hero, HeroClassDef classDef)
        {
            var skills = classDef.Skills
                .Where(s => s != null)
                .Where(s => classDef.Mounted
                    ? s != DefaultSkills.Athletics
                    : s != DefaultSkills.Riding)
                .Distinct()
                .ToList();

            if (skills.Count == 0) return 0f;

            return (float)skills.Average(hero.GetSkillValue);
        }

        /// <summary>
        /// Thresholds as configured, in order. Anything unparseable is dropped rather than
        /// throwing, so one bad character in the setting cannot stop every companion in the
        /// campaign from progressing.
        /// </summary>
        public static List<int> Thresholds(string setting)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(setting)) return result;

            foreach (string part in setting.Split(','))
            {
                if (int.TryParse(part.Trim(), out int value) && value > 0)
                    result.Add(value);
            }

            result.Sort();
            return result;
        }

        /// <summary>
        /// How far a companion is from their next tier, for chat.
        /// </summary>
        public static string ProgressText(Hero companion)
        {
            var campaign = BLTAdoptAHeroCampaignBehavior.Current;
            var classDef = campaign?.GetClass(companion);
            if (classDef == null) return "";

            var thresholds = Thresholds(BLTAdoptAHeroModule.CommonConfig?.CompanionTierThresholds);
            int tier = Math.Max(0, campaign.GetEquipmentTier(companion));
            float average = ClassSkillAverage(companion, classDef);

            if (tier >= thresholds.Count)
                return $"{average:0} skill average (top tier)";

            return $"{average:0}/{thresholds[tier]} to tier {tier + 2}";
        }
    }
}
