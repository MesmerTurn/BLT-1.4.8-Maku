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
        // When the last promotion happened, for the cooldown. Deliberately not saved: after a
        // reload the worst case is one promotion sooner than intended, which is a far smaller
        // problem than adding another field to the save format for it.
        private static CampaignTime? lastAscension;

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

            // Safety nets, requested by Maku after watching this fire more often than expected.
            // Checked before the dice, so a blocked promotion does not quietly consume the roll.
            var campaign = BLTAdoptAHeroCampaignBehavior.Current;

            if (cfg.TroopAscensionMaxLords > 0
                && campaign?.GetAscendedLordCount() >= cfg.TroopAscensionMaxLords)
            {
                Log.Trace($"[TroopAscension] Campaign limit of {cfg.TroopAscensionMaxLords} lords reached, skipping.");
                return;
            }

            // A battle can kill several adopted heroes in seconds. Without a gap between
            // promotions, one bad fight turns into a handful of permanent new clans at once.
            if (cfg.TroopAscensionCooldownDays > 0 && lastAscension.HasValue)
            {
                float daysSince = lastAscension.Value.ElapsedDaysUntilNow;
                if (daysSince < cfg.TroopAscensionCooldownDays)
                {
                    Log.Trace($"[TroopAscension] Only {daysSince:F1} days since the last promotion " +
                              $"(needs {cfg.TroopAscensionCooldownDays}), skipping.");
                    return;
                }
            }

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

            // Counted only once the clan actually exists, so a failed promotion spends neither the
            // campaign allowance nor the cooldown.
            BLTAdoptAHeroCampaignBehavior.Current?.RecordAscendedLord();
            lastAscension = CampaignTime.Now;

            Log.LogFeedEvent("{=}{TROOP} slew {VICTIM} and has risen as a lord of their own clan!"
                .Translate(("TROOP", newLord.Name.ToString()), ("VICTIM", victim.Name.ToString())));
        }

        /// <summary>
        /// Turns an existing hero into the leader of a brand new clan with no kingdom. Shared by
        /// the kill-promotion above and the !promote command, so both produce the same kind of
        /// lord. Mirrors the clan setup in ClanManagement, which is proven to work in this build.
        /// </summary>
        public static void MakeLordOfNewClan(Hero hero, int renown, int startingGold,
            Kingdom joinKingdom = null)
        {
            string clanName = "{=}Clan of {NAME}".Translate(("NAME", hero.Name.ToString()));
            var clan = Clan.CreateClan(clanName);
            clan.ChangeClanName(new TextObject(clanName), new TextObject(clanName));
            // Never leave a clan without a culture: vanilla's daily clan tick reads it, and a null
            // there crashes the campaign rather than just this clan.
            clan.Culture = hero.Culture
                           ?? hero.CharacterObject?.Culture
                           ?? Clan.PlayerClan?.Culture
                           ?? Settlement.All.FirstOrDefault(s => s.Culture != null)?.Culture;
            clan.Banner = BLTBannerSanitizerBehavior.CreateSafeBanner();
            clan.Kingdom = null;
            clan.AddRenown(renown, false);
            clan.SetInitialHomeSettlement(
                Settlement.All.Where(s => s.Culture == clan.Culture).SelectRandom()
                ?? Settlement.All.SelectRandom());

            hero.Clan = clan;
            hero.SetNewOccupation(Occupation.Lord);
            clan.SetLeader(hero);

            // Give the lord a home settlement of their own. The clan has one, but the hero is
            // asked for theirs independently: the daily tick tries to spawn a party for any lord
            // without one, and that path resolves a spawn position and starting roster through the
            // hero. A lord with no home is how that ends up dereferencing nothing.
            try { hero.UpdateHomeSettlement(); }
            catch (Exception ex) { Log.Error($"[TroopAscension] Could not set home settlement: {ex.Message}"); }
            clan.IsNoble = true;
            CampaignEventDispatcher.Instance.OnClanCreated(clan, false);

            // Promoted through a viewer, the new lord serves whoever that viewer serves. Done
            // after OnClanCreated so the kingdom receives a clan the campaign already knows
            // about, and through ChangeKingdomAction rather than by assigning Clan.Kingdom, so
            // the kingdom's own bookkeeping runs. A BLT-made kingdom and a base-game one are the
            // same kind of object here, so neither needs special handling.
            if (joinKingdom != null)
            {
                try
                {
                    // Never: this is a sworn lord of the viewer's realm, not a mercenary on a
                    // contract that runs out and leaves them free to wander off.
                    ChangeKingdomAction.ApplyByJoinToKingdom(
                        clan, joinKingdom, CampaignTime.Never, false);
                }
                catch (Exception ex)
                {
                    Log.Error($"[TroopAscension] Could not put {hero.Name} into {joinKingdom.Name}: {ex.Message}");
                }
            }

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
