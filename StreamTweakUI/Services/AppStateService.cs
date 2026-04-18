using StreamTweak;

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

        // ── Streaming mode active (NIC throttled) ─────────────────────────────

        private bool _isStreamingModeActive;
        public bool IsStreamingModeActive
        {
            get => _isStreamingModeActive;
            set
            {
                if (_isStreamingModeActive == value) return;
                _isStreamingModeActive = value;
                StreamingModeChanged?.Invoke(this, value);
            }
        }
        public event EventHandler<bool>? StreamingModeChanged;

        // ── Actions wired by App.xaml.cs ──────────────────────────────────────

        /// <summary>Start manual streaming mode (throttle NIC to 1 Gbps).</summary>
        public Func<Task>? StartStreamingModeAction { get; set; }

        /// <summary>Stop streaming mode and restore NIC speed.</summary>
        public Func<Task>? StopStreamingModeAction { get; set; }

        /// <summary>
        /// Signal StreamLight to stop the active streaming session via the next STATS response.
        /// Sets a one-shot flag consumed by the StatsProvider lambda in App.xaml.cs.
        /// </summary>
        public Action? RequestStopStreamAction { get; set; }

        /// <summary>Apply a specific speed key on a specific adapter immediately.</summary>
        public Func<string, string, Task>? ApplyAdapterSpeedAction { get; set; }

        // ── Audio monitor live update (wired by App.xaml.cs) ──────────────────

        /// <summary>Enable or disable the spatial audio monitor at runtime.</summary>
        public Action<bool>? SetAudioMonitorEnabledAction { get; set; }

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

        // ── Settings changed notification ─────────────────────────────────────

        /// <summary>
        /// Fired when any user-facing toggle (Auto Mode, Spatial Audio, HDR, Auto HDR, audio
        /// device/format) changes value. HomeViewModel subscribes to re-run LoadStatusAsync
        /// so its status tiles stay current while the Home tab is open.
        /// </summary>
        public event EventHandler? SettingsChanged;

        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
