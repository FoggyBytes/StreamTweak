using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    public class StreamingLogMonitor : IDisposable
    {
        private StreamReader? logStreamReader;
        private string? currentLogFilePath;
        private string? monitoredDirectory;
        private Task? monitoringTask;
        private CancellationTokenSource? cancellationTokenSource;
        private bool isDisposed = false;
        // Tracks whether we've seen a StreamStarted event since the last StreamStopped.
        // Prevents false StreamStopped handling when no corresponding start was observed.
        private bool seenStreamStarted = false;

        // How often to re-run the full discovery to catch dynamic logs
        // that appear after startup (e.g. Vibepollo creating logs\ after StreamTweak starts)
        private const int REDISCOVERY_INTERVAL_MS = 10000;
        private DateTime lastRediscoveryTime = DateTime.MinValue;

        // Write time of monitoredDirectory as of the last rotation scan. A new file in the
        // directory moves it; appends to the log we are already reading do not. See
        // CheckForLogRotation().
        private DateTime lastDirWriteTimeUtc = DateTime.MinValue;

        public event EventHandler<StreamingEventArgs>? StreamingEventDetected;

        /// <summary>
        /// Raised with the full path of the executable the streaming server launched, parsed
        /// from the server log's `Info: Executing: ["…"]` line. This is the authoritative game
        /// binary — more reliable than process scanning for launcher→game handoffs (Ubisoft/EA/…).
        /// </summary>
        public event Action<string>? GameLaunchDetected;

        /// <summary>
        /// Raised for every app the server starts, including the ones
        /// <see cref="GameLaunchDetected"/> deliberately ignores: protocol commands
        /// (<c>steam://rungameid/…</c>) and the Desktop entry, which runs nothing at all.
        /// Feeds <see cref="LaunchWatcher"/>, which needs to know about all three cases —
        /// a launch it cannot classify has to be reported as such, not silently dropped.
        /// </summary>
        public event Action<AppLaunchInfo>? AppLaunchDetected;

        /// <summary>
        /// Extracts the launched exe path from a Sunshine/Apollo `Info: Executing: ["path"] in […]`
        /// line. Returns null for prep commands (`Executing Do Cmd: [...]`) and non-exe cmds
        /// (e.g. `steam://…`). Matches on the `Executing: ["` prefix so `Do Cmd:` is excluded.
        /// </summary>
        private static string? TryParseLaunchedExecutable(string line)
        {
            string? cmd = TryParseLaunchedCommand(line);
            return cmd != null && cmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cmd : null;
        }

        /// <summary>
        /// Extracts the raw command from the same line, whatever it is — an executable path or a
        /// protocol URL. Same `Executing: ["` prefix, so `Executing Do Cmd:` stays excluded.
        /// </summary>
        private static string? TryParseLaunchedCommand(string line)
        {
            const string marker = "Executing: [\"";
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + marker.Length;
            int end = line.IndexOf('"', start);
            if (end <= start) return null;
            return line.Substring(start, end - start);
        }

        /// <summary>
        /// Extracts the command from a <c>Spawning [cmd] in [dir]</c> line — the server's
        /// <i>detached</i> launch path, and the only one Steam and Xbox titles ever take.
        /// <para>This matters more than it looks: StreamTweak writes Steam apps with an empty
        /// <c>cmd</c> and <c>detached: ["steam://rungameid/…"]</c>, and Xbox ones with
        /// <c>detached: ["explorer.exe shell:appsFolder\…"]</c>. Those launches never produce an
        /// <c>Executing: ["…"]</c> line at all, so anything watching only that line is blind to
        /// the two largest stores.</para>
        /// Format verified identical in all four supported servers' <c>process.cpp</c>.
        /// </summary>
        private static string? TryParseSpawnedCommand(string line)
        {
            const string marker = "Spawning [";
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + marker.Length;
            int end = line.IndexOf("] in [", start, StringComparison.Ordinal);
            if (end <= start) return null;
            string cmd = line.Substring(start, end - start).Trim();
            return cmd.Length > 0 ? cmd : null;
        }

        /// <summary>
        /// True for the line a server writes when the launched app has no direct command.
        /// <b>The wording differs between forks and both must be matched:</b> Sunshine,
        /// Vibeshine and Vibepollo write <c>Executing [Desktop]</c>, while Apollo writes
        /// <c>No commands configured, showing desktop...</c>. Verified in each fork's
        /// <c>process.cpp</c> — the same class of mistake that once made a launcher's
        /// "App exited with code" look like a game exit.
        /// <para><b>It does not mean "Desktop" on its own.</b> The server reaches it whenever
        /// <c>cmd</c> is empty, which is also true of every Steam and Xbox app — those run from
        /// <c>detached</c> and log a <c>Spawning</c> line microseconds earlier. Only a Desktop
        /// line with no launch line just before it is really the Desktop entry.</para>
        /// </summary>
        private static bool IsDesktopLaunchLine(string line) =>
            line.IndexOf("Executing [Desktop]", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("No commands configured", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// How recently a launch line must have been seen for a following "no command" line to be
        /// discounted. The two are written in the same block, microseconds apart; three seconds is
        /// generous enough for a busy log and short enough that a genuine Desktop session started
        /// right after a game is still recognised.
        /// </summary>
        private const double DESKTOP_LINE_SUPPRESSION_SEC = 3.0;
        private DateTime lastLaunchLineUtc = DateTime.MinValue;

        /// <summary>
        /// The uuid of the session the server most recently declared open, on the two forks that
        /// declare one (Vibeshine, Vibepollo — <c>session_history: begin_session uuid=…</c>).
        /// Null on Sunshine and Apollo, and until the first such line is read.
        /// </summary>
        private string? openHistoryUuid;

        /// <summary>Extracts the <c>uuid=…</c> of a session_history line, or null.</summary>
        private static string? TryParseHistoryUuid(string line)
        {
            const string marker = "uuid=";
            int i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            int end = i;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '-')) end++;
            return end > i ? line.Substring(i, end - i) : null;
        }

        private static bool IsHistoryBegin(string line) =>
            line.IndexOf("session_history: begin_session", StringComparison.Ordinal) >= 0;

        private static bool IsHistoryEnd(string line) =>
            line.IndexOf("session_history: end_session", StringComparison.Ordinal) >= 0;

        public class StreamingEventArgs : EventArgs
        {
            public LogParser.StreamingEvent Event { get; set; }
            // True when the event was inferred from log history at startup (session already active).
            // Consumers can use this to skip actions that would disrupt an in-progress stream
            // (e.g. NIC renegotiation).
            public bool IsRetrospective { get; set; }
        }

        public void StartMonitoring()
        {
            if (isDisposed)
                throw new ObjectDisposedException(nameof(StreamingLogMonitor));

            currentLogFilePath = LogParser.FindStreamingServiceLogFile();

            if (!string.IsNullOrEmpty(currentLogFilePath))
            {
                monitoredDirectory  = Path.GetDirectoryName(currentLogFilePath);
                lastDirWriteTimeUtc = DateTime.MinValue;   // force the first rotation scan
                DebugLog($"Starting log monitoring in directory: {monitoredDirectory}");
                DebugLog($"Initial log file: {Path.GetFileName(currentLogFilePath)}");
            }
            else
            {
                DebugLog("No log file found at startup — will keep retrying via rediscovery");
            }

            // Primary: is the streaming server holding the UDP sockets a session runs on? No log
            // parsing, instantaneous, and true for any Moonlight-compatible client rather than
            // just StreamLight. (Until 8.3.0 this asked about a TCP connection on 48010 instead,
            // which a live session does not hold — see LogParser.HasActiveMoonlightSession. The
            // consequence was that this branch never once fired and the scan below did all the
            // work, unassisted.)
            if (LogParser.HasActiveStreamSockets())
            {
                DebugLog("A session is already running at startup (stream sockets are open)");
                FireRetrospectiveStarted();
            }
            else if (!string.IsNullOrEmpty(currentLogFilePath))
            {
                // Fallback: log file scan, for the moment where the sockets exist but this one
                // reading missed them. Both of its phases confirm with the sockets before they
                // revive anything.
                CheckForExistingSession(currentLogFilePath);
            }

            try
            {
                cancellationTokenSource = new CancellationTokenSource();
                monitoringTask = MonitorLogFileAsync(cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                DebugLog($"ERROR starting monitoring: {ex.Message}");
            }
        }

        public void StopMonitoring()
        {
            try
            {
                cancellationTokenSource?.Cancel();
                logStreamReader?.Dispose();
                logStreamReader = null;
            }
            catch { }
        }

        private async Task MonitorLogFileAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!string.IsNullOrEmpty(currentLogFilePath) && File.Exists(currentLogFilePath))
                    OpenStreamReader();

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Periodic full rediscovery — catches dynamic logs that appear after startup
                    await CheckForRediscoveryAsync(cancellationToken);

                    if (logStreamReader == null)
                    {
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }

                    string? line = await logStreamReader.ReadLineAsync();

                    if (line != null)
                    {
                        // Capture the launched game binary (authoritative game source).
                        string? launchedExe = TryParseLaunchedExecutable(line);
                        if (launchedExe != null)
                        {
                            DebugLog($"Game launch detected in log: {launchedExe}");
                            GameLaunchDetected?.Invoke(launchedExe);
                        }

                        // Wider net for the launch curtain: it also needs the launches the line
                        // above skips — protocol commands (Steam, Xbox) and the Desktop entry.
                        string? launchedCmd = TryParseLaunchedCommand(line) ?? TryParseSpawnedCommand(line);
                        if (launchedCmd != null)
                        {
                            lastLaunchLineUtc = DateTime.UtcNow;
                            DebugLog($"App launch detected in log: {launchedCmd}");
                            AppLaunchDetected?.Invoke(new AppLaunchInfo
                            {
                                Command = launchedCmd,
                                IsExecutable = launchedCmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            });
                        }
                        else if (IsDesktopLaunchLine(line))
                        {
                            // Only a real Desktop session if nothing was launched a moment ago —
                            // Steam and Xbox reach this same line right after their Spawning one.
                            if ((DateTime.UtcNow - lastLaunchLineUtc).TotalSeconds >= DESKTOP_LINE_SUPPRESSION_SEC)
                            {
                                DebugLog("Desktop session detected in log (no command to run)");
                                AppLaunchDetected?.Invoke(new AppLaunchInfo { IsDesktop = true });
                            }
                        }

                        LogParser.StreamingEvent streamingEvent = LogParser.ParseLogLine(line);

                        if (streamingEvent != LogParser.StreamingEvent.None)
                        {
                            // Basic state machine: only treat StreamStopped if we've previously
                            // seen a StreamStarted. This reduces false positives from stray
                            // log lines or rotation artifacts.
                            if (streamingEvent == LogParser.StreamingEvent.StreamStarted)
                            {
                                if (IsHistoryBegin(line))
                                {
                                    openHistoryUuid = TryParseHistoryUuid(line);
                                    DebugLog($"Server declared session {openHistoryUuid ?? "(no uuid)"} open");
                                }

                                seenStreamStarted = true;
                                DebugLog($"Event raised: {streamingEvent}");
                                StreamingEventDetected?.Invoke(this, new StreamingEventArgs { Event = streamingEvent });
                            }
                            else if (streamingEvent == LogParser.StreamingEvent.StreamStopped)
                            {
                                // The pairing the uuid buys us: an end that belongs to a different
                                // session than the one running is not this session's end. Without
                                // it a stale line — the tail re-read at every startup routinely
                                // hands us the previous session's "Session ended" — can close a
                                // session that is actually live. Only Vibeshine and Vibepollo
                                // declare uuids; elsewhere openHistoryUuid stays null and this
                                // check stands aside.
                                if (IsHistoryEnd(line) && openHistoryUuid != null)
                                {
                                    string? ending = TryParseHistoryUuid(line);
                                    if (ending != null && ending != openHistoryUuid)
                                    {
                                        DebugLog($"Ignored end_session for {ending}: the open session is {openHistoryUuid}");
                                        continue;
                                    }
                                    openHistoryUuid = null;
                                }

                                if (seenStreamStarted)
                                {
                                    seenStreamStarted = false;
                                    DebugLog($"Event raised: {streamingEvent}");
                                    StreamingEventDetected?.Invoke(this, new StreamingEventArgs { Event = streamingEvent });
                                }
                                else
                                {
                                    DebugLog($"Ignored StreamStopped (no prior StreamStarted observed): {line}");
                                }
                            }
                        }
                    }
                    else
                    {
                        // No new lines — check for rotation within current directory, then wait
                        CheckForLogRotation();
                        await Task.Delay(100, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("Monitoring cancelled");
            }
            catch (Exception ex)
            {
                DebugLog($"ERROR in monitoring loop: {ex.Message}");
            }
        }

        /// <summary>
        /// Every REDISCOVERY_INTERVAL_MS, re-runs the full FindStreamingServiceLogFile() discovery.
        /// This handles the case where a dynamic log file (e.g. Vibepollo logs\sunshine-*.log)
        /// appears after StreamTweak has already started monitoring a static fallback file.
        /// If a better or different log is found, switches to it.
        /// </summary>
        private async Task CheckForRediscoveryAsync(CancellationToken cancellationToken)
        {
            if ((DateTime.Now - lastRediscoveryTime).TotalMilliseconds < REDISCOVERY_INTERVAL_MS)
                return;

            lastRediscoveryTime = DateTime.Now;

            try
            {
                string? discovered = LogParser.FindStreamingServiceLogFile();

                if (string.IsNullOrEmpty(discovered)) return;

                // Switch if: no file monitored yet, or a different (better) file found
                if (string.IsNullOrEmpty(currentLogFilePath) ||
                    !string.Equals(discovered, currentLogFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog($"Rediscovery: switching from '{Path.GetFileName(currentLogFilePath ?? "none")}' to '{Path.GetFileName(discovered)}'");
                    currentLogFilePath  = discovered;
                    monitoredDirectory  = Path.GetDirectoryName(discovered);
                    lastDirWriteTimeUtc = DateTime.MinValue;   // force the first rotation scan
                    OpenStreamReader(discovered);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error during rediscovery: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a newer dynamic log file has appeared in the same directory (log rotation).
        /// Only relevant for directories that contain sunshine-*.log files.
        ///
        /// Runs on every idle pass of the monitoring loop — ten times a second — because
        /// <see cref="OpenStreamReader"/> seeks to the end of the file it opens: any line
        /// written to the new log before we notice it is lost, so the cadence must not be
        /// relaxed.
        ///
        /// Instead the check itself was made cheap (issue #7). A rotation means a *new file* in
        /// the directory, which bumps the directory's own write time; appends to the log already
        /// being read do not. So the steady state is one stat of the directory, and the actual
        /// scan only runs on the pass where something appeared.
        /// </summary>
        private void CheckForLogRotation()
        {
            if (string.IsNullOrEmpty(monitoredDirectory)) return;

            try
            {
                DateTime dirStamp;
                try { dirStamp = Directory.GetLastWriteTimeUtc(monitoredDirectory); }
                catch { dirStamp = DateTime.MinValue; }

                if (dirStamp != DateTime.MinValue && dirStamp == lastDirWriteTimeUtc) return;
                lastDirWriteTimeUtc = dirStamp;

                string? latestLog = FindMostRecentLogFileInDir(monitoredDirectory);

                // Enumeration failed (transient sharing/permission error). Forget the stamp
                // so the next pass scans again instead of waiting for another directory
                // change that may never come.
                if (latestLog == null) lastDirWriteTimeUtc = DateTime.MinValue;

                if (latestLog != null &&
                    !string.Equals(latestLog, currentLogFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog($"Log rotation: switching from '{Path.GetFileName(currentLogFilePath)}' to '{Path.GetFileName(latestLog)}'");
                    currentLogFilePath = latestLog;
                    OpenStreamReader(latestLog);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error during rotation check: {ex.Message}");
            }
        }

        private string? FindMostRecentLogFileInDir(string directory)
        {
            try
            {
                // EnumerateFiles yields FileInfo objects whose timestamps come from the
                // directory enumeration itself — no stat() per file, unlike GetFiles()
                // followed by File.GetLastWriteTime() on every path.
                FileInfo? newest = null;
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("sunshine-*.log"))
                    if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                        newest = file;

                return newest?.FullName;
            }
            catch { return null; }
        }

        // FileStream intentionally NOT in a using block — it must outlive the StreamReader.
        // The StreamReader disposes the FileStream when it is itself disposed (leaveOpen=false default).
        private void OpenStreamReader(string? filePath = null)
        {
            try { logStreamReader?.Dispose(); } catch { }
            logStreamReader = null;

            string targetPath = filePath ?? currentLogFilePath ?? string.Empty;
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath)) return;

            try
            {
                var fileStream = new FileStream(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                try
                {
                    fileStream.Seek(0, SeekOrigin.End);
                    logStreamReader = new StreamReader(fileStream);
                    DebugLog($"StreamReader opened on: {Path.GetFileName(targetPath)}");
                }
                catch
                {
                    // If Seek or StreamReader construction fails, dispose the FileStream
                    // explicitly so the file handle is not leaked.
                    fileStream.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"ERROR opening stream reader: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the log file and fires a retrospective StreamStarted event when the most recent
        /// streaming event is "started" <b>and</b> the server is holding the stream's UDP sockets.
        /// Called once at startup, after the direct socket check, before the monitoring loop.
        /// <para>Both halves are load-bearing and neither is sufficient: the log alone revives
        /// sessions that ended without their end being written (issue #9), and the sockets alone
        /// would miss a session whose sockets this one reading happened not to catch.</para>
        /// </summary>
        private void CheckForExistingSession(string logFilePath)
        {
            try
            {
                // Phase 1: scan the tail backwards for the most recent streaming event.
                // Covers short/recent sessions and static single-file logs (Sunshine/Apollo).
                string[] tailLines = ReadTailLines(logFilePath, 300);
                for (int i = tailLines.Length - 1; i >= 0; i--)
                {
                    LogParser.StreamingEvent ev = LogParser.ParseLogLine(tailLines[i]);
                    if (ev == LogParser.StreamingEvent.StreamStarted)
                    {
                        // A start line in the tail says a session *was* opened, never that it is
                        // still up: the line survives in the log until the server rotates it, so
                        // on its own it revives the same dead session at every launch — which is
                        // how the phantom of issue #9 kept coming back from a restart.
                        //
                        // ⚠️ The check has to be the UDP one. Gating this on the TCP probe was
                        // tried and reverted the same day: that probe never sees a live session,
                        // so it would have stopped a StreamTweak restarted mid-session from ever
                        // picking the session back up. See LogParser for both measurements.
                        if (!StreamSocketsPresent())
                        {
                            DebugLog("Tail shows a session start, but the server holds no stream sockets — not reviving it");
                            return;
                        }

                        DebugLog("Active session detected in tail at startup — raising StreamStarted retroactively");
                        FireRetrospectiveStarted();
                        return;
                    }
                    if (ev == LogParser.StreamingEvent.StreamStopped)
                    {
                        DebugLog("No active session at startup (StreamStopped found in tail)");
                        return;
                    }
                }

                // Phase 2: no events found in the tail.
                // For per-session log files (Vibeshine/Vibepollo style), a long-running session
                // produces enough verbose output to push the initial CLIENT CONNECTED line outside
                // the tail window. Check the file head for a StreamStarted event.
                //
                // IMPORTANT: the socket check is not optional here. A stale log file containing a
                // StreamStarted from a long-past session — whose StreamStopped was written between
                // the tail window and the head — would otherwise raise a phantom retrospective
                // session at every single startup.
                string[] headLines = ReadHeadLines(logFilePath, 200);
                bool headHasStart = headLines.Any(l =>
                    LogParser.ParseLogLine(l) == LogParser.StreamingEvent.StreamStarted);
                if (headHasStart && StreamSocketsPresent())
                {
                    DebugLog("Active session detected in file head at startup (long session, sockets confirm) — raising StreamStarted retroactively");
                    FireRetrospectiveStarted();
                    return;
                }

                DebugLog("No streaming events found — assuming no active session at startup");
            }
            catch (Exception ex)
            {
                DebugLog($"CheckForExistingSession error: {ex.Message}");
            }
        }

        /// <summary>
        /// The startup form of <see cref="LogParser.HasActiveStreamSockets"/>: reads twice, a
        /// second apart, and takes yes for an answer. The watchdog can afford to be patient — it
        /// wants three refusals in a row — but this runs once, at launch, and a single unlucky
        /// reading (a stream restarting as StreamTweak starts) would lose a live session for the
        /// rest of it. Costs one extra second, and only on the path where the log says a session
        /// might be open.
        /// </summary>
        private static bool StreamSocketsPresent()
        {
            if (LogParser.HasActiveStreamSockets()) return true;
            Thread.Sleep(1000);
            return LogParser.HasActiveStreamSockets();
        }

        private void FireRetrospectiveStarted()
        {
            seenStreamStarted = true;
            StreamingEventDetected?.Invoke(this, new StreamingEventArgs
            {
                Event = LogParser.StreamingEvent.StreamStarted,
                IsRetrospective = true
            });
        }

        /// <summary>
        /// Returns the last <paramref name="lineCount"/> lines of a file using a shared read handle.
        /// Reads at most lineCount × 200 bytes from the end to avoid loading large log files entirely.
        /// </summary>
        private static string[] ReadHeadLines(string filePath, int lineCount)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var lines = new System.Collections.Generic.List<string>(lineCount);
                string? line;
                while (lines.Count < lineCount && (line = reader.ReadLine()) != null)
                    lines.Add(line);
                return lines.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static string[] ReadTailLines(string filePath, int lineCount)
        {
            const long bytesPerLine = 200;
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bool partial = false;
                long seek = lineCount * bytesPerLine;
                if (fs.Length > seek)
                {
                    fs.Seek(-seek, SeekOrigin.End);
                    partial = true;
                }

                using var reader = new StreamReader(fs);
                if (partial) reader.ReadLine(); // discard possible partial first line

                var lines = new List<string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                    lines.Add(line);

                if (lines.Count <= lineCount) return lines.ToArray();
                return lines.GetRange(lines.Count - lineCount, lineCount).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            StopMonitoring();
            cancellationTokenSource?.Dispose();
            isDisposed = true;
        }

        private static void DebugLog(string message) => DebugLogger.Log(message);
    }
}