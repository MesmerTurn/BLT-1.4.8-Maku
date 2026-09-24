using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    /// <summary>
    /// Looking after one hired companion: seeing what they are, naming them, handing them gear,
    /// and paying to train them. Modelled on GeneralEddy's system, used with his permission.
    /// </summary>
    [LocDisplayName("{=TESTING}CompanionCommand"),
     LocDescription("{=TESTING}Manage one hired companion. Usage: !companion (name), !companion (name) rename (new name), !companion (name) giveitem (number), !companion (name) takeitem (number), !companion (name) train (gold)"),
     UsedImplicitly]
    public class CompanionCommand : HeroCommandHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Rename Cost"),
             PropertyOrder(1), UsedImplicitly]
            public int RenameCost { get; set; } = 0;

            [LocDisplayName("{=}Focus Point Costs"),
             LocDescription("{=}Gold for a companion's 1st, 2nd, 3rd... focus point in one skill, comma separated. The last number is reused if they buy more than you list."),
             PropertyOrder(2), UsedImplicitly]
            public string FocusCosts { get; set; } = "40000, 55000, 70000, 85000, 100000";

            [LocDisplayName("{=}Attribute Cost"),
             LocDescription("{=}Gold for one point of a governing attribute, bought once focus points are maxed."),
             PropertyOrder(3), UsedImplicitly]
            public int AttributeCost { get; set; } = 250000;

            [LocDisplayName("{=}Maximum Focus Per Skill"),
             Range(1, 5), PropertyOrder(4), UsedImplicitly]
            public int MaxFocus { get; set; } = 5;

            [LocDisplayName("{=}Noble Title Cost"),
             LocDescription("{=}Gold to make a companion a noble of the viewer's clan, which is what lets them lead a party of their own. 0 disables the title."),
             PropertyOrder(5), UsedImplicitly]
            public int NobleCost { get; set; } = 1000000;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.Value("<strong>Usage:</strong>");
                generator.Value("- !companion (name) - what they are, how close to the next tier");
                generator.Value("- !companion (name) rename (new name)");
                generator.Value("- !companion (name) giveitem (number) - hand over one of your custom items");
                generator.Value("- !companion (name) takeitem (number) - take it back");
                generator.Value("- !companion (name) train (gold) - buy focus points, then attributes");
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

            var campaign = BLTAdoptAHeroCampaignBehavior.Current;
            var words = (context.Args ?? "").Trim().Split(' ').Where(w => w.Length > 0).ToList();

            if (words.Count == 0)
            {
                onFailure("{=}Usage: !companion (name) [rename|giveitem|takeitem|train] - !companions lists yours".Translate());
                return;
            }

            var companion = Find(adoptedHero, words[0], campaign);
            if (companion == null)
            {
                onFailure("{=}You have no companion called '{Name}'".Translate(("Name", words[0])));
                return;
            }

            string action = words.Count > 1 ? words[1].ToLowerInvariant() : "";
            string rest = string.Join(" ", words.Skip(2));

            switch (action)
            {
                case "":
                case "info":
                    onSuccess(Info(companion, campaign));
                    return;

                case "rename":
                    Rename(adoptedHero, companion, rest, settings, campaign, onSuccess, onFailure);
                    return;

                case "giveitem":
                    GiveItem(adoptedHero, companion, rest, campaign, onSuccess, onFailure);
                    return;

                case "takeitem":
                    TakeItem(adoptedHero, companion, rest, campaign, onSuccess, onFailure);
                    return;

                case "train":
                    Train(adoptedHero, companion, rest, settings, campaign, onSuccess, onFailure);
                    return;

                case "slots":
                    onSuccess($"{companion.FirstName}: {SlotList(companion)}");
                    return;

                case "noble":
                    Ennoble(adoptedHero, companion, settings, campaign, onSuccess, onFailure);
                    return;

                default:
                    onFailure("{=}Don't know '{Action}'. Try rename, giveitem, takeitem or train"
                        .Translate(("Action", action)));
                    return;
            }
        }

        /// <summary>
        /// The fixed 1-11 slot numbers, the same ones GeneralEddy's build uses, so viewers who
        /// know one know the other: 1-4 weapons, 5 head, 6 body, 7 legs, 8 gloves, 9 cape,
        /// 10 horse, 11 saddle. They never shift, whatever is or is not equipped.
        /// </summary>
        private static readonly EquipmentIndex[] SlotNumbers =
        {
            EquipmentIndex.Weapon0, EquipmentIndex.Weapon1, EquipmentIndex.Weapon2,
            EquipmentIndex.Weapon3, EquipmentIndex.Head, EquipmentIndex.Body, EquipmentIndex.Leg,
            EquipmentIndex.Gloves, EquipmentIndex.Cape, EquipmentIndex.Horse,
            EquipmentIndex.HorseHarness,
        };

        private static bool TryParseSlot(string text, out EquipmentIndex index)
        {
            index = EquipmentIndex.None;
            text = (text ?? "").Trim().ToLowerInvariant();
            if (text.StartsWith("slot")) text = text.Substring(4).Trim();

            if (!int.TryParse(text, out int number)) return false;
            if (number < 1 || number > SlotNumbers.Length) return false;

            index = SlotNumbers[number - 1];
            return true;
        }

        private static string SlotList(Hero companion)
        {
            var names = new[] { "1 wpn", "2 wpn", "3 wpn", "4 wpn", "5 head", "6 body", "7 legs",
                "8 gloves", "9 cape", "10 horse", "11 saddle" };

            return string.Join(", ", SlotNumbers.Select((slot, i) =>
            {
                var element = companion.BattleEquipment[slot];
                return $"{names[i]}: {(element.IsEmpty ? "-" : element.Item.Name.ToString())}";
            }));
        }

        private static Hero Find(Hero owner, string name, BLTAdoptAHeroCampaignBehavior campaign)
        {
            var companions = campaign.GetHiredCompanions(owner).ToList();

            return companions.FirstOrDefault(c =>
                       c.FirstName?.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                   ?? companions.FirstOrDefault(c =>
                       c.Name?.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Info(Hero companion, BLTAdoptAHeroCampaignBehavior campaign)
        {
            var classDef = campaign.GetClass(companion);
            int tier = campaign.GetEquipmentTier(companion) + 1;

            string skills = classDef == null
                ? ""
                : ", " + string.Join(" ", classDef.Skills
                    .Distinct()
                    .Select(s => $"{SkillXP.GetShortSkillName(s)} {companion.GetSkillValue(s)}"));

            return $"{companion.FirstName}: {classDef?.Name.ToString() ?? "no class"} T{tier}, "
                   + $"lvl {companion.Level}, {BLTHiredCompanionBehavior.ProgressText(companion)}{skills}";
        }

        private static void Rename(Hero owner, Hero companion, string newName, Settings settings,
            BLTAdoptAHeroCampaignBehavior campaign, Action<string> onSuccess, Action<string> onFailure)
        {
            string clean = Naming.SanitizeUserProvidedName(newName);
            if (string.IsNullOrWhiteSpace(clean))
            {
                onFailure("{=}Give a new name".Translate());
                return;
            }

            if (settings.RenameCost > 0)
            {
                int gold = campaign.GetHeroGold(owner);
                if (gold < settings.RenameCost)
                {
                    onFailure(Naming.NotEnoughGold(settings.RenameCost, gold));
                    return;
                }
                campaign.ChangeHeroGold(owner, -settings.RenameCost);
            }

            string oldName = companion.FirstName.ToString();
            companion.SetName(new TaleWorlds.Localization.TextObject(clean),
                new TaleWorlds.Localization.TextObject(clean));

            onSuccess("{=}{Old} is now called {New}".Translate(("Old", oldName), ("New", clean)));
        }

        /// <summary>
        /// Hands one of the viewer's custom items to a companion.
        ///
        /// The rule that matters is the weapon one, taken from Eddy's: a companion climbs on the
        /// skills their weapon trains, so swapping that weapon for something that trains a
        /// different skill would quietly stall them forever. A replacement weapon has to train the
        /// same skill and be worth at least as much; anything else is refused with a reason.
        /// Armour and mounts are always fine.
        /// </summary>
        private static void GiveItem(Hero owner, Hero companion, string indexStr,
            BLTAdoptAHeroCampaignBehavior campaign, Action<string> onSuccess, Action<string> onFailure)
        {
            // "giveitem 3 slot7" - the slot is optional, and only the item number is required.
            var words = indexStr.Trim().Split(' ').Where(w => w.Length > 0).ToList();
            EquipmentIndex wantedSlot = EquipmentIndex.None;
            if (words.Count > 1 && TryParseSlot(words[1], out var parsed)) wantedSlot = parsed;

            var (item, error) = campaign.FindCustomItemByIndex(owner, words.FirstOrDefault() ?? "");
            if (error != null || item.IsEmpty)
            {
                onFailure(error ?? "{=}No such item - check !customitems".Translate());
                return;
            }

            var slots = RewardHelpers.GetValidSlotsForItemType(item.Item);
            if (!slots.Any())
            {
                onFailure("{=}{Item} cannot be worn or wielded by anyone"
                    .Translate(("Item", item.Item.Name.ToString())));
                return;
            }

            if (wantedSlot != EquipmentIndex.None && !slots.Contains(wantedSlot))
            {
                onFailure("{=}{Item} does not go in that slot".Translate(("Item", item.Item.Name.ToString())));
                return;
            }

            // The slot they asked for, or an empty valid one, where there is nothing to argue
            // about. Note FirstOrDefault would hand back Weapon0 when nothing is empty - and
            // Weapon0 is a real slot, so a helmet would have gone into a weapon hand. Hence the
            // explicit "is there one" test rather than trusting the default.
            EquipmentIndex target;
            if (wantedSlot != EquipmentIndex.None)
            {
                target = wantedSlot;
            }
            else
            {
                var empty = slots.Where(s => companion.BattleEquipment[s].IsEmpty).ToList();
                target = empty.Count > 0 ? empty[0] : slots.First();
            }

            {
                var current = companion.BattleEquipment[target];

                if (!current.IsEmpty && IsWeapon(current.Item))
                {
                    if (!TrainsSameSkill(current.Item, item.Item))
                    {
                        onFailure("{=}{Name} levels on {Skill}, and {Item} does not train it - they keep what they have"
                            .Translate(("Name", companion.FirstName.ToString()),
                                ("Skill", current.Item.PrimaryWeapon?.RelevantSkill?.Name.ToString() ?? "that skill"),
                                ("Item", item.Item.Name.ToString())));
                        return;
                    }

                    if (item.Item.Tier < current.Item.Tier)
                    {
                        onFailure("{=}{Item} is worse than what {Name} carries"
                            .Translate(("Item", item.Item.Name.ToString()),
                                ("Name", companion.FirstName.ToString())));
                        return;
                    }
                }
            }

            companion.BattleEquipment[target] = item;
            campaign.RemoveCustomItem(owner, item);

            onSuccess("{=}{Name} takes {Item}"
                .Translate(("Name", companion.FirstName.ToString()),
                    ("Item", RewardHelpers.GetItemNameAndModifiers(item))));
        }

        private static void TakeItem(Hero owner, Hero companion, string nameOrIndex,
            BLTAdoptAHeroCampaignBehavior campaign, Action<string> onSuccess, Action<string> onFailure)
        {
            string search = nameOrIndex.Trim();
            if (string.IsNullOrEmpty(search))
            {
                onFailure("{=}Name the item, or the slot number - !companion {Name} slots shows them"
                    .Translate(("Name", companion.FirstName.ToString())));
                return;
            }

            // A slot number is the precise way to do it; the name match below is the convenient one.
            if (TryParseSlot(search, out var slotIndex))
            {
                var inSlot = companion.BattleEquipment[slotIndex];
                if (inSlot.IsEmpty)
                {
                    onFailure("{=}That slot is empty".Translate());
                    return;
                }

                companion.BattleEquipment[slotIndex] = EquipmentElement.Invalid;
                campaign.AddCustomItem(owner, inSlot);

                onSuccess("{=}{Name} hands back {Item}"
                    .Translate(("Name", companion.FirstName.ToString()),
                        ("Item", RewardHelpers.GetItemNameAndModifiers(inSlot))));
                return;
            }

            foreach (var (element, index) in companion.BattleEquipment.YieldFilledEquipmentSlots().ToList())
            {
                string itemName = element.Item?.Name?.ToString();
                if (itemName == null
                    || itemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                companion.BattleEquipment[index] = EquipmentElement.Invalid;
                campaign.AddCustomItem(owner, element);

                onSuccess("{=}{Name} hands back {Item}"
                    .Translate(("Name", companion.FirstName.ToString()),
                        ("Item", RewardHelpers.GetItemNameAndModifiers(element))));
                return;
            }

            onFailure("{=}{Name} is not carrying anything called '{Item}'"
                .Translate(("Name", companion.FirstName.ToString()), ("Item", search)));
        }

        /// <summary>
        /// Spends gold on a companion: focus points in their own class skills first, cheapest
        /// first, then governing attributes once every skill is capped. Stops when the budget runs
        /// out, and only charges for what it actually bought.
        /// </summary>
        private static void Train(Hero owner, Hero companion, string amountStr, Settings settings,
            BLTAdoptAHeroCampaignBehavior campaign, Action<string> onSuccess, Action<string> onFailure)
        {
            if (!TryParseGold(amountStr, out int budget) || budget <= 0)
            {
                onFailure("{=}How much gold? e.g. !companion {Name} train 500k"
                    .Translate(("Name", companion.FirstName.ToString())));
                return;
            }

            int available = campaign.GetHeroGold(owner);
            if (available < budget) budget = available;
            if (budget <= 0)
            {
                onFailure(Naming.NotEnoughGold(1, available));
                return;
            }

            var classDef = campaign.GetClass(companion);
            if (classDef == null)
            {
                onFailure("{=}{Name} has no class to train".Translate(("Name", companion.FirstName.ToString())));
                return;
            }

            var costs = settings.FocusCosts
                .Split(',')
                .Select(s => int.TryParse(s.Trim(), out int v) ? v : 0)
                .Where(v => v > 0)
                .ToList();
            if (costs.Count == 0) costs.Add(40000);

            int spent = 0, focusBought = 0, attributesBought = 0;
            var skills = classDef.Skills.Distinct().ToList();

            // Focus points, always the cheapest next one anywhere in their class skills.
            while (true)
            {
                var next = skills
                    .Where(s => companion.HeroDeveloper.GetFocus(s) < settings.MaxFocus)
                    .Select(s => (skill: s, cost: costs[Math.Min(companion.HeroDeveloper.GetFocus(s), costs.Count - 1)]))
                    .OrderBy(x => x.cost)
                    .FirstOrDefault();

                if (next.skill == null || spent + next.cost > budget) break;

                companion.HeroDeveloper.AddFocus(next.skill, 1, checkUnspentFocusPoints: false);
                spent += next.cost;
                focusBought++;
            }

            // Then attributes, but only the ones those skills actually run off.
            var attributes = skills
                .SelectMany(s => s.Attributes ?? new CharacterAttribute[0])
                .Where(a => a != null)
                .Distinct()
                .ToList();

            while (settings.AttributeCost > 0)
            {
                var next = attributes
                    .Where(a => companion.GetAttributeValue(a) < 10)
                    .OrderBy(companion.GetAttributeValue)
                    .FirstOrDefault();

                if (next == null || spent + settings.AttributeCost > budget) break;

                companion.HeroDeveloper.AddAttribute(next, 1, checkUnspentPoints: false);
                spent += settings.AttributeCost;
                attributesBought++;
            }

            if (spent == 0)
            {
                onFailure("{=}{Name} could not be trained any further with that much gold"
                    .Translate(("Name", companion.FirstName.ToString())));
                return;
            }

            companion.HeroDeveloper.DevelopCharacterStats();
            campaign.ChangeHeroGold(owner, -spent);

            onSuccess("{=}{Name}: +{Focus} focus, +{Attributes} attributes for {Gold}{GoldIcon}"
                .Translate(("Name", companion.FirstName.ToString()),
                    ("Focus", focusBought), ("Attributes", attributesBought),
                    ("Gold", spent), ("GoldIcon", Naming.Gold)));
        }

        /// <summary>
        /// Makes a companion a noble of the viewer's clan rather than a wanderer in it, which is
        /// what the game requires before anyone will let them lead a party.
        ///
        /// Deliberately one way, as in Eddy's: it is a large sum for a permanent change in what
        /// the companion is, and an undo would turn it into a rental.
        /// </summary>
        private static void Ennoble(Hero owner, Hero companion, Settings settings,
            BLTAdoptAHeroCampaignBehavior campaign, Action<string> onSuccess, Action<string> onFailure)
        {
            if (settings.NobleCost <= 0)
            {
                onFailure("{=}Titles are disabled".Translate());
                return;
            }
            if (companion.Occupation == Occupation.Lord)
            {
                onFailure("{=}{Name} is already a noble".Translate(("Name", companion.FirstName.ToString())));
                return;
            }

            var clan = owner.Clan;
            if (clan == null)
            {
                onFailure("{=}You need a clan of your own first".Translate());
                return;
            }

            int gold = campaign.GetHeroGold(owner);
            if (gold < settings.NobleCost)
            {
                onFailure(Naming.NotEnoughGold(settings.NobleCost, gold));
                return;
            }

            try
            {
                companion.SetNewOccupation(Occupation.Lord);
                companion.CompanionOf = null;
                companion.Clan = clan;

                campaign.ChangeHeroGold(owner, -settings.NobleCost);

                Log.LogFeedEvent("{=}{Name} is raised to the nobility of {Clan}"
                    .Translate(("Name", companion.Name.ToString()), ("Clan", clan.Name.ToString())));

                onSuccess("{=}{Name} is now a noble of your clan and can lead a party"
                    .Translate(("Name", companion.FirstName.ToString())));
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(CompanionCommand)}.{nameof(Ennoble)}", ex);
                onFailure("{=}That did not work - nothing was charged".Translate());
            }
        }

        private static bool IsWeapon(ItemObject item)
            => item?.PrimaryWeapon != null && item.Type != ItemObject.ItemTypeEnum.Shield;

        private static bool TrainsSameSkill(ItemObject current, ItemObject replacement)
            => current?.PrimaryWeapon?.RelevantSkill != null
               && replacement?.PrimaryWeapon?.RelevantSkill == current.PrimaryWeapon.RelevantSkill;

        /// <summary>
        /// Accepts 500000, 500k and 1m, because nobody types six zeroes into chat mid-battle.
        /// </summary>
        private static bool TryParseGold(string text, out int gold)
        {
            gold = 0;
            text = (text ?? "").Trim().ToLowerInvariant();
            if (text.Length == 0) return false;

            float multiplier = 1f;
            if (text.EndsWith("k")) { multiplier = 1000f; text = text.Substring(0, text.Length - 1); }
            else if (text.EndsWith("m")) { multiplier = 1000000f; text = text.Substring(0, text.Length - 1); }

            if (!float.TryParse(text, out float value)) return false;

            gold = (int)Math.Min(int.MaxValue, value * multiplier);
            return gold > 0;
        }
    }
}
