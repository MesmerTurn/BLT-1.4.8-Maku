using System;
using System.ComponentModel.DataAnnotations;
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

            [LocDisplayName("{=promote017}Max Companions"),
             LocDescription("{=promote018}How many companions one viewer may ever promote out of their retinue. Counted for the lifetime of the hero, so it does not refill when a promoted companion dies or leaves. Set to 0 for no limit."),
             PropertyOrder(5), Range(0, 100), UsedImplicitly]
            public int MaxCompanions { get; set; } = 5;

            [LocDisplayName("{=promote019}Max Lords"),
             LocDescription("{=promote020}How many lords one viewer may ever promote. Each one creates a permanent clan in the campaign, so this is deliberately lower than the companion limit. Counted for the lifetime of the hero. Set to 0 for no limit."),
             PropertyOrder(6), Range(0, 100), UsedImplicitly]
            public int MaxLords { get; set; } = 3;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.PropertyValuePair("Companion Cost", CompanionCost.ToString());
                generator.PropertyValuePair("Lord Cost", LordCost.ToString());
                generator.PropertyValuePair("Allow Lord Promotion", AllowLordPromotion.ToString());
                generator.PropertyValuePair("Lord Starting Renown", LordRenown.ToString());
                generator.PropertyValuePair("Max Companions", MaxCompanions == 0 ? "unlimited" : MaxCompanions.ToString());
                generator.PropertyValuePair("Max Lords", MaxLords == 0 ? "unlimited" : MaxLords.ToString());
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

            // Lifetime cap, checked before anything is spent. Deliberately not "how many are
            // alive right now": counting live characters would quietly hand the viewer a new slot
            // every time one of their lords was killed or captured, which is the opposite of a cap.
            int used = asLord
                ? BLTAdoptAHeroCampaignBehavior.Current.GetPromotedLordCount(adoptedHero)
                : BLTAdoptAHeroCampaignBehavior.Current.GetPromotedCompanionCount(adoptedHero);
            int allowed = asLord ? settings.MaxLords : settings.MaxCompanions;

            if (allowed > 0 && used >= allowed)
            {
                onFailure(asLord
                    ? "{=promote021}You have already promoted {USED} of {MAX} lords".Translate(("USED", used), ("MAX", allowed))
                    : "{=promote022}You have already promoted {USED} of {MAX} companions".Translate(("USED", used), ("MAX", allowed)));
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
                    // The new lord joins whatever kingdom the viewer is in at this moment - the
                    // troop was theirs, so the lord it becomes owes them allegiance. Works the
                    // same whether that is a base-game kingdom or one a viewer founded through
                    // BLT. A viewer with no kingdom still produces an independent clan, as before.
                    BLTTroopAscension.MakeLordOfNewClan(
                        newHero, settings.LordRenown, 0, adoptedHero.Clan?.Kingdom);
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

                // Only charge, consume the retinue slot, and count it against the cap once the
                // promotion actually succeeded - a failed attempt should not burn a slot.
                BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(adoptedHero, -cost, true);
                BLTAdoptAHeroCampaignBehavior.Current.RecordPromotion(adoptedHero, asLord);
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
