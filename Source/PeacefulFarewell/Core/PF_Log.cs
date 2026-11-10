using Verse;

namespace PeacefulFarewell
{
    public static class PF_Log
    {
        internal const string Prefix = "<color=#33CC33>[PeacefulFarewell]</color>";

        // Call sites guard on this before building the message, so debug
        // strings cost nothing when debug mode is off.
        public static bool DebugMode => PeacefulFarewellMod.Settings.debugMode;

        public static void Message(string text)
        {
            Log.Message(Prefix + " " + text);
            PF_FileLog.Write("INFO", text);
        }

        public static void Warning(string text)
        {
            Log.Warning(Prefix + " " + text);
            PF_FileLog.Write("WARN", text);
        }

        public static void Error(string text)
        {
            Log.Error(Prefix + " " + text);
            PF_FileLog.Write("ERROR", text);
        }
    }
}
