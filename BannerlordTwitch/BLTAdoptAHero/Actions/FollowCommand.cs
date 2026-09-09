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
    [LocDisplayName("{=TESTING}FollowCommand"),
     LocDescription("{=TESTING}Makes your hero follow the streamer or another summoned BLT hero into combat. Usage: !follow | !follow @username | !follow off"),
     UsedImplicitly]
    public class FollowCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=TESTING}Follow Distance (meters)"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}Distance from the leader before the hero stops and fights"),
             PropertyOrder(1), UsedImplicitly]
            public float FollowDistance { get; set; } = 4f;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.Value("<strong>Usage:</strong>");
                generator.Value("- (empty) - follow the streamer");
                generator.Value("- @username - follow another summoned BLT hero");
                generator.Value("- off / unfollow - stop following");
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

            var behavior = Mission.Current?.GetMissionBehavior<BLTFollowBehavior>();
            string arg = (context.Args ?? "").Trim();

            // "off"/"unfollow" always allowed, even if the hero isn't summoned anymore.
            if (arg.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("unfollow", StringComparison.OrdinalIgnoreCase))
            {
                if (behavior == null) { onFailure("Not in a mission."); return; }
                behavior.Deactivate(adoptedHero);
                onSuccess($"{adoptedHero.FirstName} stopped following.");
                return;
            }

            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                onFailure("Follow can only be used during an active mission.");
                return;
            }
            if (!Mission.Current.IsDeploymentFinished)
            {
                onFailure("Can't follow during deployment - wait for the battle to actually begin.");
                return;
            }
            if (behavior == null)
            {
                onFailure("Follow system not active.");
                return;
            }

            var ownAgent = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero)?.CurrentAgent;
            if (ownAgent == null || !ownAgent.IsActive())
            {
                onFailure("Your hero must be summoned in this battle.");
                return;
            }

            if (string.IsNullOrWhiteSpace(arg))
            {
                behavior.Activate(adoptedHero);
                onSuccess($"{adoptedHero.FirstName} is now following you!");
                return;
            }

            string targetName = arg.TrimStart('@');
            var targetHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(targetName);
            if (targetHero == null)
            {
                onFailure($"BLT hero '{targetName}' not found.");
                return;
            }
            if (targetHero == adoptedHero)
            {
                onFailure("You cannot follow yourself.");
                return;
            }

            var targetAgent = BLTSummonBehavior.Current?.GetHeroSummonState(targetHero)?.CurrentAgent;
            if (targetAgent == null || !targetAgent.IsActive())
            {
                onFailure($"{targetHero.FirstName} is not summoned in this battle.");
                return;
            }

            if (ownAgent.Team != null && targetAgent.Team != null && ownAgent.Team != targetAgent.Team)
            {
                onFailure($"{targetHero.FirstName} is on the enemy side - you can't follow them.");
                return;
            }

            behavior.ActivateFollowHero(adoptedHero, targetHero, settings.FollowDistance);
            onSuccess($"{adoptedHero.FirstName} is now following {targetHero.FirstName}!");
        }
    }
}