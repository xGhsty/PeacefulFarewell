using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // File-backed twin of PF_GameComponent's savedWorkAndSchedule dictionary.
    // The dictionary is Scribed (LookMode.Reference for the Pawn key), which
    // depends on RimWorld's cross-reference resolution finding the exact same
    // Pawn object again on load - normally reliable, but a pawn sitting
    // faction-less in WorldPawns for a long stretch (this mod's wanderers
    // often sit that way for several in-game days) is exactly the kind of
    // long-lived, off-map reference that's most exposed if anything ever
    // disturbs that resolution (a save made mid-transition, a mod conflict,
    // manual save editing, etc). Writing the same snapshot to a plain text
    // file, keyed by the pawn's ThingID (stable for that pawn's whole
    // lifetime in this save), gives a second, independent way to recover the
    // data that doesn't rely on Scribe reference-fixup at all - only on the
    // returning pawn's own ThingID matching a file written when they left.
    //
    // One file per pawn rather than one shared file: avoids any risk of one
    // read/write corrupting unrelated pawns' entries, and makes a stray file
    // for a pawn who never returns harmless clutter rather than a growing
    // shared file that has to be parsed and rewritten every time.
    public static class PF_WorkAndScheduleFileStore
    {
        private const string SubDirName = "Pawns";

        public static void Save(Pawn pawn, PF_WorkAndScheduleSnapshot snapshot)
        {
            string path = GetPathFor(pawn);
            if (path == null)
            {
                return;
            }

            try
            {
                var lines = new List<string>
                {
                    "PawnLabel=" + pawn.LabelShort,
                    "SavedAtTicks=" + Find.TickManager.TicksGame.ToString(CultureInfo.InvariantCulture)
                };

                foreach (KeyValuePair<WorkTypeDef, int> entry in snapshot.workPriorities)
                {
                    if (entry.Key == null)
                    {
                        continue;
                    }
                    lines.Add("Work=" + entry.Key.defName + "=" + entry.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (snapshot.timetable != null)
                {
                    IEnumerable<string> defNames = snapshot.timetable.Select(t => t?.defName ?? "");
                    lines.Add("Timetable=" + string.Join(",", defNames));
                }

                if (snapshot.apparelPolicy != null)
                {
                    lines.Add("ApparelPolicyId=" + snapshot.apparelPolicy.id.ToString(CultureInfo.InvariantCulture));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines);

                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"PF_WorkAndScheduleFileStore: saved backup file for {pawn.LabelShort} at {path}");
                }
            }
            catch (Exception ex)
            {
                PF_Log.Warning($"PF_WorkAndScheduleFileStore: failed to save backup for {pawn.LabelShort}: {ex}");
            }
        }

        // Reads back a file-based snapshot for this pawn, if one exists.
        // Deliberately does NOT delete the file itself - callers should call
        // Discard once the data has actually been applied, so a restore that
        // fails partway through doesn't lose the backup.
        public static PF_WorkAndScheduleSnapshot TryLoad(Pawn pawn)
        {
            string path = GetPathFor(pawn);
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var snapshot = new PF_WorkAndScheduleSnapshot();
                string[] lines = File.ReadAllLines(path);

                foreach (string line in lines)
                {
                    if (line.StartsWith("Work=", StringComparison.Ordinal))
                    {
                        string rest = line.Substring("Work=".Length);
                        int splitIndex = rest.LastIndexOf('=');
                        if (splitIndex < 0)
                        {
                            continue;
                        }

                        string defName = rest.Substring(0, splitIndex);
                        string valueText = rest.Substring(splitIndex + 1);
                        if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority))
                        {
                            continue;
                        }

                        WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(defName);
                        if (workType == null)
                        {
                            continue;
                        }

                        snapshot.workPriorities[workType] = priority;
                    }
                    else if (line.StartsWith("Timetable=", StringComparison.Ordinal))
                    {
                        string rest = line.Substring("Timetable=".Length);
                        snapshot.timetable = rest
                            .Split(',')
                            .Select(defName => string.IsNullOrEmpty(defName) ? null : DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(defName))
                            .ToList();
                    }
                    else if (line.StartsWith("ApparelPolicyId=", StringComparison.Ordinal))
                    {
                        string rest = line.Substring("ApparelPolicyId=".Length);
                        if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int policyId))
                        {
                            snapshot.apparelPolicy = Current.Game?.outfitDatabase?.AllOutfits
                                .FirstOrDefault(policy => policy.id == policyId);
                        }
                    }
                }

                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"PF_WorkAndScheduleFileStore: loaded backup file for {pawn.LabelShort} from {path}");
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                PF_Log.Warning($"PF_WorkAndScheduleFileStore: failed to read backup for {pawn.LabelShort}: {ex}");
                return null;
            }
        }

        public static void Discard(Pawn pawn)
        {
            string path = GetPathFor(pawn);
            if (path == null || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                PF_Log.Warning($"PF_WorkAndScheduleFileStore: failed to delete backup for {pawn.LabelShort}: {ex}");
            }
        }

        private static string GetPathFor(Pawn pawn)
        {
            string rootDir = PeacefulFarewellMod.ModRootDir;
            if (string.IsNullOrEmpty(rootDir) || pawn == null)
            {
                return null;
            }

            // ThingID is stable for this pawn's whole lifetime within one save
            // and filesystem-safe (letters/digits/underscore only).
            return Path.Combine(rootDir, SubDirName, pawn.ThingID + ".txt");
        }
    }
}
