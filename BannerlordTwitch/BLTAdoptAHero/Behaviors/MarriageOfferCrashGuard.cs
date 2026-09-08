using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

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
}
