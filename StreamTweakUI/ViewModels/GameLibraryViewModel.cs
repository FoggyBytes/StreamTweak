using System.Collections.ObjectModel;
using System.Diagnostics;
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

        public string Name       => Entry.Name;
        public string Store      => Entry.Store;
        public bool   IsManual   => Entry.IsManual;
        public string PathDisplay => Entry.ExePath is { Length: > 0 } p ? p : "—";

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
        // ── Game collection ───────────────────────────────────────────────────

        public ObservableCollection<ObservableGameEntry> Games { get; } = new();

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

        // ── Public API ────────────────────────────────────────────────────────

        public void Load()
        {
            // Detect the installed streaming server and set the sync toggle label accordingly.
            var hostInfo = LogParser.FindStreamingAppInfo();
            SyncLabel = $"Sync to {hostInfo?.AppName ?? "Sunshine"}";

            var state = GameLibraryState.Current;
            _syncEnabled = state.SyncEnabled;
            OnPropertyChanged(nameof(SyncEnabled));

            RefreshLastSyncText(state);
            RefreshGameList(state);
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

                // All other stores (Ubisoft, Epic, GOG, EA App, Battle.net, Manual):
                // ExePath is persisted from the scanner for auto-discovered games and
                // from the user's file picker for manual games — use it directly.
                if (!string.IsNullOrEmpty(entry.ExePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = entry.ExePath,
                        UseShellExecute = true
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
