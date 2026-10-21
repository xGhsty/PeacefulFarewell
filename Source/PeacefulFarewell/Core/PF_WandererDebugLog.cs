using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Verse;

namespace PeacefulFarewell
{
    // Separate from PF_FileLog/CustomLog.txt on purpose - that file mixes in
    // every other PF_Log.Message call from the whole mod, which makes it hard
    // to see at a glance what the wanderer system is actually doing over a long
    // play session. This writes a dedicated WandererDebugLog.txt with two
    // things: a running log of every check/decision, and (prepended each write)
    // a snapshot of who's currently out in the world and when they're next due
    // to be checked - so the author can open the file and immediately see both
    // "what happened recently" and "who's still out there right now".
    public static class PF_WandererDebugLog
    {
        private const string FileName = "WandererDebugLog.txt";
        private const int MaxFileSizeBytes = 50 * 1024;

        // TrimToSize does a full File.ReadAllLines + File.WriteAllLines - only
        // worth checking for periodically, not on every single write, or a long
        // debug-mode session spends a lot of main-thread time re-reading and
        // rewriting the whole file over and over right around the size limit.
        private const int SizeCheckEveryNWrites = 25;

        private static readonly object Lock = new object();
        private static string cachedPath;
        private static bool pathResolutionFailed;
        // Kept open for the life of the session instead of opening/closing the
        // file on every single write - see the matching comment in PF_FileLog.
        private static StreamWriter writer;
        private static int writesSinceSizeCheck;

        public static void LogEvent(string text)
        {
            if (PeacefulFarewellMod.Settings == null || !PeacefulFarewellMod.Settings.debugMode)
            {
                return;
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}";

            lock (Lock)
            {
                WriteLine(line);
            }
        }

        // Called once per WorldComponentTick check (~every in-game hour) with
        // the tracker's current wanderer list, so the file always reflects who
        // is still out there even if nothing happened to them this check.
        // trackedSlotCount is the raw list size (including any null/Discarded
        // slots) - passed separately from wandererDescriptions because a stale
        // slot still produces a description line, just not a normal one, so
        // description count alone can't be trusted as "how many are tracked".
        public static void LogSnapshot(int trackedSlotCount, IEnumerable<string> wandererDescriptions)
        {
            if (PeacefulFarewellMod.Settings == null || !PeacefulFarewellMod.Settings.debugMode)
            {
                return;
            }

            List<string> descriptions = wandererDescriptions.ToList();

            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] --- Snapshot: {trackedSlotCount} wanderer(s) currently tracked ---");
            foreach (string desc in descriptions)
            {
                sb.Append(Environment.NewLine).Append("    ").Append(desc);
            }

            lock (Lock)
            {
                WriteLine(sb.ToString());
            }
        }

        private static void WriteLine(string line)
        {
            StreamWriter w = GetWriter();
            if (w == null)
            {
                return;
            }

            try
            {
                w.WriteLine(line);

                writesSinceSizeCheck++;
                if (writesSinceSizeCheck >= SizeCheckEveryNWrites)
                {
                    writesSinceSizeCheck = 0;
                    CheckAndTrimSize();
                }
            }
            catch (Exception ex)
            {
                // Don't let a locked/unwritable file spam Player.log every call -
                // warn once by disabling further attempts this session instead.
                pathResolutionFailed = true;
                CloseWriter();
                Log.Warning("<color=#33CC33>[PeacefulFarewell]</color> Failed to write WandererDebugLog.txt, disabling wanderer debug logging for this session: " + ex);
            }
        }

        // Drops the oldest lines until the file is back under MaxFileSizeBytes,
        // so the log stays a bounded, recent-history window instead of growing
        // forever over a long play session. Requires closing and reopening the
        // held-open writer since it rewrites the file out from under it.
        private static void CheckAndTrimSize()
        {
            string path = cachedPath;
            if (path == null)
            {
                return;
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxFileSizeBytes)
            {
                return;
            }

            CloseWriter();

            string[] allLines = File.ReadAllLines(path);

            long keepBytes = 0;
            int startIndex = allLines.Length;
            for (int i = allLines.Length - 1; i >= 0; i--)
            {
                long lineBytes = Encoding.UTF8.GetByteCount(allLines[i]) + Environment.NewLine.Length;
                if (keepBytes + lineBytes > MaxFileSizeBytes)
                {
                    break;
                }
                keepBytes += lineBytes;
                startIndex = i;
            }

            IEnumerable<string> trimmed = allLines.Skip(startIndex);
            File.WriteAllLines(path, trimmed);
        }

        private static StreamWriter GetWriter()
        {
            if (pathResolutionFailed)
            {
                return null;
            }

            if (writer != null)
            {
                return writer;
            }

            string path = GetLogPath();
            if (path == null)
            {
                return null;
            }

            try
            {
                writer = new StreamWriter(path, append: true) { AutoFlush = true };
                return writer;
            }
            catch (Exception ex)
            {
                pathResolutionFailed = true;
                Log.Warning("<color=#33CC33>[PeacefulFarewell]</color> Failed to open WandererDebugLog.txt: " + ex);
                return null;
            }
        }

        private static void CloseWriter()
        {
            writer?.Dispose();
            writer = null;
        }

        private static string GetLogPath()
        {
            string rootDir = PeacefulFarewellMod.ModRootDir;
            if (string.IsNullOrEmpty(rootDir))
            {
                return null;
            }

            cachedPath = Path.Combine(rootDir, FileName);
            return cachedPath;
        }
    }
}
