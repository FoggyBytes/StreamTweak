using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using StreamTweak.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StreamTweak.ViewModels
{
    // ── One game of the list ──────────────────────────────────────────────────

    /// <summary>
    /// A row of the 10.0 Library (mockup A «Elenco»): the cover, the name and the store, the
    /// command written in the streaming server, and what can be done with it.
    /// </summary>
    public sealed class LibraryRow
    {
        public GameLibraryEntry Entry { get; }
        public string  Name      => Entry.Name;
        public string? CoverPath => Entry.CoverImagePath;
        public string  StoreText => Entry.IsManual ? "Added by hand" : Entry.Store;
        public Uri?    BadgeUri  { get; }
        public bool    ShowBadge => BadgeUri != null;
        public bool    ShowNameOnCover => string.IsNullOrEmpty(Entry.CoverImagePath);

        /// <summary>From apps.json; empty for a game the server does not list.</summary>
        public string Command { get; }
        public string Kind    { get; }
        public bool   HasCommand => Command.Length > 0;
        public string CommandOrNote { get; }

        public bool   CanPlay    => HasCommand;
        public string ActionText => !Entry.Enabled ? "Include again" : Entry.IsManual ? "Remove" : "Exclude";
        public string ActionTip  => !Entry.Enabled ? "Back in the streaming server's list at the next Sync and restart"
                                  : Entry.IsManual ? "Out of the library and of the streaming server's list"
                                  : "Out of the streaming server's list, and kept out at every sync";

        public LibraryRow(GameLibraryEntry entry, SunshineSync.AppCommand? command, string server)
        {
            Entry    = entry;
            BadgeUri = entry.IsManual ? null : StoreBadge(entry.Store);
            Command  = command?.Shown ?? "";
            Kind     = command?.Kind ?? "";
            CommandOrNote = Command.Length > 0 ? Command
                          : entry.Enabled ? $"Not in {server} yet: Sync and restart writes it"
                          : $"Not in {server}";
        }

        internal static Uri? StoreBadge(string store) => store switch
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

    /// <summary>
    /// 10.0 Library (plan phase 4, mockup A «Elenco»): what StreamTweak writes in the streaming
    /// server, one row per game with the exact command, in three tabs — in the server, excluded,
    /// added by hand. Every change reaches the server through Sync and restart, the same job
    /// StreamLight starts with LIBSYNC (App.Library): the servers read apps.json only when they
    /// start. Exclude keeps the game in the list, out of the server, at every sync, whoever
    /// starts it; Include again brings it back.
    /// </summary>
    public sealed class GameLibraryViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
        private DispatcherQueueTimer? _jobTimer;

        public ObservableCollection<LibraryRow> Rows { get; } = new();

        // ── The server's card ────────────────────────────────────────────────────

        private string _server = "Sunshine";
        public string Server { get => _server; private set => SetProperty(ref _server, value); }

        private string _serverState = "", _serverStateHex = "#646b67";
        /// <summary>"running", "stopped"… as Windows' service manager reports it; empty while unknown.</summary>
        public string ServerState    { get => _serverState;    private set { if (SetProperty(ref _serverState, value)) OnPropertyChanged(nameof(HasServerState)); } }
        public bool HasServerState   => _serverState.Length > 0;
        public string ServerStateHex { get => _serverStateHex; private set => SetProperty(ref _serverStateHex, value); }

        // The card's four figures (mockup A «Tabella piena», 22/10).
        private string _kpiListed = "0", _kpiExcluded = "0", _kpiManual = "0", _kpiSync = "—", _kpiSyncLabel = "Not synced yet";
        public string KpiListed    { get => _kpiListed;    private set => SetProperty(ref _kpiListed, value); }
        public string KpiExcluded  { get => _kpiExcluded;  private set => SetProperty(ref _kpiExcluded, value); }
        public string KpiManual    { get => _kpiManual;    private set => SetProperty(ref _kpiManual, value); }
        public string KpiSync      { get => _kpiSync;      private set => SetProperty(ref _kpiSync, value); }
        public string KpiSyncLabel { get => _kpiSyncLabel; private set => SetProperty(ref _kpiSyncLabel, value); }
        public string ListedLabel => $"In {_server}";
        public string CommandHeader => $"COMMAND IN {_server.ToUpperInvariant()}";

        private string _steamConflict = "";
        public string SteamConflict { get => _steamConflict; private set { if (SetProperty(ref _steamConflict, value)) OnPropertyChanged(nameof(HasSteamConflict)); } }
        public bool HasSteamConflict => _steamConflict.Length > 0;

        private bool _syncEnabled;
        /// <summary>The automatic sync when StreamTweak starts (GameLibraryState.SyncEnabled).</summary>
        public bool SyncEnabled
        {
            get => _syncEnabled;
            set
            {
                if (!SetProperty(ref _syncEnabled, value)) return;
                var state = GameLibraryState.Current;
                state.SyncEnabled = value;
                state.Save();
            }
        }

        // ── Sync and restart ─────────────────────────────────────────────────────

        private bool _jobRunning;
        public bool JobRunning
        {
            get => _jobRunning;
            private set { if (SetProperty(ref _jobRunning, value)) { OnPropertyChanged(nameof(CanChange)); OnPropertyChanged(nameof(SyncButtonText)); } }
        }
        public bool CanChange => !_jobRunning && !_busy;
        public string SyncButtonText => _jobRunning ? "Working…" : "Sync and restart";

        private string _jobText = "";
        public string JobText { get => _jobText; private set { if (SetProperty(ref _jobText, value)) OnPropertyChanged(nameof(HasJobText)); } }
        public bool HasJobText => _jobText.Length > 0;

        private bool _jobFailed;
        public bool JobFailed { get => _jobFailed; private set => SetProperty(ref _jobFailed, value); }

        private bool _pending;
        /// <summary>Changes made here since the last Sync and restart: the server does not show them yet.</summary>
        public bool Pending { get => _pending; private set { if (SetProperty(ref _pending, value)) OnPropertyChanged(nameof(PendingText)); } }
        public string PendingText => $"{_server} shows these changes after Sync and restart.";

        // ── Tabs and filter ──────────────────────────────────────────────────────

        private string _tab = "listed";   // listed · excluded · manual
        public string Tab { get => _tab; set { if (SetProperty(ref _tab, value)) Fill(); } }

        private string _filter = "";
        public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "")) Fill(); } }

        private string _listedTab = "", _excludedTab = "Excluded", _manualTab = "Added by hand";
        public string ListedTab   { get => _listedTab;   private set => SetProperty(ref _listedTab, value); }
        public string ExcludedTab { get => _excludedTab; private set => SetProperty(ref _excludedTab, value); }
        public string ManualTab   { get => _manualTab;   private set => SetProperty(ref _manualTab, value); }

        private string _emptyText = "";
        public string EmptyText { get => _emptyText; private set { if (SetProperty(ref _emptyText, value)) OnPropertyChanged(nameof(IsEmpty)); } }
        public bool IsEmpty => _emptyText.Length > 0;

        /// <summary>Games removed before 10.0, kept out of every sync by a key without a name.</summary>
        private int _legacyExcluded;
        public int LegacyExcluded { get => _legacyExcluded; private set { if (SetProperty(ref _legacyExcluded, value)) { OnPropertyChanged(nameof(HasLegacyExcluded)); OnPropertyChanged(nameof(LegacyText)); } } }
        public bool HasLegacyExcluded => _legacyExcluded > 0 && _tab == "excluded";
        public string LegacyText => _legacyExcluded == 1
            ? "1 more game, removed before StreamTweak 10.0, is kept out of every sync without a name to show here."
            : $"{_legacyExcluded} more games, removed before StreamTweak 10.0, are kept out of every sync without a name to show here.";

        // ── Host tiles (desktop.png / steam.png in <server>\assets) ──────────────

        private string? _hostAssetsDir;
        private bool _hostAssetsAvailable, _hostAssetsApplied;
        public bool HostAssetsAvailable { get => _hostAssetsAvailable; private set => SetProperty(ref _hostAssetsAvailable, value); }
        public bool HostAssetsApplied
        {
            get => _hostAssetsApplied;
            private set { if (SetProperty(ref _hostAssetsApplied, value)) OnPropertyChanged(nameof(HostAssetsButtonText)); }
        }
        public string HostAssetsButtonText => _hostAssetsApplied ? "Restore Desktop & Steam tiles" : "Replace Desktop & Steam tiles";

        // ── Status (an InfoBar) ──────────────────────────────────────────────────

        private bool _busy;
        private string _statusText = "";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
        private bool _hasStatus;
        public bool HasStatus { get => _hasStatus; set => SetProperty(ref _hasStatus, value); }
        private bool _statusIsError;
        public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

        // ── Loading ──────────────────────────────────────────────────────────────

        private Dictionary<string, SunshineSync.AppCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

        public void Load()
        {
            var hostInfo = LogParser.FindStreamingAppInfo();
            Server = hostInfo?.AppName ?? "Sunshine";
            OnPropertyChanged(nameof(PendingText));

            _hostAssetsDir      = HostAssetsManager.GetAssetsDirectory(hostInfo);
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

            _syncEnabled = GameLibraryState.Current.SyncEnabled;
            OnPropertyChanged(nameof(SyncEnabled));

            Reload();
            WatchJob();
            _ = ReadServerAsync();
        }

        /// <summary>The list again: after a sync this page did not start, or a change made here.</summary>
        public void Reload()
        {
            string? appsJson = SunshineSync.FindAppsJsonPath();
            _commands = appsJson != null ? SunshineSync.ReadCommands(appsJson) : new(StringComparer.OrdinalIgnoreCase);

            var state = GameLibraryState.Current;
            if (state.LastSyncUtc is DateTime t)
            {
                var local = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
                KpiSync      = local.ToString("HH:mm");
                KpiSyncLabel = $"Last sync · {GameStatsService.RelativeDay(local).ToLowerInvariant()}";
            }
            else { KpiSync = "—"; KpiSyncLabel = "Not synced yet"; }

            int listed = state.Games.Count(g => g.Enabled && !g.IsManual), excluded = state.Games.Count(g => !g.Enabled),
                manual = state.Games.Count(g => g.Enabled && g.IsManual);
            KpiListed = listed.ToString(); KpiExcluded = excluded.ToString(); KpiManual = manual.ToString();
            OnPropertyChanged(nameof(ListedLabel));
            OnPropertyChanged(nameof(CommandHeader));
            ListedTab   = $"In {Server}";
            ExcludedTab = "Excluded";
            ManualTab   = "Added by hand";
            LegacyExcluded = state.ExcludedGames.Count;
            Fill();
        }

        private void Fill()
        {
            var state = GameLibraryState.Current;
            IEnumerable<GameLibraryEntry> games = _tab switch
            {
                "excluded" => state.Games.Where(g => !g.Enabled),
                "manual"   => state.Games.Where(g => g.Enabled && g.IsManual),
                _          => state.Games.Where(g => g.Enabled && !g.IsManual),
            };
            if (_filter.Trim().Length > 0)
                games = games.Where(g => g.Name.Contains(_filter.Trim(), StringComparison.CurrentCultureIgnoreCase));

            Rows.Clear();
            foreach (var g in games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
                Rows.Add(new LibraryRow(g, _commands.TryGetValue(g.Name, out var c) ? c : null, Server));

            EmptyText = Rows.Count > 0 ? ""
                      : _filter.Trim().Length > 0 ? "No game matches."
                      : _tab switch
                        {
                            "excluded" => "No game is excluded. Exclude keeps a game out of the streaming server at every sync.",
                            "manual"   => "Nothing added by hand. Add a game takes any program.",
                            _          => state.Games.Count == 0
                                          ? "No games yet. Sync and restart looks for them in the stores installed on this PC."
                                          : "Every game is excluded or was added by hand.",
                        };
            OnPropertyChanged(nameof(HasLegacyExcluded));
        }

        /// <summary>The server's service state and its own Steam sync, read off the UI thread (CIM, sunshine.conf).</summary>
        private async Task ReadServerAsync()
        {
            string text = "", state = "";
            try
            {
                var svc = await Task.Run(() => StreamingServer.ServiceFor(SunshineSync.FindAppsJsonPath()));
                if (svc != null)
                {
                    // The fork's name as Status shows it (Vibeshine installs into a Sunshine folder).
                    if (svc.ServerName.Length > 0 && svc.ServerName != Server)
                    {
                        Server = svc.ServerName;
                        OnPropertyChanged(nameof(PendingText));
                        Reload();
                    }
                    state = await Task.Run(() => StreamingServer.State(svc.Name));
                    if (await Task.Run(() => StreamingServer.SteamSyncOn(svc)))
                        text = $"{svc.ServerName} syncs Steam too, so Steam games would be listed twice. Turn one of the two off.";
                }
            }
            catch { }
            SteamConflict  = text;
            ServerState    = state.Length == 0 ? "" : state.ToLowerInvariant();
            ServerStateHex = state == "Running" ? "#4ade80" : state.Length == 0 ? "#646b67" : "#fbbf24";
        }

        // ── Actions on a game ────────────────────────────────────────────────────

        /// <summary>Exclude, Include again or (added by hand) Remove — the row's one action.</summary>
        public async Task ActAsync(LibraryRow row)
        {
            if (!CanChange) return;
            var state = GameLibraryState.Current;
            if (row.Entry.IsManual && row.Entry.Enabled)
            {
                _busy = true; OnPropertyChanged(nameof(CanChange));
                try { ShowStatus(await GameLibraryService.RemoveGameAsync(row.Entry), isError: false); }
                finally { _busy = false; OnPropertyChanged(nameof(CanChange)); }
            }
            else
            {
                row.Entry.Enabled = !row.Entry.Enabled;
                state.Save();
            }
            Pending = true;
            Reload();
        }

        /// <summary>
        /// Play, as a test of the command (plan 10.0): runs what apps.json says — the detached
        /// commands, then the command — in the folder it names. Not a replay of the server: no
        /// prep commands, no elevation, no SUNSHINE_CLIENT_* variables.
        /// </summary>
        public void Play(LibraryRow row)
        {
            if (!_commands.TryGetValue(row.Name, out var c)) return;
            try
            {
                foreach (var d in c.Detached) Run(d, c.WorkingDir);
                if (c.Cmd.Length > 0) Run(c.Cmd, c.WorkingDir);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not run the command of \"{row.Name}\": {ex.Message}", isError: true);
            }
        }

        /// <summary>A command line as the server holds it: an URI (steam://…), a quoted program
        /// with arguments, or a bare program with arguments.</summary>
        private static void Run(string commandLine, string workingDir)
        {
            string line = commandLine.Trim();
            string file, args = "";
            if (line.StartsWith('"'))
            {
                int end = line.IndexOf('"', 1);
                file = end > 0 ? line[1..end] : line.Trim('"');
                args = end > 0 ? line[(end + 1)..].Trim() : "";
            }
            else if (line.Contains("://", StringComparison.Ordinal) && !line.Contains(' '))
                file = line;
            else
            {
                int space = line.IndexOf(' ');
                file = space > 0 ? line[..space] : line;
                args = space > 0 ? line[(space + 1)..].Trim() : "";
            }
            var psi = new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = true };
            // ⚠️ Without a folder the game inherits StreamTweak's, under Program Files, and one that
            // looks for its data beside where it was started refuses to run (Alan Wake 2, 7.x).
            string dir = workingDir.Length > 0 ? workingDir : (Path.IsPathRooted(file) ? Path.GetDirectoryName(file) ?? "" : "");
            if (dir.Length > 0 && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi);
        }

        public async Task AddGameAsync()
        {
            var picker = new FileOpenPicker();
            if (App.MainWindow is { } win)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(win));
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            _busy = true; OnPropertyChanged(nameof(CanChange));
            try
            {
                ShowStatus(await GameLibraryService.AddManualGameAsync(file.Path), isError: false);
                Pending = true;
                Reload();
            }
            catch (Exception ex) { ShowStatus($"Could not add the game: {ex.Message}", isError: true); }
            finally { _busy = false; OnPropertyChanged(nameof(CanChange)); }
        }

        /// <summary>The pre-10.0 removals let back in: their keys are cleared, the next sync finds them.</summary>
        public void ClearLegacyExclusions()
        {
            var state = GameLibraryState.Current;
            state.ExcludedGames.Clear();
            state.Save();
            Pending = true;
            Reload();
        }

        // ── Sync and restart ─────────────────────────────────────────────────────

        public void StartSyncAndRestart() => StartJob(AppStateService.Instance.LibrarySyncStarter, "Looking for games…");

        /// <summary>Clear library: the page has asked first.</summary>
        public void StartClearAndRestart() => StartJob(AppStateService.Instance.LibraryClearStarter, $"Clearing {Server}'s list…");

        private void StartJob(Func<string>? starter, string firstStep)
        {
            string? reply = starter?.Invoke();
            switch (reply)
            {
                case "OK": JobFailed = false; JobText = firstStep; WatchJob(); break;
                case "ERR_STREAMING":    ShowStatus("A device is streaming from this PC. Try again when the stream has ended.", isError: true); break;
                case "ERR_GAME_RUNNING": ShowStatus($"A game launched by {Server} is still running: restarting {Server} would close it. Close it first.", isError: true); break;
                case "ERR_NO_SERVER":    ShowStatus("StreamTweak found no streaming server it can restart.", isError: true); break;
                case "ERR_BUSY":
                    var job = AppStateService.Instance.LibraryJobReader?.Invoke();
                    ShowStatus(job is { CooldownSeconds: > 0 } j && j.Phase is "done" or "failed"
                        ? $"{Server} was restarted a moment ago. Try again in {j.CooldownSeconds} s."
                        : "A sync is already running.", isError: true);
                    break;
                default: ShowStatus("Sync and restart is not available.", isError: true); break;
            }
        }

        /// <summary>Follows the job once a second while it runs — started here or from StreamLight.</summary>
        private void WatchJob()
        {
            if (_jobTimer == null)
            {
                _jobTimer = _ui.CreateTimer();
                _jobTimer.Interval = TimeSpan.FromSeconds(1);
                _jobTimer.Tick += (_, _) => ReadJob();
            }
            _jobTimer.Start();
            ReadJob();
        }

        private string _lastPhase = "";

        private void ReadJob()
        {
            if (AppStateService.Instance.LibraryJobReader?.Invoke() is not { } job) { _jobTimer?.Stop(); return; }
            bool running = job.Phase is "scanning" or "writing" or "stopping" or "starting" or "waiting";
            JobRunning = running;
            string server = job.Server.Length > 0 ? job.Server : Server;
            int s = (int)job.Elapsed.TotalSeconds;

            if (running)
            {
                string step = job.Phase switch
                {
                    "scanning" => "Looking for games in the stores",
                    "writing"  => $"Writing {server}'s list",
                    "stopping" => $"Stopping {server}",
                    "starting" => $"Starting {server}",
                    _          => $"Waiting for {server} to answer",
                };
                JobText = $"{step}… · {s} s";
                JobFailed = false;
            }
            else if (_lastPhase is "scanning" or "writing" or "stopping" or "starting" or "waiting")
            {
                // It has just ended: say how, once, and take the new list.
                if (job.Phase == "done")
                {
                    string changes = (job.Added.Count, job.Removed.Count) switch
                    {
                        (0, 0) => "no change in the list",
                        var (a, r) => string.Join(" · ", new[] { a > 0 ? $"{a} added" : "", r > 0 ? $"{r} removed" : "" }.Where(x => x.Length > 0)),
                    };
                    JobText = $"Done in {s} s: {changes}. {server} is back.";
                    JobFailed = false;
                    Pending = false;
                }
                else
                {
                    JobText = job.Error switch
                    {
                        "streaming" => "Stopped: a device started streaming. Nothing was restarted.",
                        "no answer" => $"{server} restarted but does not answer yet. Check it from its own page.",
                        var e       => $"It did not finish: {e}",
                    };
                    JobFailed = true;
                }
                Reload();
                _jobTimer?.Stop();
            }
            else if (!running)
                _jobTimer?.Stop();
            _lastPhase = job.Phase;
        }

        public void Stop() => _jobTimer?.Stop();

        // ── Host tiles ───────────────────────────────────────────────────────────

        public async Task ToggleHostAssetsAsync()
        {
            if (_busy || _hostAssetsDir == null) return;
            _busy = true; OnPropertyChanged(nameof(CanChange));
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
                        ShowStatus($"Restored {Server}'s original Desktop & Steam tiles.", isError: false);
                    }
                    else ShowStatus("Could not restore the tiles. Is the StreamTweak service running?", isError: true);
                }
                else
                {
                    string desktopSrc = Path.Combine(AppContext.BaseDirectory, "Resources", "desktop.png");
                    string steamSrc   = Path.Combine(AppContext.BaseDirectory, "Resources", "steam.png");
                    if (!File.Exists(desktopSrc) || !File.Exists(steamSrc))
                    {
                        ShowStatus("The replacement tiles are missing from StreamTweak's Resources folder.", isError: true);
                        return;
                    }
                    bool ok = await HostAssetsManager.SwapAsync(_hostAssetsDir, desktopSrc, steamSrc);
                    if (ok)
                    {
                        ConfigService.Set("HostTilesApplied", true);
                        AppStateService.Instance.HostAssetsGuard?.NudgeAfterUserAction();
                        HostAssetsApplied = HostAssetsManager.IsApplied(_hostAssetsDir);
                        ShowStatus($"Replaced {Server}'s Desktop & Steam tiles. The originals are kept.", isError: false);
                    }
                    else ShowStatus("Could not replace the tiles. Is the StreamTweak service running?", isError: true);
                }
            }
            catch (Exception ex) { ShowStatus($"The tiles could not be changed: {ex.Message}", isError: true); }
            finally { _busy = false; OnPropertyChanged(nameof(CanChange)); }
        }

        private void ShowStatus(string text, bool isError)
        {
            StatusText    = text;
            StatusIsError = isError;
            HasStatus     = true;
        }
    }
}
