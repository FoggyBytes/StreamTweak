using System.Collections.ObjectModel;
using System.Diagnostics;
using StreamTweak.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StreamTweak.ViewModels
{
    // ── Per-game observable wrapper ───────────────────────────────────────────

    public sealed class ObservableGameEntry : ViewModelBase
    {
        public GameLibraryEntry Entry { get; }

        public string Name    => Entry.Name;
        public string Store   => Entry.Store;
        public bool   IsManual => Entry.IsManual;

        public string? InstallFolder
        {
            get
            {
                var e = Entry;
                if (e.Store == "Battle.net" && !string.IsNullOrEmpty(e.InstallDir))
                    return e.InstallDir;
                // Steam e Xbox: ExePath è già la directory di installazione (non un exe)
                if ((e.Store == "Steam" || e.Store == "Xbox") && !string.IsNullOrEmpty(e.ExePath))
                    return e.ExePath;
                if (!string.IsNullOrEmpty(e.ExePath))
                    return Path.GetDirectoryName(e.ExePath);
                return null;
            }
        }

        public bool ShowFolderButton => InstallFolder != null;

        // ── Sync tooltip (reads static server name set by GameLibraryViewModel.Load) ───
        public string SyncTooltip
            => $"Include in {GameLibraryViewModel.DetectedServerName} sync";

        // ── Game metadata ─────────────────────────────────────────────────────

        private GameMetadataService.GameMetadata? GetMeta()
            => GameMetadataService.GetCached(Entry);

        public string? MetaDeveloper   => GetMeta()?.Developer;
        public string? MetaReleaseDate => GetMeta()?.ReleaseDate;
        public bool    HasMeta         => GetMeta() is { } d && (d.Developer != null || d.ReleaseDate != null);

        public void RefreshMetadata()
        {
            OnPropertyChanged(nameof(MetaDeveloper));
            OnPropertyChanged(nameof(MetaReleaseDate));
            OnPropertyChanged(nameof(HasMeta));
        }

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (SetProperty(ref _enabled, value))
                    Entry.Enabled = value;
            }
        }

        private BitmapImage? _coverImage;
        public BitmapImage? CoverImage
        {
            get => _coverImage;
            set
            {
                SetProperty(ref _coverImage, value);
                OnPropertyChanged(nameof(HasNoCover));
            }
        }

        // True when cover has not yet loaded — used to show the name fallback
        public bool HasNoCover => _coverImage == null;

        // Badge URI for SvgImageSource (e.g. "ms-appx:///Resources/Badges/store_steam.svg")
        public Uri? BadgeUri { get; }
        public bool ShowBadge => BadgeUri != null;
        public string BadgeLabel { get; }

        public ObservableGameEntry(GameLibraryEntry entry)
        {
            Entry = entry;
            _enabled = entry.Enabled;
            BadgeUri = ResolveBadgeUri(entry.Store);
            BadgeLabel = entry.Store;
        }

        private static Uri? ResolveBadgeUri(string store) => store switch
        {
            "Steam"           => new Uri("ms-appx:///Resources/Badges/store_steam.svg"),
            "Epic Games"      => new Uri("ms-appx:///Resources/Badges/store_epic.svg"),
            "GOG"             => new Uri("ms-appx:///Resources/Badges/store_gog.svg"),
            "Ubisoft Connect" => new Uri("ms-appx:///Resources/Badges/store_ubisoft.svg"),
            "Xbox"            => new Uri("ms-appx:///Resources/Badges/store_xbox.svg"),
            "Battle.net"      => new Uri("ms-appx:///Resources/Badges/store_battlenet.svg"),
            "EA App"          => new Uri("ms-appx:///Resources/Badges/store_ea.svg"),
            _                 => null
        };
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class GameLibraryViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _dispatcher;

        public GameLibraryViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            GameMetadataService.CacheRefreshed += OnMetaCacheRefreshed;
        }

        public void Unsubscribe()
        {
            GameMetadataService.CacheRefreshed -= OnMetaCacheRefreshed;
        }

        private void OnMetaCacheRefreshed()
        {
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var g in Games)
                    g.RefreshMetadata();
            });
        }

        // ── Game collection ───────────────────────────────────────────────────

        public ObservableCollection<ObservableGameEntry> Games { get; } = new();

        // ── Detected server name (static — readable from ObservableGameEntry DataTemplate) ───
        internal static string DetectedServerName { get; private set; } = "Sunshine";

        // ── Sync toggle ───────────────────────────────────────────────────────

        private string _syncLabel = "Sync to Sunshine";
        public string SyncLabel
        {
            get => _syncLabel;
            private set => SetProperty(ref _syncLabel, value);
        }

        private bool _syncEnabled;
        public bool SyncEnabled
        {
            get => _syncEnabled;
            set
            {
                if (SetProperty(ref _syncEnabled, value))
                {
                    var state = GameLibraryState.Current;
                    state.SyncEnabled = value;
                    state.Save();
                }
            }
        }

        // ── Status / busy ─────────────────────────────────────────────────────

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                SetProperty(ref _isBusy, value);
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }

        public bool IsNotBusy => !_isBusy;

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        private bool _hasStatus;
        public bool HasStatus
        {
            get => _hasStatus;
            set => SetProperty(ref _hasStatus, value);
        }

        private bool _statusIsError;
        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetProperty(ref _statusIsError, value);
        }

        // ── Last sync display ─────────────────────────────────────────────────

        private string _lastSyncText = "Never synced";
        public string LastSyncText
        {
            get => _lastSyncText;
            private set => SetProperty(ref _lastSyncText, value);
        }

        // ── Host tile swap (desktop.png / steam.png in <HostInstallDir>\assets) ──

        private string? _hostAssetsDir;

        private bool _hostAssetsAvailable;
        public bool HostAssetsAvailable
        {
            get => _hostAssetsAvailable;
            private set => SetProperty(ref _hostAssetsAvailable, value);
        }

        private bool _hostAssetsApplied;
        public bool HostAssetsApplied
        {
            get => _hostAssetsApplied;
            private set
            {
                if (SetProperty(ref _hostAssetsApplied, value))
                {
                    OnPropertyChanged(nameof(HostAssetsButtonText));
                    OnPropertyChanged(nameof(HostAssetsHelpText));
                }
            }
        }

        public string HostAssetsButtonText => _hostAssetsApplied ? "Restore tiles" : "Change tiles";

        public string HostAssetsHelpText => _hostAssetsApplied
            ? $"Restore {DetectedServerName}'s original Desktop & Steam tiles"
            : $"Replace {DetectedServerName}'s default Desktop & Steam tiles";

        // ── Public API ────────────────────────────────────────────────────────

        public void Load()
        {
            // Detect the installed streaming server and set the sync toggle label accordingly.
            var hostInfo = LogParser.FindStreamingAppInfo();
            DetectedServerName = hostInfo?.AppName ?? "Sunshine";
            SyncLabel = $"Sync to {DetectedServerName}";

            // Host tile-swap state: derive the <HostInstallDir>\assets folder and check
            // whether a previous swap is still applied (a *_backup.png is present).
            _hostAssetsDir = HostAssetsManager.GetAssetsDirectory(hostInfo);
            HostAssetsAvailable = _hostAssetsDir != null;
            HostAssetsApplied   = _hostAssetsDir != null && HostAssetsManager.IsApplied(_hostAssetsDir);

            // One-time migration: installs that swapped their tiles before the guard existed
            // have no config key, so the guard would sit disabled on exactly the setups that
            // need it. The backup files are the evidence that the swap happened.
            if (HostAssetsApplied && !ConfigService.GetBool("HostTilesApplied"))
            {
                ConfigService.Set("HostTilesApplied", true);
                AppStateService.Instance.HostAssetsGuard?.NudgeAfterUserAction();
            }
            OnPropertyChanged(nameof(HostAssetsButtonText));
            OnPropertyChanged(nameof(HostAssetsHelpText));

            var state = GameLibraryState.Current;
            _syncEnabled = state.SyncEnabled;
            OnPropertyChanged(nameof(SyncEnabled));

            RefreshLastSyncText(state);
            RefreshGameList(state);
        }

        public async Task ToggleHostAssetsAsync()
        {
            if (_isBusy || _hostAssetsDir == null) return;

            IsBusy = true;
            try
            {
                if (_hostAssetsApplied)
                {
                    bool ok = await HostAssetsManager.RestoreAsync(_hostAssetsDir);
                    if (ok)
                    {
                        // Written before the guard is told, so it can never read a stale "on"
                        // and put the tiles the user just removed straight back.
                        ConfigService.Set("HostTilesApplied", false);
                        AppStateService.Instance.HostAssetsGuard?.NudgeAfterUserAction();
                        HostAssetsApplied = HostAssetsManager.IsApplied(_hostAssetsDir);
                        ShowStatus($"Restored {DetectedServerName}'s original Desktop & Steam tiles.", isError: false);
                    }
                    else
                    {
                        ShowStatus("Could not restore host tiles. Is the StreamTweak service running?", isError: true);
                    }
                }
                else
                {
                    string baseDir = AppContext.BaseDirectory;
                    string desktopSrc = Path.Combine(baseDir, "Resources", "desktop.png");
                    string steamSrc   = Path.Combine(baseDir, "Resources", "steam.png");

                    if (!File.Exists(desktopSrc) || !File.Exists(steamSrc))
                    {
                        ShowStatus("Bundled replacement tiles not found in Resources/.", isError: true);
                        return;
                    }

                    bool ok = await HostAssetsManager.SwapAsync(_hostAssetsDir, desktopSrc, steamSrc);
                    if (ok)
                    {
                        ConfigService.Set("HostTilesApplied", true);
                        AppStateService.Instance.HostAssetsGuard?.NudgeAfterUserAction();
                        HostAssetsApplied = HostAssetsManager.IsApplied(_hostAssetsDir);
                        ShowStatus($"Replaced {DetectedServerName}'s Desktop & Steam tiles. Originals backed up.", isError: false);
                    }
                    else
                    {
                        ShowStatus("Could not replace host tiles. Is the StreamTweak service running?", isError: true);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Host tiles operation failed: {ex.Message}", isError: true);
            }
            finally { IsBusy = false; }
        }

        public async Task SyncNowAsync()
        {
            if (_isBusy) return;
            IsBusy = true;
            try
            {
                string result = await GameLibraryService.PerformSyncAsync(isManual: true);
                ShowStatus(result, isError: false);
                var state = GameLibraryState.Current;
                RefreshLastSyncText(state);
                RefreshGameList(state);

                // Refresh metadata (developer / release date) for the updated game list.
                // Runs on a background thread; OnMetaCacheRefreshed updates the UI when done.
                _ = Task.Run(() => GameMetadataService.RefreshAsync(GameLibraryState.Current.Games));
            }
            catch (Exception ex)
            {
                ShowStatus($"Sync failed: {ex.Message}", isError: true);
            }
            finally { IsBusy = false; }
        }

        public async Task ClearSyncAsync()
        {
            if (_isBusy) return;
            IsBusy = true;
            try
            {
                string result = await GameLibraryService.ClearSyncAsync();
                ShowStatus(result, isError: false);
                var state = GameLibraryState.Current;
                RefreshLastSyncText(state);
                RefreshGameList(state);
            }
            catch (Exception ex)
            {
                ShowStatus($"Clear failed: {ex.Message}", isError: true);
            }
            finally { IsBusy = false; }
        }

        public async Task AddGameAsync()
        {
            var picker = new FileOpenPicker();
            if (App.MainWindow is { } win)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(win));

            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return;

            IsBusy = true;
            try
            {
                string result = await GameLibraryService.AddManualGameAsync(file.Path);
                ShowStatus(result, isError: false);
                RefreshGameList(GameLibraryState.Current);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not add game: {ex.Message}", isError: true);
            }
            finally { IsBusy = false; }
        }

        public void OpenGameFolder(ObservableGameEntry observableEntry)
        {
            string? folder = observableEntry.InstallFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                ShowStatus("Install folder not found.", isError: true);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not open folder: {ex.Message}", isError: true);
            }
        }

        public void LaunchGame(ObservableGameEntry observableEntry)
        {
            var entry = observableEntry.Entry;
            try
            {
                // Steam — use native Steam URI so the overlay works correctly
                if (entry.SteamAppId != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = $"steam://rungameid/{entry.SteamAppId}",
                        UseShellExecute = true
                    });
                    return;
                }

                // Xbox / Game Pass — UWP shell protocol
                if (entry.Store == "Xbox" && !string.IsNullOrEmpty(entry.StoreId))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName  = "explorer.exe",
                        Arguments = $"shell:appsFolder\\{entry.StoreId}"
                    });
                    return;
                }

                // Stores whose games have to be started by their own launcher — Epic today.
                // Through the same helper the apps.json sync uses, so a game cannot launch
                // correctly when streamed and fail when started from this window.
                string? storeUri = SunshineSync.StoreLaunchUri(entry.Store, entry.LaunchId);
                if (storeUri != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = storeUri,
                        UseShellExecute = true
                    });
                    return;
                }

                // All other stores (Ubisoft, GOG, EA App, Battle.net, Manual):
                // ExePath is persisted from the scanner for auto-discovered games and
                // from the user's file picker for manual games — use it directly.
                if (!string.IsNullOrEmpty(entry.ExePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = entry.ExePath,
                        UseShellExecute = true,
                        // ⚠️ Without this the game inherits *StreamTweak's* working directory,
                        // which is its install folder under Program Files. A game that resolves
                        // its data relative to the current directory then looks for it there and
                        // refuses to start — Alan Wake 2 reported exactly
                        // "Could not find: 'C:/Program Files/StreamTweak/data'".
                        WorkingDirectory = System.IO.Path.GetDirectoryName(entry.ExePath) ?? ""
                    });
                    return;
                }

                ShowStatus($"No launch path available for \"{entry.Name}\".", isError: true);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not launch \"{entry.Name}\": {ex.Message}", isError: true);
            }
        }

        public async Task RemoveGameAsync(ObservableGameEntry entry)
        {
            IsBusy = true;
            try
            {
                string result = await GameLibraryService.RemoveGameAsync(entry.Entry);
                ShowStatus(result, isError: false);
                Games.Remove(entry);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not remove game: {ex.Message}", isError: true);
            }
            finally { IsBusy = false; }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void RefreshGameList(GameLibraryState state)
        {
            Games.Clear();
            foreach (var g in state.Games)
            {
                var entry = new ObservableGameEntry(g);
                Games.Add(entry);
            }
            _ = LoadCoversAsync();
        }

        private async Task LoadCoversAsync()
        {
            var snapshot = Games.ToList();
            foreach (var entry in snapshot)
            {
                string? path = entry.Entry.CoverImagePath;
                if (path == null) continue;
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var bmp = new BitmapImage();
                    // DecodePixelWidth = 2× display width (67 px) so WIC uses its high-quality
                    // Fant resampler instead of letting the GPU do bilinear downscaling.
                    bmp.DecodePixelWidth = 134;
                    using var stream = await file.OpenReadAsync();
                    await bmp.SetSourceAsync(stream);
                    entry.CoverImage = bmp;
                }
                catch { /* non-fatal */ }
            }
        }

        private void RefreshLastSyncText(GameLibraryState state)
        {
            LastSyncText = state.LastSyncUtc == null
                ? "Never synced"
                : $"Last sync: {state.LastSyncUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}";
        }

        private void ShowStatus(string text, bool isError)
        {
            StatusText    = text;
            StatusIsError = isError;
            HasStatus     = true;
        }

    }
}
