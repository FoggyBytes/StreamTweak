using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;
using StreamTweak.Services;

namespace StreamTweak
{
    // Streaming-session lifecycle: telemetry finalize/checkpoint, the auto-streaming log
    // monitor, manual/auto session start & stop, the debug session, and the inactivity
    // timer. Split out of App.xaml.cs; operates on the session fields declared there.
    public partial class App
    {
        // ── Session telemetry ────────────────────────────────────────────────

        private void FinalizeSessionTelemetry()
        {
            try
            {
                string? sid = SessionLogger.ActiveSessionId;
                if (sid == null) return;
                var (stats, rtt, drops, bitrate, decode, hostLat) = _telemetryAccumulator.Finalize();
                var (hostGpu, hostEnc, hostCpu) = _telemetryAccumulator.GetHostSeries();
                if (stats.SampleCount >= 2)
                {
                    var grade = QualityGradeCalculator.Evaluate(stats, _telemetryAccumulator.TargetFps);
                    SessionLogger.UpdateSessionTelemetry(sid, stats, grade, rtt, drops, bitrate, decode, hostLat,
                        hostGpu, hostEnc, hostCpu);
                }
                _telemetryAccumulator.Reset();
            }
            catch (Exception ex) { DebugLogger.Log($"[Session] FinalizeSessionTelemetry failed: {ex}"); }
        }

        // ── Periodic telemetry checkpoint ────────────────────────────────────

