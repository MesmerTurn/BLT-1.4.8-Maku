using System;
using System.Linq;
using BannerlordTwitch.Util;
using BLTAdoptAHero.Behaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Requested by Maku ("The Beast"): a nameless soldier who kills an adopted hero gets a chance
    /// to be promoted into a real lord of their own clan, outside any kingdom, and is registered as
    /// a nemesis of the hero they killed.
    ///
    /// The nemesis system deliberately ignores rank-and-file troops, because a regular soldier has
    /// no Hero object attached at all - there is nothing to track a rivalry against. Rather than
    /// loosening that rule, this promotes the killer into an actual Hero first, so everything
    /// downstream (nemesis records, the encyclopedia, party spawning) works on a real character
    /// with no special-casing.
    ///
    /// Clan setup mirrors what !clan create already does, which is known to work in this build.
    /// </summary>
    public static class BLTTroopAscension
    {
        /// <summary>
        /// Called when an adopted hero is killed. Does nothing unless the killer was a nameless
        /// troop and the configured chance rolls through.
        /// </summary>
        public static void OnAdoptedHeroKilled(Hero victim, Agent killerAgent)
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg?.TroopAscensionEnabled != true) return;
            if (victim == null || killerAgent == null) return;

            var killerCharacter = killerAgent.Character as CharacterObject;
            if (killerCharacter == null) return;

            // Only nameless troops: anything that already has a Hero is a lord, a companion or
            // another adopted hero, and is handled by the ordinary nemesis path.
            if (killerCharacter.HeroObject != null) return;
            if (!killerAgent.IsHuman) return;

            if (MBRandom.RandomFloat * 100f >= cfg.TroopAscensionChancePercent) return;

            SafeCall(() => Promote(victim, killerCharacter, cfg));
        }

        private static void Promote(Hero victim, CharacterObject killerCharacter, GlobalCommonConfig cfg)
        {
            var newLord = HeroCreator.CreateSpecialHero(killerCharacter);
            if (newLord == null)
            {
                Log.Trace($"[TroopAscension] Could not create a hero from {killerCharacter.Name}.");
                return;
            }

            newLord.ChangeState(Hero.CharacterStates.Active);

            MakeLordOfNewClan(newLord, cfg.TroopAscensionRenown, cfg.TroopAscensionStartingGold);

            // Register the rivalry the promotion came from: this lord exists because they killed
            // this hero, so the very first nemesis record should say so.
            BLTNemesisBehavior.Current?.RecordDefeat(victim, newLord);

            Log.LogFeedEvent("{=}{TROOP} slew {VICTIM} and has risen as a lord of their own clan!"
                .Translate(("TROOP", newLord.Name.ToString()), ("VICTIM", victim.Name.ToString())));
        }

        /// <summary>
        /// Turns an existing hero into the leader of a brand new clan with no kingdom. Shared by
        /// the kill-promotion above and the !promote command, so both produce the same kind of
        /// lord. Mirrors the clan setup in ClanManagement, which is proven to work in this build.
        /// </summary>
        public static void MakeLordOfNewClan(Hero hero, int renown, int startingGold)
        {
            string clanName = "{=}Clan of {NAME}".Translate(("NAME", hero.Name.ToString()));
            var clan = Clan.CreateClan(clanName);
            clan.ChangeClanName(new TextObject(clanName), new TextObject(clanName));
            clan.Culture = hero.Culture ?? hero.CharacterObject?.Culture;
            clan.Banner = Banner.CreateRandomBanner();
            clan.Kingdom = null;
            clan.AddRenown(renown, false);
            clan.SetInitialHomeSettlement(
                Settlement.All.Where(s => s.Culture == clan.Culture).SelectRandom()
                ?? Settlement.All.SelectRandom());

            hero.Clan = clan;
            hero.SetNewOccupation(Occupation.Lord);
            clan.SetLeader(hero);
            clan.IsNoble = true;
            CampaignEventDispatcher.Instance.OnClanCreated(clan, false);

            if (startingGold > 0) hero.ChangeHeroGold(startingGold);
        }

        private static void SafeCall(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // A failed promotion must never cost the kill its normal handling.
                Log.Exception($"{nameof(BLTTroopAscension)}", ex);
            }
        }
    }
}
