using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
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
        // Keeps the tray "Speed: …" label current without reflecting into H.NotifyIcon's
        // internal flyout to hook its Opening event (see App.Tray.cs). Low-frequency.
        private DispatcherQueueTimer? _traySpeedTimer;
        private const int TRAY_SPEED_REFRESH_MS = 4_000;

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
        // NVIDIA Sentinel — ported NVPI DRS layer; self-detects NVAPI, null-safe on non-NVIDIA.
        private StreamTweak.Nvidia.NvidiaSentinelService? _nvidiaSentinel;

        // ── Debug mode ───────────────────────────────────────────────────────
        private bool _isDebugModeActive = false;

        // ── Inactivity timer (30 s grace period between disconnects) ─────────
        private DispatcherQueueTimer? _inactivityTimer;
        private const int INACTIVITY_TIMEOUT_MS = 30_000;

        // ── Checkpoint timer (periodic telemetry flush to disk) ───────────────
        private System.Threading.Timer? _checkpointTimer;

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

            // ── NVIDIA Sentinel ──────────────────────────────────────────────
            // Construct unconditionally; the service self-detects NVAPI and sets
            // IsNvidiaAvailable=false on AMD/Intel/no-driver (its ctor catches all
            // errors). The "NVIDIA Sentinel" sidebar item is added in MainWindow
            // only when IsNvidiaAvailable is true.
            try
            {
                _nvidiaSentinel = new StreamTweak.Nvidia.NvidiaSentinelService();
                AppStateService.Instance.NvidiaSentinel = _nvidiaSentinel;

                if (_nvidiaSentinel.IsNvidiaAvailable)
                {
                    // Persist LastRestoreAt across restarts (ISO 8601 round-trip "O").
                    _nvidiaSentinel.PersistLastRestoreCallback = at =>
                        ConfigService.Set("NvidiaLastRestoreAt", at?.ToString("O") ?? string.Empty);

                    var lastRestoreStr = ConfigService.Get("NvidiaLastRestoreAt", string.Empty);
                    if (!string.IsNullOrEmpty(lastRestoreStr)
                        && DateTime.TryParse(lastRestoreStr,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out var lastRestoreDt))
                    {
                        _nvidiaSentinel.LoadLastRestoreAt(lastRestoreDt);
                    }

                    // Auto-restore is opt-in: default false. It only ever acts when the
                    // user has captured a snapshot AND armed the toggle in the UI.
                    if (ConfigService.GetBool("NvidiaAutoRestore", false))
                        _nvidiaSentinel.SetAutoRestoreEnabled(true);
                }
            }
            catch { _nvidiaSentinel = null; }

            // When launched at Windows login via the autostart registry entry the exe
            // is invoked with --minimized: skip Activate() so the window never appears.
            // The app runs silently in the background; the tray icon is the entry point.
            bool startMinimized = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            MainWindow = new MainWindow();
            if (!startMinimized)
                MainWindow.Activate();
            SetupTrayIcon();

            // GitHub releases poll — populates AppStateService.UpdateAvailable
            // so the sidebar and Settings can surface "update available" indicators.
            // Fire-and-forget: silent on network failure, no UI blocking.
            _ = AppStateService.Instance.CheckForUpdatesAsync();

            // Spatial audio
            _dolbyMonitor.StatusChanged += OnDolbyStatusChanged;
            StartDolbyMonitor();

            // Log monitor (auto-streaming detection)
            StartAutoStreamingMonitor();

            // TCP bridge (StreamLight → StreamTweak commands)
            _bridge.PrepareRequested    += OnBridgePrepareRequested;
            _bridge.RestoreRequested    += OnBridgeRestoreRequested;
            _bridge.ShutdownRequested   += OnBridgeShutdownRequested;
            _bridge.SessionDataReceived += OnSessionDataReceived;

            // Bridge authentication (7.2.0): mandatory. Only StreamLight clients the
            // user has approved on this host may issue commands; the ability to turn
            // authentication off was removed (the previous BridgeRequireAuth toggle).
            // Configured before Start() so the very first incoming connection is gated.
            var bridgeAuth = new BridgeAuthService();
            AppStateService.Instance.BridgeAuth = bridgeAuth;
            _bridge.AuthService = bridgeAuth;
            _bridge.RequireAuth = true;
            bridgeAuth.ApprovalRequested += client =>
                _dispatcher.TryEnqueue(() => MainWindow?.ShowBridgeApproval(client));

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
            _bridge.TailscaleProvider  = () =>
            {
                var (detected, ip) = TailscaleDetector.Detect();
                return detected && !string.IsNullOrEmpty(ip) && ip != "IP unknown" ? ip : "NOT_DETECTED";
            };
            _bridge.UpdateStateProvider = () => WindowsUpdateState.ToJson();

            // Remote "Update host" — relay scan/install/poll to the LocalSystem service,
            // which drives Windows Update Agent. UPDATE_NOW reboots, so the bridge only
            // raises UpdateInstallRequested for a verified-authenticated command.
            _bridge.UpdateCheckRequested   += () => SpeedChanger.StartUpdateCheck();
            _bridge.UpdateInstallRequested += scope => SpeedChanger.StartUpdateInstall(scope);
            _bridge.UpdateProgressProvider  = () => SpeedChanger.GetUpdateProgress();

            // Wire AppStateService action delegates
            AppStateService.Instance.StartStreamingModeAction  = StartManualStreamingMode;
            AppStateService.Instance.StopStreamingModeAction   = () => HandleAutoStreamStop("User");
            AppStateService.Instance.RequestStopStreamAction   = () => _stopStreamRequested = true;
            AppStateService.Instance.StartDebugModeAction      = StartDebugSession;
            AppStateService.Instance.StopDebugModeAction       = () => { StopDebugSession(); return Task.CompletedTask; };
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
                AppStateService.Instance.CurrentAudioDeviceName = device;
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

            // Load previous run's metadata cache immediately so the
            // Game Library page shows data before the background refresh completes.
            GameMetadataService.LoadFromDisk();

            // Auto-sync game library if enabled.
            // After completion, raise SettingsChanged so HomeViewModel refreshes
            // the "last sync" tile with the timestamp just written by the sync.
            // Auto-sync then refresh metadata sequentially so RefreshAsync always
            // receives a fully-populated game list, never an empty one from a race.
            _ = Task.Run(async () =>
            {
                if (GameLibraryState.Current.SyncEnabled)
                {
                    await GameLibraryService.PerformSyncAsync();
                    _dispatcher.TryEnqueue(AppStateService.Instance.RaiseSettingsChanged);
                }
                await GameMetadataService.RefreshAsync(GameLibraryState.Current.Games);
            });

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
            while (true)
            {
                // Capture reference atomically — prevents NullReferenceException if
                // Cleanup() sets _activationEvent to null between the null check and WaitOne().
                var ev = _activationEvent;
                if (ev == null) return;
                try { ev.WaitOne(); }
                catch (ObjectDisposedException) { return; } // handle disposed during shutdown
                if (_activationEvent == null) return;       // disposed while waiting — skip ShowMainWindow
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
            AppStateService.Instance.CurrentAudioDeviceName = _audioOutputDevice;
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
                StartCheckpointTimer();
                StartInactivityTimer();
                AppStateService.Instance.IsStreamingModeActive = true;
                AppStateService.Instance.IsSessionActive = true;
                UpdateTrayStreamingState(true);

                // Start process monitor so Bridge-initiated sessions also detect games.
                _sessionProcessMonitor?.Dispose();
                _sessionProcessMonitor = new SessionProcessMonitor(GameLibraryState.Current.Games);
                _sessionProcessMonitor.Start();

                await Task.Run(() => ApplySpeed(oneGbpsKey));
                _ = PollForNicReconnectAsync();

                NotificationService.Show("StreamTweak Ready",
                    "Network set to 1 Gbps. Connect within 30 seconds or speed will be restored.");
            }
            catch (Exception ex) { DebugLogger.Log($"[Bridge] HandleBridgePrepareAsync failed: {ex}"); }
        }

        private void OnBridgeRestoreRequested()
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_isAutoStreamingActive || _isAutoSessionActive)
                    _ = HandleAutoStreamStop("User");
            });
        }

        // An approved StreamLight client asked the host to power off (Power → Host/Both).
        // The bridge only raises this for a verified-authenticated SHUTDOWN, so no further
        // auth check is needed here. Runs in the interactive UI process, which already
        // holds SeShutdownPrivilege — no service/pipe round-trip required.
        private void OnBridgeShutdownRequested(bool installUpdates)
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    // No on-screen toast here: the host is typically unattended for a
                    // remote power-off, and the shutdown tears down the notification
                    // shell, so a toast would race it. DebugLogger is the trace.
                    DebugLogger.Log($"[Bridge] {(installUpdates ? "SHUTDOWN_UPDATE" : "SHUTDOWN")} requested by approved client — powering off host");

                    // Best-effort: close out any active session so it is not left dangling.
                    if (_isAutoSessionActive)
                    {
                        FinalizeSessionTelemetry();
                        StopCheckpointTimer();
                        var games = _sessionProcessMonitor?.GetDetectedGames();
                        SessionLogger.EndSession("Host Shutdown", games);
                    }

                    ShutdownHost(installUpdates);
                }
                catch (Exception ex) { DebugLogger.Log($"[Bridge] OnBridgeShutdownRequested failed: {ex}"); }
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
                            _ = HandleAutoStreamStart(skipNicThrottle: true);
                        }
                    });
                    return;
                }

                var hostSample = _metricsCollector.GetLatestSample();
                _telemetryAccumulator.AddBatch(batch, hostSample);

                // Forward every sample to the live home-card charts (preserves
                // chronological order even on the final flush batch).
                foreach (var s in batch.Samples)
                    AppStateService.Instance.RaiseLiveSample(s.RttAvg, s.BitrateAvgMbps, s.Drops, s.FpsAvg);
            }
            catch (Exception ex) { DebugLogger.Log($"[Bridge] OnSessionDataReceived failed: {ex}"); }
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
                StopCheckpointTimer();
                SessionLogger.EndSession("App Closed");
            }
            try
            {
                _bridge.PrepareRequested    -= OnBridgePrepareRequested;
                _bridge.RestoreRequested    -= OnBridgeRestoreRequested;
                _bridge.ShutdownRequested   -= OnBridgeShutdownRequested;
                _bridge.SessionDataReceived -= OnSessionDataReceived;
                _bridge.Dispose();
            }
            catch { }
            _traySpeedTimer?.Stop();
            _traySpeedTimer = null;
            _metricsCollector.Dispose();
            StopLogMonitorForced();
            _dolbyMonitor.Disable();
            try { _nvidiaSentinel?.Dispose(); } catch { }
            _nvidiaSentinel = null;
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
                // Collect detected games BEFORE ending the session — mirrors HandleAutoStreamStop.
                // Previously this called EndSession without gamesDetected, so sessions that
                // ended via host shutdown always had GamesDetected=null (monitor never ran)
                // even though the process monitor was active throughout the session.
                List<string>? detectedGames = null;
                if (_sessionProcessMonitor != null)
                {
                    detectedGames = _sessionProcessMonitor.GetDetectedGames();
                    _sessionProcessMonitor.Dispose();
                    _sessionProcessMonitor = null;
                }
                FinalizeSessionTelemetry();
                StopCheckpointTimer();
                SessionLogger.EndSession("Host Shutdown", detectedGames);
            }
        }

    }
}
