using System.Net.NetworkInformation;
using System.Reflection;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamTweak.Services;

namespace StreamTweak
{
    public partial class App : Application
    {
        public static MainWindow? MainWindow { get; private set; }

        // Captured in OnLaunched — all backend callbacks marshal through this.
        private DispatcherQueue _dispatcher = null!;

        // ── Tray icon ────────────────────────────────────────────────────────
        private TaskbarIcon? _trayIcon;
        private MenuFlyoutItem?       _traySpeedItem;
        private MenuFlyoutItem?       _trayStreamingStatusItem;
        private MenuFlyoutItem?       _trayStreamingModeItem;
        private ToggleMenuFlyoutItem? _trayAutoModeItem;
        private ToggleMenuFlyoutItem? _traySpatialAudioItem;
        private ToggleMenuFlyoutItem? _trayHdrItem;
        private MonitorInfo?          _trayHdrMonitor;   // first HDR-capable monitor cached at startup

        // ── Single-instance guard ────────────────────────────────────────────
        private static Mutex?      _singleInstanceMutex;
        private EventWaitHandle?   _activationEvent;
        private const string MutexName = "StreamTweak_SingleInstance_v6";
        private const string EventName = "StreamTweak_Activate_v6";

        // ── NIC / streaming state ────────────────────────────────────────────
        private string _adapterName = "Ethernet";
        private bool _isAutoStreamingEnabled = false;
        private bool _isAutoStreamingActive = false;   // NIC has been throttled
        private bool _isAutoSessionActive = false;     // session is being tracked
        private string? _originalSpeedForAutoStreaming = null;
        private bool _sessionStartInProgress = false;
        private bool _bridgeRetrospectiveArmed = true; // one-shot for mid-session restart
        private List<string> _appsToRelaunch = new();

        // ── Spatial audio ────────────────────────────────────────────────────
        private readonly DolbyAudioMonitor _dolbyMonitor = new();
        private bool _isAudioMonitorEnabled = false;
        private string _audioOutputDevice = "Steam Streaming Speakers";
        private SpatialAudioFormat _audioSpatialFormat = SpatialAudioFormat.DolbyAtmos;

        // ── Stop-stream one-shot flag (set by Home button, consumed by StatsProvider) ──
        private volatile bool _stopStreamRequested;

        // ── NIC renegotiation alert window ───────────────────────────────────
        private StreamingAdjustmentWindow? _adjustmentAlert;

        // ── Backend services ─────────────────────────────────────────────────
        private readonly StreamTweakBridge _bridge = new();
        private readonly HostMetricsCollector _metricsCollector = new();
        private readonly TelemetryAccumulator _telemetryAccumulator = new();
        private StreamingLogMonitor? _logMonitor = null;
        private SessionProcessMonitor? _sessionProcessMonitor;

        // ── Inactivity timer (30 s grace period between disconnects) ─────────
        private DispatcherQueueTimer? _inactivityTimer;
        private const int INACTIVITY_TIMEOUT_MS = 30_000;

        // ────────────────────────────────────────────────────────────────────

        public App()
        {
            this.RequestedTheme = ApplicationTheme.Dark;
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            // ── Single-instance guard ────────────────────────────────────────
            // If another instance is already running, signal it (when launched by
            // the user) so it shows its window, then exit immediately.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                bool launchedByUser = !Environment.GetCommandLineArgs()
                    .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
                if (launchedByUser)
                {
                    try
                    {
                        // Open (or create, in the unlikely race) the named event and pulse it.
                        using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                        evt.Set();
                    }
                    catch { }
                }
                Environment.Exit(0);
                return;
            }

            // First instance: create the named event and watch for activation signals
            // from any future second-launch attempts.
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _ = Task.Run(WatchActivationRequests);

            NotificationService.Initialize();
            SessionLogger.Initialize();
            LoadConfig();

            // When launched at Windows login via the autostart registry entry the exe
            // is invoked with --minimized: skip Activate() so the window never appears.
            // The app runs silently in the background; the tray icon is the entry point.
            bool startMinimized = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            MainWindow = new MainWindow();
            if (!startMinimized)
                MainWindow.Activate();
            SetupTrayIcon();

            // Spatial audio
            _dolbyMonitor.StatusChanged += OnDolbyStatusChanged;
            StartDolbyMonitor();

            // Log monitor (auto-streaming detection)
            StartAutoStreamingMonitor();

