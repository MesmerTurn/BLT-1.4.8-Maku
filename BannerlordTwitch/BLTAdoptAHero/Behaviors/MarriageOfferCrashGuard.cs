using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace BLTAdoptAHero.Behaviors
{
    /// <summary>
    /// Stops one malformed clan from killing the campaign on the daily tick.
    ///
    /// Reported by Maku: NullReferenceException in Clan.get_HasNavalNavigationCapability, reached
    /// from MarriageOfferCampaignBehavior.DailyTickClan. That behaviour walks every clan in the
    /// campaign once a day, so a single clan missing something the War Sails naval check expects
    /// takes the whole game down, every day, with no way to play past it.
    ///
    /// This does not pretend to fix the clan - it stops the crash and reports which clan is at
    /// fault, which is the part we could not get from the crash report. Once the log names it we
    /// can repair the data properly instead of guessing at which field is null.
    ///
    /// Only marriage offers are skipped for that one clan, and only on the day it throws.
    /// </summary>
    [HarmonyPatch]
    public static class MarriageOfferCrashGuard
    {
        // Resolved by name rather than typeof: the behaviour is version-specific, and a
        // [HarmonyPatch(typeof(...))] against a type this game build does not have throws out of
        // PatchAll and kills every other patch in the assembly with it.
        private static IEnumerable<MethodBase> FindTargets() =>
            new[] { "MarriageOfferCampaignBehavior" }
                .Select(AccessTools.TypeByName)
                .Where(t => t != null)
                .Select(t => AccessTools.DeclaredMethod(t, "DailyTickClan"))
                .Where(m => m != null)
                .Cast<MethodBase>();

        static bool Prepare() => FindTargets().Any();

        static IEnumerable<MethodBase> TargetMethods() => FindTargets();

        // Report each offender once rather than once per in-game day, or a long campaign fills the
        // log with the same line thousands of times.
        private static readonly HashSet<string> Reported = new();

        static Exception Finalizer(Exception __exception, Clan __0)
        {
            if (__exception == null) return null;

            try
            {
                string id = __0?.StringId ?? "<null clan>";
                if (Reported.Add(id))
                {
                    Log.Error(
                        $"[MarriageGuard] Marriage offers crashed on clan '{__0?.Name}' ({id}) and were skipped. " +
                        $"culture={__0?.Culture?.StringId ?? "NULL"}, " +
                        $"leader={__0?.Leader?.Name?.ToString() ?? "NULL"}, " +
                        $"home={__0?.HomeSettlement?.Name?.ToString() ?? "NULL"}, " +
                        $"kingdom={__0?.Kingdom?.Name?.ToString() ?? "none"}, " +
                        $"fiefs={__0?.Fiefs?.Count.ToString() ?? "?"}, " +
                        $"parties={__0?.WarPartyComponents?.Count.ToString() ?? "?"} " +
                        $":: {__exception.GetType().Name}: {__exception.Message}");
                }
            }
            catch
            {
                // Reporting must never be the thing that crashes the tick.
            }

            // Swallow it: the alternative is the campaign dying every single day.
            return null;
        }
    }

    /// <summary>
    /// The same protection for the other half of the daily clan tick.
    ///
    /// Also reported by Maku: NullReferenceException in
    /// DefaultPartySizeLimitModel.FindAppropriateInitialRosterForMobileParty, reached from
    /// HeroSpawnCampaignBehavior.TrySpawnHeroesAndParties. The game spawns a party for any lord
    /// that lacks one, and building that party needs the clan's culture, its party template and
    /// the lord's home settlement. A clan short of any of those kills the tick for everyone.
    ///
    /// Guarded at the clan level rather than deeper down, so exactly one clan is skipped for one
    /// day and the rest of the campaign ticks normally.
    /// </summary>
    [HarmonyPatch]
    public static class HeroSpawnCrashGuard
    {
        private static IEnumerable<MethodBase> FindTargets() =>
            new[] { "HeroSpawnCampaignBehavior" }
                .Select(AccessTools.TypeByName)
                .Where(t => t != null)
                .Select(t => AccessTools.DeclaredMethod(t, "TrySpawnHeroesAndParties"))
                .Where(m => m != null)
                .Cast<MethodBase>();

        static bool Prepare() => FindTargets().Any();

        static IEnumerable<MethodBase> TargetMethods() => FindTargets();

        private static readonly HashSet<string> Reported = new();

        static Exception Finalizer(Exception __exception, Clan __0)
        {
            if (__exception == null) return null;

            try
            {
                string id = __0?.StringId ?? "<null clan>";
                if (Reported.Add(id))
                {
                    Log.Error(
                        $"[SpawnGuard] Party spawning crashed on clan '{__0?.Name}' ({id}) and was skipped. " +
                        $"culture={__0?.Culture?.StringId ?? "NULL"}, " +
                        $"partyTemplate={__0?.Culture?.DefaultPartyTemplate?.StringId ?? "NULL"}, " +
                        $"leader={__0?.Leader?.Name?.ToString() ?? "NULL"}, " +
                        $"leaderHome={__0?.Leader?.HomeSettlement?.Name?.ToString() ?? "NULL"}, " +
                        $"clanHome={__0?.HomeSettlement?.Name?.ToString() ?? "NULL"}, " +
                        $"kingdom={__0?.Kingdom?.Name?.ToString() ?? "none"} " +
                        $":: {__exception.GetType().Name}: {__exception.Message}");
                }
            }
            catch
            {
                // Never let the reporting be what breaks the tick.
            }

            return null;
        }
    }

    /// <summary>
    /// Stops a settlement claim election from killing the campaign every day.
    ///
    /// Reported by Maku as an infinite crash: NullReferenceException in
    /// DefaultSettlementValueModel.GeographicalAdvantageForFaction, reached from
    /// SettlementClaimantCampaignBehavior.DailyTickSettlement. The game was scoring how much a
    /// settlement is worth to each faction that might claim it, and one of those factions was
    /// missing something the scoring reads. Because it happens on a daily tick, the crash
    /// returns the moment the day rolls over again - there is no playing past it.
    ///
    /// Guarded at two depths. The value model returns zero for the faction it could not score,
    /// which lets the election finish with the remaining candidates; the daily tick is a
    /// backstop, so even an exception from somewhere else in that election only costs one
    /// settlement one day. Both report once per settlement, naming the pieces the crash report
    /// itself does not carry.
    /// </summary>
    [HarmonyPatch]
    public static class SettlementValueCrashGuard
    {
        private static IEnumerable<MethodBase> FindTargets() =>
            new[] { "DefaultSettlementValueModel" }
                .Select(AccessTools.TypeByName)
                .Where(t => t != null)
                .Select(t => AccessTools.DeclaredMethod(t, "GeographicalAdvantageForFaction"))
                .Where(m => m != null)
                .Cast<MethodBase>();

        static bool Prepare() => FindTargets().Any();

        static IEnumerable<MethodBase> TargetMethods() => FindTargets();

        private static readonly HashSet<string> Reported = new();

        static Exception Finalizer(Exception __exception, Settlement __0, IFaction __1,
            ref float __result)
        {
            if (__exception == null) return null;

            try
            {
                // Zero means "no geographical advantage", which is a neutral answer rather than
                // a favourable or hostile one. The election then runs on the candidates it could
                // actually score instead of collapsing.
                __result = 0f;

                string id = __0?.StringId ?? "<null settlement>";
                if (Reported.Add(id))
                {
                    Log.Error(
                        $"[ClaimGuard] Settlement value scoring crashed for '{__0?.Name}' ({id}) and was treated as neutral. " +
                        $"faction={__1?.Name?.ToString() ?? "NULL"}, " +
                        $"factionLeader={__1?.Leader?.Name?.ToString() ?? "NULL"}, " +
                        $"factionCulture={__1?.Culture?.StringId ?? "NULL"}, " +
                        $"factionSettlements={__1?.Settlements?.Count.ToString() ?? "NULL"}, " +
                        $"owner={__0?.OwnerClan?.Name?.ToString() ?? "NULL"}, " +
                        $"ownerKingdom={__0?.OwnerClan?.Kingdom?.Name?.ToString() ?? "none"}, " +
                        $"settlementCulture={__0?.Culture?.StringId ?? "NULL"} " +
                        $":: {__exception.GetType().Name}: {__exception.Message}");
                }
            }
            catch
            {
                // Never let the reporting be what breaks the tick.
            }

            return null;
        }
    }

    /// <summary>
    /// Backstop for the same daily crash, one level up: if anything else in the settlement claim
    /// election throws, that settlement is skipped for the day rather than the campaign stopping.
    /// </summary>
    [HarmonyPatch]
    public static class SettlementClaimantCrashGuard
    {
        private static IEnumerable<MethodBase> FindTargets() =>
            new[] { "SettlementClaimantCampaignBehavior" }
                .Select(AccessTools.TypeByName)
                .Where(t => t != null)
                .Select(t => AccessTools.DeclaredMethod(t, "DailyTickSettlement"))
                .Where(m => m != null)
                .Cast<MethodBase>();

        static bool Prepare() => FindTargets().Any();

        static IEnumerable<MethodBase> TargetMethods() => FindTargets();

        private static readonly HashSet<string> Reported = new();

        static Exception Finalizer(Exception __exception, Settlement __0)
        {
            if (__exception == null) return null;

            try
            {
                string id = __0?.StringId ?? "<null settlement>";
                if (Reported.Add(id))
                {
                    Log.Error(
                        $"[ClaimGuard] Claim election crashed on settlement '{__0?.Name}' ({id}) and was skipped for the day. " +
                        $"owner={__0?.OwnerClan?.Name?.ToString() ?? "NULL"}, " +
                        $"ownerKingdom={__0?.OwnerClan?.Kingdom?.Name?.ToString() ?? "none"}, " +
                        $"culture={__0?.Culture?.StringId ?? "NULL"} " +
                        $":: {__exception.GetType().Name}: {__exception.Message}");
                }
            }
            catch
            {
                // Never let the reporting be what breaks the tick.
            }

            return null;
        }
    }
}
