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

        public event EventHandler<StreamingEventArgs>? StreamingEventDetected;

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

            if (string.IsNullOrEmpty(currentLogFilePath))
            {
                DebugLog("No log file found at startup — will keep retrying via rediscovery");
            }
            else
            {
                monitoredDirectory = Path.GetDirectoryName(currentLogFilePath);
                DebugLog($"Starting log monitoring in directory: {monitoredDirectory}");
                DebugLog($"Initial log file: {Path.GetFileName(currentLogFilePath)}");
                // Check if a streaming session was already active before StreamTweak started.
                // This handles the case where StreamTweak is launched mid-session (e.g. after
                // auto-login or a crash/restart) and would otherwise miss the session-start event.
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
                        LogParser.StreamingEvent streamingEvent = LogParser.ParseLogLine(line);

                        if (streamingEvent != LogParser.StreamingEvent.None)
                        {
                            // Basic state machine: only treat StreamStopped if we've previously
                            // seen a StreamStarted. This reduces false positives from stray
                            // log lines or rotation artifacts.
                            if (streamingEvent == LogParser.StreamingEvent.StreamStarted)
                            {
                                seenStreamStarted = true;
                                DebugLog($"Event raised: {streamingEvent}");
                                StreamingEventDetected?.Invoke(this, new StreamingEventArgs { Event = streamingEvent });
                            }
                            else if (streamingEvent == LogParser.StreamingEvent.StreamStopped)
                            {
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
                    currentLogFilePath = discovered;
                    monitoredDirectory = Path.GetDirectoryName(discovered);
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
        /// </summary>
        private void CheckForLogRotation()
        {
            if (string.IsNullOrEmpty(monitoredDirectory)) return;

            try
            {
                string? latestLog = FindMostRecentLogFileInDir(monitoredDirectory);

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
            if (!Directory.Exists(directory)) return null;
            try
            {
                return Directory.GetFiles(directory, "sunshine-*.log")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();
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
        /// Reads the tail of the log file and fires a retrospective StreamStarted event if the
        /// most recent streaming event found is "started" (meaning the session is already active).
        /// Called once at startup before the real-time monitoring loop begins.
        /// </summary>
        private void CheckForExistingSession(string logFilePath)
        {
            try
            {
                string[] lines = ReadTailLines(logFilePath, 300);

                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    LogParser.StreamingEvent ev = LogParser.ParseLogLine(lines[i]);

                    if (ev == LogParser.StreamingEvent.StreamStarted)
                    {
                        DebugLog("Active session detected at startup — raising StreamStarted retroactively");
                        seenStreamStarted = true;
                        StreamingEventDetected?.Invoke(this, new StreamingEventArgs
                        {
                            Event = LogParser.StreamingEvent.StreamStarted,
                            IsRetrospective = true
                        });
                        return;
                    }

                    if (ev == LogParser.StreamingEvent.StreamStopped)
                    {
                        DebugLog("No active session at startup (last event was StreamStopped)");
                        return;
                    }
                }

                DebugLog("No streaming events in log tail — assuming no active session at startup");
            }
            catch (Exception ex)
            {
                DebugLog($"CheckForExistingSession error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the last <paramref name="lineCount"/> lines of a file using a shared read handle.
        /// Reads at most lineCount × 200 bytes from the end to avoid loading large log files entirely.
        /// </summary>
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