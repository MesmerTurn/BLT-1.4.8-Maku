
using System;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
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
                ActivateForCompanions(adoptedHero, heroClass);
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
        private static void ActivateForCompanions(Hero adoptedHero, HeroClassDef heroClass)
        {
            if (BLTAdoptAHeroModule.CommonConfig?.CompanionsUsePowers != true) return;

            var clan = adoptedHero.Clan;
            if (clan == null || Mission.Current?.Agents == null) return;

            try
            {
                foreach (var agent in Mission.Current.Agents.ToList())
                {
                    if (agent == null || !agent.IsActive()) continue;

                    var companion = (agent.Character as CharacterObject)?.HeroObject;
                    if (companion == null || companion == adoptedHero) continue;
                    if (companion.CompanionOf != clan) continue;

                    heroClass.ActivePower.ActivateSilently(companion);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(UsePower)}.{nameof(ActivateForCompanions)}", ex);
            }
        }
    }
}