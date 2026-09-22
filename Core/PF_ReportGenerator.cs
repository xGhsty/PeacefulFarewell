using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // Backs the settings window's "Generate and open report" button. Unlike
    // CustomLog.txt/WandererDebugLog.txt (which only grow while their
    // respective settings are enabled), this always produces a fresh,
    // self-contained PF_Report_<timestamp>.txt on demand - mod version,
    // current settings, a live wanderer/settled-pawn snapshot pulled straight
    // from PF_WandererTracker, and the tail of both log files if present -
    // so the author has one file to ask a player for instead of walking them
    // through enabling settings and finding two separate files.
    public static class PF_ReportGenerator
    {
        private const int LogTailLines = 200;

        // Returns the full path of the generated report, or null if it
        // couldn't be written (e.g. mod root unresolved or disk error - a
        // warning is logged in that case).
        public static string Generate()
        {
            string rootDir = PeacefulFarewellMod.ModRootDir;
            if (string.IsNullOrEmpty(rootDir))
            {
                PF_Log.Warning("Cannot generate report: mod root directory unresolved.");
                return null;
            }

            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendSettings(sb);
            AppendWandererSnapshot(sb);
            AppendLogTail(sb, Path.Combine(rootDir, "CustomLog.txt"), "CustomLog.txt");
            AppendLogTail(sb, Path.Combine(rootDir, "WandererDebugLog.txt"), "WandererDebugLog.txt");

            string fileName = $"PF_Report_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
            string path = Path.Combine(rootDir, fileName);

            try
            {
                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch (Exception ex)
            {
                PF_Log.Warning("Failed to write report file: " + ex);
                return null;
            }
        }

        private static void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("===== Peaceful Farewell Report =====");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Game tick: {(Current.Game != null ? Find.TickManager.TicksGame.ToString() : "no active game")}");
            sb.AppendLine();
        }

        private static void AppendSettings(StringBuilder sb)
        {
            PF_Settings s = PeacefulFarewellMod.Settings;
            sb.AppendLine("----- Settings -----");
            if (s == null)
            {
                sb.AppendLine("(settings unavailable)");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"requestChancePerVisit = {s.requestChancePerVisit}");
            sb.AppendLine($"minRelationImportance = {s.minRelationImportance}");
            sb.AppendLine($"lonelinessChancePerCheck = {s.lonelinessChancePerCheck}");
            sb.AppendLine($"lonelinessAvgOpinionThreshold = {s.lonelinessAvgOpinionThreshold}");
            sb.AppendLine($"lonelinessMaxSingleOpinionThreshold = {s.lonelinessMaxSingleOpinionThreshold}");
            sb.AppendLine($"wandererMinCheckDays = {s.wandererMinCheckDays}");
            sb.AppendLine($"wandererMaxCheckDays = {s.wandererMaxCheckDays}");
            sb.AppendLine($"wandererJoinedFactionLetterEnabled = {s.wandererJoinedFactionLetterEnabled}");
            sb.AppendLine($"wanderlustEnabled = {s.wanderlustEnabled}");
            sb.AppendLine($"wanderlustChancePerCheck = {s.wanderlustChancePerCheck}");
            sb.AppendLine($"letterFromAfarEnabled = {s.letterFromAfarEnabled}");
            sb.AppendLine($"debugMode = {s.debugMode}");
            sb.AppendLine($"customFileLoggingEnabled = {s.customFileLoggingEnabled}");
            sb.AppendLine();
        }

        private static void AppendWandererSnapshot(StringBuilder sb)
        {
            sb.AppendLine("----- Live wanderer snapshot -----");

            PF_WandererTracker tracker = Current.Game?.World?.GetComponent<PF_WandererTracker>();
            if (tracker == null)
            {
                sb.AppendLine("(no active world)");
                sb.AppendLine();
                return;
            }

            var wandering = tracker.GetOverviewEntries();
            sb.AppendLine($"Currently wandering ({wandering.Count}):");
            if (wandering.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            foreach (var entry in wandering)
            {
                sb.AppendLine(
                    $"  {entry.Pawn.LabelShort} | reason={entry.Reason} | departed={DepartureDateString(entry.DepartureTick, entry.DepartureTile)} | " +
                    $"trackedFor={entry.DaysTracked:F1}d | resolveChance={entry.ResolveChancePercent:F0}% | forceDecisionNext={entry.ForceDecisionNext} | " +
                    $"faction={entry.Pawn.Faction?.Name ?? "none"}");
            }

            sb.AppendLine();

            var settled = tracker.GetSettledOverviewEntries();
            sb.AppendLine($"Settled, writing home ({settled.Count}):");
            if (settled.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            foreach (var entry in settled)
            {
                sb.AppendLine(
                    $"  {entry.Pawn.LabelShort} | faction={entry.Pawn.Faction?.Name ?? "none"} | " +
                    $"departed={DepartureDateString(entry.DepartureTick, entry.DepartureTile)} | " +
                    $"nextLetterCheckInDays={entry.DaysUntilNextLetterCheck:F2}");
            }

            sb.AppendLine();
        }

        // Mirrors Dialog_PF_WandererOverview.DepartureDateString - full calendar
        // date if a tile could be resolved, else a tile-less relative fallback.
        private static string DepartureDateString(int departureTick, int tile)
        {
            if (tile >= 0)
            {
                return GenDate.DateFullStringAt(departureTick, Find.WorldGrid.LongLatOf(tile));
            }
            float daysAgo = (float)((Current.Game != null ? Find.TickManager.TicksGame : departureTick) - departureTick) / GenDate.TicksPerDay;
            return $"{daysAgo:F1}d ago";
        }

        private static void AppendLogTail(StringBuilder sb, string path, string displayName)
        {
            sb.AppendLine($"----- {displayName} (last {LogTailLines} lines) -----");

            if (!File.Exists(path))
            {
                sb.AppendLine("(file not found - enable the matching logging setting to produce it)");
                sb.AppendLine();
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines.Skip(Math.Max(0, lines.Length - LogTailLines)))
                {
                    sb.AppendLine(line);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(failed to read {displayName}: {ex.Message})");
            }

            sb.AppendLine();
        }
    }
}