            // TCP bridge (StreamLight → StreamTweak commands)
            _bridge.PrepareRequested    += OnBridgePrepareRequested;
            _bridge.RestoreRequested    += OnBridgeRestoreRequested;
            _bridge.SessionDataReceived += OnSessionDataReceived;
            _bridge.Start();
            _bridge.StatusProvider     = () => { var (mbps, ok) = GetCurrentSpeed(); return ok ? mbps.ToString() : "UNKNOWN"; };
            _bridge.StatsProvider      = () =>
            {
                string json = _metricsCollector.GetLatestSample().ToJson();
                if (_stopStreamRequested)
                {
                    _stopStreamRequested = false;   // one-shot: consume immediately
                    json = json.TrimEnd('}') + ",\"stop\":1}";
                }
                return json;
            };
            _bridge.AppStoresProvider  = () => GameLibraryState.Current.ToAppStoresJson();

            // Wire AppStateService action delegates
            AppStateService.Instance.StartStreamingModeAction  = StartManualStreamingMode;
            AppStateService.Instance.StopStreamingModeAction   = () => { HandleAutoStreamStop("User"); return Task.CompletedTask; };
            AppStateService.Instance.RequestStopStreamAction   = () => _stopStreamRequested = true;
            AppStateService.Instance.ApplyAdapterSpeedAction  = (adapter, speedKey) =>
            {
                _adapterName = adapter;
                ConfigService.Set("NetworkAdapterName", adapter);
                return Task.Run(() => ApplySpeed(speedKey));
            };

            // Audio live-update actions: called by AudioViewModel when the user
            // changes device/format/enabled in the Audio tab — no restart required.
            AppStateService.Instance.SetAudioMonitorEnabledAction = enabled =>
            {
                _isAudioMonitorEnabled = enabled;
                if (enabled)
                {
                    StartDolbyMonitor();
                    StartAutoStreamingMonitor();
                }
                else
                {
                    _dolbyMonitor.Disable();
                    StopAutoStreamingMonitor();
                }
                if (_traySpatialAudioItem != null)
                    _traySpatialAudioItem.IsChecked = enabled;
                AppStateService.Instance.RaiseSettingsChanged();
            };
            AppStateService.Instance.SetAudioDeviceAction = device =>
            {
                _audioOutputDevice             = device;
                _dolbyMonitor.TargetDeviceName = device;
            };
            AppStateService.Instance.SetAudioFormatAction = fmt =>
            {
                _audioSpatialFormat        = fmt;
                _dolbyMonitor.SpatialFormat = fmt;
                AppStateService.Instance.RaiseSettingsChanged();
            };

            // Manual spatial-audio control (Audio tab buttons).
            // Both actions run on a background thread to match the threading context of
            // TryEnableSpatialAudioAsync and are fully independent from the auto monitor.
            AppStateService.Instance.ActivateSpatialAudioNowAction =
                () => Task.Run(() => _dolbyMonitor.ForceActivateAsync());

            AppStateService.Instance.DeactivateSpatialAudioAction = async () =>
            {
                string result = await Task.Run(() => _dolbyMonitor.ForceDeactivateAsync());
                if (string.IsNullOrEmpty(result))
                    _dolbyMonitor.OnStreamingStopped(); // cancel any pending auto-activation
                return result;
            };

            // Load previous run's PCGW metadata cache immediately so the
            // Game Library page shows data before the background refresh completes.
            PcgwMetadataService.LoadFromDisk();

            // Auto-sync game library if enabled.
            // After completion, raise SettingsChanged so HomeViewModel refreshes
            // the "last sync" tile with the timestamp just written by the sync.
            if (GameLibraryState.Current.SyncEnabled)
                _ = Task.Run(async () =>
                {
                    await GameLibraryService.PerformSyncAsync();
                    _dispatcher.TryEnqueue(AppStateService.Instance.RaiseSettingsChanged);
                });

            // Refresh PCGW metadata in background on every startup (Metacritic scores change).
            _ = Task.Run(() => PcgwMetadataService.RefreshAsync(GameLibraryState.Current.Games));

            // Windows session-end cleanup
            Microsoft.Win32.SystemEvents.SessionEnding += OnSystemSessionEnding;
        }

        // ── Single-instance activation watcher ───────────────────────────────

