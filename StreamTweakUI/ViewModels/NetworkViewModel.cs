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

        // ── Speed (read-only status) ──────────────────────────────────────────
        // 8.1.0 moved the choice of speed to the client, which is the only side that knows
        // what its own link can do. The host publishes what the adapter supports, grants or
        // withholds permission, and can force a restore. Nothing here configures a target.

        private string _currentSpeedText = "Detecting…";
        public string CurrentSpeedText
        {
            get => _currentSpeedText;
            private set => SetProperty(ref _currentSpeedText, value);
        }

        private string _supportedSpeedsText = "—";
        /// <summary>What the adapter can do, e.g. "2.5 Gbps · 1 Gbps · 100 Mbps".
        /// This is the list clients read to pick a speed that matches their own link.</summary>
        public string SupportedSpeedsText
        {
            get => _supportedSpeedsText;
            private set => SetProperty(ref _supportedSpeedsText, value);
        }

        // ── Client control ────────────────────────────────────────────────────

        /// <summary>Permission, not configuration: the adapter is the host's hardware, and the
        /// user needs to be able to withhold it (e.g. while a big download is running).</summary>
        public bool AllowClientControl
        {
            get => AppStateService.Instance.LinkSpeed?.AllowClientControl ?? false;
            set
            {
                var mgr = AppStateService.Instance.LinkSpeed;
                if (mgr == null || mgr.AllowClientControl == value) return;
                mgr.AllowClientControl = value;
                OnPropertyChanged();
                RefreshLinkState();
            }
        }

        private string _statusPillText = "Idle";
        public string StatusPillText
        {
            get => _statusPillText;
            private set => SetProperty(ref _statusPillText, value);
        }

        // Pill colours follow the app-wide badge pattern (tinted bg + border + coloured text,
        // bound through HexToSolidColorBrushConverter) rather than a bespoke converter.
        private string _statusPillBgHex = GreyBg, _statusPillBorderHex = GreyBorder, _statusPillFgHex = GreyFg;

        public string StatusPillBgHex
        {
            get => _statusPillBgHex;
            private set => SetProperty(ref _statusPillBgHex, value);
        }
        public string StatusPillBorderHex
        {
            get => _statusPillBorderHex;
            private set => SetProperty(ref _statusPillBorderHex, value);
        }
        public string StatusPillFgHex
        {
            get => _statusPillFgHex;
            private set => SetProperty(ref _statusPillFgHex, value);
        }

        private const string GreyBg = "#17A8A49F", GreyBorder = "#3DA8A49F", GreyFg = "#A8A49F";
        private const string AmberBg = "#1Ffbbf24", AmberBorder = "#52fbbf24", AmberFg = "#fbbf24";

        private void SetPill(string text, bool amber)
        {
            StatusPillText = text;
            StatusPillBgHex     = amber ? AmberBg     : GreyBg;
            StatusPillBorderHex = amber ? AmberBorder : GreyBorder;
            StatusPillFgHex     = amber ? AmberFg     : GreyFg;
        }

        private string _statusDetailText = "No client has changed this adapter.";
        public string StatusDetailText
        {
            get => _statusDetailText;
            private set => SetProperty(ref _statusDetailText, value);
        }

        private string _restoreDetailText = "The adapter is at its normal setting — nothing to restore.";
        public string RestoreDetailText
        {
            get => _restoreDetailText;
            private set => SetProperty(ref _restoreDetailText, value);
        }

        private bool _canRestore;
        public bool CanRestore
        {
            get => _canRestore;
            private set => SetProperty(ref _canRestore, value);
        }

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

        // Live speed refresh timer
        private DispatcherQueueTimer? _speedRefreshTimer;

        // ── Constructor ───────────────────────────────────────────────────────

        public NetworkViewModel()
        {
            // Capture UI thread dispatcher before any async operations
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            AppStateService.Instance.LinkSpeedChanged += OnLinkSpeedChanged;
            RefreshLinkState();

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
                // Manageable = wired *and* exposing *SpeedDuplex. Listing anything else would
                // offer a control that silently does nothing, which is what Wi-Fi adapters did
                // before 8.1.0.
                var adapters = await Task.Run(NetworkManager.GetManageableAdapterNames)
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
                        CurrentSpeedText = "—";
                        SupportedSpeedsText = "—";
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
                // Also drives the "in N s" countdown while a restore is pending.
                RefreshLinkState();
            };
            _speedRefreshTimer.Start();
        }

        public void StopSpeedRefresh()
        {
            _speedRefreshTimer?.Stop();
            _speedRefreshTimer = null;
        }

        /// <summary>Unsubscribes from the shared manager. The NavigationView recreates this page
        /// on every visit, so without this the handlers pile up.</summary>
        public void Unsubscribe()
            => AppStateService.Instance.LinkSpeedChanged -= OnLinkSpeedChanged;

        private void OnLinkSpeedChanged() => _dispatcher.TryEnqueue(RefreshLinkState);

        /// <summary>Escape hatch: put the adapter back now, without waiting out the grace period.
        /// Needed when a client vanishes mid-session and leaves the link switched.</summary>
        public void RestoreNow() => AppStateService.Instance.LinkSpeed?.RestoreNow("Network page");

        /// <summary>Recomputes the status/restore rows from the manager. Cheap; called on every
        /// state change and once a second while a restore is pending, for the countdown.</summary>
        public void RefreshLinkState()
        {
            var mgr = AppStateService.Instance.LinkSpeed;
            if (mgr == null) return;

            OnPropertyChanged(nameof(AllowClientControl));
            CanRestore = mgr.IsSwitched;

            string back = NetworkManager.FormatMbps(mgr.OriginalMbps);
            RestoreDetailText = mgr.IsSwitched && mgr.OriginalMbps > 0
                ? $"Put the adapter back to {back} immediately, without waiting."
                : "The adapter is at its normal setting — nothing to restore.";

            if (!mgr.AllowClientControl)
            {
                SetPill("Off", amber: false);
                StatusDetailText = "Clients can see the speed but can't change it.";
                return;
            }

            if (!mgr.IsSwitched)
            {
                SetPill("Idle", amber: false);
                StatusDetailText = "No client has changed this adapter.";
                return;
            }

            string who = string.IsNullOrEmpty(mgr.SwitchedBy) ? "A client" : mgr.SwitchedBy!;

            if (mgr.RestoreAtUtc is { } due)
            {
                int secs = Math.Max(0, (int)Math.Round((due - DateTime.UtcNow).TotalSeconds));
                SetPill("Restoring", amber: true);
                StatusDetailText = $"Session ended. Going back to {back} in {secs} s — cancelled if a client reconnects.";
            }
            else
            {
                SetPill("Switched", amber: true);
                StatusDetailText = $"{who} asked for {NetworkManager.FormatMbps(mgr.CurrentMbps)} · was {back}. "
                                 + "Restores when the session ends.";
            }
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
            AppStateService.Instance.LinkSpeed?.SetAdapter(adapterName);
            UpdateCurrentSpeed(adapterName);
            _ = Task.Run(() => LoadSupportedSpeeds(adapterName));
        }

        private void LoadSupportedSpeeds(string adapterName)
        {
            // Auto Negotiation (Mbps 0) is a valid setting to restore but not a speed a client
            // can request, so it never appears in this list.
            var text = string.Join(" · ", NetworkManager.GetSupportedSpeedOptions(adapterName)
                .Where(o => o.Mbps > 0)
                .Select(o => o.Mbps)
                .Distinct()
                .OrderByDescending(m => m)
                .Select(NetworkManager.FormatMbps));

            _dispatcher.TryEnqueue(() =>
                SupportedSpeedsText = string.IsNullOrEmpty(text) ? "—" : text);
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
