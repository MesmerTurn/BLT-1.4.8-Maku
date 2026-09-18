using System;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero.Actions
{
    /// <summary>
    /// Its own command because viewers reach for "!unguard" rather than remembering "!guard off".
    /// Does exactly what "!guard off" does.
    /// </summary>
    [LocDisplayName("{=TESTING}UnguardCommand"),
     LocDescription("{=TESTING}Stops your retinue guarding your hero and sends them back to fighting normally. Usage: !unguard"),
     UsedImplicitly]
    public class UnguardCommand : HeroCommandHandlerBase
    {
        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            var behavior = Mission.Current?.GetMissionBehavior<BLTGuardBehavior>();
            if (behavior == null)
            {
                onFailure("Not in a mission.");
                return;
            }
            if (!behavior.IsGuarding(adoptedHero))
            {
                onFailure($"{adoptedHero.FirstName}'s retinue is not guarding.");
                return;
            }

            behavior.DeactivateGuard(adoptedHero);
            onSuccess($"{adoptedHero.FirstName}'s retinue stopped guarding them.");
        }
    }
}
