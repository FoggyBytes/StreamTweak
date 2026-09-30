using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
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

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (SetProperty(ref _enabled, value))
                {
                    Entry.Enabled = value;
                    OnPropertyChanged(nameof(CoverOpacity));
                    OnPropertyChanged(nameof(HiddenVisibility));
                    EnabledChanged?.Invoke(this);
                }
            }
        }

        /// <summary>Raised when the sync toggle flips, so the page can refresh its counts.</summary>
        internal Action<ObservableGameEntry>? EnabledChanged;

        // ── Cover (9.0) ───────────────────────────────────────────────────────
        // Bound through CoverPathToBitmapConverter in the tile template instead of being
        // decoded up front for every game: the GridView virtualises, so only the covers on
        // screen are decoded — at tile size — and a 400-game library stays light.

        public string? CoverPath => Entry.CoverImagePath;
        public Visibility NameFallbackVisibility => string.IsNullOrEmpty(Entry.CoverImagePath) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Games left out of the sync are dimmed, not hidden: they are still on this PC.</summary>
        public double CoverOpacity => _enabled ? 1.0 : 0.4;
        public Visibility HiddenVisibility => _enabled ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ManualVisibility => Entry.IsManual ? Visibility.Visible : Visibility.Collapsed;

        // ── Session history (9.0) ─────────────────────────────────────────────

        private GameStats? _stats;
        public GameStats? Stats
        {
            get => _stats;
            set
            {
                _stats = value;
                OnPropertyChanged(nameof(MetaText));
                OnPropertyChanged(nameof(StreamedText));
                OnPropertyChanged(nameof(SessionsText));
            }
        }

        public double   StreamedMinutes => _stats?.Minutes ?? 0;
        public DateTime? LastPlayed     => _stats?.LastPlayed;

        /// <summary>Line under the tile: when it was last streamed and for how long in total; "N/A" for a game never streamed.</summary>
        public string MetaText => _stats?.LastPlayed is { } last
            ? $"{GameStatsService.RelativeDay(last)} · {GameStatsService.FormatMinutes(_stats.Minutes)}"
            : "N/A";

        public string StreamedText => _stats is { Minutes: > 0 } st ? GameStatsService.FormatMinutes(st.Minutes) : "—";
        public string SessionsText => _stats is { Sessions: > 0 } st ? st.Sessions.ToString(CultureInfo.InvariantCulture) : "—";

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

        internal static Uri? ResolveBadgeUri(string store) => store switch
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

    /// <summary>A store filter chip: the store's badge, then "Steam 18".</summary>
    public sealed class StoreChip
    {
        public string Key     { get; init; } = "";
        public string Label   { get; init; } = "";
        /// <summary>The same badge the covers carry; null for "All" and "Hidden".</summary>
        public Uri?   IconUri { get; init; }
    }

    /// <summary>A session in the game sheet's "Recent sessions".</summary>
    public sealed class GameSessionRow
    {
        public string Id        { get; init; } = "";
        public string Title     { get; init; } = "";
        public string Sub       { get; init; } = "";
        public string StripeHex { get; init; } = "";
        public string GradeLabel { get; init; } = "";
        public string GradeFgHex { get; init; } = "";
        public string GradeBgHex { get; init; } = "";
        public string GradeBorderHex { get; init; } = "";
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class GameLibraryViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _dispatcher;

        public GameLibraryViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        // ── Game collection ───────────────────────────────────────────────────

        public ObservableCollection<ObservableGameEntry> Games { get; } = new();

        // ── 9.0: what the page shows (search, store, sort) ────────────────────

        public ObservableCollection<ObservableGameEntry> FilteredGames { get; } = new();
        public ObservableCollection<StoreChip> StoreChips { get; } = new();

        public string[] SortOptions { get; } = { "Recently streamed", "Most streamed", "Name" };

        private int _sortIndex;
        public int SortIndex
        {
            get => _sortIndex;
            set { if (SetProperty(ref _sortIndex, value)) ApplyFilter(); }
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value ?? "")) ApplyFilter(); }
        }

        /// <summary>"all", "hidden" (left out of the sync) or a store name.</summary>
        private string _storeFilter = "all";
        public string StoreFilter
        {
            get => _storeFilter;
            set { if (SetProperty(ref _storeFilter, value)) ApplyFilter(); }
        }

        private string _searchPlaceholder = "Search games";
        public string SearchPlaceholder { get => _searchPlaceholder; private set => SetProperty(ref _searchPlaceholder, value); }

        private string _countsText = "";
        public string CountsText { get => _countsText; private set => SetProperty(ref _countsText, value); }

        private bool _hasGames;
        public bool HasGames
        {
            get => _hasGames;
            private set { SetProperty(ref _hasGames, value); OnPropertyChanged(nameof(HasNoMatch)); }
        }

        private bool _hasFiltered = true;
        public bool HasNoMatch => _hasGames && !_hasFiltered;

        public string ShowInText => $"Show in {DetectedServerName}";

        private string _pageSubtitle = "";
        public string PageSubtitle { get => _pageSubtitle; private set => SetProperty(ref _pageSubtitle, value); }

        private string _syncTitle = "Sunshine sync";
        public string SyncTitle { get => _syncTitle; private set => SetProperty(ref _syncTitle, value); }

        private string _hostTilesHint = "";
        public string HostTilesHint { get => _hostTilesHint; private set => SetProperty(ref _hostTilesHint, value); }

        // ── 9.0: hero (the cover the user is on, else the game being streamed, else the last
        //    one streamed) ────

        // The cover under the pointer or the keyboard focus; the hero follows it.
        private ObservableGameEntry? _spotlight;

        /// <summary>Shows <paramref name="g"/> in the hero; null goes back to the default game.</summary>
        public void Spotlight(ObservableGameEntry? g)
        {
            if (ReferenceEquals(g, _spotlight)) return;
            _spotlight = g;
            RefreshHero();
        }

        private ObservableGameEntry? _heroGame;
        public ObservableGameEntry? HeroGame
        {
            get => _heroGame;
            private set { SetProperty(ref _heroGame, value); OnPropertyChanged(nameof(HeroCoverPath)); }
        }
        public string? HeroCoverPath => _heroGame?.CoverPath;

        private string _heroEyebrow = "", _heroTitle = "", _heroMeta = "", _heroStreamed = "—", _heroSessions = "—", _heroGrade = "—", _heroGradeFg = "#C8CFCB";
        public string HeroEyebrow  { get => _heroEyebrow;  private set => SetProperty(ref _heroEyebrow, value); }
        public string HeroTitle    { get => _heroTitle;    private set => SetProperty(ref _heroTitle, value); }
        public string HeroMeta     { get => _heroMeta;     private set => SetProperty(ref _heroMeta, value); }
        public string HeroStreamed { get => _heroStreamed; private set => SetProperty(ref _heroStreamed, value); }
        public string HeroSessions { get => _heroSessions; private set => SetProperty(ref _heroSessions, value); }
        public string HeroGrade    { get => _heroGrade;    private set => SetProperty(ref _heroGrade, value); }
        public string HeroGradeFg  { get => _heroGradeFg;  private set => SetProperty(ref _heroGradeFg, value); }

        private bool _heroIsLive;
        public bool HeroIsLive { get => _heroIsLive; private set { SetProperty(ref _heroIsLive, value); OnPropertyChanged(nameof(HeroIsNotLive)); } }
        public bool HeroIsNotLive => !_heroIsLive;

        // ── 9.0: game sheet ───────────────────────────────────────────────────

        private ObservableGameEntry? _selectedGame;
        public ObservableGameEntry? SelectedGame
        {
            get => _selectedGame;
            private set
            {
                SetProperty(ref _selectedGame, value);
                OnPropertyChanged(nameof(SelectedCoverPath));
                RefreshSheet();
            }
        }
        public string? SelectedCoverPath => _selectedGame?.CoverPath;

        private string _sheetTitle = "", _sheetStore = "", _sheetStreamed = "", _sheetSessions = "", _sheetLast = "",
                       _sheetGrade = "—", _sheetGradeFg = "#C8CFCB", _sheetGradeBg = "#0FFFFFFF", _sheetGradeBorder = "#24FFFFFF",
                       _sheetLaunch = "", _sheetOrigin = "", _sheetSyncHint = "";
        public string SheetTitle     { get => _sheetTitle;    private set => SetProperty(ref _sheetTitle, value); }
        public string SheetStore     { get => _sheetStore;    private set => SetProperty(ref _sheetStore, value); }
        public string SheetStreamed  { get => _sheetStreamed; private set => SetProperty(ref _sheetStreamed, value); }
        public string SheetSessions  { get => _sheetSessions; private set => SetProperty(ref _sheetSessions, value); }
        public string SheetLast      { get => _sheetLast;     private set => SetProperty(ref _sheetLast, value); }
        public string SheetGrade     { get => _sheetGrade;    private set => SetProperty(ref _sheetGrade, value); }
        public string SheetGradeFg   { get => _sheetGradeFg;  private set => SetProperty(ref _sheetGradeFg, value); }
        public string SheetGradeBg   { get => _sheetGradeBg;  private set => SetProperty(ref _sheetGradeBg, value); }
        public string SheetGradeBorder { get => _sheetGradeBorder; private set => SetProperty(ref _sheetGradeBorder, value); }
        public string SheetLaunch    { get => _sheetLaunch;   private set => SetProperty(ref _sheetLaunch, value); }
        public string SheetOrigin    { get => _sheetOrigin;   private set => SetProperty(ref _sheetOrigin, value); }
        public string SheetSyncHint  { get => _sheetSyncHint; private set => SetProperty(ref _sheetSyncHint, value); }
        public ObservableCollection<GameSessionRow> SheetRecent { get; } = new();

        private bool _hasSheetRecent;
        public bool HasSheetRecent { get => _hasSheetRecent; private set => SetProperty(ref _hasSheetRecent, value); }

        private bool _sheetHasFolder;
        public bool SheetHasFolder { get => _sheetHasFolder; private set => SetProperty(ref _sheetHasFolder, value); }

        private Dictionary<string, GameStats> _stats = new(StringComparer.OrdinalIgnoreCase);

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
            _spotlight = null;   // a new visit opens on the default hero

            // Detect the installed streaming server and set the sync toggle label accordingly.
            var hostInfo = LogParser.FindStreamingAppInfo();
            DetectedServerName = hostInfo?.AppName ?? "Sunshine";
            SyncLabel = $"Sync to {DetectedServerName}";
            SyncTitle = $"{DetectedServerName} sync";
            PageSubtitle = $"Games found on this PC, kept in {DetectedServerName}'s app list with their store cover art.";
            HostTilesHint = $"Desktop and Steam tiles in {DetectedServerName}";
            OnPropertyChanged(nameof(ShowInText));

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
                FilteredGames.Remove(entry);
                if (_selectedGame == entry) SelectedGame = null;
                RefreshSummary();
                RefreshHero();
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
            foreach (var g in Games) g.EnabledChanged = null;
            Games.Clear();
            foreach (var g in state.Games)
            {
                var entry = new ObservableGameEntry(g) { EnabledChanged = _ => RefreshSummary() };
                if (_stats.TryGetValue(g.Name, out var st)) entry.Stats = st;
                Games.Add(entry);
            }
            RefreshSummary();
            ApplyFilter();
            RefreshHero();
            _ = LoadStatsAsync();
        }

        /// <summary>
        /// Reads the session history off the UI thread and attaches to every game how long,
        /// how often and how well it was streamed. Nothing new is stored for this.
        /// </summary>
        private async Task LoadStatsAsync()
        {
            Dictionary<string, GameStats> stats;
            try { stats = await Task.Run(() => GameStatsService.Compute(SessionLogger.Load())); }
            catch { return; }
            _stats = stats;
            foreach (var g in Games)
                g.Stats = _stats.TryGetValue(g.Name, out var st) ? st : null;
            ApplyFilter();
            RefreshHero();
            if (_selectedGame != null) RefreshSheet();
        }

        // ── Filter / sort ─────────────────────────────────────────────────────

        public void ApplyFilter()
        {
            string q = _searchText.Trim();
            IEnumerable<ObservableGameEntry> list = Games.Where(g =>
                (_storeFilter == "all" || (_storeFilter == "hidden" ? !g.Enabled : g.Store == _storeFilter)) &&
                (q.Length == 0 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));

            list = _sortIndex switch
            {
                1 => list.OrderByDescending(g => g.StreamedMinutes).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
                2 => list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => list.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            };

            // Edited in place, never cleared: a Clear() is a Reset, and a Reset sends the
            // GridView back to the top — the store chips live in its header, so every click on
            // one used to scroll them out of sight.
            var result = list.ToList();
            var keep = new HashSet<ObservableGameEntry>(result);
            for (int i = FilteredGames.Count - 1; i >= 0; i--)
                if (!keep.Contains(FilteredGames[i])) FilteredGames.RemoveAt(i);
            for (int i = 0; i < result.Count; i++)
            {
                int at = FilteredGames.IndexOf(result[i]);
                if (at < 0) FilteredGames.Insert(i, result[i]);
                else if (at != i) FilteredGames.Move(at, i);
            }
            // Only HasNoMatch reads this: "no game matches" is shown when the library has games but none pass.
            _hasFiltered = FilteredGames.Count > 0;
            OnPropertyChanged(nameof(HasNoMatch));
        }

        private void RefreshSummary()
        {
            HasGames = Games.Count > 0;
            SearchPlaceholder = Games.Count == 1 ? "Search 1 game" : $"Search {Games.Count} games";

            int shown = Games.Count(g => g.Enabled), manual = Games.Count(g => g.IsManual);
            var bits = new List<string> { Games.Count == 1 ? "1 game" : $"{Games.Count} games", $"{shown} shown in {DetectedServerName}" };
            if (manual > 0) bits.Add(manual == 1 ? "1 manual entry you added" : $"{manual} manual entries you added");
            CountsText = string.Join(" · ", bits);

            // Most games first; stores with the same count in alphabetical order.
            var byStore = Games.GroupBy(g => g.Store).Select(grp => (Store: grp.Key, Count: grp.Count()))
                               .OrderByDescending(x => x.Count)
                               .ThenBy(x => x.Store, StringComparer.CurrentCultureIgnoreCase).ToList();

            StoreChips.Clear();
            StoreChips.Add(new StoreChip { Key = "all", Label = $"All  {Games.Count}" });
            foreach (var (store, count) in byStore)
                StoreChips.Add(new StoreChip { Key = store, Label = $"{store}  {count}", IconUri = ObservableGameEntry.ResolveBadgeUri(store) });
            int hidden = Games.Count - shown;
            if (hidden > 0)
                StoreChips.Add(new StoreChip { Key = "hidden", Label = $"Hidden from {DetectedServerName}  {hidden}" });
            if (_storeFilter != "all" && StoreChips.All(c => c.Key != _storeFilter))
                StoreFilter = "all";
        }

        // ── Hero ──────────────────────────────────────────────────────────────

        public void RefreshHero()
        {
            string? live = AppStateService.Instance.IsSessionActive
                ? SessionLogger.CurrentGameName(TimeSpan.FromMinutes(2)) : null;
            ObservableGameEntry? liveGame = live != null
                ? Games.FirstOrDefault(x => string.Equals(x.Name, live, StringComparison.OrdinalIgnoreCase))
                : null;
            ObservableGameEntry? g = _spotlight != null && Games.Contains(_spotlight) ? _spotlight : null;
            g ??= liveGame;
            g ??= Games.Where(x => x.LastPlayed != null).OrderByDescending(x => x.LastPlayed).FirstOrDefault();
            g ??= Games.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
            HeroIsLive = g != null && ReferenceEquals(g, liveGame);
            HeroGame = g;
            if (g == null) return;
            bool played = g.LastPlayed != null;

            HeroEyebrow = HeroIsLive ? "STREAMING NOW"
                        : played ? $"LAST STREAMED · {GameStatsService.RelativeDay(g.LastPlayed!.Value).ToUpperInvariant()}"
                        : "IN YOUR LIBRARY";
            HeroTitle = g.Name;
            HeroMeta = g.IsManual ? "Added by you" : g.Store;
            HeroStreamed = g.StreamedText;
            HeroSessions = g.SessionsText;
            var gc = GameStatsService.GradeColors(g.Stats?.TypicalGrade);
            HeroGrade = gc.Label;
            HeroGradeFg = gc.Fg;
        }

        // ── Game sheet ────────────────────────────────────────────────────────

        public void OpenSheet(ObservableGameEntry g) => SelectedGame = g;

        public void CloseSheet() => SelectedGame = null;

        public ObservableGameEntry? FindGame(string name)
            => Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

        private void RefreshSheet()
        {
            SheetRecent.Clear();
            var g = _selectedGame;
            if (g == null) { HasSheetRecent = false; return; }
            var e = g.Entry;

            SheetTitle = g.Name;
            SheetStore = g.IsManual ? "Manual entry" : g.Store;
            var st = g.Stats;
            SheetStreamed = st is { Minutes: > 0 } ? GameStatsService.FormatMinutes(st.Minutes) : "Never";
            SheetSessions = (st?.Sessions ?? 0).ToString(CultureInfo.InvariantCulture);
            SheetLast = st?.LastPlayed is { } lp ? GameStatsService.RelativeDay(lp) : "—";
            var gc = GameStatsService.GradeColors(st?.TypicalGrade);
            (SheetGrade, SheetGradeFg, SheetGradeBg, SheetGradeBorder) = gc;

            SheetLaunch = e.SteamAppId != null ? $"steam://rungameid/{e.SteamAppId}"
                        : e.Store == "Xbox" && !string.IsNullOrEmpty(e.StoreId) ? $"shell:appsFolder\\{e.StoreId}"
                        : SunshineSync.StoreLaunchUri(e.Store, e.LaunchId) ?? e.ExePath ?? "—";
            SheetOrigin = g.IsManual ? "Manual entries stay across re-syncs." : "Found by the library scan.";
            SheetSyncHint = $"Kept in {DetectedServerName}'s app list across re-syncs";
            SheetHasFolder = g.ShowFolderButton;

            if (st != null)
            {
                foreach (var s in st.RecentSessions.Take(6))
                {
                    var c = GameStatsService.GradeColors(s.Grade);
                    string dur = s.EndTime is { } end ? GameStatsService.FormatMinutes((end - s.StartTime).TotalMinutes) : "";
                    string rtt = s.QualityStats is { RttAvgMs: > 0 } q ? $" · RTT {q.RttAvgMs.ToString("0.0", CultureInfo.InvariantCulture)} ms" : "";
                    SheetRecent.Add(new GameSessionRow
                    {
                        Id = s.Id,
                        Title = s.StartTime.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
                        Sub = $"{s.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture)} · {dur}{rtt}",
                        StripeHex = GameStatsService.GradeStripe(s.Grade),
                        GradeLabel = c.Label, GradeFgHex = c.Fg, GradeBgHex = c.Bg, GradeBorderHex = c.Border,
                    });
                }
            }
            HasSheetRecent = SheetRecent.Count > 0;
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
