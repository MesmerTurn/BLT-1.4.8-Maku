using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using BLTAdoptAHero;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    [LocDisplayName("{=BLTFormation}Formation"),
     LocDescription("{=BLTFormationDesc}Lets a viewer see the formations in the current battle, move between them, take a place at the front or the back, and - if detachments are enabled - leave the line entirely and take their own orders"),
     UsedImplicitly]
    public class FormationCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=BLTFormRespect}Respect Class"),
             LocCategory("General", "{=C5T5nnix}General"),
             LocDescription("{=BLTFormRespectDesc}On, a viewer may only move between formations of their own kind - infantry to infantry, archers to archers. Off, they may join any formation in the battle, so an archer can go and stand in the shield wall."),
             PropertyOrder(1), UsedImplicitly]
            public bool Filter { get; set; } = true;

            [LocDisplayName("{=BLTFormDetach}Allow Detachments"),
             LocCategory("General", "{=C5T5nnix}General"),
             LocDescription("{=BLTFormDetachDesc}Allows detach/attach and the personal orders that go with them (charge, hold, follow, gate, walls). Turn off to keep every viewer inside the battle line."),
             PropertyOrder(2), UsedImplicitly]
            public bool Detach { get; set; } = true;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.Value("<strong>Usage:</strong>");
                generator.Value("- (nothing) - list the formations and show where you are");
                generator.Value("- a number - move to that formation");
                generator.Value("- front / back - take a place in the front or the back rank");
                generator.Value("- detach / attach - leave the line, or rejoin it");
                generator.Value("- (while detached) charge (or engage) / hold / follow / gate / walls");
                generator.Value("- help - the short version of this list, in chat");
            }
        }

        public override Type HandlerConfigType => typeof(Settings);

        private static readonly string[] DetachKeywords =
            { "detach", "attach", "charge", "engage", "hold", "follow", "gate", "walls" };

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            if (Mission.Current == null)
            {
                onFailure("{=BLTFormNoMission}There is no battle happening".Translate());
                return;
            }
            if (Mission.Current.IsNavalBattle)
            {
                onFailure("{=BLTFormNaval}Formations cannot be changed in a naval battle".Translate());
                return;
            }
            if (MissionHelpers.InTournament())
            {
                onFailure("{=BLTFormTourney}Formations cannot be changed in a tournament".Translate());
                return;
            }

            // Args can be null as well as empty, and the old code split it before checking, so a
            // bare command could come in and take the whole handler down with it.
            string arg = (context.Args ?? string.Empty).Trim();
            string keyword = arg.Split(' ').FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;

            if (keyword == "help")
            {
                onSuccess(HelpText(settings));
                return;
            }

            var agent = adoptedHero.GetAgent();
            if (agent == null)
            {
                onFailure("{=BLTFormNoAgent}You are not in this battle".Translate());
                return;
            }

            var currentFormation = agent.Formation;
            if (currentFormation == null)
            {
                onFailure("{=BLTFormNoFormation}You are not in a formation".Translate());
                return;
            }

            if (DetachKeywords.Contains(keyword))
            {
                HandleDetachCommand(keyword, agent, settings, onSuccess, onFailure);
                return;
            }

            if (keyword == "front" || keyword == "back")
            {
                if (agent.IsDetachedFromFormation)
                {
                    onFailure("{=BLTFormReattachMove}Rejoin your formation before moving within it - try: attach".Translate());
                    return;
                }
                SetHeroFormationPosition(agent, keyword, onSuccess, onFailure);
                return;
            }

            // Everything else is either a formation number or a request to see the list.
            HandleFormationList(keyword, agent, currentFormation, settings, onSuccess, onFailure);
        }

        private static string HelpText(Settings settings)
        {
            var sb = new StringBuilder("Formation: (empty) list, <number> move, front/back place in rank");
            if (settings.Detach)
                sb.Append(", detach/attach leave or rejoin, then charge (or engage)/hold/follow/gate/walls");
            return sb.ToString();
        }

        private void HandleDetachCommand(string keyword, Agent agent, Settings settings,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (!settings.Detach)
            {
                onFailure("{=BLTFormDetachOff}Detaching is turned off in this battle".Translate());
                return;
            }

            var behavior = BLTHeroDetachmentBehavior.Current;
            if (behavior == null)
            {
                onFailure("{=BLTFormDetachDown}The detachment system is not running".Translate());
                return;
            }
            if (!Mission.Current.IsDeploymentFinished)
            {
                onFailure("{=BLTFormDeploying}Wait until the battle starts".Translate());
                return;
            }

            // The orders below only mean anything to someone who has already left the line, and
            // "nothing happened" is the least useful reply a viewer can get.
            if (keyword != "detach" && keyword != "attach" && !behavior.IsDetached(agent))
            {
                onFailure("{=BLTFormNotDetached}You are still in formation - use detach first".Translate());
                return;
            }

            string error = keyword switch
            {
                "detach" => behavior.Detach(agent),
                "attach" => behavior.Attach(agent),
                // engage reads more naturally than charge to a lot of people, and asking a
                // viewer to remember which of two words the mod happens to use is a poor trade.
                "charge" or "engage" => behavior.Charge(agent),
                "hold" => behavior.Hold(agent),
                "follow" => behavior.Follow(agent),
                "gate" => behavior.TargetDoor(agent),
                "walls" => behavior.Walls(agent),
                _ => "Unknown command",
            };

            if (error != null) onFailure(error);
            else onSuccess(ConfirmationFor(keyword));
        }

        private static string ConfirmationFor(string keyword) => keyword switch
        {
            "detach" => "You have left the formation and take your own orders now",
            "attach" => "You have rejoined your formation",
            "charge" or "engage" => "Charging the nearest enemy",
            "hold" => "Holding position",
            "follow" => "Following",
            "gate" => "Heading for the gate",
            "walls" => "Heading for the walls",
            _ => "Done",
        };

        /// <summary>
        /// Lists the formations, or moves the viewer into one of them by number.
        ///
        /// Filtering by class only changes which formations are on the list - the listing, the
        /// numbering and the move are otherwise identical, which is why this is one path now
        /// rather than the two near-identical copies it used to be: every fix had to be made
        /// twice, and eventually one of them would not have been.
        /// </summary>
        private void HandleFormationList(string keyword, Agent agent, Formation currentFormation,
            Settings settings, Action<string> onSuccess, Action<string> onFailure)
        {
            FormationClass ownClass = ClassOf(currentFormation);

            var formations = agent.Team.FormationsIncludingSpecialAndEmpty
                .Where(f => f.CountOfUnits > 0)
                .Where(f => !settings.Filter || f.PhysicalClass == ownClass)
                .OrderBy(f => f.Index)
                .ToList();

            if (formations.Count == 0)
            {
                onFailure("{=BLTFormNone}There are no formations you can join".Translate());
                return;
            }

            // No number given: show the list and stop.
            if (string.IsNullOrEmpty(keyword) || !int.TryParse(keyword, out int wanted))
            {
                onSuccess(DescribeFormations(formations, currentFormation, settings));
                return;
            }

            if (agent.IsDetachedFromFormation)
            {
                onFailure("{=BLTFormReattach}Rejoin your formation before changing formations - try: attach".Translate());
                return;
            }
            if (wanted < 1 || wanted > formations.Count)
            {
                onFailure($"Pick a number between 1 and {formations.Count}");
                return;
            }

            var target = formations[wanted - 1];
            if (target == currentFormation)
            {
                onFailure("You are already in that formation");
                return;
            }

            TransferHeroToFormation(agent, target);
            onSuccess($"Moved to formation {wanted} - {Describe(target, settings)}");
        }

        /// <summary>
        /// The list as a viewer reads it in chat: where they are, then each formation with the
        /// number they would type to join it. Deliberately spelled out rather than packed into
        /// punctuation - it is read once, quickly, in a scrolling chat window.
        /// </summary>
        private string DescribeFormations(List<Formation> formations, Formation current, Settings settings)
        {
            int position = formations.IndexOf(current) + 1;

            var sb = new StringBuilder();
            sb.Append(position > 0
                ? $"You are in formation {position} of {formations.Count} ({current.CountOfUnits} men). "
                : $"You are not in any of these {formations.Count} formation(s). ");

            for (int i = 0; i < formations.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append($"{i + 1}: {Describe(formations[i], settings)}");
            }

            return sb.ToString();
        }

        private string Describe(Formation f, Settings settings)
        {
            var sb = new StringBuilder();

            // The class is only worth saying when the list can hold more than one of them.
            if (!settings.Filter) sb.Append($"{NameOf(ClassOf(f))} ");

            sb.Append($"{f.CountOfUnits} men, {MovementName(f)}/{ArrangementName(f)}");

            if (f.TargetFormation != null)
            {
                float distance = (f.TargetFormation.CachedAveragePosition - f.CachedAveragePosition).Length;
                sb.Append($" vs {NameOf(ClassOf(f.TargetFormation))} {distance:0}m");
            }

            return sb.ToString();
        }

        private static FormationClass ClassOf(Formation f)
        {
            var q = f?.QuerySystem;
            return q switch
            {
                null => FormationClass.Infantry,
                _ when q.IsInfantryFormationReadOnly => FormationClass.Infantry,
                _ when q.IsRangedFormationReadOnly => FormationClass.Ranged,
                _ when q.IsCavalryFormationReadOnly => FormationClass.Cavalry,
                _ when q.IsRangedCavalryFormationReadOnly => FormationClass.HorseArcher,
                _ => FormationClass.Infantry,
            };
        }

        private static string NameOf(FormationClass c) => c switch
        {
            FormationClass.Infantry => "infantry",
            FormationClass.Ranged => "archers",
            FormationClass.Cavalry => "cavalry",
            FormationClass.HorseArcher => "horse archers",
            _ => "troops",
        };

        private void TransferHeroToFormation(Agent heroAgent, Formation target)
        {
            if (heroAgent == null || target == null) return;

            var oldFormation = heroAgent.Formation;
            heroAgent.Formation = target;

            oldFormation?.Team.TriggerOnFormationsChanged(oldFormation);
            target.Team.TriggerOnFormationsChanged(target);

            Log.Trace($"{heroAgent.Name} transferred to {target.FormationIndex.GetName()}");
        }

        private static string MovementName(Formation f) =>
            f.GetReadonlyMovementOrderReference().OrderEnum switch
            {
                MovementOrder.MovementOrderEnum.Charge => "charging",
                MovementOrder.MovementOrderEnum.ChargeToTarget => "charging",
                MovementOrder.MovementOrderEnum.Advance => "advancing",
                MovementOrder.MovementOrderEnum.FallBack => "falling back",
                MovementOrder.MovementOrderEnum.Retreat => "retreating",
                MovementOrder.MovementOrderEnum.Invalid => "holding",
                MovementOrder.MovementOrderEnum.Stop => "holding",
                MovementOrder.MovementOrderEnum.Follow => "following",
                MovementOrder.MovementOrderEnum.FollowEntity => "following",
                MovementOrder.MovementOrderEnum.Move => "moving",
                _ => "?",
            };

        private static string ArrangementName(Formation f) =>
            f.ArrangementOrder.OrderEnum switch
            {
                ArrangementOrder.ArrangementOrderEnum.Line => "line",
                ArrangementOrder.ArrangementOrderEnum.ShieldWall => "shield wall",
                ArrangementOrder.ArrangementOrderEnum.Loose => "loose",
                ArrangementOrder.ArrangementOrderEnum.Square => "square",
                ArrangementOrder.ArrangementOrderEnum.Circle => "circle",
                ArrangementOrder.ArrangementOrderEnum.Column => "column",
                ArrangementOrder.ArrangementOrderEnum.Scatter => "scattered",
                _ => "--",
            };

        /// <summary>
        /// Puts the viewer in the front or the back rank by swapping places with a rank-and-file
        /// soldier there. Other heroes are left alone - swapping two viewers would move someone
        /// who did not ask to be moved.
        /// </summary>
        private void SetHeroFormationPosition(Agent heroAgent, string position,
            Action<string> onSuccess, Action<string> onFailure)
        {
            var formation = heroAgent.Formation;
            if (formation == null) { onFailure("You are not in a formation"); return; }

            if (heroAgent is not IFormationUnit unit)
            {
                onFailure("You cannot be moved within this formation");
                return;
            }

            var arrangement = formation.Arrangement;
            bool toFront = position == "front";

            try
            {
                var candidates = arrangement.GetAllUnits()
                    .Select(u => u as Agent)
                    .Where(a => a != null && a != heroAgent && a.GetHero() == null);

                var ordered = toFront
                    ? candidates.OrderBy(a => ((IFormationUnit)a).FormationRankIndex)
                                .ThenBy(a => ((IFormationUnit)a).FormationFileIndex)
                    : candidates.OrderByDescending(a => ((IFormationUnit)a).FormationRankIndex)
                                .ThenBy(a => ((IFormationUnit)a).FormationFileIndex);

                var candidate = ordered.Take(Math.Max(1, (int)arrangement.Width)).SelectRandom();
                if (candidate == null)
                {
                    // Nothing to swap with: the formation is the viewer, or only heroes.
                    onFailure(toFront
                        ? "There is nobody in the front rank to swap with"
                        : "There is nobody in the back rank to swap with");
                    return;
                }

                arrangement.SwitchUnitLocations(candidate, unit);
                onSuccess(toFront ? "You push through to the front" : "You fall back through the ranks");
            }
            catch (Exception e)
            {
                onFailure($"This formation does not allow moving within it ({e.Message})");
            }
        }
    }
}
