using System.Diagnostics;
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

        /// <summary>
        /// Starts the streaming-server log watcher. Unconditional since 8.1.0.
        /// <para>It used to be gated on "Auto Streaming" (the link-speed auto-switch) or spatial
        /// audio, which made sense when those were the only things it fed. It is now the app's
        /// only way to know a session exists at all — sessions, telemetry, spatial audio and
        /// played-game capture all hang off it — so gating it on a link-speed setting that no
        /// longer exists would have silently switched off session history for anyone who had
        /// that toggle off.</para>
        /// </summary>
        private void StartAutoStreamingMonitor()
        {
            if (_logMonitor != null) return;
            try
            {
                _logMonitor = new StreamingLogMonitor();
                _logMonitor.StreamingEventDetected += LogMonitor_StreamingEventDetected;
                _logMonitor.GameLaunchDetected += OnGameLaunchDetected;
                _logMonitor.AppLaunchDetected += _launchWatcher.OnAppLaunched;
                _logMonitor.StartMonitoring();
            }
            catch { }
        }

        // The monitor is infrastructure now and only stops when the app does —
        // see StopLogMonitorForced, called from the shutdown path.

        private void StopLogMonitorForced()
        {
            try
            {
                if (_logMonitor == null) return;
                _logMonitor.StreamingEventDetected -= LogMonitor_StreamingEventDetected;
                _logMonitor.GameLaunchDetected -= OnGameLaunchDetected;
                _logMonitor.AppLaunchDetected -= _launchWatcher.OnAppLaunched;
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
                        // A client-declared unlock session: plumbing, not something the user
                        // did. Bail out above everything — spatial audio, managed apps, the
                        // history row, game capture, the tray. The 60 s minimum length (§35)
                        // would not cover this: a mistyped PIN makes the session longer than
                        // that, and the side effects fire the moment it starts either way.
                        if (ConsumeUnlockSessionMark())
                        {
                            DebugLogger.Log("[Unlock] session start suppressed (client declared an unlock session)");
                            return;
                        }

                        // A start that was NOT declared closes the book on any previous unlock,
                        // whether or not its stop was ever logged. Without this the flag is
                        // sticky: a client killed mid-unlock would leave it set, and the next
                        // real session would have its stop swallowed instead.
                        _unlockSessionActive = false;

                        _dolbyMonitor.OnStreamingStarted(e.IsRetrospective);
                        // Allow session tracking to start even when NIC is already throttled
                        // by manual streaming mode — just skip the NIC change in that case.
                        if (!_isAutoSessionActive && !_sessionStartInProgress)
                            _ = HandleAutoStreamStart(retrospective: e.IsRetrospective);
                        else
                        {
                            StopInactivityTimer(); // reconnected within grace period

                            // ⚠️ Load-bearing since the link flag started clearing on disconnect.
                            // This branch is the resume path, and HandleAutoStreamStart — which is
                            // where OnSessionStarted() otherwise runs — is deliberately skipped on
                            // it. Without this the manager would go on believing no stream is live
                            // for the whole resumed session and let a client renegotiate under it.
                            // (LiveSessionProbe would still catch it, but relying on the backstop
                            // when the primary is one line away is not a trade worth making.)
                            _linkSpeed?.OnSessionStarted();
                        }
                    }
                    else if (e.Event == LogParser.StreamingEvent.StreamStopped)
                    {
                        // The other half of a suppressed session. Without this the stop would
                        // still run the spatial-audio teardown for a session that, as far as
                        // everything else here is concerned, never happened.
                        if (_unlockSessionActive)
                        {
                            _unlockSessionActive = false;
                            DebugLogger.Log("[Unlock] session end suppressed");
                            return;
                        }

                        _dolbyMonitor.OnStreamingStopped();
                        if (_isAutoSessionActive)
                        {
                            // Remember *when* we saw the disconnect, not when the grace period
                            // eventually winds the session up: a launch armed in between belongs
                            // to the next session and must survive the cleanup.
                            _lastStopDetectedUtc = DateTime.UtcNow;

                            // The link manager is told now, not when the grace period is up. Its
                            // flag governs whether a client may renegotiate the adapter, and no
                            // stream is live from here on — holding it for another thirty seconds
                            // turned down the link match for exactly as long, in the window where
                            // starting another game is most likely. The session itself stays open.
                            _linkSpeed?.OnStreamDisconnected();

                            StartInactivityTimer();
                        }
                    }
                });
            }
            catch { }
        }

        // The server log names the exact executable it launched (~1 s before CLIENT CONNECTED),
        // which is the authoritative game — more reliable than process scanning for launcher→game
        // handoffs (Ubisoft/EA/Battle.net) the scanner misses. Resolve it to the app's display
        // name via apps.json; the process monitor is seeded at session start, and if a session is
        // already active (second game mid-session) it's credited immediately.
        private string? _lastLaunchedGameName;
        private DateTime _lastLaunchedGameAtUtc = DateTime.MinValue;

        /// <summary>
        /// How long a buffered launch stays eligible for seeding. The Executing: line normally
        /// precedes CLIENT CONNECTED by about a second; anything older means the launch never
        /// became a session (client failed to connect, user backed out) and must not be
        /// credited to whatever session happens to start later.
        /// </summary>
        private const double LAUNCHED_GAME_MAX_AGE_SEC = 120;

        // When the client's disconnect was seen. The session is only wound up a grace period
        // later, and anything armed in between belongs to whatever comes next.
        private DateTime _lastStopDetectedUtc = DateTime.MinValue;

        // ── Client-declared unlock sessions ─────────────────────────────────────────────
        // A remote PIN unlock opens a real streaming session for a few seconds purely to type
        // into the logon screen. Nothing about it is worth recording, and its side effects are
        // actively unwanted. The host cannot recognise such a session by looking at it, so the
        // client declares it over UNLOCKBEGIN and the mark is consumed by the next start.

        // Deadline rather than a plain flag: if the client dies or the user backs out between
        // the declaration and the session, the mark must expire on its own rather than swallow
        // whatever session happens to start next.
        private DateTime _unlockMarkUntilUtc = DateTime.MinValue;

        // Set once a start has actually been suppressed, so the matching stop is too.
        private bool _unlockSessionActive;

        /// <summary>
        /// How long a declared unlock stays eligible. Generous next to the few seconds a PIN
        /// takes, because the client may still be waiting for the host to finish booting — and
        /// far shorter than a session anyone would care about losing.
        /// </summary>
        private const double UNLOCK_MARK_MAX_AGE_SEC = 180;

        private void OnBridgeUnlockSessionMarked(bool begin)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (begin)
                {
                    _unlockMarkUntilUtc = DateTime.UtcNow.AddSeconds(UNLOCK_MARK_MAX_AGE_SEC);
                    DebugLogger.Log("[Unlock] client declared an unlock session");
                }
                else
                {
                    // Only the pending mark. _unlockSessionActive is deliberately left alone:
                    // if the session has already started it was suppressed, and its stop has
                    // to be suppressed too — otherwise clearing up after a successful unlock
                    // would resurrect the teardown for a session nothing else knows about.
                    _unlockMarkUntilUtc = DateTime.MinValue;
                    DebugLogger.Log("[Unlock] client cleared the pending unlock declaration");
                }
            });
        }

        /// <summary>
        /// True once, for the first session start after a declaration. One-shot on purpose:
        /// an unlock is followed almost immediately by the real session the user wanted, and
        /// that one must be recorded normally.
        /// </summary>
        private bool ConsumeUnlockSessionMark()
        {
            if (DateTime.UtcNow >= _unlockMarkUntilUtc) return false;
            _unlockMarkUntilUtc  = DateTime.MinValue;
            _unlockSessionActive = true;
            return true;
        }

        private void OnGameLaunchDetected(string exePath)
        {
            try
            {
                string? name = SunshineSync.ResolveAppNameForExecutable(exePath);
                if (string.IsNullOrEmpty(name)) return;
                _dispatcher.TryEnqueue(() =>
                {
                    _lastLaunchedGameName  = name;
                    _lastLaunchedGameAtUtc = DateTime.UtcNow;
                    _sessionProcessMonitor?.AddDetectedByName(name);
                });
            }
            catch (Exception ex) { DebugLogger.Log($"[Session] OnGameLaunchDetected failed: {ex}"); }
        }

        // Seed the just-launched game (buffered from the log) into a freshly-created process
        // monitor — the Executing: line arrives before the session (and monitor) starts.
        private void SeedLaunchedGameIntoMonitor()
        {
            // Consume-once: clear before using, so a launch can never be seeded into two
            // sessions, and an unused one can't linger until the next unrelated session.
            string? name = _lastLaunchedGameName;
            _lastLaunchedGameName = null;
            if (string.IsNullOrEmpty(name)) return;

            if ((DateTime.UtcNow - _lastLaunchedGameAtUtc).TotalSeconds > LAUNCHED_GAME_MAX_AGE_SEC)
            {
                DebugLogger.Log($"[Session] Ignoring stale launched game '{name}' — logged more than {LAUNCHED_GAME_MAX_AGE_SEC}s ago");
                return;
            }
            _sessionProcessMonitor?.AddDetectedByName(name);
        }

        /// <summary>
        /// Starts session tracking when the streaming server logs a client connection.
        /// <para>Since 8.1.0 this no longer touches the link speed. The server log reports a
        /// connection only *after* the session exists, so renegotiating the adapter here killed
        /// the very stream it was meant to help — the client asks beforehand instead, over
        /// SETSPEED. All this method does now is tell <see cref="LinkSpeedManager"/> a session is
        /// live, which suppresses any change while it lasts.</para>
        /// </summary>
        /// <param name="retrospective">True when the session was already running at startup.</param>
        private async Task HandleAutoStreamStart(bool retrospective = false)
        {
            if (_sessionStartInProgress) return;
            _sessionStartInProgress = true;
            try
            {
                _linkSpeed?.OnSessionStarted();

                _telemetryAccumulator.Reset();
                _appsToRelaunch = ManagedAppController.KillRunning();
                _isAutoSessionActive = true;
                AppStateService.Instance.IsSessionActive = true;
                UpdateTrayStreamingState(true);  // updates tray text + icon
                SessionLogger.StartSession(retrospective ? "Retrospective" : "Auto", string.Empty);
                StartCheckpointTimer();

                // Start process monitor to detect which games run during this session
                _sessionProcessMonitor?.Dispose();
                var games = GameLibraryState.Current.Games;
                _sessionProcessMonitor = new SessionProcessMonitor(games);
                _sessionProcessMonitor.Start();
                SeedLaunchedGameIntoMonitor();  // credit the game named in the server log
            }
            catch (Exception ex) { DebugLogger.Log($"[Streaming] HandleAutoStreamStart failed: {ex}"); }
            finally { _sessionStartInProgress = false; }
        }

        private async Task HandleAutoStreamStop(string endReason = "User")
        {
            try
            {
                if (_isDebugModeActive) return;
                if (!_isAutoSessionActive) return;

                // The link goes back on its own: LinkSpeedManager arms a grace period so a
                // client reconnecting within a minute doesn't pay for a second renegotiation.
                _linkSpeed?.OnSessionEnded();

                // Forget the launch we were watching: a stale phase would tell the next client's
                // curtain that a game is already on screen before anything has been launched.
                // Passing when the disconnect was seen keeps a launch that started during the
                // grace period — that one belongs to the session about to begin, not this one.
                _launchWatcher.OnSessionEnded(_lastStopDetectedUtc);

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
                    _lastLaunchedGameName = null;
                    _lastSessionDataUtc   = DateTime.MinValue;   // disarm the client-heartbeat watchdog
                    StopHeartbeatWatchdog();                     // …and release its timer
                    AppStateService.Instance.IsSessionActive = false;
                    UpdateTrayStreamingState(false);
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

                // Feed the live Dashboard cockpit with synthetic per-second samples so
                // the stat cards + charts populate exactly as in a real session.
                StartDebugLiveFeed();
            }
            catch { _isDebugModeActive = false; AppStateService.Instance.IsDebugModeActive = false; }
            finally { _sessionStartInProgress = false; }
        }

        public void StopDebugSession()
        {
            if (!_isDebugModeActive) return;
            try
            {
                StopDebugLiveFeed();

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

        // ── Debug live feed (drives the Dashboard cockpit in debug mode) ────────

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debugLiveTimer;
        private int _debugTick;

        private void StartDebugLiveFeed()
        {
            _debugTick = 0;
            _debugLiveTimer ??= _dispatcher.CreateTimer();
            _debugLiveTimer.Interval    = TimeSpan.FromSeconds(1);
            _debugLiveTimer.IsRepeating = true;
            _debugLiveTimer.Tick -= OnDebugLiveTick;   // guard against double-subscribe
            _debugLiveTimer.Tick += OnDebugLiveTick;
            _debugLiveTimer.Start();
            OnDebugLiveTick(_debugLiveTimer, null!);   // emit one immediately
        }

        private void StopDebugLiveFeed() => _debugLiveTimer?.Stop();

        private void OnDebugLiveTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            int t = _debugTick++;
            float rtt     = 10f + 3.5f * (float)Math.Sin(t * 0.40) + Random.Shared.Next(0, 3);
            float jitter  = 1.2f + Random.Shared.Next(0, 12) / 10f;
            float bitrate = 146f + 5f * (float)Math.Sin(t * 0.20) + Random.Shared.Next(0, 4);
            int   drops   = t % 23 == 0 ? 1 : 0;
            float fps     = 60f;
            float hostLat = 5.6f + 1.6f * (float)Math.Sin(t * 0.30);
            int   gpu     = 60 + (int)(9 * Math.Sin(t * 0.25)) + Random.Shared.Next(0, 4);
            int   enc     = 22 + (int)(5 * Math.Sin(t * 0.50)) + Random.Shared.Next(0, 3);
            int   cpu     = 30 + (int)(6 * Math.Sin(t * 0.35)) + Random.Shared.Next(0, 3);

            AppStateService.Instance.RaiseLiveSample(new AppStateService.LiveSample(
                rtt, jitter, bitrate, drops, fps, hostLat, gpu, enc, cpu));
        }

        // ── Client-heartbeat watchdog ────────────────────────────────────────
        // Fallback session-end that does NOT depend on the server log. StreamLight sends
        // SESSIONDATA every second while streaming; its cessation is a reliable "client gone"
        // signal even when the server hangs/crashes on teardown and never logs a disconnect
        // (observed: Sunshine "Fatal: Hang detected! … Stuck waiting for: post-join cleanup",
        // which left the session counter running for hours). Armed only once SESSIONDATA has been
        // seen for the active session, so non-telemetry clients are never force-ended.
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _heartbeatWatchdog;
        private DateTime _lastSessionDataUtc = DateTime.MinValue;
        private const int CLIENT_HEARTBEAT_TIMEOUT_MS = 60_000;

        private void EnsureHeartbeatWatchdog()
        {
            if (_heartbeatWatchdog != null) return;
            _heartbeatWatchdog = _dispatcher.CreateTimer();
            _heartbeatWatchdog.Interval    = TimeSpan.FromSeconds(10);
            _heartbeatWatchdog.IsRepeating = true;
            _heartbeatWatchdog.Tick += (_, _) =>
            {
                if (_isDebugModeActive) return;                          // debug feed bypasses the bridge
                if (!_isAutoSessionActive) return;                      // no session → nothing to end
                if (_lastSessionDataUtc == DateTime.MinValue) return;   // no SESSIONDATA seen yet this session
                if ((DateTime.UtcNow - _lastSessionDataUtc).TotalMilliseconds < CLIENT_HEARTBEAT_TIMEOUT_MS) return;

                DebugLogger.Log($"[Session] Client telemetry silent for >{CLIENT_HEARTBEAT_TIMEOUT_MS / 1000}s — ending session (server logged no disconnect).");
                _lastSessionDataUtc = DateTime.MinValue;                // disarm to avoid a double fire
                _ = HandleAutoStreamStop("Client heartbeat lost");
            };
            _heartbeatWatchdog.Start();
        }

        /// <summary>
        /// Stops and releases the heartbeat watchdog. Without this the timer created for the
        /// first session kept ticking every 10 s for the rest of the process lifetime — the
        /// guards made it harmless, but it is pure waste once no session is running.
        /// Marshalled: the timer belongs to the UI thread, and callers may be elsewhere.
        /// </summary>
        private void StopHeartbeatWatchdog()
        {
            _dispatcher.TryEnqueue(() =>
            {
                _heartbeatWatchdog?.Stop();
                _heartbeatWatchdog = null;
            });
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
                    if (_isAutoSessionActive)
                        _ = HandleAutoStreamStop("Disconnected");
                };
            }
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }

        private void StopInactivityTimer() => _inactivityTimer?.Stop();
    }
}
