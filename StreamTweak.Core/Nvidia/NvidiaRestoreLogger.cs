using System;
using System.IO;
using System.Text;

namespace StreamTweak.Nvidia
{
    /// <summary>
    /// Append-only audit log for NVIDIA Sentinel restore events, written to
    /// %LocalAppData%\StreamTweak\nvidia-restore.log. Thread-safe (every write is
    /// lock-guarded). Lines are semicolon-separated:
    ///   timestamp ; level ; reason ; changedCount ; changedKeysList
    /// </summary>
    public static class NvidiaRestoreLogger
    {
        private static readonly object _lock = new();

        public static string LogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "nvidia-restore.log");

        /// <summary>Logs a successful (manual or automatic) restore with the changed-setting summary.</summary>
        public static void LogRestore(string reason, int changedCount, string changedKeysCsv)
        {
            Write("RESTORE", reason, changedCount.ToString(), changedKeysCsv);
        }

        public static void LogInfo(string message)
        {
            Write("INFO", message, "", "");
        }

        public static void LogError(string message)
        {
            Write("ERROR", message, "", "");
        }

        private static void Write(string level, string field1, string field2, string field3)
        {
            try
            {
                var line = new StringBuilder();
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                line.Append(';').Append(level);
                line.Append(';').Append(Sanitize(field1));
                line.Append(';').Append(Sanitize(field2));
                line.Append(';').Append(Sanitize(field3));

                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, line.ToString() + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never throw into the caller (auto-restore path runs on a timer thread).
            }
        }

        private static string Sanitize(string? s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',');
    }
}
