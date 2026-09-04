using System;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using BannerlordTwitch.Rewards;
using BLTAdoptAHero.Behaviors;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero
{
    [LocDisplayName("{=bounty001}Bounty"),
     LocDescription("{=bounty002}Place a gold bounty on an enemy lord - !bounty <lord name> <amount>"),
     UsedImplicitly]
    public class Bounty : ActionHandlerBase
    {
        private class Settings : IDocumentable
        {
            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
            }
        }

        protected override Type ConfigType => typeof(Settings);

        private static Hero FindLord(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Hero.AllAliveHeroes
                .Where(h => h != null && h.IsLord)
                .FirstOrDefault(h => h.Name?.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        protected override void ExecuteInternal(ReplyContext context, object config, Action<string> onSuccess, Action<string> onFailure)
        {
            var adoptedHero = BLTAdoptAHeroCampaignBehavior.Current.GetAdoptedHero(context.UserName);
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            if (string.IsNullOrWhiteSpace(context.Args))
            {
                onFailure(context.ArgsErrorMessage("{=bounty003}(lord name) (amount)".Translate()));
                return;
            }

            var args = context.Args.Trim().Split(' ');
            if (args.Length < 2 || !int.TryParse(args[args.Length - 1], out int amount))
            {
                onFailure(context.ArgsErrorMessage("{=bounty003}(lord name) (amount)".Translate()));
                return;
            }

            string lordName = string.Join(" ", args.Take(args.Length - 1));
            var target = FindLord(lordName);
            if (target == null)
            {
                onFailure($"Couldn't find a lord matching '{lordName}'.");
                return;
            }

            var (success, status) = BLTBountyBehavior.Current.PlaceBounty(adoptedHero, target, amount);
            if (success) onSuccess(status); else onFailure(status);
        }
    }

    [LocDisplayName("{=bounty004}Bounties"),
     LocDescription("{=bounty005}Show the top active bounties"),
     UsedImplicitly]
    public class Bounties : ActionHandlerBase
    {
        private class Settings : IDocumentable
        {
            [LocDisplayName("{=bounty006}Max Shown"),
             LocDescription("{=bounty007}Maximum number of bounties to list"),
             PropertyOrder(1), UsedImplicitly]
            public int MaxShown { get; set; } = 3;

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.PropertyValuePair("Max Shown", MaxShown.ToString());
            }
        }

        protected override Type ConfigType => typeof(Settings);

        protected override void ExecuteInternal(ReplyContext context, object config, Action<string> onSuccess, Action<string> onFailure)
        {
            var settings = (Settings)config;
            var top = BLTBountyBehavior.Current.GetTopBounties(settings.MaxShown).ToList();
            if (top.Count == 0)
            {
                onSuccess("No active bounties.");
                return;
            }
            onSuccess("Active bounties: " + string.Join(" | ", top.Select(b => $"{b.TargetName}: {b.TotalGold} gold")));
        }
    }
}
