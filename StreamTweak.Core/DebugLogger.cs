using System;
using System.IO;
using System.Text;

namespace StreamTweak
{
    /// <summary>
    /// Shared debug logger — writes timestamped entries to %LocalAppData%\StreamTweak\debug.log.
    /// All classes that previously had their own private DebugLog method use this instead.
    ///
    /// Two levels, deliberately no more:
    ///   Log(...)     — events worth keeping: state changes, errors, rejected commands.
    ///   Verbose(...) — routine per-tick / per-packet chatter (process scan, bridge traffic,
    ///                  log rediscovery), suppressed unless <see cref="VerboseEnabled"/>.
    ///
    /// The file is rolled at <see cref="MaxBytes"/> keeping a single .1 generation, so the log
    /// is bounded at ~10 MB regardless of uptime.
    ///
    /// Writes deliberately still go through File.AppendAllText rather than a long-lived
    /// StreamWriter: holding the file open for writing makes every reader that doesn't ask for
    /// FileShare.ReadWrite — File.ReadAllText, new StreamReader(path), most scripts — fail with
    /// "used by another process", and this log exists to be read. The open/close cost only
    /// mattered because of the ~99% of lines that are now suppressed.
    /// </summary>
    public static class DebugLogger
    {
        private const long MaxBytes = 5 * 1024 * 1024;

        // Bytes appended between two real size checks. Keeps the roll test off the per-line
        // path without letting the file overshoot the cap by more than this.
        private const long SizeCheckInterval = 32 * 1024;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "debug.log");

        private static readonly string PreviousLogPath = LogPath + ".1";

        private static readonly object _logLock = new();

        // Explicit: File.AppendAllText's default encoding would prepend a BOM to a new file,
        // and existing debug.log files have none.
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static bool _directoryChecked;
        private static long _bytesSinceSizeCheck;
        private static bool _sizeEverChecked; // forces one real stat on the first write

        // Set when a roll fails (log open in an external viewer, AV scan, …). Retrying the
        // File.Move on every subsequent line would turn one transient lock into a per-line
        // file operation, so back off and let it heal on its own.
        private static DateTime _rollSuppressedUntilUtc = DateTime.MinValue;

        /// <summary>
        /// Enables <see cref="Verbose"/> output. Off by default — verbose covers the
        /// high-frequency paths that historically accounted for ~99% of the file.
        /// </summary>
        public static bool VerboseEnabled { get; set; }

        /// <summary>Logs an event. Always written.</summary>
        public static void Log(string message) => Write(message);

        /// <summary>Logs routine high-frequency detail. Written only when <see cref="VerboseEnabled"/>.</summary>
        public static void Verbose(string message)
        {
            if (VerboseEnabled) Write(message);
        }

        private static void Write(string message)
        {
            try
            {
                lock (_logLock)
                {
                    if (!_directoryChecked)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                        _directoryChecked = true;
                    }

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}"
                                + Environment.NewLine;

                    RollIfNeeded(line.Length);
                    File.AppendAllText(LogPath, line, Utf8NoBom);
                    _bytesSinceSizeCheck += line.Length;
                }
            }
            catch { }
        }

        /// <summary>Stats the log at most once per <see cref="SizeCheckInterval"/> appended bytes
        /// and rolls it when it reaches the cap. Caller must hold <see cref="_logLock"/>.</summary>
        private static void RollIfNeeded(int pendingBytes)
        {
            // Note: no sentinel arithmetic here. A "force the first check" sentinel of
            // long.MaxValue overflows on the += and silently disables rotation for good.
            if (_sizeEverChecked && _bytesSinceSizeCheck + pendingBytes < SizeCheckInterval) return;

            _sizeEverChecked = true;
            _bytesSinceSizeCheck = 0;

            if (DateTime.UtcNow < _rollSuppressedUntilUtc) return;

            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length + pendingBytes >= MaxBytes && !TryRoll())
                    _rollSuppressedUntilUtc = DateTime.UtcNow.AddMinutes(5);
            }
            catch { }
        }

        /// <summary>Moves debug.log to debug.log.1, discarding the previous generation.
        /// Caller must hold <see cref="_logLock"/>.</summary>
        private static bool TryRoll()
        {
            try
            {
                if (File.Exists(PreviousLogPath)) File.Delete(PreviousLogPath);
                File.Move(LogPath, PreviousLogPath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
