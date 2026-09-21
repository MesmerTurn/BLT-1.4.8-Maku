using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    /// <summary>
    /// Shared plumbing for the battle event commands: they all need a battle, a summoned hero, the
    /// events behaviour, a cooldown and a price.
    /// </summary>
    public abstract class BattleEventCommandBase : HeroCommandHandlerBase
    {
        public class CommonSettings
        {
            [LocDisplayName("{=}Gold Cost"), PropertyOrder(1), UsedImplicitly]
            public int GoldCost { get; set; }

            [LocDisplayName("{=}Cooldown (seconds)"),
             LocDescription("{=}Shared by everyone in the battle, not per viewer."),
             PropertyOrder(2), UsedImplicitly]
            public float CooldownSeconds { get; set; } = 120f;
        }

        private float lastUse = float.MinValue;

        protected bool Prepare(Hero hero, CommonSettings settings, Action<string> onFailure,
            out BLTBattleEventsBehavior behavior, out Agent agent)
        {
            behavior = null;
            agent = null;

            if (hero == null) { onFailure(AdoptAHero.NoHeroMessage); return false; }

            if (Mission.Current == null || !Mission.Current.IsDeploymentFinished)
            {
                onFailure("{=}That can only be done during a battle".Translate());
                return false;
            }

            behavior = Mission.Current.GetMissionBehavior<BLTBattleEventsBehavior>();
            agent = BLTSummonBehavior.Current?.GetHeroSummonState(hero)?.CurrentAgent;
            if (behavior == null || agent == null || !agent.IsActive())
            {
                onFailure("{=}You must be summoned in this battle".Translate());
                return false;
            }

            float now = Mission.Current.CurrentTime;
            if (settings.CooldownSeconds > 0 && now - lastUse < settings.CooldownSeconds)
            {
                onFailure("{=}Not for another {Seconds}s"
                    .Translate(("Seconds", (int)(settings.CooldownSeconds - (now - lastUse)))));
                return false;
            }

            if (settings.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current.GetHeroGold(hero);
                if (gold < settings.GoldCost)
                {
                    onFailure(Naming.NotEnoughGold(settings.GoldCost, gold));
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Called once the thing actually happened, so a failed attempt costs nothing and does not
        /// start the cooldown.
        /// </summary>
        protected void Charge(Hero hero, CommonSettings settings)
        {
            if (settings.GoldCost > 0)
                BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(hero, -settings.GoldCost);
            lastUse = Mission.Current.CurrentTime;
        }
    }

    [LocDisplayName("{=TESTING}BurningArrowsCommand"),
     LocDescription("{=TESTING}Your archers - and your retinue's - shoot fire for a while: anyone they hit burns. Usage: !burningarrows"),
     UsedImplicitly]
    public class BurningArrowsCommand : BattleEventCommandBase
    {
        public class Settings : CommonSettings, IDocumentable
        {
            [LocDisplayName("{=}Duration (seconds)"),
             LocDescription("{=}How long their arrows stay lit."),
             PropertyOrder(3), UsedImplicitly]
            public float DurationSeconds { get; set; } = 30f;

            [LocDisplayName("{=}Light Effect"),
             LocDescription("{=}Effect played on the archers when their arrows are lit."),
             PropertyOrder(4), ExpandableObject, Expand, UsedImplicitly]
            public OneShotEffect LightEffect { get; set; }

            public void GenerateDocumentation(IDocumentationGenerator generator)
                => generator.P($"Your hero, retinue and companions shoot fire for {DurationSeconds}s. Burn damage is set in BLT Configure under Battle Events.");
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (!Prepare(adoptedHero, settings, onFailure, out var behavior, out _)) return;

            int count = behavior.LightArrows(adoptedHero, settings.DurationSeconds, settings.LightEffect);
            if (count == 0)
            {
                onFailure("{=}Nobody of yours is in this battle to light".Translate());
                return;
            }

            Charge(adoptedHero, settings);
            onSuccess("{=}{Name}'s arrows are alight! ({Count} fighters, {Seconds}s)"
                .Translate(("Name", adoptedHero.FirstName.ToString()), ("Count", count),
                    ("Seconds", (int)settings.DurationSeconds)));
        }
    }

    [LocDisplayName("{=TESTING}BannerCommand"),
     LocDescription("{=TESTING}Take up the army banner. While you hold it, nearby allies fight harder - and if you fall, they lose heart. Usage: !banner"),
     UsedImplicitly]
    public class BannerCommand : BattleEventCommandBase
    {
        public class Settings : CommonSettings, IDocumentable
        {
            public void GenerateDocumentation(IDocumentationGenerator generator)
                => generator.P("While you carry the banner, allies around you fight harder. If you are killed, the men near you lose morale. Only one bearer at a time.");
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (!Prepare(adoptedHero, settings, onFailure, out var behavior, out var agent)) return;

            var (taken, message) = behavior.TakeBanner(adoptedHero, agent);
            if (!taken) { onFailure(message); return; }

            Charge(adoptedHero, settings);
            onSuccess(message);
        }
    }

    [LocDisplayName("{=TESTING}ReinforcementsCommand"),
     LocDescription("{=TESTING}Calls in a wave of troops for your side. Usage: !reinforcements"),
     UsedImplicitly]
    public class ReinforcementsCommand : BattleEventCommandBase
    {
        public class Settings : CommonSettings, IDocumentable
        {
            [LocDisplayName("{=}Troop Count"),
             LocDescription("{=}How many men arrive."),
             Range(1, 100), PropertyOrder(3), UsedImplicitly]
            public int TroopCount { get; set; } = 10;

            [LocDisplayName("{=}Allow In Sieges"),
             LocDescription("{=}Off by default. A siege decides for itself where men may appear, and pushing extra troops into one is the likeliest way to break a battle."),
             PropertyOrder(4), UsedImplicitly]
            public bool AllowInSieges { get; set; } = false;

            [LocDisplayName("{=}Horn Effect"),
             LocDescription("{=}Effect played when the reinforcements are called - a distant horn suits it."),
             PropertyOrder(5), ExpandableObject, Expand, UsedImplicitly]
            public OneShotEffect CallEffect { get; set; }

            public void GenerateDocumentation(IDocumentationGenerator generator)
                => generator.P($"Adds {TroopCount} men to your side. The game brings them onto the field in its own next reinforcement wave, so they arrive shortly rather than instantly.");
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (!Prepare(adoptedHero, settings, onFailure, out _, out var agent)) return;

            if (!settings.AllowInSieges && MissionHelpers.InSiegeMission())
            {
                onFailure("{=}Reinforcements cannot be called in a siege".Translate());
                return;
            }

            // Deliberately done by adding men to the roster rather than spawning agents directly:
            // the game then brings them in through its own reinforcement wave, at a spawn point it
            // chose itself. Spawning them by hand is what breaks missions.
            var party = agent.Origin?.BattleCombatant as PartyBase
                        ?? adoptedHero.PartyBelongedTo?.Party;
            var troop = BLTAdoptAHeroCampaignBehavior.Current.GetRetinue(adoptedHero).FirstOrDefault()
                        ?? party?.MemberRoster?.GetTroopRoster()
                            .FirstOrDefault(t => t.Character?.IsHero == false).Character;

            if (party?.MemberRoster == null || troop == null)
            {
                onFailure("{=}There is nobody to send".Translate());
                return;
            }

            party.MemberRoster.AddToCounts(troop, settings.TroopCount);
            settings.CallEffect.Trigger(agent);

            Charge(adoptedHero, settings);
            onSuccess("{=}{Name} calls in {Count} {Troop} - they are on their way"
                .Translate(("Name", adoptedHero.FirstName.ToString()), ("Count", settings.TroopCount),
                    ("Troop", troop.Name.ToString())));
        }
    }

    [LocDisplayName("{=TESTING}DuelCommand"),
     LocDescription("{=TESTING}Challenge the nearest enemy lord to a duel of champions. Whoever falls first, their whole side loses heart. Usage: !duel"),
     UsedImplicitly]
    public class DuelCommand : BattleEventCommandBase
    {
        public class Settings : CommonSettings, IDocumentable
        {
            public void GenerateDocumentation(IDocumentationGenerator generator)
                => generator.P("Names you and the nearest enemy lord as champions. When one of you dies, the winner's side gains morale and the loser's side loses it. The armies keep fighting - nothing is paused.");
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (!Prepare(adoptedHero, settings, onFailure, out var behavior, out var agent)) return;

            var (started, message) = behavior.StartDuel(adoptedHero, agent);
            if (!started) { onFailure(message); return; }

            Charge(adoptedHero, settings);
            onSuccess(message);
        }
    }
}
