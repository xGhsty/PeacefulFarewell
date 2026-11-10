using System;
using System.IO;
using System.Linq;
using System.Text;
using Verse;

namespace PeacefulFarewell
{
    // A plain-text log file in the mod's root folder, kept open for the whole
    // session (opening and closing it on every write was measurable disk I/O
    // on the main thread). Any write failure disables the file for the rest
    // of the session instead of spamming Player.log.
    internal sealed class PF_LogFile
    {
        // Trimming re-reads and rewrites the whole file, so the size is only
        // checked every this many writes.
        private const int SizeCheckEveryNWrites = 25;

        private readonly string fileName;
        private readonly int maxFileSizeBytes;
        private readonly object sync = new object();
        private StreamWriter writer;
        private string path;
        private bool failed;
        private int writesSinceSizeCheck;

        // maxFileSizeBytes 0 = never trimmed.
        public PF_LogFile(string fileName, int maxFileSizeBytes = 0)
        {
            this.fileName = fileName;
            this.maxFileSizeBytes = maxFileSizeBytes;
        }

        public static string Timestamp => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public void WriteLine(string line)
        {
            lock (sync)
            {
                StreamWriter w = GetWriter();
                if (w == null)
                {
                    return;
                }

                try
                {
                    w.WriteLine(line);
                    if (maxFileSizeBytes > 0 && ++writesSinceSizeCheck >= SizeCheckEveryNWrites)
                    {
                        writesSinceSizeCheck = 0;
                        TrimToSize();
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    CloseWriter();
                    Log.Warning($"{PF_Log.Prefix} Failed to write {fileName}, disabling it for this session: {ex}");
                }
            }
        }

        // Drops the oldest lines until the file is back under the size limit.
        // Closes the writer first since the file is rewritten underneath it;
        // the next write reopens it.
        private void TrimToSize()
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= maxFileSizeBytes)
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
                if (keepBytes + lineBytes > maxFileSizeBytes)
                {
                    break;
                }
                keepBytes += lineBytes;
                startIndex = i;
            }

            File.WriteAllLines(path, allLines.Skip(startIndex));
        }

        private StreamWriter GetWriter()
        {
            if (failed || writer != null)
            {
                return writer;
            }

            string rootDir = PeacefulFarewellMod.ModRootDir;
            if (string.IsNullOrEmpty(rootDir))
            {
                return null;
            }

            path = Path.Combine(rootDir, fileName);
            try
            {
                writer = new StreamWriter(path, append: true) { AutoFlush = true };
                return writer;
            }
            catch (Exception ex)
            {
                failed = true;
                Log.Warning($"{PF_Log.Prefix} Failed to open {fileName}: {ex}");
                return null;
            }
        }

        private void CloseWriter()
        {
            writer?.Dispose();
            writer = null;
        }
    }
}
