using System;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using BLTAdoptAHero;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    [LocDisplayName("{=TESTING}GuardCommand"),
     LocDescription("{=TESTING}Makes your retinue guard and stay close to your hero, helping fend off attackers. Usage: !guard | !guard off"),
     UsedImplicitly]
    public class GuardCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.Value("<strong>Usage:</strong>");
                generator.Value("- (empty) - retinue guards the hero");
                generator.Value("- off / unguard - stop guarding");
            }
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            var behavior = Mission.Current?.GetMissionBehavior<BLTGuardBehavior>();
            string arg = (context.Args ?? "").Trim();

            if (arg.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("unguard", StringComparison.OrdinalIgnoreCase))
            {
                if (behavior == null) { onFailure("Not in a mission."); return; }
                behavior.DeactivateGuard(adoptedHero);
                onSuccess($"{adoptedHero.FirstName}'s retinue stopped guarding them.");
                return;
            }

            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                onFailure("Guard can only be used during an active mission.");
                return;
            }
            if (!Mission.Current.IsDeploymentFinished)
            {
                onFailure("Can't activate guard during deployment - wait for the battle to actually begin.");
                return;
            }
            if (behavior == null)
            {
                onFailure("Guard system not active.");
                return;
            }

            var heroAgent = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero)?.CurrentAgent;
            if (heroAgent == null || !heroAgent.IsActive())
            {
                onFailure("Your hero must be summoned in this battle.");
                return;
            }

            behavior.ActivateGuard(adoptedHero);
            onSuccess($"{adoptedHero.FirstName}'s retinue is now guarding them!");
        }
    }
}