        /// <summary>
        /// Background thread: blocks on the named event. When a second launch signals it
        /// (because the user relaunched the exe while it was already running), bring the
        /// main window to the foreground on the UI thread.
        /// </summary>
        private void WatchActivationRequests()
        {
            while (_activationEvent != null)
            {
                _activationEvent.WaitOne();
                _dispatcher?.TryEnqueue(ShowMainWindow);
            }
        }

        // ── Config ───────────────────────────────────────────────────────────

        private void LoadConfig()
        {
            _adapterName            = ConfigService.Get("NetworkAdapterName", "Ethernet");
            _isAutoStreamingEnabled = ConfigService.GetBool("AutoStreamingEnabled", false);
            _isAudioMonitorEnabled  = ConfigService.GetBool("AudioMonitorEnabled", false);
            _audioOutputDevice      = ConfigService.Get("AudioOutputDevice", "Steam Streaming Speakers");
            string fmt              = ConfigService.Get("AudioSpatialFormat", "DolbyAtmos");
            _audioSpatialFormat     = fmt == "WindowsSonic" ? SpatialAudioFormat.WindowsSonic : SpatialAudioFormat.DolbyAtmos;

            _dolbyMonitor.TargetDeviceName = _audioOutputDevice;
            _dolbyMonitor.SpatialFormat    = _audioSpatialFormat;
        }

        // ── Public API (called by ViewModels / tray menu handlers) ───────────

        public static void ShowToast(string title, string message, string? attribution = null)
            => NotificationService.Show(title, message, attribution);

        public void ShowMainWindow()
        {
            if (MainWindow == null) return;
            MainWindow.Activate();
            MainWindow.BringToFront();
        }

        public void ExitApp()
        {
            Cleanup();
            _trayIcon?.Dispose();
            // Environment.Exit is the only reliable way to terminate a WinUI 3
            // unpackaged process: Application.Exit() may not flush the message pump,
            // and MainWindow.Close() is intercepted by the hide-instead-of-close handler.
            Environment.Exit(0);
        }

        /// <summary>Updates tray status text, Start/Stop button label, and tray icon.</summary>
        public void UpdateTrayStreamingState(bool isActive)
        {
            if (_trayStreamingStatusItem != null)
                _trayStreamingStatusItem.Text = isActive ? "Streaming: Active  ●" : "Streaming: Inactive";
            if (_trayStreamingModeItem != null)
            {
                _trayStreamingModeItem.Text      = isActive ? "Stop Streaming Mode" : "Start Streaming Mode";
                _trayStreamingModeItem.IsEnabled = true; // always enabled — toggles between start and stop
            }
            SetTrayIcon(isActive);
        }

        private void SetTrayIcon(bool sessionActive)
        {
            if (_trayIcon == null) return;
            string name     = sessionActive ? "streammodeok.ico" : "streammodeko.ico";
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", name);
            try
            {
                using var icon = new System.Drawing.Icon(iconPath, 32, 32);
                _trayIcon.UpdateIcon(icon);
            }
            catch { }
        }

        // ── NIC helpers ──────────────────────────────────────────────────────