        /// <summary>
        /// Starts a timer that writes the checkpoint every 30 s.
        /// Call immediately after <see cref="SessionLogger.StartSession"/>.
        /// </summary>
        private void StartCheckpointTimer()
        {
            string? sessionId = SessionLogger.ActiveSessionId;
            if (sessionId == null) return;
            _checkpointTimer?.Dispose();
            _checkpointTimer = new System.Threading.Timer(
                _ => WriteCheckpoint(sessionId),
                state: null,
                dueTime:  TimeSpan.FromSeconds(30),
                period:   TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// Serializes the current accumulator snapshot to disk atomically.
        /// Called from the timer's thread pool — does not touch the UI thread.
        /// </summary>
        private void WriteCheckpoint(string sessionId)
        {
            try
            {
                var (stats, rtt, drops, bitrate, decode, hostLat) = _telemetryAccumulator.Finalize();
                var (hostGpu, hostEnc, hostCpu) = _telemetryAccumulator.GetHostSeries();

                // Snapshot detected games BEFORE the SampleCount guard.
                // A retrospective session (StreamTweak started mid-stream with no prior
                // telemetry) or a desktop session (no StreamLight connected) may have
                // SampleCount < 2 but still have games detected by the process monitor.
                // Previously the early return on SampleCount < 2 also skipped this
                // snapshot, so if the host was shut down the checkpoint was never written
                // and Initialize() would recover the session with no game data.
                var detectedGames = _sessionProcessMonitor?.GetDetectedGames();

                bool hasTelemetry = stats.SampleCount >= 2;
                bool hasGames     = detectedGames is { Count: > 0 };

                // Nothing useful to persist — skip this tick.
                if (!hasTelemetry && !hasGames) return;

                var grade = hasTelemetry
                    ? QualityGradeCalculator.Evaluate(stats, _telemetryAccumulator.TargetFps)
                    : QualityGrade.NoData;

                var cp = new TelemetryCheckpoint
                {
                    SessionId     = sessionId,
                    Timestamp     = DateTime.Now,
                    Stats         = stats,      // SampleCount may be 0; Initialize() checks before using
                    Grade         = (int)grade,
                    RttSeries     = rtt,
                    DropsSeries   = drops,
                    BitrateSeries = bitrate,
                    DecodeSeries  = decode,
                    HostLatencySeries = hostLat,
                    HostGpuSeries = hostGpu,
                    HostEncSeries = hostEnc,
                    HostCpuSeries = hostCpu,
                    GamesDetected = hasGames ? detectedGames : null,
                };

                // Scrittura atomica: .tmp → File.Move overwrite per evitare file corrotti.
                string tmp = SessionLogger.CheckpointPath + ".tmp";
                System.IO.File.WriteAllText(tmp,
                    System.Text.Json.JsonSerializer.Serialize(cp));
                System.IO.File.Move(tmp, SessionLogger.CheckpointPath, overwrite: true);
            }
            catch (Exception ex) { DebugLogger.Log($"[Session] WriteCheckpoint failed: {ex}"); }
        }

        /// <summary>
        /// Stops the timer and deletes the checkpoint file.
        /// Call after <see cref="FinalizeSessionTelemetry"/> + <see cref="SessionLogger.EndSession"/>
        /// to prevent a stale checkpoint from being read on the next startup.
        /// </summary>
        private void StopCheckpointTimer()
        {
            _checkpointTimer?.Dispose();
            _checkpointTimer = null;
            try { System.IO.File.Delete(SessionLogger.CheckpointPath); } catch { }
        }

        // ── Auto streaming monitor (log watcher) ─────────────────────────────

        private void StartAutoStreamingMonitor()
        {
            if (_logMonitor != null) return;
            if (!_isAutoStreamingEnabled && !_isAudioMonitorEnabled) return;
            try
            {
                _logMonitor = new StreamingLogMonitor();
                _logMonitor.StreamingEventDetected += LogMonitor_StreamingEventDetected;
                _logMonitor.StartMonitoring();
            }
            catch { }
        }

        private void StopAutoStreamingMonitor()
        {
            if (_isAutoStreamingEnabled || _isAudioMonitorEnabled) return;
            // Never kill the log monitor while a session is in progress.
            // If the user disables both Auto Streaming and Auto Spatial Audio mid-session,
            // the monitor must keep running so CLIENT DISCONNECTED can be detected.
            // HandleAutoStreamStop will call StopAutoStreamingMonitor again after
            // _isAutoSessionActive becomes false, giving it a clean opportunity to stop.
            if (_isAutoSessionActive) return;
            StopLogMonitorForced();
        }

        private void StopLogMonitorForced()
        {
            try
            {
                if (_logMonitor == null) return;
                _logMonitor.StreamingEventDetected -= LogMonitor_StreamingEventDetected;
                _logMonitor.StopMonitoring();
                _logMonitor.Dispose();
                _logMonitor = null;
            }
            catch { }
        }

        private void LogMonitor_StreamingEventDetected(object? sender, StreamingLogMonitor.StreamingEventArgs e)
        {
            try
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (e.Event == LogParser.StreamingEvent.StreamStarted)
                    {
                        _dolbyMonitor.OnStreamingStarted(e.IsRetrospective);
                        // Allow session tracking to start even when NIC is already throttled
                        // by manual streaming mode — just skip the NIC change in that case.
                        if (!_isAutoSessionActive && !_sessionStartInProgress)
                            _ = HandleAutoStreamStart(skipNicThrottle: e.IsRetrospective || _isAutoStreamingActive);
                        else
                            StopInactivityTimer(); // reconnected within grace period
                    }
                    else if (e.Event == LogParser.StreamingEvent.StreamStopped)
                    {
                        _dolbyMonitor.OnStreamingStopped();
                        if (_isAutoStreamingActive || _isAutoSessionActive)
                            StartInactivityTimer();
                    }
                });
            }
            catch { }
        }

        private async Task StartManualStreamingMode()
        {
            if (_isAutoStreamingActive || _sessionStartInProgress) return;
            _sessionStartInProgress = true;
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(_adapterName, StringComparison.OrdinalIgnoreCase));
                if (ni == null || ni.OperationalStatus != OperationalStatus.Up) return;

                long mbps = ni.Speed / 1_000_000;
                var speeds = NetworkManager.GetSupportedSpeeds(_adapterName);
                string capturedOriginalSpeed = string.Empty;
                foreach (var kvp in speeds)
                {
                    string kl = kvp.Key.ToLower();
                    bool match = mbps >= 2000
                        ? kl.Contains("2.5") || kl.Contains("2500")
                        : kl.Contains(mbps.ToString());
                    if (match) { capturedOriginalSpeed = kvp.Key; break; }
                }

                string? oneGbpsKey = Find1GbpsKey();
                if (oneGbpsKey == null) return;

