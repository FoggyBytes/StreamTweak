using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using StreamTweak.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace StreamTweak.ViewModels
{
    public sealed class NetworkViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _dispatcher;

        // ── Adapters ──────────────────────────────────────────────────────────

        public ObservableCollection<string> Adapters { get; } = new();

        private string? _selectedAdapter;
        public string? SelectedAdapter
        {
            get => _selectedAdapter;
            set
            {
                if (!SetProperty(ref _selectedAdapter, value) || value == null) return;
                OnAdapterSelected(value);
            }
        }

        // ── Speeds ────────────────────────────────────────────────────────────

        public ObservableCollection<string> Speeds { get; } = new();

        private string? _selectedSpeed;
        /// <summary>The link speed the NIC is switched to while streaming (user's choice).
        /// Persisted so the auto/manual/tray paths apply it instead of a hardcoded 1 Gbps.</summary>
        public string? SelectedSpeed
        {
            get => _selectedSpeed;
            set
            {
                if (!SetProperty(ref _selectedSpeed, value) || value == null) return;
                ConfigService.Set("StreamingLinkSpeed", value);
            }
        }

        /// <summary>"Previous" (the speed captured before the switch) + every supported speed.</summary>
        public ObservableCollection<string> RestoreOptions { get; } = new();

        private string? _selectedRestoreSpeed = "Previous";
        public string? SelectedRestoreSpeed
        {
            get => _selectedRestoreSpeed;
            set
            {
                if (!SetProperty(ref _selectedRestoreSpeed, value) || value == null) return;
                ConfigService.Set("RestoreLinkSpeed", value);
            }
        }

        private string _currentSpeedText = "Detecting…";
        public string CurrentSpeedText
        {
            get => _currentSpeedText;
            private set => SetProperty(ref _currentSpeedText, value);
        }

        // ── Auto Streaming ────────────────────────────────────────────────────

        private bool _autoStreamingEnabled;
        public bool AutoStreamingEnabled
        {
            get => _autoStreamingEnabled;
            set
            {
                if (!SetProperty(ref _autoStreamingEnabled, value)) return;
                ConfigService.Set("AutoStreamingEnabled", value);
            }
        }

        // ── Manual streaming mode ─────────────────────────────────────────────

        private bool _isStreamingModeActive;
        public bool IsStreamingModeActive
        {
            get => _isStreamingModeActive;
            private set
            {
                if (!SetProperty(ref _isStreamingModeActive, value)) return;
                OnPropertyChanged(nameof(StreamingButtonText));
            }
        }

        public string StreamingButtonText => _isStreamingModeActive ? "Restore link speed" : "Switch link speed now";

        // ── UI state ──────────────────────────────────────────────────────────

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        private bool _hasAdapters = true;
        public bool HasAdapters
        {
            get => _hasAdapters;
            private set => SetProperty(ref _hasAdapters, value);
        }

        // ── Tailscale ─────────────────────────────────────────────────────────

        private string _tailscaleIp = string.Empty;
        public string TailscaleIp
        {
            get => _tailscaleIp;
            private set
            {
                if (!SetProperty(ref _tailscaleIp, value)) return;
                OnPropertyChanged(nameof(TailscaleDetected));
            }
        }

        /// <summary>True when Tailscale is installed and a 100.x address was found.</summary>
        public bool TailscaleDetected => !string.IsNullOrEmpty(_tailscaleIp);

        private bool _tailscaleCopiedVisible;
        public bool TailscaleCopiedVisible
        {
            get => _tailscaleCopiedVisible;
            private set => SetProperty(ref _tailscaleCopiedVisible, value);
        }

        private BitmapImage? _tailscaleIcon;
        public BitmapImage? TailscaleIcon
        {
            get => _tailscaleIcon;
            private set
            {
                if (SetProperty(ref _tailscaleIcon, value))
                    OnPropertyChanged(nameof(HasTailscaleIcon));
            }
        }

        public bool HasTailscaleIcon => _tailscaleIcon != null;

        private CancellationTokenSource? _copiedFeedbackCts;

        // ── Registry values / timers ──────────────────────────────────────────

        // Registry values for the currently loaded adapter (key = display name, value = registry value)
        private Dictionary<string, string> _adapterSpeeds = new();

        // Live speed refresh timer
        private DispatcherQueueTimer? _speedRefreshTimer;

        // ── Constructor ───────────────────────────────────────────────────────

        public NetworkViewModel()
        {
            // Capture UI thread dispatcher before any async operations
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _autoStreamingEnabled = ConfigService.GetBool("AutoStreamingEnabled", false);
            _isStreamingModeActive = AppStateService.Instance.IsStreamingModeActive;

            AppStateService.Instance.StreamingModeChanged += (_, isActive) =>
                _dispatcher.TryEnqueue(() => IsStreamingModeActive = isActive);

            // Tailscale detection is synchronous (just iterates NetworkInterface).
            // Run once at construction and expose the result; the user can refresh
            // by navigating away and back.
            RefreshTailscale();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            IsLoading = true;
            try
            {
                // Run CIM query off-thread; ConfigureAwait(false) avoids relying on
                // SynchronizationContext (not guaranteed in WinUI 3 unpackaged).
                var adapters = await Task.Run(NetworkManager.GetPhysicalAdapterNames)
                    .ConfigureAwait(false);
                string savedAdapter = ConfigService.Get("NetworkAdapterName");

                // All ObservableCollection + property updates must be on UI thread.
                _dispatcher.TryEnqueue(() =>
                {
                    Adapters.Clear();
                    foreach (var a in adapters) Adapters.Add(a);

                    if (Adapters.Count == 0)
                    {
                        HasAdapters = false;
                        CurrentSpeedText = "No adapters found";
                        IsLoading = false;
                        return;
                    }

                    HasAdapters = true;
                    SelectedAdapter = Adapters.Contains(savedAdapter) ? savedAdapter : Adapters[0];
                    IsLoading = false;
                });
            }
            catch
            {
                _dispatcher.TryEnqueue(() => IsLoading = false);
            }
        }

        public void StartSpeedRefresh()
        {
            if (_speedRefreshTimer != null) return;
            _speedRefreshTimer = _dispatcher.CreateTimer();
            _speedRefreshTimer.Interval = TimeSpan.FromSeconds(2);
            _speedRefreshTimer.IsRepeating = true;
            _speedRefreshTimer.Tick += (_, _) =>
            {
                if (_selectedAdapter != null)
                    UpdateCurrentSpeed(_selectedAdapter);
            };
            _speedRefreshTimer.Start();
        }

        public void StopSpeedRefresh()
        {
            _speedRefreshTimer?.Stop();
            _speedRefreshTimer = null;
        }

        /// <summary>Manual "Switch now": apply the streaming target link speed (restored on stream end).</summary>
        public async Task SwitchNowAsync()
        {
            if (_isStreamingModeActive) return;
            await (AppStateService.Instance.StartStreamingModeAction?.Invoke() ?? Task.CompletedTask);
        }

        /// <summary>Manual "Restore link speed": undo the switch now.</summary>
        public async Task RestoreNowAsync()
        {
            if (!_isStreamingModeActive) return;
            await (AppStateService.Instance.StopStreamingModeAction?.Invoke() ?? Task.CompletedTask);
        }

        /// <summary>Re-checks Tailscale presence. Call from the UI when re-navigating to the page.</summary>
        public void RefreshTailscale()
        {
            var (detected, ip) = GetTailscaleInfo();
            TailscaleIp = detected ? ip : string.Empty;
            if (detected && _tailscaleIcon == null)
                _ = LoadTailscaleIconAsync();
        }

        /// <summary>Copies the Tailscale IP to the clipboard and shows a 3-second "Copied!" banner.</summary>
        public async Task CopyTailscaleIpAsync()
        {
            if (!TailscaleDetected) return;

            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(TailscaleIp);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

            // Cancel any previous in-flight feedback timer
            _copiedFeedbackCts?.Cancel();
            _copiedFeedbackCts?.Dispose();
            _copiedFeedbackCts = new CancellationTokenSource();
            var token = _copiedFeedbackCts.Token;

            TailscaleCopiedVisible = true;
            try   { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { return; }
            TailscaleCopiedVisible = false;
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void OnAdapterSelected(string adapterName)
        {
            ConfigService.Set("NetworkAdapterName", adapterName);
            UpdateCurrentSpeed(adapterName);
            _ = Task.Run(() => LoadAdapterSpeeds(adapterName));
        }

        private void LoadAdapterSpeeds(string adapterName)
        {
            var speeds = NetworkManager.GetSupportedSpeeds(adapterName);

            var displayKeys = speeds.Keys
                .Where(k => !IsSpeedAtOrBelow100Mbps(k))
                .ToList();

            // Pre-select the entry that matches the current link speed
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            long currentMbps = (ni?.Speed ?? 0) / 1_000_000;
            string? bestMatch = FindBestSpeedMatch(displayKeys, currentMbps);

            string savedTarget  = ConfigService.Get("StreamingLinkSpeed", "");
            string savedRestore = ConfigService.Get("RestoreLinkSpeed", "Previous");

            _dispatcher.TryEnqueue(() =>
            {
                _adapterSpeeds = speeds;
                Speeds.Clear();
                foreach (var k in displayKeys) Speeds.Add(k);

                // Streaming target: saved choice → 1 Gbps default → best match → first.
                SelectedSpeed = displayKeys.Contains(savedTarget) ? savedTarget
                              : FindBestSpeedMatch(displayKeys, 1000)
                              ?? bestMatch
                              ?? (Speeds.Count > 0 ? Speeds[0] : null);

                // Restore options: "Previous" (captured original) + every supported speed.
                RestoreOptions.Clear();
                RestoreOptions.Add("Previous");
                foreach (var k in displayKeys) RestoreOptions.Add(k);

                // Re-assert the selection through the backing field + an explicit notification,
                // NOT through the property setter. Emptying ItemsSource makes the ComboBox null
                // its SelectedItem, and the TwoWay binding writes that null straight back here;
                // the box then stays blank because a plain assignment of the value it already
                // held is swallowed by SetProperty's equality check, so no PropertyChanged is
                // raised and the ComboBox is never told to re-select. This bites "Restore to"
                // and not "Streaming speed" only because _selectedRestoreSpeed starts out at
                // "Previous" while _selectedSpeed starts null. Assigning the field directly is
                // also right on the load path: reading config should not write it back.
                _selectedRestoreSpeed = RestoreOptions.Contains(savedRestore) ? savedRestore : "Previous";
                OnPropertyChanged(nameof(SelectedRestoreSpeed));
            });
        }

        private void UpdateCurrentSpeed(string adapterName)
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));

            if (ni == null) { CurrentSpeedText = "Unknown"; return; }

            long mbps = ni.Speed / 1_000_000;
            // Invariant so the badge reads "2.5 Gbps" (dot), matching the driver speed
            // keys in the ComboBoxes — not the culture-local "2,5 Gbps".
            CurrentSpeedText = mbps <= 0    ? "Negotiating…"
                             : mbps >= 1000 ? $"{(mbps / 1000.0).ToString("0.##", CultureInfo.InvariantCulture)} Gbps"
                             :                $"{mbps} Mbps";
        }

        private static bool IsSpeedAtOrBelow100Mbps(string key)
        {
            // Filter out 10/100 Mbps options — they're not useful for streaming scenarios
            string k = key.ToLowerInvariant();
            return (k.StartsWith("10 ") || k.StartsWith("100 "))
                && !k.Contains("gbps");
        }

        private static string? FindBestSpeedMatch(List<string> keys, long mbps)
        {
            foreach (var key in keys)
            {
                string k = key.ToLowerInvariant();
                bool match = mbps >= 2000
                    ? k.Contains("2.5") || k.Contains("2500")
                    : mbps >= 900 && mbps <= 1100
                        ? k.Contains("1") && k.Contains("gbps") && k.Contains("full")
                        : k.Contains(mbps.ToString());
                if (match) return key;
            }
            return null;
        }

        private static string? FindTailscaleExePath()
        {
            string[] candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),   "Tailscale", "tailscale-ipn.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale-ipn.exe"),
            ];
            return Array.Find(candidates, File.Exists);
        }

        private async Task LoadTailscaleIconAsync()
        {
            string? exePath = FindTailscaleExePath();
            if (exePath == null) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(exePath);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
                if (thumbnail == null) return;
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumbnail);
                _dispatcher.TryEnqueue(() => TailscaleIcon = bmp);
            }
            catch { }
        }

        private static (bool detected, string ip) GetTailscaleInfo() => TailscaleDetector.Detect();
    }
}
