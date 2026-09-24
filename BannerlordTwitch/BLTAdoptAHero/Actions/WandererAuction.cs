using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero.Actions
{
    /// <summary>
    /// Wanderer auctions: the streamer puts a companion up, viewers bid, the winner gets them.
    ///
    /// The second way of getting a companion, alongside !buycompanion, from GeneralEddy's system
    /// (used with his permission). Buying is a private transaction; an auction is an event on
    /// stream, which is the entire point of it.
    ///
    /// Kept separate from BLT's existing item auction rather than folded into it: an item auction
    /// moves something that already exists between two viewers, while this one creates a hero and
    /// only charges the winner. Sharing the plumbing would mean one set of rules pretending to fit
    /// two different transactions.
    /// </summary>
    public static class WandererAuctionState
    {
        private class Bid
        {
            public Hero Hero;
            public int Amount;
        }

        private static readonly List<Bid> bids = new();

        public static bool InProgress { get; private set; }
        public static HeroClassDef ClassDef { get; private set; }
        public static int ReservePrice { get; private set; }

        public static void Open(HeroClassDef classDef, int reservePrice)
        {
            bids.Clear();
            ClassDef = classDef;
            ReservePrice = reservePrice;
            InProgress = true;
        }

        public static void Close() => InProgress = false;

        public static (bool accepted, string message) PlaceBid(Hero hero, int amount)
        {
            if (!InProgress)
                return (false, "{=}No wanderer is up for auction".Translate());

            if (amount < ReservePrice)
                return (false, "{=}The reserve is {Reserve}{GoldIcon}"
                    .Translate(("Reserve", ReservePrice), ("GoldIcon", Naming.Gold)));

            // Bid against what they can actually pay, not what they can type. Checked again when
            // the auction closes, because a viewer can spend their gold in the meantime.
            int gold = BLTAdoptAHeroCampaignBehavior.Current.GetHeroGold(hero);
            if (gold < amount)
                return (false, Naming.NotEnoughGold(amount, gold));

            var highest = HighestBid();
            if (highest != null && amount <= highest.Amount)
                return (false, "{=}The bid is already {Amount}{GoldIcon}"
                    .Translate(("Amount", highest.Amount), ("GoldIcon", Naming.Gold)));

            bids.RemoveAll(b => b.Hero == hero);
            bids.Add(new Bid { Hero = hero, Amount = amount });

            return (true, "{=}{Name} bids {Amount}{GoldIcon}"
                .Translate(("Name", hero.FirstName.ToString()), ("Amount", amount),
                    ("GoldIcon", Naming.Gold)));
        }

        private static Bid HighestBid()
            => bids.OrderByDescending(b => b.Amount).FirstOrDefault();

        /// <summary>
        /// The winner: the highest bidder who is still alive and can still afford what they bid.
        /// Anyone who has spent their gold since bidding simply loses their place to the next.
        /// </summary>
        public static (Hero hero, int amount) Resolve()
        {
            foreach (var bid in bids.OrderByDescending(b => b.Amount))
            {
                if (bid.Hero == null || bid.Hero.IsDead) continue;
                if (BLTAdoptAHeroCampaignBehavior.Current.GetHeroGold(bid.Hero) < bid.Amount) continue;

                return (bid.Hero, bid.Amount);
            }

            return (null, 0);
        }
    }

    [LocDisplayName("{=TESTING}WandererAuctionCommand"),
     LocDescription("{=TESTING}Streamer only: put a wanderer up for auction. Viewers bid with !bidwanderer. Usage: !wanderer, or !wanderer (class) (reserve price)"),
     UsedImplicitly]
    // Deliberately NOT a HeroCommandHandlerBase: that base refuses anyone without an adopted hero
    // of their own, and this is the streamer's command. A streamer who has not adopted a hero
    // could never have started an auction.
    public class WandererAuctionCommand : ActionHandlerBase
    {
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=}Auction Duration (seconds)"), PropertyOrder(1), UsedImplicitly]
            public int DurationSeconds { get; set; } = 60;

            [LocDisplayName("{=}Reminder Interval (seconds)"), PropertyOrder(2), UsedImplicitly]
            public int ReminderSeconds { get; set; } = 20;

            [LocDisplayName("{=}Default Reserve Price"),
             LocDescription("{=}Lowest bid accepted when the streamer does not name one."),
             PropertyOrder(3), UsedImplicitly]
            public int DefaultReserve { get; set; } = 100000;

            [LocDisplayName("{=}Starting Equipment Tier"),
             PropertyOrder(4), UsedImplicitly]
            public int StartingTier { get; set; } = 1;

            public void GenerateDocumentation(IDocumentationGenerator generator)
                => generator.P($"Puts a wanderer up for {DurationSeconds}s. Viewers bid with !bidwanderer (gold). The winner pays what they bid.");
        }

        protected override Type ConfigType => typeof(Settings);

        protected override void ExecuteInternal(ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;

            if (WandererAuctionState.InProgress)
            {
                onFailure("{=}An auction is already running".Translate());
                return;
            }
            if (Mission.Current != null)
            {
                onFailure("{=}Not during a battle".Translate());
                return;
            }

            var words = (context.Args ?? "").Trim().Split(' ').Where(w => w.Length > 0).ToList();

            // Trailing number is the reserve, everything before it is the class name.
            int reserve = settings.DefaultReserve;
            if (words.Count > 0 && int.TryParse(words.Last(), out int parsed) && parsed >= 0)
            {
                reserve = parsed;
                words.RemoveAt(words.Count - 1);
            }

            var classDef = BuyCompanionCommand.ResolveClassPublic(string.Join(" ", words));
            if (classDef == null)
            {
                onFailure("{=}No such class, and no classes to pick from".Translate());
                return;
            }

            RunAuction(context, classDef, reserve, settings);
        }

        private static async void RunAuction(ReplyContext context, HeroClassDef classDef,
            int reserve, Settings settings)
        {
            WandererAuctionState.Open(classDef, reserve);

            ActionManager.SendNonReply(context,
                "{=}A {Class} is up for auction! Bid with !bidwanderer (gold). Reserve {Reserve}{GoldIcon}, {Seconds}s"
                    .Translate(("Class", classDef.Name.ToString()), ("Reserve", reserve),
                        ("GoldIcon", Naming.Gold), ("Seconds", settings.DurationSeconds)));

            int remaining = settings.DurationSeconds;
            int interval = Math.Max(5, settings.ReminderSeconds);

            while (remaining > interval)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval));
                remaining -= interval;
                int seconds = remaining;

                MainThreadSync.Run(() =>
                {
                    var (hero, amount) = WandererAuctionState.Resolve();
                    ActionManager.SendNonReply(context, hero == null
                        ? "{=}{Seconds}s left on the {Class}, no bids yet"
                            .Translate(("Seconds", seconds), ("Class", classDef.Name.ToString()))
                        : "{=}{Seconds}s left on the {Class}, high bid {Amount}{GoldIcon} (@{Name})"
                            .Translate(("Seconds", seconds), ("Class", classDef.Name.ToString()),
                                ("Amount", amount), ("GoldIcon", Naming.Gold),
                                ("Name", hero.FirstName.ToString())));
                });
            }

            await Task.Delay(TimeSpan.FromSeconds(remaining));

            MainThreadSync.Run(() =>
            {
                try
                {
                    var (winner, amount) = WandererAuctionState.Resolve();
                    WandererAuctionState.Close();

                    if (winner == null)
                    {
                        ActionManager.SendNonReply(context,
                            "{=}The {Class} went unsold - no bid met the reserve"
                                .Translate(("Class", classDef.Name.ToString())));
                        return;
                    }

                    // Charge before the hero exists, and put the gold back if creating them fails.
                    // The other way round, a failure after the charge would leave a viewer paying
                    // for nothing.
                    var companion = BuyCompanionCommand.CreateCompanionPublic(
                        winner, winner.Clan, classDef, settings.StartingTier);

                    if (companion == null)
                    {
                        ActionManager.SendNonReply(context,
                            "{=}The {Class} could not be brought in - nobody is charged"
                                .Translate(("Class", classDef.Name.ToString())));
                        return;
                    }

                    BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(winner, -amount);

                    ActionManager.SendNonReply(context,
                        "{=}@{Name} wins the {Class} for {Amount}{GoldIcon} - {Companion} joins them"
                            .Translate(("Name", winner.FirstName.ToString()),
                                ("Class", classDef.Name.ToString()), ("Amount", amount),
                                ("GoldIcon", Naming.Gold),
                                ("Companion", companion.FirstName.ToString())));
                }
                catch (Exception ex)
                {
                    WandererAuctionState.Close();
                    Log.Exception($"{nameof(WandererAuctionCommand)}", ex);
                }
            });
        }
    }

    [LocDisplayName("{=TESTING}BidWandererCommand"),
     LocDescription("{=TESTING}Bid on the wanderer currently up for auction. Usage: !bidwanderer (gold)"),
     UsedImplicitly]
    public class BidWandererCommand : HeroCommandHandlerBase
    {
        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            string text = (context.Args ?? "").Trim().ToLowerInvariant();
            float multiplier = 1f;
            if (text.EndsWith("k")) { multiplier = 1000f; text = text.Substring(0, text.Length - 1); }
            else if (text.EndsWith("m")) { multiplier = 1000000f; text = text.Substring(0, text.Length - 1); }

            if (!float.TryParse(text, out float value) || value <= 0)
            {
                onFailure("{=}How much? e.g. !bidwanderer 150k".Translate());
                return;
            }

            var (accepted, message) = WandererAuctionState.PlaceBid(adoptedHero,
                (int)Math.Min(int.MaxValue, value * multiplier));

            if (accepted) onSuccess(message);
            else onFailure(message);
        }
    }
}