                // Manual streaming mode: throttles NIC only.
                // Managed apps are NOT touched here — app kill/relaunch is driven
                // exclusively by the log monitor (auto session) or StreamLight bridge.
                // Session tracking (log, tray icon, home dot) starts only when the
                // streaming client actually connects — detected via the log monitor.
                _isAutoStreamingActive        = true;
                _originalSpeedForAutoStreaming = capturedOriginalSpeed;
                ConfigService.Set("StreamingMode", true);
                ConfigService.Set("OriginalSpeed", capturedOriginalSpeed);
                AppStateService.Instance.IsStreamingModeActive = true;
                if (_trayStreamingModeItem != null)
                    _trayStreamingModeItem.Text = "Stop Streaming Mode";

                await Task.Run(() => ApplySpeed(oneGbpsKey));
                NotificationService.ShowSpeedApplied(_adapterName, "1 Gbps");
                _ = PollForNicReconnectAsync();
            }
            catch (Exception ex) { DebugLogger.Log($"[Streaming] StartManualStreamingMode failed: {ex}"); }
            finally { _sessionStartInProgress = false; }
        }

        private async Task HandleAutoStreamStart(bool skipNicThrottle = false)
        {
            if (_sessionStartInProgress) return;
            _sessionStartInProgress = true;
            try
            {
                string capturedOriginalSpeed = string.Empty;

                if (!skipNicThrottle)
                {
                    // NIC throttle is attempted but never blocks session tracking.
                    // If the adapter is absent or down we skip only the speed-change block.
                    var ni = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.Name.Equals(_adapterName, StringComparison.OrdinalIgnoreCase));

                    if (ni != null && ni.OperationalStatus == OperationalStatus.Up)
                    {
                        long mbps = ni.Speed / 1_000_000;
                        if (_isAutoStreamingEnabled && mbps >= 1200)
                        {
                            var speeds = NetworkManager.GetSupportedSpeeds(_adapterName);
                            foreach (var kvp in speeds)
                            {
                                string kl = kvp.Key.ToLower();
                                bool match = mbps >= 2000
                                    ? kl.Contains("2.5") || kl.Contains("2500")
                                    : kl.Contains(mbps.ToString());
                                if (match) { capturedOriginalSpeed = kvp.Key; break; }
                            }

                            string? oneGbpsKey = Find1GbpsKey();
                            if (oneGbpsKey != null)
                            {
                                NotificationService.ShowStreamingDetected(_adapterName, "1 Gbps");

                                // Show topmost overlay: user sees feedback even when window is hidden.
                                _adjustmentAlert = new StreamingAdjustmentWindow();
                                _adjustmentAlert.Activate();

                                await Task.Delay(7900);

                                try { _adjustmentAlert?.Close(); } catch { }
                                _adjustmentAlert = null;

                                if (_isAutoStreamingEnabled)
                                {
                                    _isAutoStreamingActive = true;
                                    _originalSpeedForAutoStreaming = capturedOriginalSpeed;
                                    ConfigService.Set("StreamingMode", true);
                                    ConfigService.Set("OriginalSpeed", capturedOriginalSpeed);
                                    AppStateService.Instance.IsStreamingModeActive = true;

                                    await Task.Run(() => ApplySpeed(oneGbpsKey));
                                    NotificationService.ShowSpeedApplied(_adapterName, "1 Gbps");
                                    _ = PollForNicReconnectAsync();
                                }
                            }
                        }
                    }
                    // NIC unreachable or no speed change needed — still track session below.
                }
                // else: retrospective detection — session already in progress, skip NIC entirely.

                // Always track session and update UI regardless of NIC throttle outcome.
                _telemetryAccumulator.Reset();
                // Kill managed apps only if not already done by manual streaming mode.
                if (!_isAutoStreamingActive)
                    _appsToRelaunch = ManagedAppController.KillRunning();
                _isAutoSessionActive = true;
                AppStateService.Instance.IsSessionActive = true;
                UpdateTrayStreamingState(true);  // updates tray text + icon
                SessionLogger.StartSession(skipNicThrottle ? "Retrospective" : "Auto", capturedOriginalSpeed);
                StartCheckpointTimer();

                // Start process monitor to detect which games run during this session
                _sessionProcessMonitor?.Dispose();
                var games = GameLibraryState.Current.Games;
                _sessionProcessMonitor = new SessionProcessMonitor(games);
                _sessionProcessMonitor.Start();
            }
            catch (Exception ex) { DebugLogger.Log($"[Streaming] HandleAutoStreamStart failed: {ex}"); }
            finally { _sessionStartInProgress = false; }
        }

        private async Task HandleAutoStreamStop(string endReason = "User")
        {
            try
            {
                if (_isDebugModeActive) return;
                if (!_isAutoStreamingActive && !_isAutoSessionActive) return;
                StopInactivityTimer();

                if (_isAutoStreamingActive)
                {
                    if (!string.IsNullOrEmpty(_originalSpeedForAutoStreaming))
                    {
                        await Task.Run(() => ApplySpeed(_originalSpeedForAutoStreaming!));
                        _ = PollForNicReconnectAsync();
                    }

                    _isAutoStreamingActive = false;
                    _originalSpeedForAutoStreaming = null;
                    ConfigService.Set("StreamingMode", false);
                    ConfigService.Set("OriginalSpeed", "");
                    AppStateService.Instance.IsStreamingModeActive = false;
                    if (_trayStreamingModeItem != null)
                        _trayStreamingModeItem.Text = "Start Streaming Mode";
                    NotificationService.ShowStreamingEnded(endReason == "Disconnected");
                }

                if (_isAutoSessionActive)
                {
                    // Stop process monitor and collect detected games before ending session
                    List<string>? detectedGames = null;
                    if (_sessionProcessMonitor != null)
                    {
                        detectedGames = _sessionProcessMonitor.GetDetectedGames();
                        _sessionProcessMonitor.Dispose();
                        _sessionProcessMonitor = null;
                    }

                    FinalizeSessionTelemetry();
                    StopCheckpointTimer();
                    // Pass detectedGames even when empty: null = monitor never ran (pre-feature / manual mode)
                    //                                   []   = monitor ran but no games found (desktop session etc.)
                    //                                   [...] = games were detected
                    SessionLogger.EndSession(endReason, detectedGames);
                    _isAutoSessionActive = false;
                    AppStateService.Instance.IsSessionActive = false;
                    UpdateTrayStreamingState(false);
                    // If both Auto Streaming and Auto Spatial Audio are disabled, the
                    // StopAutoStreamingMonitor() call that ran mid-session returned early
                    // (guarded by _isAutoSessionActive). Now that the session is over,
                    // give it another chance to stop the monitor if appropriate.
                    StopAutoStreamingMonitor();
                }

                // Relaunch managed apps after any session end, regardless of whether
                // NIC throttling was active. Apps may have been killed by either
                // HandleAutoStreamStart (log-detected session) or StartManualStreamingMode.
                if (_appsToRelaunch.Count > 0)
                {
                    ManagedAppController.StartApps(_appsToRelaunch);
                    _appsToRelaunch.Clear();
                }
            }
            catch (Exception ex) { DebugLogger.Log($"[Streaming] HandleAutoStreamStop failed: {ex}"); }
        }

        // ── Debug mode ───────────────────────────────────────────────────────

        public async Task StartDebugSession()
        {
            if (_isDebugModeActive || _isAutoSessionActive || _sessionStartInProgress) return;
            _isDebugModeActive = true;
            AppStateService.Instance.IsDebugModeActive = true;
            _sessionStartInProgress = true;
            try
            {
                if (_isAudioMonitorEnabled)
                    _dolbyMonitor.OnStreamingStarted(isRetrospective: false);

                SessionLogger.StartSession("Debug", string.Empty);
                SessionLogger.MarkActiveSessionAsDebug();

                string? sid = SessionLogger.ActiveSessionId;
                if (sid == null) { _isDebugModeActive = false; AppStateService.Instance.IsDebugModeActive = false; return; }

                var fakeStats = new SessionQualityStats
                {
                    SampleCount     = 1800,
                    FpsAvg          = 60f,
                    FpsMin          = 58,
                    TotalDrops      = 3,
                    DropRatePct     = 0.003f,
                    RttAvgMs        = 8f,
                    RttMaxMs        = 18f,
                    JitterAvgMs     = 1.2f,
                    JitterMaxMs     = 4f,
                    DecodeAvgMs     = 2.1f,
                    BitrateAvgMbps  = 70f,
                    HostGpuAvg      = 42,
                    HostGpuPeak     = 61,
                    HostGpuEncAvg   = 35,
                    HostGpuEncPeak  = 48,
                    HostGpuTempAvg  = 61,
                    HostGpuTempMax  = 64,
                    HostCpuAvg      = 18,
                    HostCpuPeak     = 29,
                    HostNetTxAvg    = 72,
                    HostLatencyAvgMs = 6.4f,
                    HostLatencyMaxMs = 11.2f,
                };

                var rttSeries     = Enumerable.Range(0, 30).Select(i => 8f  + i % 3).ToList();
                var dropsSeries   = Enumerable.Range(0, 30).Select(i => i % 15 == 0 ? 1f : 0f).ToList();
                var bitrateSeries = Enumerable.Range(0, 30).Select(i => 68f + i % 5).ToList();
                var decodeSeries  = Enumerable.Range(0, 30).Select(i => 2f  + (i % 4) * 0.1f).ToList();
                var hostLatSeries = Enumerable.Range(0, 30).Select(i => 5f  + (i % 6) * 0.5f).ToList();
                var hostGpuSeries = Enumerable.Range(0, 30).Select(i => 70f + (i % 8) * 2f).ToList();
                var hostEncSeries = Enumerable.Range(0, 30).Select(i => 30f + (i % 5) * 3f).ToList();
                var hostCpuSeries = Enumerable.Range(0, 30).Select(i => 15f + (i % 7) * 2f).ToList();

                var fakeGames = GameLibraryState.Current.Games
                    .OrderBy(_ => Random.Shared.Next())
                    .Take(3)
                    .Select(g => g.Name)
                    .ToList();

                var gameMap    = GameLibraryState.Current.Games
                    .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
                var coverPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in fakeGames)
                {
                    if (gameMap.TryGetValue(name, out var g) && g.CoverImagePath != null)
                        coverPaths[name] = g.CoverImagePath;
                }

                await Task.Run(() =>
                {
                    SessionLogger.UpdateSessionTelemetry(sid, fakeStats, QualityGrade.High,
                        rttSeries, dropsSeries, bitrateSeries, decodeSeries, hostLatSeries,
                        hostGpuSeries, hostEncSeries, hostCpuSeries);

                    var sessions = SessionLogger.Load();
                    var entry    = sessions.FirstOrDefault(s => s.Id == sid);
                    if (entry != null)
                    {
                        entry.StartTime              = DateTime.Now.AddMinutes(-30);
                        entry.GamesDetected          = fakeGames;
                        if (coverPaths.Count > 0)
                            entry.GamesDetectedCoverPaths = coverPaths;
                        SessionLogger.SavePublic(sessions);
                    }
                });

                _isAutoSessionActive = true;
                AppStateService.Instance.IsSessionActive = true;
                UpdateTrayStreamingState(true);
            }
            catch { _isDebugModeActive = false; AppStateService.Instance.IsDebugModeActive = false; }
            finally { _sessionStartInProgress = false; }
        }

        public void StopDebugSession()
        {
            if (!_isDebugModeActive) return;
            try
            {
                if (_isAudioMonitorEnabled)
                    _dolbyMonitor.OnStreamingStopped();

                SessionLogger.EndSession("Debug Stop");
                _isDebugModeActive   = false;
                AppStateService.Instance.IsDebugModeActive = false;
                _isAutoSessionActive = false;
                AppStateService.Instance.IsSessionActive = false;
                UpdateTrayStreamingState(false);
            }
            catch { _isDebugModeActive = false; AppStateService.Instance.IsDebugModeActive = false; }
        }

        // ── Inactivity timer ─────────────────────────────────────────────────

        private void StartInactivityTimer()
        {
            if (_inactivityTimer == null)
            {
                _inactivityTimer = _dispatcher.CreateTimer();
                _inactivityTimer.Interval    = TimeSpan.FromMilliseconds(INACTIVITY_TIMEOUT_MS);
                _inactivityTimer.IsRepeating = false;
                _inactivityTimer.Tick += (_, _) =>
                {
                    StopInactivityTimer();
                    if (_isAutoStreamingActive || _isAutoSessionActive)
                        _ = HandleAutoStreamStop("Disconnected");
                };
            }
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }

        private void StopInactivityTimer() => _inactivityTimer?.Stop();
    }
}
