using StreamTweak;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace StreamTweak.Services
{
    /// <summary>
    /// Singleton that holds runtime application state shared between App.xaml.cs and ViewModels.
    /// Replaces the WPF pattern of calling settingsWindow?.SetSessionActive() directly.
    /// </summary>
    public sealed class AppStateService
    {
        public static AppStateService Instance { get; } = new();

        // ── Session active ────────────────────────────────────────────────────

        private bool _isSessionActive;
        public bool IsSessionActive
        {
            get => _isSessionActive;
            set
            {
                if (_isSessionActive == value) return;
                _isSessionActive = value;
                SessionStateChanged?.Invoke(this, value);
            }
        }
        public event EventHandler<bool>? SessionStateChanged;

        // "Streaming mode active" (IsStreamingModeActive + StreamingModeChanged) was removed in
        // 8.1.0: whether the link is switched now lives on LinkSpeedManager, which raises
        // LinkSpeedChanged below. Nothing read the old pair once the Network page stopped
        // owning the switch.

        // ── Actions wired by App.xaml.cs ──────────────────────────────────────

        // ── Link speed ────────────────────────────────────────────────────────
        // The state machine itself, shared with the Network page. Clients drive it over
        // the bridge; the host only grants permission and can force a restore.

        public LinkSpeedManager? LinkSpeed { get; set; }

        /// <summary>Raised whenever the link-speed state changes, on the UI thread.</summary>
        public event Action? LinkSpeedChanged;

        public void RaiseLinkSpeedChanged() => LinkSpeedChanged?.Invoke();

        /// <summary>
        /// Signal StreamLight to stop the active streaming session via the next STATS response.
        /// Sets a one-shot flag consumed by the StatsProvider lambda in App.xaml.cs.
        /// </summary>
        public Action? RequestStopStreamAction { get; set; }

        /// <summary>Start a synthetic debug session (no NIC throttle, no real stream).</summary>
        public Func<Task>? StartDebugModeAction { get; set; }

        /// <summary>Stop the active debug session.</summary>
        public Func<Task>? StopDebugModeAction { get; set; }

        /// <summary>
        /// True while a debug session is active. Written by App.xaml.cs, read by
        /// SettingsViewModel.Load() so the toggle restores its state on re-navigation.
        /// </summary>
        public bool IsDebugModeActive { get; set; }

        // ── Audio monitor live update (wired by App.xaml.cs) ──────────────────

        /// <summary>Enable or disable the spatial audio monitor at runtime.</summary>
        public Action<bool>? SetAudioMonitorEnabledAction { get; set; }

        /// <summary>
        /// The audio output device name currently tracked by DolbyAudioMonitor.
        /// Set by App.xaml.cs at startup and whenever the user changes the device.
        /// Always reflects the actual device (never empty — falls back to "Steam Streaming Speakers").
        /// </summary>
        public string CurrentAudioDeviceName { get; set; } = "Steam Streaming Speakers";

        /// <summary>Change the target audio output device on the running monitor.</summary>
        public Action<string>? SetAudioDeviceAction { get; set; }

        /// <summary>Change the spatial audio format (Dolby / Sonic) on the running monitor.</summary>
        public Action<SpatialAudioFormat>? SetAudioFormatAction { get; set; }

        /// <summary>
        /// Immediately activate the configured spatial audio format on the target device.
        /// Returns empty string on success, error message on failure.
        /// Independent from the auto spatial audio monitor state.
        /// </summary>
        public Func<Task<string>>? ActivateSpatialAudioNowAction { get; set; }

        /// <summary>
        /// Deactivate spatial audio right now.
        /// Returns empty string on success, error message on failure.
        /// Runs on a background thread — callers must not assume UI thread.
        /// </summary>
        public Func<Task<string>>? DeactivateSpatialAudioAction { get; set; }

        // ── Spatial audio live status (fires from background DolbyAudioMonitor timer) ──

        private string _currentSpatialAudioStatus = string.Empty;
        public string CurrentSpatialAudioStatus => _currentSpatialAudioStatus;

        /// <summary>
        /// Raised whenever DolbyAudioMonitor reports a status change.
        /// Payload is the status string (e.g. "✓ Dolby Atmos enabled.", "Stream detected — waiting 30s…").
        /// Fired on the calling thread — subscribers must marshal to UI thread if needed.
        /// </summary>
        public event Action<string>? SpatialAudioStatusChanged;

        public void RaiseSpatialAudioStatus(string status)
        {
            _currentSpatialAudioStatus = status;
            SpatialAudioStatusChanged?.Invoke(status);
        }

        /// <summary>10.0: Sync and restart, as the Library job of App.Library reports it.</summary>
        public readonly record struct LibraryJob(
            string Phase, TimeSpan Elapsed, List<string> Added, List<string> Removed,
            string Error, int CooldownSeconds, string Server);

        /// <summary>10.0: starts Sync and restart from the host's Library page — the job StreamLight
        /// starts with LIBSYNC. Returns "OK" or the refusal (ERR_BUSY, ERR_STREAMING, ERR_GAME_RUNNING,
        /// ERR_NO_SERVER).</summary>
        public Func<string>? LibrarySyncStarter { get; set; }

        /// <summary>10.0: Clear library from the host's Library page — the same job, whose first step
        /// empties the list instead of syncing it. Same replies as <see cref="LibrarySyncStarter"/>.
        /// Host only: no bridge verb starts it.</summary>
        public Func<string>? LibraryClearStarter { get; set; }

        /// <summary>10.0: the job's state, for the host's Library page.</summary>
        public Func<LibraryJob>? LibraryJobReader { get; set; }

        /// <summary>10.0: the name of the device streaming now, for Status's "Streaming now";
        /// null when nothing streams or the client sends no telemetry. Set by App.HostSection.</summary>
        public Func<string?>? StreamingClientProvider { get; set; }

        /// <summary>
        /// The tile guard, so the Library page can tell it the user just changed their mind —
        /// otherwise a manual restore would look exactly like the server overwriting us, and
        /// get "repaired" within the minute.
        /// </summary>
        public HostAssetsGuard? HostAssetsGuard { get; set; }

        // ── Glossary deep-link ────────────────────────────────────────────────
        // An ⓘ InfoHint sets this to the term it wants, then navigates to the Glossary;
        // GlossaryView reads + clears it on navigation and scrolls to that row. Plain
        // property (read once at navigation time) — no event needed.
        public string? PendingGlossaryTerm { get; set; }

        /// <summary>
        /// Fired when any user-facing toggle (Auto Mode, Spatial Audio, HDR, Auto HDR, audio
        /// device/format) changes value. HomeViewModel subscribes to re-run LoadStatusAsync
        /// so its status tiles stay current while the Home tab is open.
        /// </summary>
        public event EventHandler? SettingsChanged;

        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

        // ── Main window visibility ────────────────────────────────────────────

        /// <summary>
        /// Whether the main window is actually on screen. Minimising hides the window rather
        /// than shrinking it (and <c>--minimized</c> autostart never shows it at all), but the
        /// pages stay constructed and navigated, so <c>OnNavigatedFrom</c> never fires and any
        /// per-second refresh a page started keeps running against nothing. Pages with polling
        /// timers watch this and suspend while it is false (issue #7).
        /// </summary>
        public bool IsMainWindowVisible { get; private set; }

        /// <summary>Raised when <see cref="IsMainWindowVisible"/> changes.</summary>
        public event EventHandler<bool>? MainWindowVisibilityChanged;

        public void SetMainWindowVisible(bool visible)
        {
            if (IsMainWindowVisible == visible) return;
            IsMainWindowVisible = visible;
            MainWindowVisibilityChanged?.Invoke(this, visible);
        }

        // ── NVIDIA Sentinel ───────────────────────────────────────────────────
        //
        // Singleton NvidiaSentinelService (ported NVIDIA Profile Inspector DRS layer).
        // Null until App.xaml.cs creates it at boot. The "NVIDIA Sentinel" sidebar
        // entry in MainWindow is added at runtime only when this is non-null AND its
        // IsNvidiaAvailable is true (i.e. an NVIDIA GPU + working NVAPI is present).
        public StreamTweak.Nvidia.NvidiaSentinelService? NvidiaSentinel { get; set; }

        // ── Bridge authentication (7.1.0) ─────────────────────────────────────
        //
        // Set by App.xaml.cs at boot. Holds the approved StreamLight client list
        // and verifies per-command signatures on the TCP bridge. The approval
        // dialog (MainWindow) and the Settings "Bridge clients" list read/write
        // through this instance.
        public StreamTweak.BridgeAuthService? BridgeAuth { get; set; }

        // ── Update availability (GitHub releases poll) ────────────────────────
        //
        // The update check used to live in HomeViewModel and only ran when the
        // Home tab was opened. Now centralized here so the result can be surfaced
        // in places that have nothing to do with Home (sidebar, Settings).
        // The check fires once at app startup (App.xaml.cs); on failure it stays
        // silent — UpdateAvailable remains false and no UI is shown.

        private static readonly HttpClient _updateHttp = new();

        private bool _updateAvailable;
        public bool UpdateAvailable
        {
            get => _updateAvailable;
            private set
            {
                if (_updateAvailable == value) return;
                _updateAvailable = value;
                UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private string _latestVersion = string.Empty;
        public string LatestVersion
        {
            get => _latestVersion;
            private set
            {
                if (_latestVersion == value) return;
                _latestVersion = value;
                UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raised when UpdateAvailable or LatestVersion changes.
        /// Subscribers (sidebar, Settings) marshal to UI thread themselves.
        /// </summary>
        public event EventHandler? UpdateAvailabilityChanged;

        /// <summary>
        /// Polls the GitHub releases API once and updates UpdateAvailable / LatestVersion.
        /// Silent on failure (network down, GitHub rate-limit) — no UI ever shows an error.
        /// Safe to fire-and-forget from the UI thread.
        /// </summary>
        public async Task CheckForUpdatesAsync()
        {
            try
            {
                _updateHttp.DefaultRequestHeaders.UserAgent.TryParseAdd("StreamTweak-UpdateCheck");
                string json = await _updateHttp.GetStringAsync(
                    "https://api.github.com/repos/FoggyBytes/StreamTweak/releases/latest");

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
                string? tag = tagEl.GetString();
                if (string.IsNullOrEmpty(tag)) return;

                string latestStr = tag.TrimStart('v', 'V');
                var current = Assembly.GetExecutingAssembly().GetName().Version;
                if (current == null || !Version.TryParse(latestStr, out var latest)) return;

                // Compare on Major.Minor.Build only — ignore Revision (always 0 on our builds).
                bool isNewer = latest > new Version(current.Major, current.Minor, current.Build);
                LatestVersion = latestStr;
                UpdateAvailable = isNewer;
            }
            catch
            {
                // Silent failure — the update notice simply never appears.
            }
        }
    }
}
