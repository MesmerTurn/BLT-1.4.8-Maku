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
    [LocDisplayName("{=TESTING}WarHornCommand"),
     LocDescription("{=TESTING}Blows a war horn: every ally around your hero fights harder for a while, and enemies who hear it lose heart. Usage: !warhorn"),
     UsedImplicitly]
    public class WarHornCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Gold Cost"), PropertyOrder(1), UsedImplicitly]
            public int GoldCost { get; set; } = 0;

            [LocDisplayName("{=}Cooldown (seconds)"),
             LocDescription("{=}How long a viewer waits between horns. This one is shared by everyone in the battle, so the field is not one long horn blast."),
             PropertyOrder(2), UsedImplicitly]
            public float CooldownSeconds { get; set; } = 120f;

            [LocDisplayName("{=}Radius (metres)"),
             LocDescription("{=}How far the horn carries."),
             PropertyOrder(3), UsedImplicitly]
            public float Radius { get; set; } = 30f;

            [LocDisplayName("{=}Duration (seconds)"), PropertyOrder(4), UsedImplicitly]
            public float DurationSeconds { get; set; } = 30f;

            [LocDisplayName("{=}Damage Dealt Percent"),
             LocDescription("{=}Damage allies deal while the horn holds. 115 means +15%."),
             PropertyOrder(5), UsedImplicitly]
            public float DamageDealtPercent { get; set; } = 115f;

            [LocDisplayName("{=}Damage Taken Percent"),
             LocDescription("{=}Damage allies take while the horn holds. 90 means -10%."),
             PropertyOrder(6), UsedImplicitly]
            public float DamageTakenPercent { get; set; } = 90f;

            [LocDisplayName("{=}Enemy Morale Loss"),
             LocDescription("{=}How much morale nearby enemies lose. Morale runs 0 to 100; drop it far enough and they break and run."),
             PropertyOrder(7), UsedImplicitly]
            public float EnemyMoraleLoss { get; set; } = 10f;

            [LocDisplayName("{=}Horn Effect"),
             LocDescription("{=}Particle effect and sound played where the horn is blown - pick a horn sound here."),
             PropertyOrder(8), ExpandableObject, Expand, UsedImplicitly]
            public OneShotEffect HornEffect { get; set; }

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.P($"Allies within {Radius}m fight harder for {DurationSeconds}s, and enemies in earshot lose {EnemyMoraleLoss} morale.");
                if (GoldCost > 0) generator.P($"Costs {GoldCost}{Naming.Gold}.");
            }
        }

        public override Type HandlerConfigType => typeof(Settings);

        // Shared by everyone, not per viewer: the horn is a battlefield event, and twenty viewers
        // each with their own cooldown would mean it never stops sounding.
        private float lastHorn = float.MinValue;

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
                onFailure("{=}The horn can only be blown during a battle".Translate());
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<BLTBattleEventsBehavior>();
            var agent = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero)?.CurrentAgent;
            if (behavior == null || agent == null || !agent.IsActive())
            {
                onFailure("{=}You must be summoned in this battle".Translate());
                return;
            }

            float now = Mission.Current.CurrentTime;
            if (settings.CooldownSeconds > 0 && now - lastHorn < settings.CooldownSeconds)
            {
                onFailure("{=}The horn cannot be blown again for another {Seconds}s"
                    .Translate(("Seconds", (int)(settings.CooldownSeconds - (now - lastHorn)))));
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
                BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(adoptedHero, -settings.GoldCost);
            }

            int affected = behavior.BlowWarHorn(agent, settings.Radius, settings.DurationSeconds,
                settings.DamageDealtPercent, settings.DamageTakenPercent, settings.EnemyMoraleLoss,
                settings.HornEffect);

            lastHorn = now;

            onSuccess("{=}{Name} sounds the war horn! {Count} fighters answer it"
                .Translate(("Name", adoptedHero.FirstName.ToString()), ("Count", affected)));
        }
    }
}
