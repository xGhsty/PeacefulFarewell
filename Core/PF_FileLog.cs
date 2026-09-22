using System;
using System.IO;
using Verse;

namespace PeacefulFarewell
{
    // Writes PF_Log output to a plain-text file next to the mod's own folder
    // (<ModRoot>/CustomLog.txt) in addition to the normal Player.log, so the
    // player/author can hand over a short, PeacefulFarewell-only log without
    // digging through the much larger vanilla log or asking the player to dig
    // through Player.log for the relevant lines by hand.
    public static class PF_FileLog
    {
        private const string FileName = "CustomLog.txt";

        private static readonly object Lock = new object();
        private static string cachedPath;
        private static bool pathResolutionFailed;
        // Kept open for the life of the session instead of opening/closing the
        // file on every single Write() call - File.AppendAllText does a fresh
        // open+seek-to-end+close each time, which is disk I/O on the main game
        // thread and was measurably contributing to freezes with debug/file
        // logging enabled during normal play.
        private static StreamWriter writer;

        public static void Write(string level, string text)
        {
            if (PeacefulFarewellMod.Settings == null || !PeacefulFarewellMod.Settings.customFileLoggingEnabled)
            {
                return;
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {text}";

            lock (Lock)
            {
                StreamWriter w = GetWriter();
                if (w == null)
                {
                    return;
                }

                try
                {
                    w.WriteLine(line);
                }
                catch (Exception ex)
                {
                    // Don't let a locked/unwritable file spam Player.log every call -
                    // warn once by disabling further attempts this session instead.
                    pathResolutionFailed = true;
                    CloseWriter();
                    Log.Warning("<color=#33CC33>[PeacefulFarewell]</color> Failed to write CustomLog.txt, disabling file logging for this session: " + ex);
                }
            }
        }

        // Call when the setting is turned on (or at each write) so a fresh
        // session starts a fresh file section rather than appending forever
        // across unrelated play sessions.
        public static void WriteSessionHeaderIfNeeded()
        {
            lock (Lock)
            {
                StreamWriter w = GetWriter();
                if (w == null)
                {
                    return;
                }

                try
                {
                    w.WriteLine();
                    w.WriteLine($"===== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                }
                catch (Exception ex)
                {
                    pathResolutionFailed = true;
                    CloseWriter();
                    Log.Warning("<color=#33CC33>[PeacefulFarewell]</color> Failed to write CustomLog.txt session header: " + ex);
                }
            }
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
                Log.Warning("<color=#33CC33>[PeacefulFarewell]</color> Failed to open CustomLog.txt: " + ex);
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
