using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]

namespace StreamTweak
{
    public partial class App : Application
    {
        private TaskbarIcon tb = default!;
        private string adapterName = "Ethernet";
        private readonly string iconOkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Resources\streammodeok.ico");
        private readonly string iconKoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Resources\streammodeko.ico");

        private SettingsWindow? settingsWindow = null;
        private bool isStreamingModeActive = false;
        private string originalSpeedForStreaming = string.Empty;

        // Auto-monitoring
        private StreamingLogMonitor? logMonitor = null;
        private bool isAutoStreamingActive = false;   // true only when NIC has been throttled
        private bool _isAutoSessionActive = false;    // true whenever a streaming session is being tracked
        private string? originalSpeedForAutoStreaming = null;
        private bool isAutoStreamingEnabled = true;

        // Managed apps — paths of apps killed at stream start, relaunched at stream end
        private List<string> _appsToRelaunch = new();
        // Re-entry guard for HandleAutoStreamStart: prevents a second StreamStarted event
        // from starting a new session while the first start is still pending (e.g. during
        // the 7.9-second NIC renegotiation delay).
        private bool _sessionStartInProgress = false;
        // One-shot: armed at startup to allow retrospective detection via SESSIONDATA.
        // Disarmed after the first SESSIONDATA check so session-end races never re-trigger it.
        private bool _bridgeRetrospectiveArmed = true;

        // Spatial audio monitoring
        private readonly DolbyAudioMonitor _dolbyMonitor = new();
        private bool isAudioMonitorEnabled = false;
        private string _lastDolbyStatus = "Disabled.";
        private string _audioOutputDevice = "Steam Streaming Speakers";
        private SpatialAudioFormat _audioSpatialFormat = SpatialAudioFormat.DolbyAtmos;

        // HDR tray state
        private bool _trayHdrEnabled = false;
        private bool _trayAutoHdrEnabled = false;
        private MonitorInfo? _primaryMonitor = null;

        // Moonlight fork TCP bridge
        private readonly StreamTweakBridge _bridge = new();

        // Real-time host metrics (GPU, CPU, VRAM, network) for the STATS command
        private readonly HostMetricsCollector _metricsCollector = new();

        // Session quality telemetry — accumulates client metrics sent by StreamLight
        private readonly TelemetryAccumulator _telemetryAccumulator = new();

        // Inactivity timer — prevents restoring speed on temporary reconnect disconnects
        private System.Windows.Threading.DispatcherTimer? inactivityTimer = null;
        private const int INACTIVITY_TIMEOUT_MS = 30000;

        private string GetConfigPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "config.json");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            this.SessionEnding += App_SessionEnding;

            LoadConfig();
            SessionLogger.Initialize();

            tb = (TaskbarIcon)FindResource("MyNotifyIcon")!;
            UpdateTrayIcon();
            UpdateTrayMenu();

            // Initialize WinRT toast notifications
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Resources\streamtweak.ico");
            ToastHelper.Initialize("StreamTweak", iconPath);

            _dolbyMonitor.StatusChanged   += OnDolbyStatusChanged;
            _dolbyMonitor.FormatActivated += OnDolbyFormatActivated;
            StartDolbyMonitor();
            StartAutoStreamingMonitor();
            _ = InitHdrStateAsync();

            // Start Moonlight fork TCP bridge
            _bridge.PrepareRequested   += OnBridgePrepareRequested;
            _bridge.RestoreRequested   += OnBridgeRestoreRequested;
            _bridge.SessionDataReceived += OnSessionDataReceived;
            _bridge.Start();
            _bridge.StatusProvider = () => {
                var (mbps, connected) = GetCurrentSpeed();
                return connected ? mbps.ToString() : "UNKNOWN";
            };
            _bridge.StatsProvider      = () => _metricsCollector.GetLatestSample().ToJson();
            _bridge.AppStoresProvider  = () => GameLibraryState.Current.ToAppStoresJson();

