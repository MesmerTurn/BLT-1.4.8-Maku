using System;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    [LocDisplayName("{=TESTING}RallyCommand"),
     LocDescription("{=TESTING}Heals your hero to full, patches up your retinue and companions, and makes the whole group fight harder for a while. Usage: !rally"),
     UsedImplicitly]
    public class RallyCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Gold Cost"),
             LocDescription("{=}Gold the viewer pays to rally. 0 makes it free."),
             PropertyOrder(1), UsedImplicitly]
            public int GoldCost { get; set; } = 0;

            [LocDisplayName("{=}Cooldown (seconds)"),
             LocDescription("{=}How long a viewer must wait between rallies, in seconds of battle time. 0 means no cooldown."),
             PropertyOrder(2), UsedImplicitly]
            public float CooldownSeconds { get; set; } = 180f;

            [LocDisplayName("{=}Duration (seconds)"),
             LocDescription("{=}How long the rally bonuses last."),
             PropertyOrder(3), UsedImplicitly]
            public float DurationSeconds { get; set; } = 90f;

            [LocDisplayName("{=}Retinue Heal Percent"),
             LocDescription("{=}How much of their maximum health retinue and companions are healed by. The hero themselves is always healed to full."),
             PropertyOrder(4), UsedImplicitly]
            public float RetinueHealPercent { get; set; } = 50f;

            [LocDisplayName("{=}Damage Dealt Percent"),
             LocDescription("{=}Damage the rallied group deals, as a percent. 125 means +25%."),
             PropertyOrder(5), UsedImplicitly]
            public float DamageDealtPercent { get; set; } = 125f;

            [LocDisplayName("{=}Damage Taken Percent"),
             LocDescription("{=}Damage the rallied group takes, as a percent. 75 means -25%."),
             PropertyOrder(6), UsedImplicitly]
            public float DamageTakenPercent { get; set; } = 75f;

            [LocDisplayName("{=}Lifesteal Percent"),
             LocDescription("{=}Percent of the damage they deal that is healed back."),
             PropertyOrder(7), UsedImplicitly]
            public float LifestealPercent { get; set; } = 20f;

            [LocDisplayName("{=}Archer Fire Rate Percent"),
             LocDescription("{=}Reload speed for rallied archers and crossbowmen, as a percent. 150 means half again as fast. Only given to those actually carrying a ranged weapon."),
             PropertyOrder(8), UsedImplicitly]
            public float RangedFireRatePercent { get; set; } = 150f;

            [LocDisplayName("{=}Rally Effect"),
             LocDescription("{=}Particle effect and sound played on everyone who is rallied - the 'rally sparks'."),
             PropertyOrder(9), ExpandableObject, Expand, UsedImplicitly]
            public OneShotEffect RallyEffect { get; set; }

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.P($"Heals you to full, heals retinue and companions by {RetinueHealPercent}% of their health, "
                            + $"then rallies the group for {DurationSeconds}s: "
                            + $"{DamageDealtPercent - 100:+0;-0}% damage, {100 - DamageTakenPercent:+0;-0}% damage taken, "
                            + $"{LifestealPercent}% lifesteal, {RangedFireRatePercent}% archer fire rate.");
                if (GoldCost > 0) generator.P($"Costs {GoldCost}{Naming.Gold}.");
                if (CooldownSeconds > 0) generator.P($"Can be used once every {CooldownSeconds}s.");
            }
        }

        public override Type HandlerConfigType => typeof(Settings);

        // Battle time of each hero's last rally. Not saved: a cooldown only has meaning inside the
        // battle it was started in, and missions restart the clock anyway.
        private readonly System.Collections.Generic.Dictionary<Hero, float> lastRally = new();

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                onFailure("{=}Rally can only be used during a battle".Translate());
                return;
            }
            if (!Mission.Current.IsDeploymentFinished)
            {
                onFailure("{=}Wait until the battle starts".Translate());
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<BLTRallyBehavior>();
            if (behavior == null)
            {
                onFailure("{=}The rally system is not running".Translate());
                return;
            }

            var heroAgent = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero)?.CurrentAgent;
            if (heroAgent == null || !heroAgent.IsActive())
            {
                onFailure("{=}You must be summoned in this battle".Translate());
                return;
            }

            float now = Mission.Current.CurrentTime;
            if (settings.CooldownSeconds > 0
                && lastRally.TryGetValue(adoptedHero, out float last)
                && now - last < settings.CooldownSeconds)
            {
                onFailure("{=}You cannot rally again for another {Seconds}s"
                    .Translate(("Seconds", (int)(settings.CooldownSeconds - (now - last)))));
                return;
            }

            // Charged only once everything else has passed, so a rally that could not happen never
            // costs the viewer anything.
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

            int rallied = 0;
            foreach (var agent in BLTRallyBehavior.RallyGroup(adoptedHero).ToList())
            {
                try
                {
                    // The hero is put back on their feet completely; everyone else gets a share of
                    // their own maximum, so a healthy man is not handed more than he can hold.
                    if (agent == heroAgent)
                        agent.Health = agent.HealthLimit;
                    else if (settings.RetinueHealPercent > 0)
                        agent.Health = Math.Min(agent.HealthLimit,
                            agent.Health + agent.HealthLimit * settings.RetinueHealPercent / 100f);

                    behavior.Rally(agent, settings.DurationSeconds, settings.DamageDealtPercent,
                        settings.DamageTakenPercent, settings.LifestealPercent,
                        settings.RangedFireRatePercent);

                    settings.RallyEffect.Trigger(agent);
                    rallied++;
                }
                catch (Exception ex)
                {
                    Log.Exception($"{nameof(RallyCommand)}", ex);
                }
            }

            lastRally[adoptedHero] = now;

            onSuccess(rallied > 1
                ? "{=}{Name} rallies! {Count} fighters healed and fired up for {Seconds}s"
                    .Translate(("Name", adoptedHero.FirstName.ToString()), ("Count", rallied),
                        ("Seconds", (int)settings.DurationSeconds))
                : "{=}{Name} rallies! Healed and fired up for {Seconds}s"
                    .Translate(("Name", adoptedHero.FirstName.ToString()),
                        ("Seconds", (int)settings.DurationSeconds)));
        }
    }
}
