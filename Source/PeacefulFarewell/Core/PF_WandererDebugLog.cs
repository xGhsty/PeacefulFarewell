using System;
using System.Collections.Generic;
using System.Text;

namespace PeacefulFarewell
{
    // Debug-mode-only WandererDebugLog.txt, separate from CustomLog.txt so the
    // wanderer system can be followed on its own: every check/decision, plus
    // a periodic snapshot of who is out in the world. Capped at 50 KB.
    public static class PF_WandererDebugLog
    {
        private static readonly PF_LogFile File = new PF_LogFile("WandererDebugLog.txt", 50 * 1024);

        private static bool Enabled => PeacefulFarewellMod.Settings != null && PeacefulFarewellMod.Settings.debugMode;

        public static void LogEvent(string text)
        {
            if (Enabled)
            {
                File.WriteLine($"[{PF_LogFile.Timestamp}] {text}");
            }
        }

        // Called once per tracker check (~hourly). trackedSlotCount is the raw
        // list size, including any stale slots that still get a description.
        public static void LogSnapshot(int trackedSlotCount, IEnumerable<string> wandererDescriptions)
        {
            if (!Enabled)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"[{PF_LogFile.Timestamp}] --- Snapshot: {trackedSlotCount} wanderer(s) currently tracked ---");
            foreach (string desc in wandererDescriptions)
            {
                sb.Append(Environment.NewLine).Append("    ").Append(desc);
            }
            File.WriteLine(sb.ToString());
        }
    }
}
