
using System;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using System.Collections.Generic;
using System.Linq;
using BLTAdoptAHero.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    [LocDisplayName("{=Wpx7tHFL}Use Power"),
     LocDescription("{=FJwn6kGW}Allows activation of the adopted heroes class active powers"),
     UsedImplicitly]
    public class UsePower : HeroActionHandlerBase
    {
        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            var heroClass = adoptedHero.GetClass();
            if (heroClass == null)
            {
                onFailure("{=85HYGklq}You don't have a class, so no powers are available!".Translate());
                return;
            }

            (bool canActivate, string failReason) = heroClass.ActivePower.CanActivate(adoptedHero);
            if (!canActivate)
            {
                onFailure("{=nXSUvuyD}You cannot activate your powers now: {FailReason}!"
                    .Translate(("FailReason", failReason)));
                return;
            }

            if (heroClass.ActivePower.IsActive(adoptedHero))
            {
                onFailure("{=o23xAj6M}Your powers are already active!".Translate());
                return;
            }

            (bool allowed, string message) = heroClass.ActivePower.Activate(adoptedHero, context);

            if (allowed)
            {
                int companions = ActivateForCompanions(adoptedHero, heroClass);

                // Say so in chat and on screen. Without it, a viewer has no way of knowing their
                // companions joined in - and the whole feature is invisible.
                if (companions > 0)
                {
                    string companionMessage = "{=}{Count} of {Name}'s companions use their powers"
                        .Translate(("Count", companions), ("Name", adoptedHero.FirstName.ToString()));

                    Log.ShowInformation(companionMessage, adoptedHero.CharacterObject);
                    Log.LogFeedEvent(companionMessage);
                    message += $" ({companions} companions too)";
                }

                onSuccess(message);
            }
            else
            {
                onFailure(message);
            }
        }

        /// <summary>
        /// The viewer's companions use the same powers, at the same moment, for as long. They
        /// fight as one group, so they should look like one.
        /// </summary>
        private static int ActivateForCompanions(Hero adoptedHero, HeroClassDef heroClass)
        {
            if (BLTAdoptAHeroModule.CommonConfig?.CompanionsUsePowers != true) return 0;

            var clan = adoptedHero.Clan;
            if (Mission.Current?.Agents == null) return 0;

            int activated = 0;

            try
            {
                var campaign = BLTAdoptAHeroCampaignBehavior.Current;
                var hired = campaign?.GetHiredCompanions(adoptedHero).ToList() ?? new List<Hero>();

                foreach (var agent in Mission.Current.Agents.ToList())
                {
                    if (agent == null || !agent.IsActive()) continue;

                    var companion = (agent.Character as CharacterObject)?.HeroObject;
                    if (companion == null || companion == adoptedHero) continue;

                    // Either kind: a companion of the viewer's clan, or one they hired. A hired
                    // companion given a noble title is no longer "companion of" anything, and was
                    // being left out of the viewer's power exactly when they were at their best.
                    if (companion.CompanionOf != clan && !hired.Contains(companion)) continue;

                    // Their own class if they have one - a hired companion is a fighter in their
                    // own right, not a copy of their owner - otherwise the viewer's.
                    var theirClass = campaign?.GetClass(companion) ?? heroClass;
                    theirClass.ActivePower.ActivateSilently(companion);
                    activated++;
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(UsePower)}.{nameof(ActivateForCompanions)}", ex);
            }

            return activated;
        }
    }
}