        private (long mbps, bool connected) GetCurrentSpeed()
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(_adapterName, StringComparison.OrdinalIgnoreCase));
            return ni?.OperationalStatus == OperationalStatus.Up
                ? (ni.Speed / 1_000_000, true)
                : (0, false);
        }

        private string? Find1GbpsKey()
        {
            var speeds = NetworkManager.GetSupportedSpeeds(_adapterName);
            foreach (var kvp in speeds)
            {
                string kl = kvp.Key.ToLower();
                if (((kl.Contains("1 gbps") || kl.Contains("1gbps") || kl.Contains("1000")) && kl.Contains("full"))
                    || kvp.Value == "6")
                    return kvp.Key;
            }
            return null;
        }

        private void ApplySpeed(string speedKey)
        {
            var speeds = NetworkManager.GetSupportedSpeeds(_adapterName);
            if (!speeds.TryGetValue(speedKey, out string? regValue)) return;
            if (!SpeedChanger.Apply(_adapterName, regValue))
                SpeedChanger.ApplyWithUac(_adapterName, regValue);
        }

        // ── Session telemetry ────────────────────────────────────────────────

        private void FinalizeSessionTelemetry()
        {
            try
            {
                string? sid = SessionLogger.ActiveSessionId;
                if (sid == null) return;
                var (stats, rtt, drops, bitrate, decode) = _telemetryAccumulator.Finalize();
                if (stats.SampleCount >= 2)
                {
                    var grade = QualityGradeCalculator.Evaluate(stats, _telemetryAccumulator.TargetFps);
                    SessionLogger.UpdateSessionTelemetry(sid, stats, grade, rtt, drops, bitrate, decode);
                }
                _telemetryAccumulator.Reset();
            }
            catch { }
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
                            HandleAutoStreamStart(skipNicThrottle: e.IsRetrospective || _isAutoStreamingActive);
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
            catch { }
            finally { _sessionStartInProgress = false; }
        }

        private async void HandleAutoStreamStart(bool skipNicThrottle = false)
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

                // Start process monitor to detect which games run during this session
                _sessionProcessMonitor?.Dispose();
                var games = GameLibraryState.Current.Games;
                _sessionProcessMonitor = new SessionProcessMonitor(games);
                _sessionProcessMonitor.Start();
            }
            catch { }
            finally { _sessionStartInProgress = false; }
        }

        private async void HandleAutoStreamStop(string endReason = "User")
        {
            try
            {
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
                    // Pass detectedGames even when empty: null = monitor never ran (pre-feature / manual mode)
                    //                                   []   = monitor ran but no games found (desktop session etc.)
                    //                                   [...] = games were detected
                    SessionLogger.EndSession(endReason, detectedGames);
                    _isAutoSessionActive = false;
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
            catch { }
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
                        HandleAutoStreamStop("Disconnected");
                };
            }
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }

        private void StopInactivityTimer() => _inactivityTimer?.Stop();

        // ── TCP Bridge handlers ───────────────────────────────────────────────

        private void OnBridgePrepareRequested()
        {
            // Bridge fires on a thread-pool thread — marshal to UI thread.
            _dispatcher.TryEnqueue(() => _ = HandleBridgePrepareAsync());
        }

        private async Task HandleBridgePrepareAsync()
        {
            try
            {
                if (_isAutoStreamingActive) return;

                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(_adapterName, StringComparison.OrdinalIgnoreCase));
                if (ni == null || ni.OperationalStatus != OperationalStatus.Up) return;

                long mbps = ni.Speed / 1_000_000;
                if (mbps < 1200) return; // already at or below 1 Gbps

                var speeds = NetworkManager.GetSupportedSpeeds(_adapterName);
                foreach (var kvp in speeds)
                {
                    string kl = kvp.Key.ToLower();
                    bool match = mbps >= 2000
                        ? kl.Contains("2.5") || kl.Contains("2500")
                        : kl.Contains(mbps.ToString());
                    if (match) { _originalSpeedForAutoStreaming = kvp.Key; break; }
                }

                string? oneGbpsKey = Find1GbpsKey();
                if (oneGbpsKey == null) return;

                _isAutoStreamingActive = true;
                _isAutoSessionActive   = true;
                _telemetryAccumulator.Reset();
                ConfigService.Set("StreamingMode", true);
                ConfigService.Set("OriginalSpeed", _originalSpeedForAutoStreaming ?? string.Empty);
                _appsToRelaunch = ManagedAppController.KillRunning();
                SessionLogger.StartSession("Bridge", _originalSpeedForAutoStreaming ?? string.Empty);
                StartInactivityTimer();
                AppStateService.Instance.IsStreamingModeActive = true;
                AppStateService.Instance.IsSessionActive = true;
                UpdateTrayStreamingState(true);

                await Task.Run(() => ApplySpeed(oneGbpsKey));
                _ = PollForNicReconnectAsync();

                NotificationService.Show("StreamTweak Ready",
                    "Network set to 1 Gbps. Connect within 30 seconds or speed will be restored.");
            }
            catch { }
        }

        private void OnBridgeRestoreRequested()
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_isAutoStreamingActive || _isAutoSessionActive)
                    HandleAutoStreamStop("User");
            });
        }

        private void OnSessionDataReceived(ClientBatch batch)
        {
            try
            {
                if (SessionLogger.ActiveSessionId == null)
                {
                    // Move the entire one-shot logic onto the UI thread to avoid a race
                    // where two concurrent SESSIONDATA batches both see _bridgeRetrospectiveArmed=true
                    // before either resets it, potentially launching two parallel sessions.
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (_bridgeRetrospectiveArmed
                            && !_isAutoSessionActive
                            && !_sessionStartInProgress)
                        {
                            _bridgeRetrospectiveArmed = false; // one-shot, safe on UI thread
                            _dolbyMonitor.OnStreamingStarted(isRetrospective: true);
                            HandleAutoStreamStart(skipNicThrottle: true);
                        }
                    });
                    return;
                }

                var hostSample = _metricsCollector.GetLatestSample();
                _telemetryAccumulator.AddBatch(batch, hostSample);
            }
            catch { }
        }

        // ── Spatial audio ─────────────────────────────────────────────────────

        private void StartDolbyMonitor()
        {
            if (!_isAudioMonitorEnabled || _dolbyMonitor.IsEnabled) return;
            _dolbyMonitor.TargetDeviceName = _audioOutputDevice;
            _dolbyMonitor.SpatialFormat    = _audioSpatialFormat;
            _dolbyMonitor.Enable();
        }

        private void OnDolbyStatusChanged(string status)
        {
            DebugLogger.Log($"[Dolby] {status}");
            AppStateService.Instance.RaiseSpatialAudioStatus(status);
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        private void Cleanup()
        {
            if (_isAutoSessionActive)
            {
                FinalizeSessionTelemetry();
                SessionLogger.EndSession("App Closed");
            }
            try
            {
                _bridge.PrepareRequested    -= OnBridgePrepareRequested;
                _bridge.RestoreRequested    -= OnBridgeRestoreRequested;
                _bridge.SessionDataReceived -= OnSessionDataReceived;
                _bridge.Dispose();
            }
            catch { }
            _metricsCollector.Dispose();
            StopLogMonitorForced();
            _dolbyMonitor.Disable();
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSystemSessionEnding;

            // Release single-instance resources so the watcher thread can exit cleanly.
            _activationEvent?.Set();   // unblocks WatchActivationRequests if waiting
            _activationEvent?.Dispose();
            _activationEvent = null;
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        private void OnSystemSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
        {
            if (_isAutoSessionActive)
            {
                FinalizeSessionTelemetry();
                SessionLogger.EndSession("Host Shutdown");
            }
        }

        // ── Tray icon ─────────────────────────────────────────────────────────

        private void SetupTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText        = "StreamTweak",
                DoubleClickCommand = new SimpleCommand(ShowMainWindow),
                // SecondWindow mode renders the actual WinUI 3 MenuFlyout in a
                // transparent helper window, so all Click event handlers fire.
                // The default PopupMenu mode converts the flyout to a Win32 native
                // HMENU and only executes Command — Click is silently ignored.
                ContextMenuMode    = H.NotifyIcon.ContextMenuMode.SecondWindow,
            };

            var flyout = new MenuFlyout();

            // ── Open app (Home) ──────────────────────────────────────────────
            var openItem = new MenuFlyoutItem { Text = "StreamTweak" };
            openItem.Click += (_, _) => { ShowMainWindow(); MainWindow?.NavigateTo("Home"); };
            flyout.Items.Add(openItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Status: live NIC speed + streaming state (non-clickable) ───
            _traySpeedItem = new MenuFlyoutItem { Text = "Speed: …", IsEnabled = false };
            flyout.Items.Add(_traySpeedItem);

            _trayStreamingStatusItem = new MenuFlyoutItem { Text = "Streaming: Inactive", IsEnabled = false };
            flyout.Items.Add(_trayStreamingStatusItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Start / Stop Streaming Mode ──────────────────────────────────
            _trayStreamingModeItem = new MenuFlyoutItem { Text = "Start Streaming Mode", MinWidth = 240 };
            _trayStreamingModeItem.Click += async (_, _) =>
            {
                if (_isAutoStreamingActive || _isAutoSessionActive)
                    HandleAutoStreamStop("User");
                else
                    await StartManualStreamingMode();
            };
            flyout.Items.Add(_trayStreamingModeItem);

            // ── Auto Mode toggle ─────────────────────────────────────────────
            _trayAutoModeItem = new ToggleMenuFlyoutItem
            {
                Text      = "Auto Streaming Mode",
                IsChecked = _isAutoStreamingEnabled
            };
            _trayAutoModeItem.Click += (_, _) =>
            {
                _isAutoStreamingEnabled = _trayAutoModeItem.IsChecked;
                ConfigService.Set("AutoStreamingEnabled", _isAutoStreamingEnabled);
                if (_isAutoStreamingEnabled)
                    StartAutoStreamingMonitor();
                else
                    StopAutoStreamingMonitor();
                AppStateService.Instance.RaiseSettingsChanged();
            };
            flyout.Items.Add(_trayAutoModeItem);

            // ── Spatial Audio toggle ─────────────────────────────────────────
            _traySpatialAudioItem = new ToggleMenuFlyoutItem
            {
                Text      = "Auto Spatial Audio",
                IsChecked = _isAudioMonitorEnabled
            };
            _traySpatialAudioItem.Click += (_, _) =>
            {
                _isAudioMonitorEnabled = _traySpatialAudioItem.IsChecked;
                ConfigService.Set("AudioMonitorEnabled", _isAudioMonitorEnabled);
                if (_isAudioMonitorEnabled)
                    StartDolbyMonitor();
                else
                    _dolbyMonitor.Disable();
                // Re-evaluate whether the log monitor should run
                if (_isAudioMonitorEnabled)
                    StartAutoStreamingMonitor();
                else
                    StopAutoStreamingMonitor();
                AppStateService.Instance.RaiseSettingsChanged();
            };
            flyout.Items.Add(_traySpatialAudioItem);

            // ── HDR toggle (first HDR-capable monitor) ───────────────────────
            _trayHdrItem = new ToggleMenuFlyoutItem { Text = "HDR", IsChecked = false };
            _trayHdrItem.Click += async (_, _) =>
            {
                if (_trayHdrMonitor == null) return;
                bool newState = _trayHdrItem.IsChecked;
                try
                {
                    await HdrService.SetHdrAsync(_trayHdrMonitor.AdapterId, _trayHdrMonitor.TargetId, newState);
                    _trayHdrMonitor.HdrEnabled = newState;
                    AppStateService.Instance.RaiseSettingsChanged();
                }
                catch
                {
                    _trayHdrItem.IsChecked = !newState; // revert on failure
                }
            };
            flyout.Items.Add(_trayHdrItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Exit ────────────────────────────────────────────────────────
            var exitItem = new MenuFlyoutItem { Text = "Exit" };
            exitItem.Click += (_, _) => ExitApp();
            flyout.Items.Add(exitItem);

            // Reduce font size slightly so the menu fits without a vertical scrollbar.
            foreach (var item in flyout.Items.OfType<Microsoft.UI.Xaml.Controls.Control>())
                item.FontSize = 12;

            _trayIcon.ContextFlyout = flyout;
            _trayIcon.ForceCreate(false);

            // H.NotifyIcon (SecondWindow mode) moves all items from our flyout into its own
            // private internal MenuFlyout (ContextMenuFlyout) during ForceCreate. Our original
            // flyout is left empty and is never shown — its Opening/Opened events never fire.
            // We must subscribe to the INTERNAL flyout instead, which we reach via reflection.
            //
            // Opening → refresh the speed label before the menu is visible.
            // Opened  → resize the AppWindow to eliminate the vertical scrollbar.
            //           This MUST run in Opened, not Opening: on the first show ever,
            //           XamlRoot is null during Opening (H.NotifyIcon's internal frame hasn't
            //           loaded yet) so DesiredSize of every item is 0 and any resize attempt
            //           produces the wrong height. By Opened time the frame has loaded,
            //           XamlRoot is valid, DesiredSize is correct, and the fix is guaranteed.
            var internalFlyoutProp = _trayIcon.GetType()
                .GetProperty("ContextMenuFlyout", BindingFlags.NonPublic | BindingFlags.Instance);
            if (internalFlyoutProp?.GetValue(_trayIcon) is MenuFlyout internalFlyout)
            {
                internalFlyout.Opening += (_, _) => RefreshTraySpeedItem();
                internalFlyout.Opened  += (_, _) => ExpandTrayContextMenuWindow(internalFlyout);
            }

            // Initialize speed immediately so the first open never shows "…".
            RefreshTraySpeedItem();

            // UpdateIcon bypasses the XAML ImageSource → HICON pipeline and sets
            // the icon directly via native HICON handle — the only reliable path in
            // WinUI 3 unpackaged (SoftwareBitmapSource / BitmapImage both rendered blank).
            SetTrayIcon(sessionActive: false);

            // Load HDR/AutoHDR state in background and update toggle items when ready
            _ = LoadDisplayStateForTrayAsync();
        }

        private void RefreshTraySpeedItem()
        {
            if (_traySpeedItem == null) return;
            var (mbps, connected) = GetCurrentSpeed();
            string speedText = !connected ? "Unknown"
                : mbps <= 0   ? "Negotiating…"
                : mbps >= 1000 ? $"{mbps / 1000.0:0.##} Gbps"
                :                $"{mbps} Mbps";
            _traySpeedItem.Text = $"Speed: {speedText}";
            UpdateTrayTooltip(connected ? speedText : null);
        }

        private void UpdateTrayTooltip(string? speedText = null)
        {
            if (_trayIcon == null) return;
            if (speedText != null)
                _trayIcon.ToolTipText = $"StreamTweak\n{_adapterName}: {speedText}";
            else
                _trayIcon.ToolTipText = "StreamTweak";
        }

        // Polls the NIC every second for up to 15 s after a speed change so the
        // tooltip reflects the new speed once the adapter reconnects.
        private async Task PollForNicReconnectAsync()
        {
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                var (mbps, connected) = GetCurrentSpeed();
                if (!connected) continue;
                string speedText = mbps >= 1000
                    ? $"{mbps / 1000.0:0.##} Gbps"
                    : $"{mbps} Mbps";
                _dispatcher.TryEnqueue(() =>
                {
                    RefreshTraySpeedItem();   // updates both menu item and tooltip
                });
                break;
            }
        }

        private async Task LoadDisplayStateForTrayAsync()
        {
            try
            {
                var monitors = await HdrService.GetMonitorsAsync();
                var primary  = monitors.FirstOrDefault(m => m.HdrSupported && !m.IsVirtual);

                _dispatcher.TryEnqueue(() =>
                {
                    _trayHdrMonitor = primary;
                    if (_trayHdrItem != null)
                    {
                        _trayHdrItem.IsChecked = primary?.HdrEnabled ?? false;
                        _trayHdrItem.IsEnabled = primary != null;
                    }
                });
            }
            catch { }
        }

        // ── Tray context-menu scrollbar fix ──────────────────────────────────

        /// <summary>
        /// Ensures H.NotifyIcon's internal popup AppWindow is tall enough to show all menu
        /// items without a vertical scrollbar, at any DPI scaling factor.
        ///
        /// Root cause: H.NotifyIcon's MeasureFlyout() uses flyout.XamlRoot.RasterizationScale
        /// to convert logical→physical pixels when sizing the AppWindow. On the FIRST open,
        /// the internal Window has never been shown, so XamlRoot is null → scale falls back
        /// to 1.0 regardless of actual DPI → window can be 80-170 physical pixels too short
        /// at 125-150% DPI.  On subsequent opens the warm-up has run and XamlRoot is set,
        /// so only a 1-2 px rounding error remains.
        ///
        /// Fix: at Opening time (flyout is being shown, XamlRoot is now valid) we re-run the
        /// same measurement H.NotifyIcon would have run with the correct scale, then resize
        /// the AppWindow to the correct physical height if it is currently undersized.
        /// </summary>
        private void ExpandTrayContextMenuWindow(MenuFlyout internalFlyout)
        {
            try
            {
                // Called from Opened: XamlRoot and DesiredSize are always valid at this point.
                if (internalFlyout.XamlRoot == null) return; // safety guard, should never trigger

                double scale = internalFlyout.XamlRoot.RasterizationScale;

                double totalHeight = 4.0;
                foreach (var item in internalFlyout.Items)
                    totalHeight += item.DesiredSize.Height;

                int neededHeight = (int)Math.Round(scale * totalHeight + 4.0) + 2;

                var appWindowProp = _trayIcon!.GetType()
                    .GetProperty("ContextMenuAppWindow",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                if (appWindowProp?.GetValue(_trayIcon) is Microsoft.UI.Windowing.AppWindow appWindow)
                {
                    var sz = appWindow.Size;
                    if (sz.Height > 0 && sz.Height < neededHeight)
                        appWindow.ResizeClient(
                            new Windows.Graphics.SizeInt32(sz.Width, neededHeight));
                }
            }
            catch { /* best-effort; never block the tray */ }
        }

        // ── Minimal ICommand for tray double-click ────────────────────────────

        private sealed class SimpleCommand(Action execute) : ICommand
        {
#pragma warning disable CS0067
            public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => execute();
        }
    }
}
