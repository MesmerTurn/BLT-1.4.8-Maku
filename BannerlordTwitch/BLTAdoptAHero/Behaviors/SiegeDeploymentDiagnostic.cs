using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Siege;

namespace BLTAdoptAHero.Behaviors
{
    /// <summary>
    /// Diagnostic only. Changes no behaviour, swallows nothing, fixes nothing.
    ///
    /// Maku's siege crash is a NullReferenceException inside
    /// DeploymentMissionController.SetupTeams - the game's own code, which nothing here patches.
    /// It is failing on data: one of the parties in the battle is missing something. The crash
    /// report proves that a party is malformed but cannot say WHICH, and a previous attempt to fix
    /// this by guessing produced a patch that did not help.
    ///
    /// So: write down every party in the battle just before the game sets up teams, along with the
    /// fields most likely to be null. If it crashes again the file says which party to look at,
    /// and whether that party belongs to a BLT clan, to Lowborn, or to something else entirely.
    ///
    /// The game still crashes exactly as before. This only leaves evidence behind.
    /// </summary>
    [HarmonyPatch]
    public static class SiegeDeploymentDiagnostic
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord", "BLT_SiegeDiagnostic.log");

        // Resolved by name: a typeof patch against a type this build might not have throws out of
        // PatchAll and takes every other patch in the assembly down with it.
        private static IEnumerable<MethodBase> FindTargets() =>
            new[] { "DeploymentMissionController" }
                .Select(AccessTools.TypeByName)
                .Where(t => t != null)
                .Select(t => AccessTools.DeclaredMethod(t, "SetupTeams"))
                .Where(m => m != null)
                .Cast<MethodBase>();

        static bool Prepare() => FindTargets().Any();

        static IEnumerable<MethodBase> TargetMethods() => FindTargets();

        static void Prefix()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("======================================================================");
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Siege deployment about to set up teams");

                var battle = PlayerEncounter.Battle;
                if (battle == null)
                {
                    sb.AppendLine("  No MapEvent - deployment without a campaign battle behind it.");
                }
                else
                {
                    sb.AppendLine($"  Battle type: {battle.EventType}");
                    sb.AppendLine($"  Settlement : {Safe(() => battle.MapEventSettlement?.Name?.ToString())}");

                    foreach (var side in new[] { battle.AttackerSide, battle.DefenderSide })
                    {
                        sb.AppendLine($"  --- {Safe(() => side?.MissionSide.ToString())} side, " +
                                      $"faction {Safe(() => side?.MapFaction?.Name?.ToString())}");

                        var parties = Safe(() => (IEnumerable<MapEventParty>)side?.Parties)
                                      ?? Enumerable.Empty<MapEventParty>();
                        foreach (var p in parties)
                        {
                            Describe(sb, Safe(() => p?.Party));
                        }
                    }
                }

                // The besieging side is what the naval and defender checks resolve through, and it
                // is null between assault waves - worth recording either way.
                var siege = Safe(() => PlayerEncounter.EncounterSettlement?.SiegeEvent);
                sb.AppendLine($"  SiegeEvent : {(siege == null ? "NULL" : "present")}");
                if (siege != null)
                {
                    sb.AppendLine($"  Besieger   : {Safe(() => siege.BesiegerCamp?.MapFaction?.Name?.ToString()) ?? "NULL"}");
                }

                File.AppendAllText(LogPath, sb.ToString());
            }
            catch (Exception ex)
            {
                // A diagnostic that crashes the game would be worse than no diagnostic at all.
                try { Log.Error($"[SiegeDiag] {ex.Message}"); } catch { }
            }
        }

        private static void Describe(StringBuilder sb, PartyBase party)
        {
            if (party == null)
            {
                sb.AppendLine("      party: NULL  <-- suspicious");
                return;
            }

            string name = Safe(() => party.Name?.ToString()) ?? "NULL";
            string culture = Safe(() => party.Culture?.StringId) ?? "NULL";
            string template = Safe(() => party.Culture?.DefaultPartyTemplate?.StringId) ?? "NULL";
            string leader = Safe(() => party.LeaderHero?.Name?.ToString()) ?? "none";
            string clan = Safe(() => party.MobileParty?.ActualClan?.Name?.ToString()) ?? "none";
            string clanCulture = Safe(() => party.MobileParty?.ActualClan?.Culture?.StringId) ?? "NULL";
            string home = Safe(() => party.LeaderHero?.HomeSettlement?.Name?.ToString()) ?? "NULL";
            string banner = Safe(() => party.MobileParty?.ActualClan?.Banner) == null ? "NULL" : "ok";
            int members = Safe(() => (int?)party.MemberRoster?.Count) ?? -1;

            sb.AppendLine($"      party: {name}");
            sb.AppendLine($"             culture={culture}  partyTemplate={template}");
            sb.AppendLine($"             leader={leader}  leaderHome={home}");
            sb.AppendLine($"             clan={clan}  clanCulture={clanCulture}  banner={banner}  members={members}");
        }

        /// <summary>
        /// Reads a value that may itself throw. The whole point here is inspecting objects that are
        /// suspected of being broken, so every read has to be allowed to fail on its own.
        /// </summary>
        private static T Safe<T>(Func<T> get)
        {
            try { return get(); }
            catch { return default; }
        }
    }
}
