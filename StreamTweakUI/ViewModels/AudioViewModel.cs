using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using StreamTweak;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    public sealed class AudioViewModel : ViewModelBase
    {
        // ── Device list ───────────────────────────────────────────────────────

        public ObservableCollection<string> Devices { get; } = new();

        private string? _selectedDevice;
        public string? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (!SetProperty(ref _selectedDevice, value) || value == null) return;
                ConfigService.Set("AudioOutputDevice", value);
                // Update the live monitor immediately (no restart needed).
                AppStateService.Instance.SetAudioDeviceAction?.Invoke(value);
                _ = RefreshCapabilitiesAsync(value);
                _ = CheckFormatActiveAsync();
            }
        }

        // ── Spatial audio enable/disable ──────────────────────────────────────

        private bool _spatialAudioEnabled;
        public bool SpatialAudioEnabled
        {
            get => _spatialAudioEnabled;
            set
            {
                if (!SetProperty(ref _spatialAudioEnabled, value)) return;
                ConfigService.Set("AudioMonitorEnabled", value);
                // Notify App.xaml.cs so the live DolbyAudioMonitor starts/stops immediately.
                AppStateService.Instance.SetAudioMonitorEnabledAction?.Invoke(value);
            }
        }

        // ── Auto-format preference (for the dropdown) ────────────────────────

        private SpatialAudioFormat _spatialFormat = SpatialAudioFormat.DolbyAtmos;

        /// <summary>Index for the auto-format ComboBox: 0 = Dolby, 1 = Sonic.</summary>
        public int AutoFormatIndex
        {
            get => _spatialFormat == SpatialAudioFormat.DolbyAtmos ? 0 : 1;
            set
            {
                if (value < 0) return; // guard against ComboBox pre-init -1
                SetFormat(value == 0 ? SpatialAudioFormat.DolbyAtmos : SpatialAudioFormat.WindowsSonic);
            }
        }

        private void SetFormat(SpatialAudioFormat fmt)
        {
            if (_spatialFormat == fmt) return;
            _spatialFormat = fmt;
            OnPropertyChanged(nameof(AutoFormatIndex));
            ConfigService.Set("AudioSpatialFormat", fmt == SpatialAudioFormat.DolbyAtmos
                ? "DolbyAtmos" : "WindowsSonic");
            // Update the live monitor immediately (no restart needed).
            AppStateService.Instance.SetAudioFormatAction?.Invoke(fmt);
        }

        // ── Real-time format toggle states ───────────────────────────────────

        private bool _isDolbyActive;
        public bool IsDolbyActive
        {
            get => _isDolbyActive;
            private set => SetProperty(ref _isDolbyActive, value);
        }

        private bool _isSonicActive;
        public bool IsSonicActive
        {
            get => _isSonicActive;
            private set => SetProperty(ref _isSonicActive, value);
        }

        // ── Format availability (enables / disables the toggles) ─────────────

        private bool _isDolbyAvailable;
        public bool IsDolbyAvailable
        {
            get => _isDolbyAvailable;
            private set => SetProperty(ref _isDolbyAvailable, value);
        }

        private bool _isSonicAvailable;
        public bool IsSonicAvailable
        {
            get => _isSonicAvailable;
            private set => SetProperty(ref _isSonicAvailable, value);
        }

        // ── Capability status strings ─────────────────────────────────────────

        private string _dolbyStatusText = "—";
        public string DolbyStatusText
        {
            get => _dolbyStatusText;
            private set => SetProperty(ref _dolbyStatusText, value);
        }

        private string _dolbyStatusColorHex = "#99FFFFFF";
        public string DolbyStatusColorHex
        {
            get => _dolbyStatusColorHex;
            private set => SetProperty(ref _dolbyStatusColorHex, value);
        }

        private string _sonicStatusText = "—";
        public string SonicStatusText
        {
            get => _sonicStatusText;
            private set => SetProperty(ref _sonicStatusText, value);
        }

        private string _sonicStatusColorHex = "#99FFFFFF";
        public string SonicStatusColorHex
        {
            get => _sonicStatusColorHex;
            private set => SetProperty(ref _sonicStatusColorHex, value);
        }

        // ── Loading state ─────────────────────────────────────────────────────

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        // ── Live activation status (errors / auto-monitor messages) ──────────

        private string _liveStatusText = string.Empty;
        public string LiveStatusText
        {
            get => _liveStatusText;
            private set
            {
                if (SetProperty(ref _liveStatusText, value))
                    OnPropertyChanged(nameof(HasLiveStatus));
            }
        }

        public bool HasLiveStatus => !string.IsNullOrEmpty(_liveStatusText);

        private string _liveStatusColorHex = "#99FFFFFF";
        public string LiveStatusColorHex
        {
            get => _liveStatusColorHex;
            private set => SetProperty(ref _liveStatusColorHex, value);
        }

        // ── Currently active format (real-time, from polling) ─────────────────

        private string _activeFormatName = string.Empty;
        public string ActiveFormatName
        {
            get => _activeFormatName;
            private set
            {
                if (SetProperty(ref _activeFormatName, value))
                    OnPropertyChanged(nameof(HasActiveFormat));
            }
        }

        public bool HasActiveFormat => !string.IsNullOrEmpty(_activeFormatName);

        // ── Constructor / cleanup ─────────────────────────────────────────────

        private readonly DispatcherQueue _dispatcher;
        private DispatcherQueueTimer?    _pollingTimer;
        private bool                     _checkInProgress;

        public AudioViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _spatialAudioEnabled = ConfigService.GetBool("AudioMonitorEnabled", false);
            string savedDevice   = ConfigService.Get("AudioOutputDevice");
            string savedFormat   = ConfigService.Get("AudioSpatialFormat");
            _spatialFormat = savedFormat == "WindowsSonic"
                ? SpatialAudioFormat.WindowsSonic
                : SpatialAudioFormat.DolbyAtmos;
            _selectedDevice = string.IsNullOrEmpty(savedDevice) ? null : savedDevice;

            AppStateService.Instance.SpatialAudioStatusChanged += OnSpatialAudioStatusChanged;

            // Surface any already-cached status (e.g. "Ready — waiting for next stream…")
            string initial = AppStateService.Instance.CurrentSpatialAudioStatus;
            if (!string.IsNullOrEmpty(initial))
                UpdateLiveStatus(initial);
        }

        public void Unsubscribe()
        {
            AppStateService.Instance.SpatialAudioStatusChanged -= OnSpatialAudioStatusChanged;
            StopPolling();
        }

        private void StartPolling()
        {
            if (_pollingTimer == null)
            {
                _pollingTimer = _dispatcher.CreateTimer();
                _pollingTimer.Interval    = TimeSpan.FromSeconds(2);
                _pollingTimer.IsRepeating = true;
                _pollingTimer.Tick += (_, _) => _ = CheckFormatActiveAsync();
            }
            _pollingTimer.Start();
        }

        private void StopPolling()
        {
            _pollingTimer?.Stop();
        }

        private void OnSpatialAudioStatusChanged(string status)
            => _dispatcher.TryEnqueue(() => UpdateLiveStatus(status));

        private void UpdateLiveStatus(string status)
        {
            LiveStatusText = status;
            LiveStatusColorHex = status.StartsWith("✓") ? "#FF4ade80"
                : status.Contains("error", StringComparison.OrdinalIgnoreCase)
                  || status.StartsWith("Failed")
                  || status.Contains("not available")
                  || status.Contains("not found") ? "#FFDC4632"
                : status.Contains("detected") || status.Contains("retrying") ? "#FFFFC107"
                : "#99FFFFFF";

            // After auto-monitor confirms activation, refresh the real Windows state.
            // Never set IsSpatialAudioActive from status strings — polling owns that.
            if (status.StartsWith("✓"))
                _ = CheckFormatActiveAsync();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            IsLoading = true;
            try
            {
                var devices = await DolbyAudioMonitor.GetAudioOutputDevicesAsync();
                Devices.Clear();
                foreach (var d in devices) Devices.Add(d);

                // Restore saved device or fall back to Steam Streaming Speakers, then first
                string? toSelect = devices.Contains(_selectedDevice ?? "")
                    ? _selectedDevice
                    : devices.FirstOrDefault(d =>
                        d.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase))
                      ?? devices.FirstOrDefault();

                // Set without triggering the setter's RefreshCapabilities yet
                _selectedDevice = toSelect;
                OnPropertyChanged(nameof(SelectedDevice));

                // Persist the resolved device name so that consumers that read
                // "AudioOutputDevice" from config (e.g. HomeViewModel tile subtitle)
                // always see the correct name even before the user changes the selection.
                if (toSelect != null && string.IsNullOrEmpty(ConfigService.Get("AudioOutputDevice")))
                    ConfigService.Set("AudioOutputDevice", toSelect);
            }
            finally
            {
                IsLoading = false;
            }

            // Refresh capabilities and start real-time polling
            if (_selectedDevice != null)
                await RefreshCapabilitiesAsync(_selectedDevice);

            await CheckFormatActiveAsync();
            StartPolling();
        }

        /// <summary>Called by the Dolby ToggleSwitch Toggled handler.</summary>
        public async Task ToggleDolbyAsync(bool activate)
        {
            if (_selectedDevice == null) return;
            LiveStatusText = string.Empty;
            string deviceSnapshot = _selectedDevice;
            if (activate)
            {
                // Run on a background thread: SetDefaultSpatialAudioFormatAsync must be called
                // from an MTA context to actually propagate the change to the system level.
                string err = await Task.Run(() =>
                    DolbyAudioMonitor.ActivateFormatAsync(deviceSnapshot, SpatialAudioFormat.DolbyAtmos));
                if (!string.IsNullOrEmpty(err)) UpdateLiveStatus($"Failed: {err}");
            }
            else
            {
                string? err = await Task.Run(() =>
                    DolbyAudioMonitor.DeactivateSpatialAudioAsync(deviceSnapshot));
                if (err != null) UpdateLiveStatus($"Failed: {err}");
            }
            await Task.Delay(200); // give Windows time to commit the state change
            await CheckFormatActiveAsync();
        }

        /// <summary>Called by the Windows Sonic ToggleSwitch Toggled handler.</summary>
        public async Task ToggleSonicAsync(bool activate)
        {
            if (_selectedDevice == null) return;
            LiveStatusText = string.Empty;
            string deviceSnapshot = _selectedDevice;
            if (activate)
            {
                string err = await Task.Run(() =>
                    DolbyAudioMonitor.ActivateFormatAsync(deviceSnapshot, SpatialAudioFormat.WindowsSonic));
                if (!string.IsNullOrEmpty(err)) UpdateLiveStatus($"Failed: {err}");
            }
            else
            {
                string? err = await Task.Run(() =>
                    DolbyAudioMonitor.DeactivateSpatialAudioAsync(deviceSnapshot));
                if (err != null) UpdateLiveStatus($"Failed: {err}");
            }
            await Task.Delay(200);
            await CheckFormatActiveAsync();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private async Task CheckFormatActiveAsync()
        {
            if (_selectedDevice == null || _checkInProgress) return;
            _checkInProgress = true;
            try
            {
                string deviceSnapshot = _selectedDevice;

                string? activeName = await DolbyAudioMonitor.GetActiveFormatNameAsync(deviceSnapshot);

                // Discard stale result if device changed while awaiting.
                if (_selectedDevice != deviceSnapshot) return;

                bool dolbyActive = activeName == "Dolby Atmos for Headphones";
                bool sonicActive = activeName == "Windows Sonic for Headphones";

                // Assign backing fields directly and raise PropertyChanged unconditionally,
                // bypassing SetProperty's equality check. This ensures OnViewModelPropertyChanged
                // in the code-behind always runs and snaps the toggles to the real Windows state,
                // even when the value hasn't changed (e.g. activation failed silently).
                _isDolbyActive = dolbyActive;
                _isSonicActive = sonicActive;
                OnPropertyChanged(nameof(IsDolbyActive));
                OnPropertyChanged(nameof(IsSonicActive));

                ActiveFormatName = activeName != null ? $"Active: {activeName}" : string.Empty;
            }
            finally { _checkInProgress = false; }
        }

        private async Task RefreshCapabilitiesAsync(string deviceName)
        {
            DolbyStatusText     = "Checking Dolby Atmos for Headphones…";
            DolbyStatusColorHex = "#99FFFFFF";
            SonicStatusText     = "Checking Windows Sonic for Headphones…";
            SonicStatusColorHex = "#99FFFFFF";
            IsDolbyAvailable    = false;
            IsSonicAvailable    = false;

            var (dolby, sonic) = await DolbyAudioMonitor.GetSpatialAudioCapabilitiesAsync(deviceName);

            DolbyStatusText     = dolby ? "Dolby Atmos for Headphones: available"
                                        : "Dolby Atmos for Headphones: unavailable";
            DolbyStatusColorHex = dolby ? "#FF4ade80" : "#FFDC4632";
            IsDolbyAvailable    = dolby;

            SonicStatusText     = sonic ? "Windows Sonic for Headphones: available"
                                        : "Windows Sonic for Headphones: not available";
            SonicStatusColorHex = sonic ? "#FF4ade80" : "#FFDC4632";
            IsSonicAvailable    = sonic;
        }
    }
}
