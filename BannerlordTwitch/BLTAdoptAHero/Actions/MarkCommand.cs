using System;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Util;
using BannerlordTwitch.Localization;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    [LocDisplayName("{=TESTING}MarkCommand"),
     LocDescription("{=TESTING}Puts a bounty on the nearest enemy lord or commander. Whoever kills them takes the gold. Usage: !mark"),
     UsedImplicitly]
    public class MarkCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Gold Cost"),
             LocDescription("{=}What the viewer pays to put the mark up. The reward itself is set in BLT Configure under Battle Events."),
             PropertyOrder(1), UsedImplicitly]
            public int GoldCost { get; set; } = 1000;

            [LocDisplayName("{=}Mark Duration (seconds)"),
             LocDescription("{=}How long the mark lasts before it fades."),
             PropertyOrder(2), UsedImplicitly]
            public float DurationSeconds { get; set; } = 120f;

            [LocDisplayName("{=}Mark Effect"),
             LocDescription("{=}Effect played on the target the moment they are marked."),
             PropertyOrder(3), ExpandableObject, Expand, UsedImplicitly]
            public OneShotEffect MarkEffect { get; set; }

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.P($"Marks the nearest enemy lord or commander for {DurationSeconds}s. Whoever kills them is paid the bounty set in Battle Events.");
                if (GoldCost > 0) generator.P($"Costs {GoldCost}{Naming.Gold}.");
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
            if (Mission.Current == null || !Mission.Current.IsDeploymentFinished)
            {
                onFailure("{=}You can only mark someone during a battle".Translate());
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<BLTBattleEventsBehavior>();
            var agent = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero)?.CurrentAgent;
            if (behavior == null || agent == null || !agent.IsActive())
            {
                onFailure("{=}You must be summoned in this battle".Translate());
                return;
            }

            // Checked before charging: a mark that could not be placed must not cost anything.
            if (behavior.HasMark)
            {
                onFailure("{=}{Name} is already marked".Translate(("Name", behavior.MarkedName)));
                return;
            }

            if (settings.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current.GetHeroGold(adoptedHero);
                if (gold < settings.GoldCost)
                {
                    onFailure(Naming.NotEnoughGold(settings.GoldCost, gold));
                    return;
                }
            }

            var (marked, message) = behavior.Mark(adoptedHero, agent, settings.DurationSeconds,
                settings.MarkEffect);

            if (!marked)
            {
                onFailure(message);
                return;
            }

            if (settings.GoldCost > 0)
                BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(adoptedHero, -settings.GoldCost);

            onSuccess(message);
        }
    }
}
