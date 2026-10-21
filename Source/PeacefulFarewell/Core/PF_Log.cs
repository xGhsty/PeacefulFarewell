using Verse;

namespace PeacefulFarewell
{
    public static class PF_Log
    {
        private const string Prefix = "<color=#33CC33>[PeacefulFarewell]</color>";

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