            // Auto-sync game library in background if enabled
            if (GameLibraryState.Current.SyncEnabled)
                _ = GameLibraryService.PerformSyncAsync();
        }

        private void LoadConfig()
        {
            try
            {
                string configPath = GetConfigPath();
                if (!File.Exists(configPath)) return;

                string json = File.ReadAllText(configPath);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("NetworkAdapterName", out var adapterEl))
                    adapterName = adapterEl.GetString() ?? "Ethernet";

                if (root.TryGetProperty("StreamingMode", out var streamingEl))
                    isStreamingModeActive = streamingEl.GetBoolean();

                if (root.TryGetProperty("OriginalSpeed", out var originalSpeedEl))
                    originalSpeedForStreaming = originalSpeedEl.GetString() ?? string.Empty;

                if (root.TryGetProperty("AutoStreamingEnabled", out var autoEl))
                    isAutoStreamingEnabled = autoEl.GetBoolean();
                else
                    isAutoStreamingEnabled = true;

                if (root.TryGetProperty("AudioMonitorEnabled", out var audioEl))
                    isAudioMonitorEnabled = audioEl.GetBoolean();

                if (root.TryGetProperty("AudioOutputDevice", out var devEl))
                    _audioOutputDevice = devEl.GetString() ?? "Steam Streaming Speakers";

                if (root.TryGetProperty("AudioSpatialFormat", out var fmtEl))
                    _audioSpatialFormat = fmtEl.GetString() == "WindowsSonic"
                        ? SpatialAudioFormat.WindowsSonic
                        : SpatialAudioFormat.DolbyAtmos;
            }
            catch { }

            _dolbyMonitor.TargetDeviceName = _audioOutputDevice;
            _dolbyMonitor.SpatialFormat    = _audioSpatialFormat;
        }

        private bool IsCurrentSpeed1G()
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            if (ni?.OperationalStatus == OperationalStatus.Up)
            {
                long mbps = ni.Speed / 1_000_000;
                return mbps >= 900 && mbps <= 1100;
            }
            return false;
        }

        private (long mbps, bool connected) GetCurrentSpeed()
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            if (ni?.OperationalStatus == OperationalStatus.Up)
                return (ni.Speed / 1_000_000, true);
            return (0, false);
        }

        private void UpdateTrayIcon()
        {
            tb.Icon = new System.Drawing.Icon(_isAutoSessionActive ? iconOkPath : iconKoPath);
        }

        private bool UpdateIconBasedOnSpeed(bool showToast = false)
        {
            try
            {
                var (mbps, connected) = GetCurrentSpeed();

                if (!connected)
                {
                    tb.ToolTipText = $"StreamTweak\n{adapterName}: Disconnected / Negotiating";
                    return false;
                }

                string speedText = mbps >= 1000
                    ? $"{mbps / 1000.0:0.##} Gbps"
                    : $"{mbps} Mbps";

                tb.ToolTipText = $"StreamTweak\n{adapterName}: {speedText}";

                if (showToast)
                    ToastHelper.Show("Speed Applied", $"{adapterName} is now at {speedText}.");

                return true;
            }
            catch
            {
                tb.ToolTipText = "StreamTweak\n(Status Unknown)";
                return false;
            }
        }

        /// <summary>
        /// Updates all dynamic tray menu items: speed, streaming status, auto mode checkbox.
        /// </summary>
        private void UpdateTrayMenu()
        {
            if (tb?.ContextMenu == null) return;

            var (mbps, connected) = GetCurrentSpeed();
            string speedText = !connected
                ? "Speed: Disconnected"
                : mbps >= 1000
                    ? $"Speed: {mbps / 1000.0:0.##} Gbps"
                    : $"Speed: {mbps} Mbps";

            if (SpeedStatusMenuItem != null)
                SpeedStatusMenuItem.Header = speedText;

            if (StreamingStatusMenuItem != null)
                StreamingStatusMenuItem.Header = isStreamingModeActive
                    ? "Streaming: Active ●"
                    : "Streaming: Inactive";

            if (StreamingModeMenuItem != null)
            {
                if (isStreamingModeActive)
                {
                    StreamingModeMenuItem.Header = "Stop Streaming Mode";
                    StreamingModeMenuItem.IsEnabled = true;
                }
                else
                {
                    StreamingModeMenuItem.Header = "Start Streaming Mode";
                    StreamingModeMenuItem.IsEnabled = !IsCurrentSpeed1G();
                }
            }

            if (AutoModeMenuItem != null)
            {
                AutoModeMenuItem.IsChecked = isAutoStreamingEnabled;
                AutoModeMenuItem.Header = isAutoStreamingEnabled
                    ? "Auto Mode: Enabled"
                    : "Auto Mode: Disabled";
            }

            if (DolbyModeMenuItem != null)
            {
                DolbyModeMenuItem.IsChecked = isAudioMonitorEnabled;
                DolbyModeMenuItem.Header = isAudioMonitorEnabled
                    ? "Spatial Audio: Enabled"
                    : "Spatial Audio: Disabled";
            }

            if (HdrModeMenuItem != null)
            {
                HdrModeMenuItem.IsEnabled = _primaryMonitor?.HdrSupported ?? false;
                HdrModeMenuItem.IsChecked = _trayHdrEnabled;
                HdrModeMenuItem.Header = _trayHdrEnabled ? "HDR: On" : "HDR: Off";
            }

            if (AutoHdrModeMenuItem != null)
            {
                AutoHdrModeMenuItem.IsChecked = _trayAutoHdrEnabled;
                AutoHdrModeMenuItem.Header = _trayAutoHdrEnabled ? "Auto HDR: On" : "Auto HDR: Off";
            }
        }

        private MenuItem? SpeedStatusMenuItem =>
            GetMenuItem("SpeedStatusMenuItem");
        private MenuItem? StreamingStatusMenuItem =>
            GetMenuItem("StreamingStatusMenuItem");
        private MenuItem? StreamingModeMenuItem =>
            GetMenuItem("StreamingModeMenuItem");
        private MenuItem? AutoModeMenuItem =>
            GetMenuItem("AutoModeMenuItem");
        private MenuItem? DolbyModeMenuItem =>
            GetMenuItem("DolbyModeMenuItem");
        private MenuItem? HdrModeMenuItem =>
            GetMenuItem("HdrModeMenuItem");
        private MenuItem? AutoHdrModeMenuItem =>
            GetMenuItem("AutoHdrModeMenuItem");

        private MenuItem? GetMenuItem(string name) =>
            tb?.ContextMenu?.Items.OfType<MenuItem>()
              .FirstOrDefault(m => m.Name == name);

        private void ApplySpeed(string speedKey)
        {
            var speeds = NetworkManager.GetSupportedSpeeds(adapterName);
            if (!speeds.TryGetValue(speedKey, out string? registryValue)) return;

            bool ok = SpeedChanger.Apply(adapterName, registryValue);
            if (!ok) SpeedChanger.ApplyWithUac(adapterName, registryValue);
        }

        private string? Find1GbpsKey()
        {
            var speeds = NetworkManager.GetSupportedSpeeds(adapterName);

            foreach (var kvp in speeds)
            {
                string kl = kvp.Key.ToLower();
                bool nameMatch = (kl.Contains("1 gbps") || kl.Contains("1gbps") ||
                                  kl.Contains("1000")) && kl.Contains("full");
                if (nameMatch || kvp.Value == "6") return kvp.Key;
            }
            return null;
        }

        private void SaveStreamingStateToConfig(bool streamingMode, string originalSpeedKey) =>
            PatchConfig(d => { d["StreamingMode"] = streamingMode; d["OriginalSpeed"] = originalSpeedKey; });

        private void SaveAudioMonitorToConfig(bool enabled) =>
            PatchConfig(d => d["AudioMonitorEnabled"] = enabled);

        private void SaveAutoStreamingToConfig(bool enabled) =>
            PatchConfig(d => d["AutoStreamingEnabled"] = enabled);

        private void PatchConfig(Action<System.Collections.Generic.Dictionary<string, object>> patch)
        {
            try
            {
                string configPath = GetConfigPath();
                string json = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";
                var data = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json)
                           ?? new System.Collections.Generic.Dictionary<string, object>();
                patch(data);
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, JsonSerializer.Serialize(data,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private async void MenuStreamingMode_Click(object sender, RoutedEventArgs e)
        {
            if (!isStreamingModeActive)
            {
                // Capture current speed as original
                var speeds = NetworkManager.GetSupportedSpeeds(adapterName);
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
                if (ni != null)
                {
                    long mbps = ni.Speed / 1_000_000;
                    foreach (var kvp in speeds)
                    {
                        string kl = kvp.Key.ToLower();
                        bool match = mbps >= 2000
                            ? kl.Contains("2.5") || kl.Contains("2500")
                            : kl.Contains(mbps.ToString());
                        if (match) { originalSpeedForStreaming = kvp.Key; break; }
                    }
                }

                string? oneGbpsKey = Find1GbpsKey();
                if (oneGbpsKey == null) return;

                var alert = new StreamingAdjustmentAlert();
                alert.Show();
                await Task.Delay(7900);

                isStreamingModeActive = true;
                SaveStreamingStateToConfig(true, originalSpeedForStreaming);
                _appsToRelaunch = ManagedAppController.KillRunning();
                SessionLogger.StartSession("Manual", originalSpeedForStreaming);
                ApplySpeed(oneGbpsKey);
                await PollForIconUpdate(true);
            }
            else
            {
                if (!string.IsNullOrEmpty(originalSpeedForStreaming))
                    ApplySpeed(originalSpeedForStreaming);

                SessionLogger.EndSession("User");
                ManagedAppController.StartApps(_appsToRelaunch);
                _appsToRelaunch.Clear();
                isStreamingModeActive = false;
                SaveStreamingStateToConfig(false, string.Empty);
                await PollForIconUpdate(true);
                ToastHelper.Show("Streaming Ended", "Network speed restored to original.");
            }

            UpdateTrayMenu();
            settingsWindow?.SyncStreamingState(isStreamingModeActive, originalSpeedForStreaming);
            settingsWindow?.RefreshSessionHistory();
        }

        private void MenuAutoMode_Click(object sender, RoutedEventArgs e)
        {
            isAutoStreamingEnabled = !isAutoStreamingEnabled;
            SaveAutoStreamingToConfig(isAutoStreamingEnabled);
            UpdateTrayMenu();

            if (isAutoStreamingEnabled)
                StartAutoStreamingMonitor();
            else
                StopAutoStreamingMonitor();

            settingsWindow?.SyncAutoStreamingState(isAutoStreamingEnabled);
            settingsWindow?.RefreshHomePanel(
                _trayHdrEnabled, _trayAutoHdrEnabled,
                isAudioMonitorEnabled, _audioSpatialFormat,
                GameLibraryState.Current.SyncEnabled);
        }

        private void MenuDolbyMode_Click(object sender, RoutedEventArgs e)
        {
            isAudioMonitorEnabled = !isAudioMonitorEnabled;
            SaveAudioMonitorToConfig(isAudioMonitorEnabled);
            UpdateTrayMenu();

            if (isAudioMonitorEnabled)
            {
                StartDolbyMonitor();
                StartAutoStreamingMonitor();
            }
            else
            {
                _dolbyMonitor.Disable();
                StopAutoStreamingMonitor();
            }

            settingsWindow?.SyncAudioMonitorState(isAudioMonitorEnabled);
            settingsWindow?.SyncDolbyMonitorStatus(
                _dolbyMonitor.IsEnabled ? "Ready — waiting for next stream…" : "Disabled.");
            settingsWindow?.RefreshHomePanel(
                _trayHdrEnabled, _trayAutoHdrEnabled,
                isAudioMonitorEnabled, _audioSpatialFormat,
                GameLibraryState.Current.SyncEnabled);
        }

        private async Task InitHdrStateAsync()
        {
            try
            {
                var monitors = await HdrService.GetMonitorsAsync();
                _primaryMonitor = monitors.FirstOrDefault(m => !m.IsVirtual && m.HdrSupported)
                                  ?? monitors.FirstOrDefault(m => !m.IsVirtual);
                _trayHdrEnabled = _primaryMonitor?.HdrEnabled ?? false;
                _trayAutoHdrEnabled = await HdrService.GetAutoHdrAsync();
                UpdateTrayMenu();
            }
            catch { }
        }

        private async void MenuHdrMode_Click(object sender, RoutedEventArgs e)
        {
            if (_primaryMonitor == null) return;
            bool enable = !_trayHdrEnabled;
            try
            {
                await HdrService.SetHdrAsync(_primaryMonitor.AdapterId, _primaryMonitor.TargetId, enable);
                _primaryMonitor.HdrEnabled = enable;
                _trayHdrEnabled = enable;
                UpdateTrayMenu();
                settingsWindow?.RefreshDisplayPanelIfVisible();
                settingsWindow?.RefreshHomePanel(
                    _trayHdrEnabled, _trayAutoHdrEnabled,
                    isAudioMonitorEnabled, _audioSpatialFormat,
                    GameLibraryState.Current.SyncEnabled);
            }
            catch { }
        }

        private async void MenuAutoHdrMode_Click(object sender, RoutedEventArgs e)
        {
            bool enable = !_trayAutoHdrEnabled;
            try
            {
                await HdrService.SetAutoHdrAsync(enable);
                _trayAutoHdrEnabled = enable;
                UpdateTrayMenu();
                settingsWindow?.RefreshDisplayPanelIfVisible();
                settingsWindow?.RefreshHomePanel(
                    _trayHdrEnabled, _trayAutoHdrEnabled,
                    isAudioMonitorEnabled, _audioSpatialFormat,
                    GameLibraryState.Current.SyncEnabled);
            }
            catch { }
        }

        private async Task PollForIconUpdate(bool showToast)
        {
            tb.ToolTipText = $"StreamTweak\n{adapterName}: Renegotiating...";
            await Task.Delay(3000);

            int attempts = 0;
            bool connected = false;

            while (attempts < 15)
            {
                connected = UpdateIconBasedOnSpeed(false);
                if (connected) break;
                await Task.Delay(1000);
                attempts++;
            }

            if (connected) UpdateIconBasedOnSpeed(showToast);
            UpdateTrayMenu();
            settingsWindow?.RefreshCurrentSpeedDisplay();

            var (mbps, conn) = GetCurrentSpeed();
            string speedTxt = !conn ? "—"
                : mbps >= 1000 ? $"{mbps / 1000.0:0.##} Gbps"
                : $"{mbps} Mbps";
            if (isAutoStreamingActive && conn)
                speedTxt += "  ·  throttled";
            settingsWindow?.UpdateHomeNicSpeed(speedTxt);
        }

        private void OpenSettings()
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow();

                settingsWindow.ExitRequested += (s, args) => ExitApp();

                settingsWindow.SpeedApplied += async (s, args) =>
                {
                    LoadConfig();
                    await PollForIconUpdate(true);
                };

                settingsWindow.StreamingModeChanged += (s, args) =>
                {
                    LoadConfig();
                    UpdateTrayMenu();
                };

                settingsWindow.AutoStreamingEnabledChanged += (s, args) =>
                {
                    LoadConfig();
                    UpdateTrayMenu();
                    if (isAutoStreamingEnabled)
                        StartAutoStreamingMonitor();
                    else
                        StopAutoStreamingMonitor();
                };

                settingsWindow.AudioMonitorEnabledChanged += (s, args) =>
                {
                    LoadConfig();
                    if (isAudioMonitorEnabled)
                    {
                        StartDolbyMonitor();
                        StartAutoStreamingMonitor();
                    }
                    else
                    {
                        _dolbyMonitor.Disable();
                        StopAutoStreamingMonitor();
                    }
                    UpdateTrayMenu();
                };

                settingsWindow.AudioDeviceChanged += (s, args) =>
                {
                    _audioOutputDevice = settingsWindow.SelectedAudioDevice;
                    _dolbyMonitor.TargetDeviceName = _audioOutputDevice;
                };

                settingsWindow.AudioFormatChanged += (s, args) =>
                {
                    _audioSpatialFormat = settingsWindow.SelectedAudioFormat;
                    _dolbyMonitor.SpatialFormat = _audioSpatialFormat;
                };

                settingsWindow.Closed += (s, args) =>
                {
                    LoadConfig();
                    UpdateTrayMenu();
                    settingsWindow = null;
                };

                settingsWindow.SyncAudioMonitorState(isAudioMonitorEnabled);
                settingsWindow.SyncDolbyMonitorStatus(_lastDolbyStatus);
                settingsWindow.SetSessionActive(_isAutoSessionActive);
                settingsWindow.RefreshHomePanel(
                    _trayHdrEnabled,
                    _trayAutoHdrEnabled,
                    isAudioMonitorEnabled,
                    _audioSpatialFormat,
                    GameLibraryState.Current.SyncEnabled);
                _ = settingsWindow.RefreshHomeHdrAsync();

                var (mbps, conn) = GetCurrentSpeed();
                string speedTxt = !conn ? "—"
                    : mbps >= 1000 ? $"{mbps / 1000.0:0.##} Gbps"
                    : $"{mbps} Mbps";
                if (isAutoStreamingActive && conn)
                    speedTxt += "  ·  throttled";
                settingsWindow.UpdateHomeNicSpeed(speedTxt);

                settingsWindow.Show();
            }
            else
            {
                if (!settingsWindow.IsVisible)
                    settingsWindow.Show();
                if (settingsWindow.WindowState == WindowState.Minimized)
                    settingsWindow.WindowState = WindowState.Normal;
                settingsWindow.Activate();
            }
        }

        private void TaskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => OpenSettings();
        private void MenuSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            if (_isAutoSessionActive)
            {
                FinalizeSessionTelemetry();
                SessionLogger.EndSession("Host Shutdown");
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e) => ExitApp();

        private void ExitApp()
        {
            if (_isAutoSessionActive)
            {
                FinalizeSessionTelemetry();
                SessionLogger.EndSession("App Closed");
            }
            _bridge.PrepareRequested    -= OnBridgePrepareRequested;
            _bridge.RestoreRequested    -= OnBridgeRestoreRequested;
            _bridge.SessionDataReceived -= OnSessionDataReceived;
            _bridge.Dispose();
            _metricsCollector.Dispose();
            StopLogMonitorForced();
            _dolbyMonitor.Disable();
            tb?.Dispose();
            Application.Current.Shutdown();
        }

        #region Auto-Streaming Monitoring

        private void StartAutoStreamingMonitor()
        {
            if (logMonitor != null) return;
            if (!isAutoStreamingEnabled && !isAudioMonitorEnabled) return;

            try
            {
                logMonitor = new StreamingLogMonitor();
                logMonitor.StreamingEventDetected += LogMonitor_StreamingEventDetected;
                logMonitor.StartMonitoring();
            }
            catch { }
        }

        private void StopAutoStreamingMonitor()
        {
            if (isAutoStreamingEnabled || isAudioMonitorEnabled) return;
            StopLogMonitorForced();
        }

        private void StopLogMonitorForced()
        {
            try
            {
                if (logMonitor != null)
                {
                    logMonitor.StreamingEventDetected -= LogMonitor_StreamingEventDetected;
                    logMonitor.StopMonitoring();
                    logMonitor.Dispose();
                    logMonitor = null;
                }
            }
            catch { }
        }

        private void LogMonitor_StreamingEventDetected(object? sender, StreamingLogMonitor.StreamingEventArgs e)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (e.Event == LogParser.StreamingEvent.StreamStarted)
                    {
                        _dolbyMonitor.OnStreamingStarted(e.IsRetrospective);
                        if (!isAutoStreamingActive && !_isAutoSessionActive && !_sessionStartInProgress)
                            HandleAutoStreamStart(skipNicThrottle: e.IsRetrospective);
                        else
                            StopInactivityTimer(); // reconnected within grace period
                    }
                    else if (e.Event == LogParser.StreamingEvent.StreamStopped)
                    {
                        _dolbyMonitor.OnStreamingStopped();
                        if (isAutoStreamingActive || _isAutoSessionActive)
                            StartInactivityTimer(); // start grace period — wait for possible reconnect
                    }
                });
            }
            catch { }
        }

        private async void HandleAutoStreamStart(bool skipNicThrottle = false)
        {
            if (isStreamingModeActive || _sessionStartInProgress) return;
            _sessionStartInProgress = true;
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));

                if (ni == null || ni.OperationalStatus != OperationalStatus.Up) return;

                long mbps = ni.Speed / 1_000_000;
                // skipNicThrottle=true when the session was already in progress at StreamTweak startup:
                // renegotiating the NIC mid-session would disrupt an active stream.
                bool needsThrottle = !skipNicThrottle && isAutoStreamingEnabled && mbps >= 1200;
                string capturedOriginalSpeed = string.Empty;

                if (needsThrottle)
                {
                    // Capture original speed key
                    var speeds = NetworkManager.GetSupportedSpeeds(adapterName);
                    foreach (var kvp in speeds)
                    {
                        string kl = kvp.Key.ToLower();
                        bool match = mbps >= 2000
                            ? kl.Contains("2.5") || kl.Contains("2500")
                            : kl.Contains(mbps.ToString());
                        if (match) { capturedOriginalSpeed = kvp.Key; break; }
                    }

                    string? oneGbpsKey = Find1GbpsKey();
                    if (oneGbpsKey == null)
                    {
                        needsThrottle = false; // no suitable 1G key — log session without throttle
                    }
                    else
                    {
                        var alert = new StreamingAdjustmentAlert();
                        alert.Show();
                        await Task.Delay(7900);

                        if (!isAutoStreamingEnabled) { alert.Close(); return; }

                        isAutoStreamingActive = true;
                        isStreamingModeActive = true;
                        originalSpeedForAutoStreaming = capturedOriginalSpeed;
                        ApplySpeed(oneGbpsKey);
                        SaveStreamingStateToConfig(true, capturedOriginalSpeed);
                        UpdateTrayMenu();
                        await PollForIconUpdate(false);

                        ToastHelper.Show("Streaming Detected",
                            "Network speed set to 1 Gbps. Reconnect within 30 seconds.");

                        settingsWindow?.SyncStreamingState(true, capturedOriginalSpeed);
                    }
                }

                // Always log the session, with or without NIC throttle
                _telemetryAccumulator.Reset();
                _appsToRelaunch = ManagedAppController.KillRunning();
                _isAutoSessionActive = true;
                UpdateTrayIcon();
                SessionLogger.StartSession("Auto", capturedOriginalSpeed);
                settingsWindow?.RefreshSessionHistory();
                settingsWindow?.SetSessionActive(true);
            }
            catch { }
            finally
            {
                _sessionStartInProgress = false;
            }
        }

        private async void HandleAutoStreamStop(string endReason = "User")
        {
            try
            {
                if (!isAutoStreamingActive && !_isAutoSessionActive) return;

                StopInactivityTimer();

                if (isAutoStreamingActive)
                {
                    if (!string.IsNullOrEmpty(originalSpeedForAutoStreaming))
                        ApplySpeed(originalSpeedForAutoStreaming);

                    isAutoStreamingActive = false;
                    isStreamingModeActive = false;
                    originalSpeedForAutoStreaming = null;
                    SaveStreamingStateToConfig(false, string.Empty);
                    UpdateTrayMenu();
                    await PollForIconUpdate(false);

                    string toastBody = endReason == "Disconnected"
                        ? "Connection lost. Network speed restored."
                        : "Network speed restored to original.";
                    ToastHelper.Show("Streaming Ended", toastBody);

                    settingsWindow?.SyncStreamingState(false, string.Empty);
                }

                if (_isAutoSessionActive)
                {
                    FinalizeSessionTelemetry();
                    SessionLogger.EndSession(endReason);
                    ManagedAppController.StartApps(_appsToRelaunch);
                    _appsToRelaunch.Clear();
                    _isAutoSessionActive = false;
                    UpdateTrayIcon();
                    settingsWindow?.RefreshSessionHistory();
                    settingsWindow?.SetSessionActive(false);
                }
            }
            catch { }
        }

        /// <summary>
        /// Saves accumulated telemetry for the active session before EndSession is called.
        /// Must be called on the UI thread. Safe to call even if no data has accumulated.
        /// </summary>
        private void FinalizeSessionTelemetry()
        {
            try
            {
                string? sessionId = SessionLogger.ActiveSessionId;
                if (sessionId == null) return;

                var (stats, rttSeries, dropsSeries, bitrateSeries, decodeSeries) = _telemetryAccumulator.Finalize();
                if (stats.SampleCount >= 2)
                {
                    var grade = QualityGradeCalculator.Evaluate(stats, _telemetryAccumulator.TargetFps);
                    SessionLogger.UpdateSessionTelemetry(sessionId, stats, grade, rttSeries, dropsSeries, bitrateSeries, decodeSeries);
                }
                _telemetryAccumulator.Reset();
            }
            catch { }
        }

        private void StartInactivityTimer()
        {
            if (inactivityTimer == null)
            {
                inactivityTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(INACTIVITY_TIMEOUT_MS)
                };
                inactivityTimer.Tick += (s, e) =>
                {
                    StopInactivityTimer();
                    DebugLog("Inactivity timer expired");
                    if (isAutoStreamingActive || _isAutoSessionActive)
                        HandleAutoStreamStop("Disconnected");
                };
            }
            inactivityTimer.Stop(); // reset countdown if already running
            inactivityTimer.Start();
            DebugLog($"Inactivity timer started ({INACTIVITY_TIMEOUT_MS}ms)");
        }

        private void StopInactivityTimer()
        {
            inactivityTimer?.Stop();
            DebugLog("Inactivity timer stopped");
        }

        #endregion

        #region Moonlight Fork Bridge

        /// <summary>
        /// Called when Moonlight fork sends PREPARE.
        /// The user has not launched the stream yet — we set the NIC to 1 Gbps now
        /// so no disconnect occurs when the session starts.
        /// Note: intentionally independent of isAutoStreamingEnabled — the Bridge is a
        /// separate input channel and must work even when Auto Mode is off.
        /// </summary>
        private void OnBridgePrepareRequested()
        {
            // InvokeAsync avoids blocking the UI thread; ApplySpeed runs off-thread via
            // Task.Run to prevent the named-pipe Connect() from freezing the UI.
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (isStreamingModeActive) return;

                    var ni = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));

                    if (ni == null || ni.OperationalStatus != OperationalStatus.Up) return;

                    long mbps = ni.Speed / 1_000_000;
                    if (mbps < 1200) return; // Already at or below 1 Gbps — nothing to do

                    // Capture original speed
                    var speeds = NetworkManager.GetSupportedSpeeds(adapterName);
                    foreach (var kvp in speeds)
                    {
                        string kl = kvp.Key.ToLower();
                        bool match = mbps >= 2000
                            ? kl.Contains("2.5") || kl.Contains("2500")
                            : kl.Contains(mbps.ToString());
                        if (match) { originalSpeedForAutoStreaming = kvp.Key; break; }
                    }

                    string? oneGbpsKey = Find1GbpsKey();
                    if (oneGbpsKey == null) return;

                    // Show alert — user sees it while NIC renegotiates,
                    // before they launch the stream in Moonlight
                    var alert = new StreamingAdjustmentAlert();
                    alert.Show();

                    isAutoStreamingActive = true;
                    isStreamingModeActive = true;
                    _isAutoSessionActive = true;
                    _telemetryAccumulator.Reset();
                    SaveStreamingStateToConfig(true, originalSpeedForAutoStreaming ?? string.Empty);
                    _appsToRelaunch = ManagedAppController.KillRunning();
                    SessionLogger.StartSession("Bridge", originalSpeedForAutoStreaming ?? string.Empty);
                    StartInactivityTimer(); // auto-RESTORE if no client connects within 30s
                    UpdateTrayIcon();
                    settingsWindow?.SetSessionActive(true);
                    UpdateTrayMenu();

                    // Run the blocking named-pipe call off the UI thread
                    await Task.Run(() => ApplySpeed(oneGbpsKey));

                    await PollForIconUpdate(false);

                    ToastHelper.Show("StreamTweak Ready",
                        "Network set to 1 Gbps. Connect within 30 seconds or speed will be restored.");

                    settingsWindow?.SyncStreamingState(true, originalSpeedForAutoStreaming ?? string.Empty);
                    settingsWindow?.RefreshSessionHistory();

                    DebugLog("Bridge: PREPARE handled — NIC set to 1 Gbps, connection timeout started (30s)");
                }
                catch (Exception ex)
                {
                    DebugLog($"Bridge: PREPARE error — {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Called on a thread-pool thread when StreamLight sends a SESSIONDATA batch.
        /// Guards on active session and session ID cross-check before accumulating.
        /// If SESSIONDATA arrives with no active session, StreamTweak restarted mid-stream:
        /// trigger retrospective session start (Dolby activation + session tracking).
        /// </summary>
        private void OnSessionDataReceived(ClientBatch batch)
        {
            try
            {
                if (SessionLogger.ActiveSessionId == null)
                {
                    // SESSIONDATA with no active session: either StreamTweak restarted
                    // mid-stream (legitimate retrospective case) or a race at session end
                    // (last batch arriving after EndSession cleared ActiveSessionId).
                    // The one-shot flag ensures we only act on the startup case — once
                    // disarmed, any later SESSIONDATA with no active session is ignored.
                    if (_bridgeRetrospectiveArmed)
                    {
                        _bridgeRetrospectiveArmed = false; // disarm immediately — one-shot
                        if (!_isAutoSessionActive && !_sessionStartInProgress)
                        {
                            DebugLog("Bridge: SESSIONDATA received with no active session — triggering retrospective start");
                            Application.Current.Dispatcher.BeginInvoke(() =>
                            {
                                if (!_isAutoSessionActive && !_sessionStartInProgress)
                                {
                                    _dolbyMonitor.OnStreamingStarted(isRetrospective: true);
                                    HandleAutoStreamStart(skipNicThrottle: true);
                                }
                            });
                        }
                    }
                    return;
                }

                var hostSample = _metricsCollector.GetLatestSample();
                _telemetryAccumulator.AddBatch(batch, hostSample);
                DebugLog($"Bridge: SESSIONDATA received — {batch.Samples.Count} samples");
            }
            catch (Exception ex)
            {
                DebugLog($"Bridge: SESSIONDATA handler error — {ex.Message}");
            }
        }

        /// <summary>
        /// Called when Moonlight fork sends RESTORE after session ends.
        /// Acts as an explicit fallback alongside the log monitor.
        /// If the log monitor already restored the speed, this is a no-op.
        /// </summary>
        private void OnBridgeRestoreRequested()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                DebugLog("Bridge: RESTORE received");
                if (isAutoStreamingActive)
                    HandleAutoStreamStop("User");
            });
        }

        #endregion

        #region Spatial Audio Monitoring

        private void StartDolbyMonitor()
        {
            if (!isAudioMonitorEnabled || _dolbyMonitor.IsEnabled) return;
            _dolbyMonitor.TargetDeviceName = _audioOutputDevice;
            _dolbyMonitor.SpatialFormat    = _audioSpatialFormat;
            _dolbyMonitor.Enable();
            _lastDolbyStatus = "Ready — waiting for next stream…";
        }

        private void OnDolbyStatusChanged(string status)
        {
            _lastDolbyStatus = status;
            Application.Current.Dispatcher.Invoke(() =>
                settingsWindow?.SyncDolbyMonitorStatus(status));
        }

        private void OnDolbyFormatActivated(SpatialAudioFormat _)
        {
            // Auto-activation changed the real Windows state — sync the format toggles in the UI.
            Application.Current.Dispatcher.Invoke(() =>
                settingsWindow?.RefreshSpatialToggleStatesAsync());
        }

        #endregion

        private static void DebugLog(string message) => DebugLogger.Log(message);
    }
}
