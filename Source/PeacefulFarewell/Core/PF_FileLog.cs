using System;

namespace PeacefulFarewell
{
    // Mirrors PF_Log output to <ModRoot>/CustomLog.txt, so a player can hand
    // over a short, PeacefulFarewell-only log instead of the whole Player.log.
    public static class PF_FileLog
    {
        private static readonly PF_LogFile File = new PF_LogFile("CustomLog.txt");

        public static void Write(string level, string text)
        {
            if (PeacefulFarewellMod.Settings == null || !PeacefulFarewellMod.Settings.customFileLoggingEnabled)
            {
                return;
            }

            File.WriteLine($"[{PF_LogFile.Timestamp}] [{level}] {text}");
        }

        // Marks where each play session starts in the file.
        public static void WriteSessionHeaderIfNeeded()
        {
            File.WriteLine($"{Environment.NewLine}===== Session started {PF_LogFile.Timestamp} =====");
        }
    }
}
