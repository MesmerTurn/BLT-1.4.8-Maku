using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    /// <summary>
    /// Hired companions, the system Maku asked for after seeing GeneralEddy's (used with his
    /// permission, given 2026-09-24).
    ///
    /// A hired companion is bought outright rather than promoted out of a viewer's retinue. They
    /// are a hero of the viewer's clan with a BLT class of their own - their own kit, their own
    /// skills, their own progression - instead of a frozen copy of whatever troop they used to be.
    /// </summary>
    [LocDisplayName("{=TESTING}BuyCompanionCommand"),
     LocDescription("{=TESTING}Hire a companion with a class of their own. Usage: !buycompanion, or !buycompanion (class name)"),
     UsedImplicitly]
    public class BuyCompanionCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Cost"),
             LocDescription("{=}Gold a companion costs to hire."),
             PropertyOrder(1), UsedImplicitly]
            public int Cost { get; set; } = 250000;

            [LocDisplayName("{=}Maximum Companions"),
             LocDescription("{=}How many hired companions one viewer may have at once. 0 means no limit."),
             PropertyOrder(2), UsedImplicitly]
            public int MaxCompanions { get; set; } = 5;

            [LocDisplayName("{=}Starting Equipment Tier"),
             LocDescription("{=}Equipment tier a newly hired companion starts at, 0 to 5."),
             Range(0, 5), PropertyOrder(3), UsedImplicitly]
            public int StartingTier { get; set; } = 1;

            [LocDisplayName("{=}Require A Clan"),
             LocDescription("{=}Companions belong to the viewer's clan, so by default the viewer needs one first. Turn this off to let clanless viewers hire anyway."),
             PropertyOrder(4), UsedImplicitly]
            public bool RequireClan { get; set; } = true;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.P($"Hires a companion with a class of their own for {Cost}{Naming.Gold}.");
                generator.P("Usage: !buycompanion for a random class, or !buycompanion (class name) to choose.");
                if (MaxCompanions > 0) generator.P($"Limit: {MaxCompanions} at once.");
            }
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
            if (Mission.Current != null)
            {
                onFailure("{=}You cannot hire anyone during a battle".Translate());
                return;
            }

            var clan = adoptedHero.Clan;
            if (settings.RequireClan && clan == null)
            {
                onFailure("{=}You need a clan before you can hire companions".Translate());
                return;
            }

            var campaign = BLTAdoptAHeroCampaignBehavior.Current;
            int owned = campaign.GetHiredCompanions(adoptedHero).Count();
            if (settings.MaxCompanions > 0 && owned >= settings.MaxCompanions)
            {
                onFailure("{=}You already have {Count} companions, which is the limit"
                    .Translate(("Count", owned)));
                return;
            }

            // Class first: asking for one that does not exist should cost nothing and change
            // nothing, so it is resolved before any gold moves.
            string wanted = (context.Args ?? "").Trim();
            var classDef = ResolveClass(wanted);
            if (classDef == null)
            {
                onFailure(string.IsNullOrEmpty(wanted)
                    ? "{=}There are no classes configured to hire from".Translate()
                    : "{=}No class called '{Name}'. Try !buycompanion with no name for a random one"
                        .Translate(("Name", wanted)));
                return;
            }

            int gold = campaign.GetHeroGold(adoptedHero);
            if (gold < settings.Cost)
            {
                onFailure(Naming.NotEnoughGold(settings.Cost, gold));
                return;
            }

            var companion = Create(adoptedHero, clan, classDef, settings, campaign);
            if (companion == null)
            {
                onFailure("{=}Could not find anyone to hire".Translate());
                return;
            }

            campaign.ChangeHeroGold(adoptedHero, -settings.Cost);

            onSuccess("{=}{Name} joins you as a {Class}"
                .Translate(("Name", companion.FirstName.ToString()), ("Class", classDef.Name.ToString())));
        }

        /// <summary>
        /// Same class lookup and same companion creation as !buycompanion, for the wanderer
        /// auction to use - so a companion won at auction is built exactly like a bought one.
        /// </summary>
        public static HeroClassDef ResolveClassPublic(string wanted) => ResolveClass(wanted);

        public static Hero CreateCompanionPublic(Hero owner, Clan clan, HeroClassDef classDef, int tier)
            => Create(owner, clan, classDef,
                new Settings { StartingTier = tier }, BLTAdoptAHeroCampaignBehavior.Current);

        private static HeroClassDef ResolveClass(string wanted)
        {
            var classes = BLTAdoptAHeroModule.HeroClassConfig?.ValidClasses?.ToList();
            if (classes == null || classes.Count == 0) return null;

            if (string.IsNullOrEmpty(wanted)) return classes.SelectRandom();

            // Exact name first, then a partial match, so "queens champion" finds "Queen's
            // Champion" without the viewer having to punctuate it correctly in chat.
            return classes.FirstOrDefault(c =>
                       c.Name.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase))
                   ?? classes.FirstOrDefault(c =>
                       c.Name.ToString().IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Builds the companion: a real hero of the viewer's clan, carrying the chosen class and
        /// kitted out for it. Built from a wanderer template of the viewer's own culture, which is
        /// the same source the game uses for companions it hands out itself.
        /// </summary>
        private static Hero Create(Hero owner, Clan clan, HeroClassDef classDef, Settings settings,
            BLTAdoptAHeroCampaignBehavior campaign)
        {
            try
            {
                var culture = owner.Culture ?? CampaignHelpers.MainCultures.FirstOrDefault();
                var template = CampaignHelpers.GetWandererTemplates(culture).SelectRandom()
                               ?? CampaignHelpers.AllWandererTemplates.SelectRandom();
                if (template == null) return null;

                var companion = HeroCreator.CreateSpecialHero(template);
                if (companion == null) return null;

                companion.ChangeState(Hero.CharacterStates.Active);

                if (clan != null)
                {
                    companion.Clan = clan;
                    companion.CompanionOf = clan;
                }
                companion.SetNewOccupation(Occupation.Wanderer);

                campaign.MarkHiredCompanion(companion, owner);
                campaign.SetClass(companion, classDef);
                campaign.SetEquipmentTier(companion, settings.StartingTier);

                EquipHero.UpgradeEquipment(companion, settings.StartingTier, classDef,
                    replaceSameTier: true);

                // Put them with the viewer if the viewer travels, otherwise in a town, so they are
                // somewhere findable rather than nowhere at all.
                var party = owner.PartyBelongedTo;
                if (party != null)
                {
                    AddHeroToPartyAction.Apply(companion, party);
                }
                else
                {
                    var settlement = owner.CurrentSettlement
                                     ?? owner.LastKnownClosestSettlement
                                     ?? Settlement.All.FirstOrDefault(s => s.IsTown);
                    if (settlement != null)
                        EnterSettlementAction.ApplyForCharacterOnly(companion, settlement);
                }

                Log.LogFeedEvent("{=}{Owner} hired {Name}, a {Class}"
                    .Translate(("Owner", owner.FirstName.ToString()),
                        ("Name", companion.Name.ToString()),
                        ("Class", classDef.Name.ToString())));

                return companion;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BuyCompanionCommand)}", ex);
                return null;
            }
        }
    }
}
