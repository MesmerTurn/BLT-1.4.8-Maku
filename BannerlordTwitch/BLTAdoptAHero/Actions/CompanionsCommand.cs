using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    /// <summary>
    /// Asked for by Maku: viewers could promote companions but then had no way of finding them
    /// again. Lists who they have, how strong they are, and where each one actually is.
    /// </summary>
    [LocDisplayName("{=TESTING}CompanionsCommand"),
     LocDescription("{=TESTING}Lists your companions, their level and where they are. Usage: !companions"),
     UsedImplicitly]
    public class CompanionsCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Show Location"),
             LocDescription("{=}Say where each companion is - in your party, in a battle, or waiting in a town."),
             PropertyOrder(1), UsedImplicitly]
            public bool ShowLocation { get; set; } = true;

            [LocDisplayName("{=}Maximum Listed"),
             LocDescription("{=}How many companions to name before summarising the rest, so one viewer cannot flood chat."),
             PropertyOrder(2), UsedImplicitly]
            public int MaxListed { get; set; } = 8;

            public void GenerateDocumentation(IDocumentationGenerator generator)
                => generator.P("Lists your companions with their level and where they are.");
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            var clan = adoptedHero.Clan;
            var campaign = BLTAdoptAHeroCampaignBehavior.Current;

            // Both kinds, and no duplicates: the clan's companions, plus everyone this viewer has
            // hired. A hired companion given a noble title stops being a "companion of" the clan
            // in the game's eyes, and would otherwise vanish from this list the moment they were
            // promoted - which is precisely when a viewer wants to see them.
            var companions = (clan?.Companions ?? Enumerable.Empty<Hero>())
                .Concat(campaign?.GetHiredCompanions(adoptedHero) ?? Enumerable.Empty<Hero>())
                .Where(c => c != null && c != adoptedHero && !c.IsDead)
                .Distinct()
                .ToList();

            if (companions.Count == 0)
            {
                onFailure("{=}You have no companions. Promote one from your retinue first".Translate());
                return;
            }

            int listed = Math.Max(1, settings.MaxListed);
            var parts = companions
                .Take(listed)
                .Select(c => Describe(c, settings.ShowLocation));

            string message = string.Join(", ", parts);
            if (companions.Count > listed)
                message += $" and {companions.Count - listed} more";

            onSuccess($"{companions.Count} companions: {message}");
        }

        /// <summary>
        /// One companion in a line of chat: their class and equipment tier when they are a hired
        /// companion, their level when they are not, and where they are.
        /// </summary>
        private static string Describe(Hero companion, bool showLocation)
        {
            var campaign = BLTAdoptAHeroCampaignBehavior.Current;
            string what = $"lvl {companion.Level}";

            if (campaign?.IsHiredCompanion(companion) == true)
            {
                var classDef = campaign.GetClass(companion);
                int tier = campaign.GetEquipmentTier(companion) + 1;
                what = classDef != null ? $"{classDef.Name} T{tier}" : $"T{tier}";

                // How close they are to the next tier, so a viewer can see them climbing rather
                // than wondering whether anything is happening at all.
                string progress = BLTHiredCompanionBehavior.ProgressText(companion);
                if (!string.IsNullOrEmpty(progress)) what += $", {progress}";
            }

            return showLocation
                ? $"{companion.FirstName} ({what}, {WhereIs(companion)})"
                : $"{companion.FirstName} ({what})";
        }

        /// <summary>
        /// Where a companion is, in words a viewer can act on - the party they travel with, the
        /// battle they are in, or the settlement they are sitting in.
        /// </summary>
        private static string WhereIs(Hero companion)
        {
            try
            {
                if (Mission.Current != null && companion.GetAgent()?.IsActive() == true)
                    return "in this battle";

                var settlement = companion.CurrentSettlement;
                if (settlement != null) return $"in {settlement.Name}";

                var party = companion.PartyBelongedTo;
                if (party != null)
                {
                    if (party.LeaderHero == companion) return "leading their own party";
                    if (party.LeaderHero != null) return $"with {party.LeaderHero.FirstName}";
                    return "travelling";
                }

                var near = companion.LastKnownClosestSettlement;
                return near != null ? $"near {near.Name}" : "whereabouts unknown";
            }
            catch (Exception ex)
            {
                Log.Trace($"[Companions] Could not locate {companion?.Name}: {ex.Message}");
                return "whereabouts unknown";
            }
        }
    }
}
