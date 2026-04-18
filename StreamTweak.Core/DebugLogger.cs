using System;
using System.IO;

namespace StreamTweak
{
    /// <summary>
    /// Shared debug logger — writes timestamped entries to %LocalAppData%\StreamTweak\debug.log.
    /// All classes that previously had their own private DebugLog method use this instead.
    /// </summary>
    public static class DebugLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "debug.log");

        private static readonly object _logLock = new();

        public static void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
