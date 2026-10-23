using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    /// <summary>One of Status's eight tiles: a component, how it is, where it is set.</summary>
    public sealed class StatusRow
    {
        public string Key    { get; init; } = "";
        public string Name   { get; init; } = "";
        /// <summary>The big figure ("2.5 Gbps", "Vibeshine", "12").</summary>
        public string Value  { get; init; } = "";
        /// <summary>The unit after it, smaller ("games", "devices"); empty when none.</summary>
        public string Unit   { get; init; } = "";
        /// <summary>One line of detail, under the figure.</summary>
        public string Line   { get; init; } = "";
        /// <summary>ok · warn · bad · off</summary>
        public string Level  { get; init; } = "ok";
        /// <summary>Navigation tag of the page that sets it ("Apps", "Settings"…).</summary>
        public string Target { get; init; } = "";
        /// <summary>The one tile with a button of its own: Network, while a client has the link.</summary>
        public bool   CanRestore { get; init; }
        /// <summary>Segoe Fluent glyph and the tile's own colour (the bubble behind the glyph).</summary>
        public string Glyph  { get; init; } = "";
        public string Tint   { get; init; } = "#4ade80";

        public bool   HasUnit   => Unit.Length > 0;
        public string DotHex    => Level switch { "warn" => "#fbbf24", "bad" => "#f87171", "off" => "#646b67", _ => "#4ade80" };
        public string LineHex   => Level switch { "warn" => "#fbbf24", "bad" => "#f87171", _ => "#929a96" };
        public string IconHex   => Level switch { "warn" => "#fbbf24", "bad" => "#f87171", _ => Tint };
        public string BubbleHex => "#24" + IconHex.TrimStart('#');
        public string BorderHex => Level switch { "warn" => "#66fbbf24", "bad" => "#66f87171", _ => "#2a2724" };
        public string FillHex   => Level switch { "warn" => "#FF201C13", "bad" => "#FF211616", _ => "#FF1B1D1D" };
    }

    /// <summary>
    /// 10.0 Status, the page StreamTweak opens on and the one the README shows (plan phase 4, mockup
    /// «Vetrina» B, 22/10): a hero that says in one line how the host is — over the covers of the
    /// games on this PC, picked afresh each time — with three figures; then a device asking for
    /// access and the stream running now when there are; then eight tiles, one per component, each
    /// opening the page that sets it. The everyday switches are in StreamLight 8.
    /// </summary>
    public sealed class StatusViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
        private DispatcherQueueTimer? _clock;
        private int _tick;
        private bool _loading;

        // The first read, started by App while the window is being built (Prewarm): it costs a second
        // or two — four CIM queries to the services, the firewall's rules, the session history — and
        // the page would sit empty for that long on its first appearance.
        private static Task<Probe>? s_warm;

        /// <summary>Starts Status's first read on the thread pool. Called once by App, after the
        /// services are wired (the read asks them) and before MainWindow builds the page.</summary>
        public static void Prewarm() => s_warm ??= Task.Run(Probe.Read);

        public ObservableCollection<StatusRow> Rows { get; } = new();

        // ── Hero ─────────────────────────────────────────────────────────────────
        private string _heroTitle = "Ready to stream", _heroSub = "", _eyebrow = "All set", _eyebrowHex = "#4ade80";
        public string HeroTitle  { get => _heroTitle;  private set => SetProperty(ref _heroTitle, value); }
        public string HeroSub    { get => _heroSub;    private set => SetProperty(ref _heroSub, value); }
        public string Eyebrow    { get => _eyebrow;    private set => SetProperty(ref _eyebrow, value); }
        public string EyebrowHex { get => _eyebrowHex; private set => SetProperty(ref _eyebrowHex, value); }

        private string _kpiLink = "—", _kpiLinkUnit = "", _kpiGames = "—", _kpiGrade = "—", _kpiGradeHex = "#929a96", _kpiGradeWhen = "No session yet";
        public string KpiLink      { get => _kpiLink;      private set => SetProperty(ref _kpiLink, value); }
        public string KpiLinkUnit  { get => _kpiLinkUnit;  private set => SetProperty(ref _kpiLinkUnit, value); }
        public string KpiGames     { get => _kpiGames;     private set => SetProperty(ref _kpiGames, value); }
        public string KpiGrade     { get => _kpiGrade;     private set => SetProperty(ref _kpiGrade, value); }
        public string KpiGradeHex  { get => _kpiGradeHex;  private set => SetProperty(ref _kpiGradeHex, value); }
        public string KpiGradeWhen { get => _kpiGradeWhen; private set => SetProperty(ref _kpiGradeWhen, value); }

        /// <summary>The covers behind the hero: the games on this PC, shuffled once per visit.</summary>
        public event Action<IReadOnlyList<string>>? CoversReady;
        private bool _coversSent;

        // ── Needs you: the first device waiting for approval ─────────────────────
        private BridgeClient? _pending;
        public bool   HasPending   => _pending != null;
        public string PendingTitle => _pending == null ? "" : $"{DisplayName(_pending)} asks for access";
        public string PendingPin   => string.IsNullOrEmpty(_pending?.Pin) ? "————" : _pending!.Pin!;
        public string PendingMore  { get; private set; } = "";

        // ── Streaming now ────────────────────────────────────────────────────────
        private bool _streaming;
        public bool Streaming { get => _streaming; private set => SetProperty(ref _streaming, value); }

        private string _liveTitle = "", _liveSub = "";
        public string LiveTitle { get => _liveTitle; private set => SetProperty(ref _liveTitle, value); }
        public string LiveSub   { get => _liveSub;   private set => SetProperty(ref _liveSub, value); }

        private string? _liveCover;
        public string? LiveCover { get => _liveCover; private set { if (SetProperty(ref _liveCover, value)) OnPropertyChanged(nameof(HasLiveCover)); } }
        public bool HasLiveCover => !string.IsNullOrEmpty(_liveCover);

        // ── Getting started: the four steps of a first run ───────────────────────
        private bool _showSteps;
        public bool ShowSteps { get => _showSteps; private set => SetProperty(ref _showSteps, value); }
        public bool StepServer  { get; private set; }
        public bool StepPort    { get; private set; }
        public bool StepClient  { get; private set; }
        public bool StepApps    { get; private set; }
        public string StepsCaption => $"Getting started · {new[] { StepServer, StepPort, StepClient, StepApps }.Count(b => b)} of 4";

        // ── Lifetime ─────────────────────────────────────────────────────────────

        public void Start()
        {
            var app = AppStateService.Instance;
            app.SessionStateChanged    += OnSomethingChanged;
            app.SettingsChanged        += OnSomethingChanged;
            ManagedAppController.Changed += OnManagedApps;
            if (app.BridgeAuth != null)   app.BridgeAuth.ClientsChanged += OnClients;
            if (app.LinkSpeed != null)    app.LinkSpeed.Changed += OnClients;
            if (app.NvidiaSentinel != null)
            {
                app.NvidiaSentinel.AutoRestorePerformed    += OnSomethingChanged;
                app.NvidiaSentinel.AutoRestoreStateChanged += OnSomethingChanged;
            }

            // Once a second: the stream's clock. Every 10 s: everything, for what has no event
            // (the server's service, the firewall, a sync done by the server itself).
            _clock = _ui.CreateTimer();
            _clock.Interval = TimeSpan.FromSeconds(1);
            _clock.Tick += (_, _) =>
            {
                RefreshLive();
                if (++_tick % 10 == 0) _ = RefreshAsync();
            };
            _clock.Start();
            _ = RefreshAsync();
        }

        public void Stop()
        {
            var app = AppStateService.Instance;
            app.SessionStateChanged    -= OnSomethingChanged;
            app.SettingsChanged        -= OnSomethingChanged;
            ManagedAppController.Changed -= OnManagedApps;
            if (app.BridgeAuth != null)   app.BridgeAuth.ClientsChanged -= OnClients;
            if (app.LinkSpeed != null)    app.LinkSpeed.Changed -= OnClients;
            if (app.NvidiaSentinel != null)
            {
                app.NvidiaSentinel.AutoRestorePerformed    -= OnSomethingChanged;
                app.NvidiaSentinel.AutoRestoreStateChanged -= OnSomethingChanged;
            }
            _clock?.Stop();
            _clock = null;
        }

        private void OnSomethingChanged(object? sender, EventArgs e) => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnSomethingChanged(object? sender, bool e)     => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnManagedApps() => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnClients()     => _ui.TryEnqueue(() => _ = RefreshAsync());

        // ── Actions ──────────────────────────────────────────────────────────────

        public void Approve(bool hostManagement)
        {
            if (_pending != null) AppStateService.Instance.BridgeAuth?.Approve(_pending.UniqueId, hostManagement);
        }

        public void Deny()
        {
            if (_pending != null) AppStateService.Instance.BridgeAuth?.Deny(_pending.UniqueId);
        }

        public void ReturnToClient() => AppStateService.Instance.RequestStopStreamAction?.Invoke();

        public void RestoreLink() => AppStateService.Instance.LinkSpeed?.RestoreNow("Status page");

        public void HideSteps()
        {
            ConfigService.Set("StatusStepsHidden", true);
            ShowSteps = false;
        }

        // ── Reading ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds everything. The slow reads (the services through CIM, the firewall's rules,
        /// sunshine.conf, the session history) run on the thread pool; the tiles are swapped in on
        /// the UI thread.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var probe = await (Interlocked.Exchange(ref s_warm, null) ?? Task.Run(Probe.Read));
                var rows  = BuildRows(probe);

                // Swapped in place only when something moved, so the tiles do not flicker every 10 s.
                bool same = rows.Count == Rows.Count && rows.Zip(Rows).All(p =>
                    p.First.Key == p.Second.Key && p.First.Value == p.Second.Value && p.First.Unit == p.Second.Unit
                    && p.First.Line == p.Second.Line && p.First.Level == p.Second.Level && p.First.CanRestore == p.Second.CanRestore);
                if (!same)
                {
                    Rows.Clear();
                    foreach (var r in rows) Rows.Add(r);
                }

                var clients = AppStateService.Instance.BridgeAuth?.GetClients() ?? new List<BridgeClient>();
                _pending = clients.FirstOrDefault(c => c.Status == "pending");
                int others = clients.Count(c => c.Status == "pending") - 1;
                PendingMore = others > 0 ? $"{others} more waiting under Setup › Clients" : "Approve only if the device shows this PIN";
                OnPropertyChanged(nameof(HasPending));
                OnPropertyChanged(nameof(PendingTitle));
                OnPropertyChanged(nameof(PendingPin));
                OnPropertyChanged(nameof(PendingMore));

                StepServer = probe.ServerName != null;
                StepPort   = probe.PortOpen != false;
                StepClient = clients.Any(c => c.Status == "approved");
                StepApps   = probe.ManagedApps > 0;
                ShowSteps  = !ConfigService.GetBool("StatusStepsHidden", false)
                             && !(StepServer && StepPort && StepClient && StepApps);
                OnPropertyChanged(nameof(StepServer));
                OnPropertyChanged(nameof(StepPort));
                OnPropertyChanged(nameof(StepClient));
                OnPropertyChanged(nameof(StepApps));
                OnPropertyChanged(nameof(StepsCaption));

                // The hero's figures.
                KpiLink     = probe.HasAdapter ? probe.SpeedNumber : "—";
                KpiLinkUnit = probe.HasAdapter ? probe.SpeedUnit : "";
                KpiGames    = probe.Listed.ToString(CultureInfo.InvariantCulture);
                KpiGrade     = probe.LastGrade ?? "—";
                KpiGradeHex  = probe.LastGradeHex ?? "#929a96";
                KpiGradeWhen = probe.LastGradeWhen ?? "No session yet";
                _attention = rows.Count(r => r.Level is "warn" or "bad") + (HasPending ? 1 : 0);
                _serverLine = probe.ServerName == null ? null
                    : $"{probe.ServerName} is {(probe.ServerState is "Running" or "" ? "running" : probe.ServerState.ToLowerInvariant())}"
                      + (probe.Approved switch { 0 => " and no device is approved yet.", 1 => " and 1 device may connect.", var n => $" and {n} devices may connect." });

                if (!_coversSent && probe.Covers.Count > 0)
                {
                    _coversSent = true;
                    CoversReady?.Invoke(probe.Covers);
                }

                RefreshLive();
            }
            catch (Exception ex) { DebugLogger.Log($"[Status] refresh failed: {ex.Message}"); }
            finally { _loading = false; }
        }

        private int _attention;
        private string? _serverLine;
        private string? _liveGameName;

        private void RefreshLive()
        {
            Streaming = AppStateService.Instance.IsSessionActive;
            string? who = Streaming ? AppStateService.Instance.StreamingClientProvider?.Invoke() : null;

            if (!Streaming)
            {
                _liveGameName = null;
                HeroTitle  = _serverLine == null ? "Not ready yet" : "Ready to stream";
                HeroSub    = _serverLine ?? "No streaming server found on this PC. Install Sunshine, Apollo, Vibeshine or Vibepollo.";
                Eyebrow    = _serverLine == null ? "Needs a streaming server"
                           : _attention == 0 ? "All set"
                           : _attention == 1 ? "Ready · 1 thing to look at" : $"Ready · {_attention} things to look at";
                EyebrowHex = _serverLine == null ? "#f87171" : _attention == 0 ? "#4ade80" : "#fbbf24";
                return;
            }

            string? game = SessionLogger.CurrentGameName(TimeSpan.FromSeconds(20));
            if (game != _liveGameName)
            {
                _liveGameName = game;
                string? cover = null;
                if (!string.IsNullOrEmpty(game))
                {
                    try
                    {
                        var entry = GameLibraryState.Current.Games.FirstOrDefault(g =>
                            string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase));
                        if (entry?.CoverImagePath != null && File.Exists(entry.CoverImagePath)) cover = entry.CoverImagePath;
                    }
                    catch { }
                }
                LiveCover = cover;
            }
            LiveTitle = string.IsNullOrEmpty(game) ? "A stream is running" : game;

            var start = SessionLogger.ActiveSessionStartTime;
            var d = start != default ? DateTime.Now - start : TimeSpan.Zero;
            string clock = d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : $"{Math.Max(0, d.Minutes)} min";
            LiveSub = string.IsNullOrEmpty(who) ? $"For {clock}" : $"To {who} · for {clock}";

            HeroTitle  = string.IsNullOrEmpty(game) ? "Streaming now" : $"Streaming {game}";
            HeroSub    = $"Started {clock} ago" + (string.IsNullOrEmpty(who) ? "." : $" on {who}.");
            Eyebrow    = string.IsNullOrEmpty(who) ? "Live" : $"Live · {who}";
            EyebrowHex = "#4ade80";
        }

        private static List<StatusRow> BuildRows(Probe p)
        {
            var rows = new List<StatusRow>();
            const string gService = "", gBridge = "", gServer = "", gLibrary = "",
                         gApps = "", gAudio = "", gNet = "", gSentinel = "";

            rows.Add(p.ServiceState switch
            {
                "Running" => new StatusRow { Key = "service", Name = "Service", Value = "Running", Line = "LocalSystem · no UAC prompts", Target = "Settings", Glyph = gService, Tint = "#4ade80" },
                ""        => new StatusRow { Key = "service", Name = "Service", Value = "Missing", Line = "Reinstall StreamTweak: link speed and Sync and restart need it", Level = "bad", Target = "Settings", Glyph = gService },
                var s     => new StatusRow { Key = "service", Name = "Service", Value = s, Line = "Link speed and Sync and restart need it", Level = "bad", Target = "Settings", Glyph = gService },
            });

            rows.Add(p.PortOpen == false
                ? new StatusRow { Key = "bridge", Name = "Bridge", Value = "Blocked", Line = "Port 47998 is closed in Windows Firewall · run the installer again", Level = "warn", Target = "Clients", Glyph = gBridge }
                : new StatusRow { Key = "bridge", Name = "Bridge", Value = p.Approved.ToString(CultureInfo.InvariantCulture), Unit = p.Approved == 1 ? "device" : "devices",
                                  Line = p.Approved == 0 ? "No device approved yet" : p.PortOpen == true ? "Port 47998 open" : "Listening on port 47998",
                                  Level = p.Approved == 0 ? "off" : "ok", Target = "Clients", Glyph = gBridge, Tint = "#38bdf8" });

            rows.Add(p.ServerName == null
                ? new StatusRow { Key = "server", Name = "Streaming server", Value = "None", Line = "Install Sunshine, Apollo, Vibeshine or Vibepollo", Level = "bad", Target = "Settings", Glyph = gServer }
                : p.ServerState is "Running" or ""
                    ? new StatusRow { Key = "server", Name = "Streaming server", Value = p.ServerName, Line = p.ServerState == "Running" ? "Running" : "Found on this PC", Target = "Settings", Glyph = gServer, Tint = "#a78bfa" }
                    : new StatusRow { Key = "server", Name = "Streaming server", Value = p.ServerName, Line = p.ServerState, Level = "warn", Target = "Settings", Glyph = gServer });

            string games = p.Listed.ToString(CultureInfo.InvariantCulture), gamesUnit = p.Listed == 1 ? "game" : "games";
            rows.Add(p.SteamSyncConflict
                ? new StatusRow { Key = "library", Name = "Library", Value = games, Unit = gamesUnit, Line = $"{p.ServerName} syncs Steam too: turn one off", Level = "warn", Target = "GameLibrary", Glyph = gLibrary }
                : new StatusRow { Key = "library", Name = "Library", Value = games, Unit = gamesUnit, Line = p.SyncEnabled ? p.LastSync : "Sync at start-up is off · " + p.LastSync,
                                  Level = p.Listed > 0 ? "ok" : "off", Target = "GameLibrary", Glyph = gLibrary, Tint = "#fb923c" });

            rows.Add(new StatusRow { Key = "apps", Name = "Apps", Value = p.ManagedApps.ToString(CultureInfo.InvariantCulture), Unit = p.ManagedApps == 1 ? "app" : "apps",
                                     Line = p.ManagedApps > 0 ? "Closed while streaming" : "Nothing is closed when a stream starts",
                                     Level = p.ManagedApps > 0 ? "ok" : "off", Target = "Apps", Glyph = gApps, Tint = "#f472b6" });

            rows.Add(p.AudioOn
                ? new StatusRow { Key = "audio", Name = "Spatial audio", Value = p.AudioShort, Line = "On when a stream starts", Target = "Audio", Glyph = gAudio, Tint = "#22d3ee" }
                : new StatusRow { Key = "audio", Name = "Spatial audio", Value = "Off", Line = "Dolby Atmos or Windows Sonic, when a stream starts", Level = "off", Target = "Audio", Glyph = gAudio, Tint = "#22d3ee" });

            if (!p.HasAdapter)
                rows.Add(new StatusRow { Key = "network", Name = "Network", Value = "No cable", Line = "Link-speed matching works over Ethernet only", Level = "off", Target = "Network", Glyph = gNet, Tint = "#60a5fa" });
            else if (p.Switched)
                rows.Add(new StatusRow { Key = "network", Name = "Network", Value = p.SpeedNumber, Unit = p.SpeedUnit, Line = $"For {p.SwitchedBy} · was {p.Original}", Level = "warn", Target = "Network", CanRestore = true, Glyph = gNet });
            else
                rows.Add(new StatusRow { Key = "network", Name = "Network", Value = p.SpeedNumber, Unit = p.SpeedUnit, Line = $"{p.Adapter} · " + (p.ClientControl ? "clients may match it" : "clients may not change it"),
                                         Target = "Network", Glyph = gNet, Tint = "#60a5fa" });

            if (p.Nvidia)
            {
                rows.Add(p.SentinelStuck
                    ? new StatusRow { Key = "sentinel", Name = "NVIDIA Sentinel", Value = "Stuck", Line = "Auto-restore cannot put your profile back", Level = "warn", Target = "Sentinel", Glyph = gSentinel }
                    : p.SentinelSaved == 0
                        ? new StatusRow { Key = "sentinel", Name = "NVIDIA Sentinel", Value = "No profile", Line = "Save your driver profile to keep it", Level = "off", Target = "Sentinel", Glyph = gSentinel, Tint = "#76b900" }
                        : new StatusRow { Key = "sentinel", Name = "NVIDIA Sentinel", Value = p.SentinelSaved.ToString(CultureInfo.InvariantCulture), Unit = "settings",
                                          Line = "Saved · auto-restore " + (p.SentinelAuto ? "on" : "off"), Target = "Sentinel", Glyph = gSentinel, Tint = "#76b900" });
            }
            return rows;
        }

        private static string DisplayName(BridgeClient c) => string.IsNullOrWhiteSpace(c.Name) ? "A StreamLight device" : c.Name;

        /// <summary>Everything Status reads, gathered off the UI thread in one go.</summary>
        private sealed class Probe
        {
            public string ServiceState = "";
            public bool?  PortOpen;
            public int    Approved;
            public string? ServerName;
            public string ServerState = "";
            public bool   SteamSyncConflict, SyncEnabled;
            public int    Listed;
            public string LastSync = "";
            public int    ManagedApps;
            public bool   AudioOn;
            public string AudioShort = "";
            public bool   HasAdapter, Switched, ClientControl;
            public string Adapter = "", SpeedNumber = "", SpeedUnit = "", Original = "", SwitchedBy = "";
            public bool   Nvidia, SentinelStuck, SentinelAuto;
            public int    SentinelSaved;
            public string? LastGrade, LastGradeHex, LastGradeWhen;
            public List<string> Covers = new();

            public static Probe Read()
            {
                var p = new Probe();
                var app = AppStateService.Instance;

                try { p.ServiceState = StreamingServer.State("StreamTweakService"); } catch { }
                p.PortOpen = BridgeFirewall.IsOpen();
                try { p.Approved = app.BridgeAuth?.GetClients().Count(c => c.Status == "approved") ?? 0; } catch { }

                try
                {
                    var info = LogParser.FindStreamingAppInfo();
                    var svc  = StreamingServer.ServiceFor(SunshineSync.FindAppsJsonPath());
                    p.ServerName = svc?.ServerName ?? info?.AppName;
                    if (svc != null)
                    {
                        p.ServerState       = StreamingServer.State(svc.Name);
                        p.SteamSyncConflict = StreamingServer.SteamSyncOn(svc);
                    }
                }
                catch { }

                try
                {
                    var state = GameLibraryState.Current;
                    p.SyncEnabled = state.SyncEnabled;
                    p.Listed      = state.Games.Count(g => g.Enabled);
                    if (state.LastSyncUtc is DateTime t)
                    {
                        var local = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
                        p.LastSync = $"Last sync {GameStatsService.RelativeDay(local).ToLowerInvariant()} {local:HH:mm}";
                    }
                    else p.LastSync = "Not synced yet";

                    // The hero's backdrop: the covers of the games listed on this PC, in a new
                    // order each time StreamTweak starts (the page is kept alive between visits).
                    p.Covers = state.Games.Where(g => g.Enabled && !string.IsNullOrEmpty(g.CoverImagePath) && File.Exists(g.CoverImagePath))
                                          .Select(g => g.CoverImagePath!)
                                          .OrderBy(_ => Random.Shared.Next()).ToList();
                }
                catch { }

                try { p.ManagedApps = ManagedAppController.List().Count(a => a.AutoManage); } catch { }

                p.AudioOn    = ConfigService.GetBool("AudioMonitorEnabled", false);
                p.AudioShort = ConfigService.Get("AudioSpatialFormat", "DolbyAtmos") == "WindowsSonic" ? "Windows Sonic" : "Dolby Atmos";

                var link = app.LinkSpeed;
                if (link != null && link.HasManageableAdapter)
                {
                    p.HasAdapter    = true;
                    p.Adapter       = link.AdapterName;
                    (p.SpeedNumber, p.SpeedUnit) = Split(NetworkManager.FormatMbps(link.CurrentMbps));
                    p.Switched      = link.IsSwitched;
                    p.Original      = NetworkManager.FormatMbps(link.OriginalMbps);
                    p.SwitchedBy    = string.IsNullOrEmpty(link.SwitchedBy) ? "a client" : link.SwitchedBy!;
                    p.ClientControl = link.AllowClientControl;
                }

                var nv = app.NvidiaSentinel;
                if (nv?.IsNvidiaAvailable == true)
                {
                    p.Nvidia        = true;
                    p.SentinelStuck = nv.AutoRestoreEnabled && nv.IsStuck;
                    p.SentinelAuto  = nv.AutoRestoreEnabled;
                    try { p.SentinelSaved = nv.HasSnapshot ? StreamTweak.Nvidia.NvidiaSentinelService.ReadSnapshot(nv.SnapshotPath)?.Count ?? 0 : 0; } catch { }
                }

                // The last finished session's grade, as Sessions shows it.
                try
                {
                    var last = SessionLogger.Load().FirstOrDefault(s => s.EndTime != null && s.HasGrade);
                    if (last != null)
                    {
                        p.LastGrade     = last.GradeShortLabel;
                        p.LastGradeHex  = last.GradeColorHex;
                        p.LastGradeWhen = $"Last session · {GameStatsService.RelativeDay(last.StartTime).ToLowerInvariant()}";
                    }
                }
                catch { }
                return p;
            }

            /// <summary>"2.5 Gbps" → ("2.5", "Gbps").</summary>
            private static (string, string) Split(string speed)
            {
                int space = speed.LastIndexOf(' ');
                return space > 0 ? (speed[..space], speed[(space + 1)..]) : (speed, "");
            }
        }
    }

    /// <summary>
    /// Whether Windows Firewall lets StreamLight reach the bridge: an enabled inbound Allow rule
    /// for TCP 47998 or for StreamTweak's own exe (the installer makes one of each kind since
    /// 9.2.1). Null when the rules cannot be read — then Status says nothing rather than guess.
    /// Read at most every 30 s: enumerating the rules costs a few hundred milliseconds.
    /// </summary>
    internal static class BridgeFirewall
    {
        private static bool?    _last;
        private static DateTime _readUtc = DateTime.MinValue;

        public static bool? IsOpen()
        {
            if (DateTime.UtcNow - _readUtc < TimeSpan.FromSeconds(30)) return _last;
            _readUtc = DateTime.UtcNow;
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return _last = null;
                dynamic policy = Activator.CreateInstance(type)!;
                foreach (dynamic rule in policy.Rules)
                {
                    // NET_FW_RULE_DIR_IN = 1, NET_FW_ACTION_ALLOW = 1
                    if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1) continue;
                    string ports = (string?)rule.LocalPorts ?? "";
                    string exe   = (string?)rule.ApplicationName ?? "";
                    if (ports.Split(',').Any(x => x.Trim() == "47998")
                        || exe.EndsWith("StreamTweakUI.exe", StringComparison.OrdinalIgnoreCase))
                        return _last = true;
                }
                return _last = false;
            }
            catch { return _last = null; }
        }
    }
}
