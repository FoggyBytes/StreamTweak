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
        private LinkSpeedManager?     _linkSpeed;
        // Mirrors the session state the icon already shows, so the tooltip can name it too.
        private bool                  _trayStreamingActive;
        // Keeps the tray tooltip's speed line current (see App.Tray.cs). Low-frequency.
        private DispatcherQueueTimer? _traySpeedTimer;
        // Each refresh walks every network interface (~7 ms), and this is the last thing
        // still ticking with the window hidden. The tooltip only exists while the pointer
        // rests on the tray icon, and a stream-driven speed change refreshes it directly
        // via PollForNicReconnectAsync, so a slow cadence loses nothing. Issue #7.
        private const int TRAY_SPEED_REFRESH_MS = 15_000;

        // ── Single-instance guard ────────────────────────────────────────────
        private static Mutex?      _singleInstanceMutex;
        private EventWaitHandle?   _activationEvent;
        private const string MutexName = "StreamTweak_SingleInstance_v6";
        private const string EventName = "StreamTweak_Activate_v6";

        // ── NIC / streaming state ────────────────────────────────────────────
        private string _adapterName = "Ethernet";
        private bool _isAutoSessionActive = false;     // session is being tracked
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


        // ── Backend services ─────────────────────────────────────────────────
        private readonly StreamTweakBridge _bridge = new();
        private readonly HostMetricsCollector _metricsCollector = new();

        // Watches for the launched game's window so the client can hold its launch curtain up
        // until the game is on screen instead of dropping the user into a reconfiguring desktop.
        private readonly LaunchWatcher _launchWatcher = new();

        // Puts our Desktop/Steam tiles back when the streaming server's own updater reinstalls
        // its defaults over them. Without it the setting stayed on and stopped doing anything.
        private HostAssetsGuard? _hostAssetsGuard;
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
            // Set before Activate(): the pages are constructed and navigated by the window's
            // own ctor, so their timers consult this on the way up. Under --minimized the
            // window is never shown, and nothing on it should be polling.
            AppStateService.Instance.SetMainWindowVisible(!startMinimized);
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

            // Link speed. Everything that touches the adapter lives here; the client drives it
            // over SETSPEED before connecting, because the server log reports a session only
            // once it already exists — far too late to renegotiate a link.
            _linkSpeed = new LinkSpeedManager(new ConfigLinkSpeedStore());
            // Independent of the log-derived SessionActive flag, which a server that never logs a
            // disconnect leaves stuck at false while it is still streaming.
            _linkSpeed.LiveSessionProbe = LogParser.HasActiveMoonlightSession;
            _linkSpeed.Notify  += (title, body) => _dispatcher.TryEnqueue(() => NotificationService.Show(title, body));
            _linkSpeed.Changed += () => _dispatcher.TryEnqueue(() =>
            {
                AppStateService.Instance.RaiseLinkSpeedChanged();
                // The tray's "Speed: …" label would otherwise sit stale until its 4 s timer.
                _ = PollForNicReconnectAsync();
            });
            AppStateService.Instance.LinkSpeed = _linkSpeed;
            _linkSpeed.RecoverAtStartup();

            // TCP bridge (StreamLight → StreamTweak commands)
            _bridge.LinkSpeed            = _linkSpeed;
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
            _bridge.GameStateProvider  = () => _launchWatcher.ToJson();
            _bridge.LastSessionProvider = () => LastSessionReport.BuildJson();
            _bridge.AppStoresProvider  = () => GameLibraryState.Current.ToAppStoresJson();
            _bridge.TailscaleProvider  = () =>
            {
                var (detected, ip) = TailscaleDetector.Detect();
                return detected && !string.IsNullOrEmpty(ip) && ip != "IP unknown" ? ip : "NOT_DETECTED";
            };
            _bridge.UpdateStateProvider = () => WindowsUpdateState.ToJson();
            _bridge.LockStateProvider   = () => LockState.ToJson();

            // The tile guard. Enabled state comes from config rather than from the backup files
            // still sitting in the assets folder: an updater that wipes the folder would take
            // those with it, and the guard would then quietly stop guarding.
            _hostAssetsGuard = new HostAssetsGuard(
                Path.Combine(AppContext.BaseDirectory, "Resources"),
                () => HostAssetsManager.GetAssetsDirectory(LogParser.FindStreamingAppInfo()),
                (dir, desktopSrc, steamSrc) => HostAssetsManager.SwapAsync(dir, desktopSrc, steamSrc))
            {
                EnabledProvider = () => ConfigService.GetBool("HostTilesApplied")
            };
            AppStateService.Instance.HostAssetsGuard = _hostAssetsGuard;
            _hostAssetsGuard.Start();
            _bridge.UnlockSessionMarked += OnBridgeUnlockSessionMarked;

            // Dashboard idle vitals — the collector already samples every second from
            // boot (it feeds the STATS bridge command); this just lets the Dashboard
            // read the same snapshot while no session is running. Display-only.
            AppStateService.Instance.HostMetricsProvider = () => _metricsCollector.GetLatestSample();

            // Remote "Update host" — relay scan/install/poll to the LocalSystem service,
            // which drives Windows Update Agent. UPDATE_NOW reboots, so the bridge only
            // raises UpdateInstallRequested for a verified-authenticated command.
            _bridge.UpdateCheckRequested   += () => SpeedChanger.StartUpdateCheck();
            _bridge.UpdateInstallRequested += scope => SpeedChanger.StartUpdateInstall(scope);
            _bridge.UpdateProgressProvider  = () => SpeedChanger.GetUpdateProgress();

            // Wire AppStateService action delegates
            AppStateService.Instance.RequestStopStreamAction   = () => _stopStreamRequested = true;
            AppStateService.Instance.StartDebugModeAction      = StartDebugSession;
            AppStateService.Instance.StopDebugModeAction       = () => { StopDebugSession(); return Task.CompletedTask; };

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
                }
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

        /// <summary>Updates the tray icon and the tooltip's session line.</summary>
        public void UpdateTrayStreamingState(bool isActive)
        {
            _trayStreamingActive = isActive;
            RefreshTrayTooltip();
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

        // ── TCP Bridge handlers ───────────────────────────────────────────────

        // The client stopped the session deliberately (quit the app / the Desktop tile), as opposed
        // to merely disconnecting. That is the one case where the host can be certain the user has
        // finished, so the link goes back immediately instead of parking — and it is the only way
        // to know it for a Desktop session, which has no process to watch.
        private void OnBridgeRestoreRequested()
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_isAutoSessionActive)
                    _ = HandleAutoStreamStop("User");

                _linkSpeed?.RestoreNow("the client stopped the session");
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
                            _ = HandleAutoStreamStart(retrospective: true);
                        }
                    });
                    return;
                }

                // Client heartbeat: a session is active and the client is still sending data.
                // Arms/refreshes the watchdog that ends the session if this telemetry goes
                // silent (client gone) even when the server never logs a disconnect.
                //
                // This method runs on the bridge's TCP thread (see the TryEnqueue above), but a
                // DispatcherQueueTimer must be created and started on its dispatcher thread —
                // doing it here left the watchdog silently never ticking. The null check just
                // avoids queueing work every second; EnsureHeartbeatWatchdog re-checks on the
                // UI thread, where it is the only writer, so a racy read here is harmless.
                _lastSessionDataUtc = DateTime.UtcNow;
                if (_heartbeatWatchdog == null)
                    _dispatcher.TryEnqueue(EnsureHeartbeatWatchdog);

                var hostSample = _metricsCollector.GetLatestSample();
                _telemetryAccumulator.AddBatch(batch, hostSample);

                // Session-constant, so it rides the batch rather than each sample.
                // Assigned unconditionally: an older client reports 0 and the Dashboard
                // falls back to showing the delivered rate alone.
                AppStateService.Instance.CurrentTargetBitrateMbps = batch.TargetBitrateMbps;

                // Forward every sample to the live Dashboard cockpit (preserves
                // chronological order even on the final flush batch). Client fields
                // come from the sample; host compute from the just-read host snapshot.
                foreach (var s in batch.Samples)
                    AppStateService.Instance.RaiseLiveSample(new AppStateService.LiveSample(
                        s.RttAvg, s.JitterAvg, s.BitrateAvgMbps, s.Drops, s.FpsAvg,
                        s.HostLatencyAvg, hostSample.Gpu, hostSample.GpuEnc, hostSample.Cpu));
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
                // Collect detected games BEFORE ending the session — mirrors OnSystemSessionEnding.
                // Closing StreamTweak mid-session used to record GamesDetected=null even with the
                // monitor running all along, which also made the session look game-less to the
                // "only record sessions with a game" rule.
                List<string>? detectedGames = null;
                if (_sessionProcessMonitor != null)
                {
                    detectedGames = _sessionProcessMonitor.GetDetectedGames();
                    _sessionProcessMonitor.Dispose();
                    _sessionProcessMonitor = null;
                }
                FinalizeSessionTelemetry();
                StopCheckpointTimer();
                SessionLogger.EndSession("App Closed", detectedGames);
            }
            try
            {
                _bridge.RestoreRequested     -= OnBridgeRestoreRequested;
                _bridge.ShutdownRequested    -= OnBridgeShutdownRequested;
                _bridge.SessionDataReceived  -= OnSessionDataReceived;
                _bridge.UnlockSessionMarked  -= OnBridgeUnlockSessionMarked;
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
