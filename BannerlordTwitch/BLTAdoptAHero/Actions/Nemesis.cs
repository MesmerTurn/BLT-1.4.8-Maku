using System;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BLTAdoptAHero.Behaviors;
using JetBrains.Annotations;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero
{
    [LocDisplayName("{=nemesis001}Nemesis"),
     LocDescription("{=nemesis002}Show your adopted hero's current rivals - enemy lords who have fought them"),
     UsedImplicitly]
    public class Nemesis : ActionHandlerBase
    {
        private class Settings : IDocumentable
        {
            [LocDisplayName("{=nemesis003}Max Shown"),
             LocDescription("{=nemesis004}Maximum number of rivals to list"),
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
            var adoptedHero = BLTAdoptAHeroCampaignBehavior.Current.GetAdoptedHero(context.UserName);

            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            var rivals = BLTNemesisBehavior.Current?.GetNemeses(adoptedHero).Take(settings.MaxShown).ToList();

            if (rivals == null || rivals.Count == 0)
            {
                onSuccess($"{adoptedHero.FirstName} has no active rivals right now.");
                return;
            }

            string list = string.Join(" | ", rivals.Select(r =>
                $"{r.EnemyName} (beat you {r.TimesDefeatedYou}x, you beat them {r.TimesYouDefeated}x)"));

            onSuccess($"{adoptedHero.FirstName}'s rivals: {list}");
        }
    }
}
