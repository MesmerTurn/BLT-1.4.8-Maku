using System;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Requested by Maku ("The Beast"): let a viewer turn one of their retinue into a real
    /// character under their control - either a companion riding with their hero, or a lord
    /// leading a clan of their own.
    ///
    /// Retinue entries are CharacterObject troop templates, not Heroes, so a promotion has to
    /// create a Hero from the template first. That is the same step BLTTroopAscension performs
    /// when a soldier kills a hero, and both end up going through the ordinary game systems
    /// afterwards rather than needing special cases.
    ///
    /// Usage: !promote companion   or   !promote lord
    /// </summary>
    [LocDisplayName("{=promote001}Promote Retinue"),
     LocDescription("{=promote002}Promote one of your retinue into a companion of your hero, or a lord leading their own clan"),
     UsedImplicitly]
    public class PromoteRetinue : ActionHandlerBase
    {
        private class Settings : IDocumentable
        {
            [LocDisplayName("{=promote003}Companion Cost"),
             LocDescription("{=promote004}Gold the viewer pays to promote a retinue member into a companion of their hero"),
             PropertyOrder(1), UsedImplicitly]
            public int CompanionCost { get; set; } = 25000;

            [LocDisplayName("{=promote005}Lord Cost"),
             LocDescription("{=promote006}Gold the viewer pays to promote a retinue member into a lord of their own clan. Higher than the companion cost - a lord is permanent and adds a clan to the campaign."),
             PropertyOrder(2), UsedImplicitly]
            public int LordCost { get; set; } = 150000;

            [LocDisplayName("{=promote007}Allow Lord Promotion"),
             LocDescription("{=promote008}Whether 'promote lord' is available at all. Each lord promotion creates a permanent clan, so turn this off if your campaign is getting crowded."),
             PropertyOrder(3), UsedImplicitly]
            public bool AllowLordPromotion { get; set; } = true;

            [LocDisplayName("{=promote009}Lord Starting Renown"),
             LocDescription("{=promote010}Renown the new lord's clan starts with"),
             PropertyOrder(4), UsedImplicitly]
            public int LordRenown { get; set; } = 150;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.PropertyValuePair("Companion Cost", CompanionCost.ToString());
                generator.PropertyValuePair("Lord Cost", LordCost.ToString());
                generator.PropertyValuePair("Allow Lord Promotion", AllowLordPromotion.ToString());
                generator.PropertyValuePair("Lord Starting Renown", LordRenown.ToString());
            }
        }

        protected override Type ConfigType => typeof(Settings);

        protected override void ExecuteInternal(ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            var settings = (Settings)config;
            var adoptedHero = BLTAdoptAHeroCampaignBehavior.Current.GetAdoptedHero(context.UserName);

            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            // Promotion pulls a troop out of the retinue and creates a permanent character, so it
            // must not happen while that retinue is fighting in a live mission.
            if (Mission.Current != null)
            {
                onFailure("{=promote011}You cannot promote retinue during a mission".Translate());
                return;
            }

            string arg = (context.Args ?? "").Trim().ToLower();
            bool asLord = arg.StartsWith("lord");
            bool asCompanion = arg.StartsWith("companion") || arg.Length == 0;

            if (!asLord && !asCompanion)
            {
                onFailure("{=promote012}Use 'companion' or 'lord'".Translate());
                return;
            }

            if (asLord && !settings.AllowLordPromotion)
            {
                onFailure("{=promote013}Lord promotion is disabled".Translate());
                return;
            }

            // Both lists count: Maku asked for elite retinue to be promotable as well, and this
            // build keeps the elite roster separate from the normal one.
            var retinue = BLTAdoptAHeroCampaignBehavior.Current.GetRetinue(adoptedHero).ToList();
            var eliteRetinue = BLTAdoptAHeroCampaignBehavior.Current.GetRetinue2(adoptedHero).ToList();
            var all = retinue.Concat(eliteRetinue).ToList();
            if (all.Count == 0)
            {
                onFailure("{=promote014}You have no retinue to promote".Translate());
                return;
            }

            int cost = asLord ? settings.LordCost : settings.CompanionCost;
            int gold = BLTAdoptAHeroCampaignBehavior.Current.GetHeroGold(adoptedHero);
            if (gold < cost)
            {
                onFailure("{=promote015}You need {COST}{GOLDSYM} to do that, you have {GOLD}{GOLDSYM}"
                    .Translate(("COST", cost), ("GOLD", gold), ("GOLDSYM", Naming.Gold)));
                return;
            }

            // Promote the best troop the viewer has, elite included - they paid for a character,
            // not a lottery.
            var troopType = all.OrderByDescending(t => t.Level).First();
            bool fromElite = eliteRetinue.Contains(troopType) && !retinue.Contains(troopType);

            try
            {
                var newHero = HeroCreator.CreateSpecialHero(troopType);
                if (newHero == null)
                {
                    onFailure("{=promote016}Could not promote that troop".Translate());
                    return;
                }

                newHero.ChangeState(Hero.CharacterStates.Active);

                if (asLord)
                {
                    BLTTroopAscension.MakeLordOfNewClan(newHero, settings.LordRenown, 0);
                }
                else
                {
                    // A companion of the viewer's hero, so it travels and fights with them.
                    newHero.Clan = adoptedHero.Clan;
                    newHero.CompanionOf = adoptedHero.Clan;
                    newHero.SetNewOccupation(Occupation.Wanderer);

                    if (adoptedHero.PartyBelongedTo != null)
                    {
                        AddHeroToPartyAction.Apply(newHero, adoptedHero.PartyBelongedTo);
                    }
                }

                // Only charge, and only consume the retinue slot, once the promotion succeeded.
                BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(adoptedHero, -cost, true);
                // Take it out of whichever list it came from, so the slot is spent once and the
                // troop is not duplicated between the two rosters.
                if (fromElite)
                {
                    BLTAdoptAHeroCampaignBehavior.Current.RemoveEliteRetinueTroop(adoptedHero, troopType);
                }
                else if (!BLTAdoptAHeroCampaignBehavior.Current.RemoveRetinueTroop(adoptedHero, troopType))
                {
                    BLTAdoptAHeroCampaignBehavior.Current.RemoveEliteRetinueTroop(adoptedHero, troopType);
                }

                onSuccess(asLord
                    ? "{=promote017}{NAME} has risen from your retinue as a lord of their own clan!"
                        .Translate(("NAME", newHero.Name.ToString()))
                    : "{=promote018}{NAME} has joined you as a companion!"
                        .Translate(("NAME", newHero.Name.ToString())));
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(PromoteRetinue)}", ex);
                onFailure("{=promote016}Could not promote that troop".Translate());
            }
        }
    }
}
