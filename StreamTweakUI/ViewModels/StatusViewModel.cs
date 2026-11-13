using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    /// <summary>One of Status's tiles: a component, how it is, where it is set.</summary>
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
        /// <summary>10.3.0: the sidebar block it belongs to — "setup" (what a stream needs between
        /// host and client) or "pc" (the devices of this PC a stream touches).</summary>
        public string Group  { get; init; } = "setup";
        /// <summary>10.3.0: a picture in place of the glyph (the Xbox logo, white); empty for a glyph.</summary>
        public string Svg    { get; init; } = "";
        public bool   HasSvg   => Svg.Length > 0;
        public bool   HasGlyph => Svg.Length == 0;
        public Uri?   SvgUri   => HasSvg ? new Uri(Svg) : null;

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
    /// access and the stream running now when there are; then the tiles, one per component — in the sidebar's two blocks since 10.3.0 — each
    /// opening the page that sets it. The everyday switches are in StreamLight 8.
    /// </summary>
    public sealed class StatusViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
        private DispatcherQueueTimer? _clock;
        private int _tick;
        private bool _loading;
        /// <summary>A refresh asked for while one was running: it runs once more at the end (10.4.1),
        /// so an event — a controller switched on — arriving mid-read is not lost until the next round.</summary>
        private bool _again;

        // The first read, started by App while the window is being built (Prewarm): it costs a second
        // or two — four CIM queries to the services, the firewall's rules, the session history — and
        // the page would sit empty for that long on its first appearance.
        private static Task<Probe>? s_warm;

        /// <summary>Starts Status's first read on the thread pool. Called once by App, after the
        /// services are wired (the read asks them) and before MainWindow builds the page.</summary>
        public static void Prewarm() => s_warm ??= Task.Run(Probe.Read);

        public ObservableCollection<StatusRow> Rows { get; } = new();
        /// <summary>10.3.0: the same tiles in the sidebar's two blocks, Setup and This PC.</summary>
        public ObservableCollection<StatusRow> SetupRows { get; } = new();
        public ObservableCollection<StatusRow> PcRows { get; } = new();

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

        private string? _liveCover;
        public string? LiveCover { get => _liveCover; private set { if (SetProperty(ref _liveCover, value)) OnPropertyChanged(nameof(HasLiveCover)); } }
        public bool HasLiveCover => !string.IsNullOrEmpty(_liveCover);
        // 10.3.0: the game's box in the hero, while a stream runs — its name, and the codec and
        // start time under it.
        private string _liveGame = "", _liveDetail = "";
        public string LiveGame   { get => _liveGame;   private set => SetProperty(ref _liveGame, value); }
        public string LiveDetail { get => _liveDetail; private set => SetProperty(ref _liveDetail, value); }

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
            // 10.4.1: a controller switched on or off shows on its tile at once, as on the Xbox
            // controllers page — until now only the 10-s round or coming back to the page did it.
            try
            {
                Windows.Gaming.Input.RawGameController.RawGameControllerAdded   += OnControllers;
                Windows.Gaming.Input.RawGameController.RawGameControllerRemoved += OnControllers;
            }
            catch { /* Windows.Gaming.Input unavailable: the 10-s round still reads them */ }

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
            try
            {
                Windows.Gaming.Input.RawGameController.RawGameControllerAdded   -= OnControllers;
                Windows.Gaming.Input.RawGameController.RawGameControllerRemoved -= OnControllers;
            }
            catch { }
            _again = false;
            _clock?.Stop();
            _clock = null;
        }

        private void OnSomethingChanged(object? sender, EventArgs e) => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnSomethingChanged(object? sender, bool e)     => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnManagedApps() => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnClients()     => _ui.TryEnqueue(() => _ = RefreshAsync());
        private void OnControllers(object? sender, Windows.Gaming.Input.RawGameController e) => _ui.TryEnqueue(() => _ = RefreshAsync());

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
            if (_loading) { _again = true; return; }
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
                    SetupRows.Clear();
                    PcRows.Clear();
                    foreach (var r in rows)
                    {
                        Rows.Add(r);
                        (r.Group == "pc" ? PcRows : SetupRows).Add(r);
                    }
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
            finally
            {
                _loading = false;
                // Only while the page is open: Stop() clears the clock.
                if (_again && _clock != null) { _again = false; _ui.TryEnqueue(() => _ = RefreshAsync()); }
            }
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

            var start = SessionLogger.ActiveSessionStartTime;
            var d = start != default ? DateTime.Now - start : TimeSpan.Zero;
            string clock = d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : $"{Math.Max(0, d.Minutes)} min";

            HeroTitle  = "Streaming now";
            HeroSub    = string.IsNullOrEmpty(who) ? $"For {clock}" : $"To {who} · for {clock}";
            Eyebrow    = "Live";
            EyebrowHex = "#4ade80";

            LiveGame = string.IsNullOrEmpty(game) ? "A stream is running" : game;
            string since = start != default ? $"since {start:HH:mm}" : "";
            string? encoder = SessionLogger.ActiveStreamEncoder;
            LiveDetail = encoder != null
                ? EncoderNames.Label(encoder) + (since.Length > 0 ? " · " + since : "")
                : since.Length > 0 ? char.ToUpperInvariant(since[0]) + since[1..] : "";
        }

        private static List<StatusRow> BuildRows(Probe p)
        {
            var rows = new List<StatusRow>();
            const string gService = "", gBridge = "", gServer = "", gLibrary = "",
                         gApps = "", gAudio = "", gNet = "", gSentinel = "";

            rows.Add(p.ServiceState switch
            {
                "Running" => new StatusRow { Key = "service", Name = "Service", Value = "Running", Line = "LocalSystem · no UAC prompts", Target = "Settings", Glyph = gService, Tint = "#4ade80" },
                ""        => new StatusRow { Key = "service", Name = "Service", Value = "Missing", Line = "Reinstall StreamTweak", Level = "bad", Target = "Settings", Glyph = gService },
                var s     => new StatusRow { Key = "service", Name = "Service", Value = s, Line = "Link speed and Sync and restart need it", Level = "bad", Target = "Settings", Glyph = gService },
            });

            rows.Add(p.PortOpen == false
                ? new StatusRow { Key = "bridge", Name = "Bridge", Value = "Blocked", Line = "Port 47998 closed · run the installer again", Level = "warn", Target = "Clients", Glyph = gBridge }
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
                                     Line = p.ManagedApps > 0 ? "Closed while streaming" : "Nothing closed while streaming",
                                     Level = p.ManagedApps > 0 ? "ok" : "off", Target = "Apps", Glyph = gApps, Tint = "#f472b6" });

            rows.Add(p.AudioOn
                ? new StatusRow { Key = "audio", Group = "pc", Name = "Spatial audio", Value = p.AudioShort, Line = "On when a stream starts", Target = "Audio", Glyph = gAudio, Tint = "#22d3ee" }
                : new StatusRow { Key = "audio", Group = "pc", Name = "Spatial audio", Value = "Off", Line = "Dolby Atmos or Windows Sonic", Level = "off", Target = "Audio", Glyph = gAudio, Tint = "#22d3ee" });

            if (!p.HasAdapter)
                rows.Add(new StatusRow { Key = "network", Name = "Network", Value = "No cable", Line = "Link-speed matching needs Ethernet", Level = "off", Target = "Network", Glyph = gNet, Tint = "#60a5fa" });
            else if (p.Switched)
                rows.Add(new StatusRow { Key = "network", Name = "Network", Value = p.SpeedNumber, Unit = p.SpeedUnit, Line = $"For {p.SwitchedBy} · was {p.Original}", Level = "warn", Target = "Network", CanRestore = true, Glyph = gNet });
            else
                rows.Add(new StatusRow { Key = "network", Name = "Network", Value = p.SpeedNumber, Unit = p.SpeedUnit, Line = $"{p.Adapter} · " + (p.ClientControl ? "clients may match it" : "clients may not change it"),
                                         Target = "Network", Glyph = gNet, Tint = "#60a5fa" });

            if (p.Nvidia)
            {
                rows.Add(p.SentinelStuck
                    ? new StatusRow { Key = "sentinel", Group = "pc", Name = "NVIDIA Sentinel", Value = "Stuck", Line = "Auto-restore cannot put your profile back", Level = "warn", Target = "Sentinel", Glyph = gSentinel }
                    : p.SentinelSaved == 0
                        ? new StatusRow { Key = "sentinel", Group = "pc", Name = "NVIDIA Sentinel", Value = "No profile", Line = "Save your driver profile to keep it", Level = "off", Target = "Sentinel", Glyph = gSentinel, Tint = "#76b900" }
                        : new StatusRow { Key = "sentinel", Group = "pc", Name = "NVIDIA Sentinel", Value = p.SentinelSaved.ToString(CultureInfo.InvariantCulture), Unit = "settings",
                                          Line = "Saved · auto-restore " + (p.SentinelAuto ? "on" : "off"), Target = "Sentinel", Glyph = gSentinel, Tint = "#76b900" });
            }

            // 10.3.0: Xbox controllers — shown when there is an adapter or a rule is on.
            if (p.XboxKnown && (p.XboxAdapter || p.XboxShut || p.XboxSleep))
            {
                const string xboxTint = "#52b043", xboxSvg = "ms-appx:///Resources/xbox-logo.svg";
                string rules = p.XboxShut && p.XboxSleep ? "Off at shut down and sleep"
                             : p.XboxShut ? "Off at shut down" : p.XboxSleep ? "Off at sleep" : "";
                string count = p.XboxPads.ToString(CultureInfo.InvariantCulture), unit = p.XboxPads == 1 ? "controller" : "controllers";
                if (p.XboxLow != null)
                    rows.Add(new StatusRow { Key = "xbox", Group = "pc", Name = "Xbox controllers", Value = count, Unit = unit, Line = p.XboxLow,
                                             Level = "warn", Target = "Xbox", Svg = xboxSvg, Tint = xboxTint });
                else if (!p.XboxAdapter)
                    rows.Add(new StatusRow { Key = "xbox", Group = "pc", Name = "Xbox controllers", Value = "No adapter", Line = "Plug in the Xbox Wireless Adapter",
                                             Level = "off", Target = "Xbox", Svg = xboxSvg, Tint = xboxTint });
                else if (rules.Length == 0)
                    rows.Add(new StatusRow { Key = "xbox", Group = "pc", Name = "Xbox controllers", Value = "Off", Line = "Left on at a Fast Startup shut down",
                                             Level = "off", Target = "Xbox", Svg = xboxSvg, Tint = xboxTint });
                else
                    rows.Add(new StatusRow { Key = "xbox", Group = "pc", Name = "Xbox controllers", Value = count, Unit = unit, Line = rules,
                                             Level = p.XboxPads > 0 ? "ok" : "off", Target = "Xbox", Svg = xboxSvg, Tint = xboxTint });
            }

            // 10.3.0: Hue Sync — shown when it is installed or the bridge is paired.
            if (p.HueInstalled || p.HuePaired)
            {
                const string gHue = "\uEA80", hueTint = "#e879f9";
                if (!p.HuePaired)
                    rows.Add(new StatusRow { Key = "hue", Group = "pc", Name = "Hue Sync", Value = "Not paired", Line = "Pair the bridge first",
                                             Level = "off", Target = "Hue", Glyph = gHue, Tint = hueTint });
                else if (!p.HueHold)
                    rows.Add(new StatusRow { Key = "hue", Group = "pc", Name = "Hue Sync", Value = "Off", Line = "Starts without waiting",
                                             Level = "off", Target = "Hue", Glyph = gHue, Tint = hueTint });
                else if (p.HueStartsWithWindows)
                    rows.Add(new StatusRow { Key = "hue", Group = "pc", Name = "Hue Sync", Value = "Starts by itself", Line = "Turn its start with Windows off",
                                             Level = "warn", Target = "Hue", Glyph = gHue });
                else
                    rows.Add(new StatusRow { Key = "hue", Group = "pc", Name = "Hue Sync", Value = p.HueValue, Line = p.HueLine,
                                             Target = "Hue", Glyph = gHue, Tint = hueTint });
            }

            // The sidebar's order (10.3.0): Setup, then This PC.
            string[] order = { "service", "bridge", "server", "library", "network", "apps", "sentinel", "audio", "xbox", "hue" };
            return rows.OrderBy(x => Array.IndexOf(order, x.Key)).ToList();
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
            // 10.3.0
            public bool   XboxKnown, XboxAdapter, XboxShut, XboxSleep;
            public int    XboxPads;
            public string? XboxLow;
            public bool   HueInstalled, HuePaired, HueHold, HueStartsWithWindows;
            public string HueValue = "", HueLine = "";

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

                // 10.3.0: the Xbox controllers — the service's rules and adapter, Windows' pads.
                try
                {
                    // Only with the service running: a missing one would hold every refresh for the
                    // pipe's 5 s, and the Service tile already says it.
                    string? xbox = p.ServiceState == "Running" ? SpeedChanger.GetXboxControllerState() : null;
                    if (xbox != null)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(xbox);
                        var root = doc.RootElement;
                        p.XboxKnown   = true;
                        p.XboxAdapter = root.GetProperty("adapter").GetBoolean();
                        p.XboxShut    = root.GetProperty("offAtShutdown").GetBoolean();
                        p.XboxSleep   = root.GetProperty("offAtSleep").GetBoolean();
                        var pads = XboxControllersViewModel.ReadPadsFromWindows();
                        p.XboxPads = pads.Count;
                        int low = pads.FindIndex(x => x.Level == 0 && !x.Charging);   // Critical, as the page's amber
                        if (low >= 0)
                            p.XboxLow = $"{XboxControllersViewModel.PadName(low, pads.Count)} is running out";
                    }
                }
                catch { }

                // 10.3.0: Hue Sync — installed, paired, the rule, and who has the lights.
                try
                {
                    var gate = HueSyncGate.Instance;
                    p.HueInstalled = HueSyncGate.HueSyncPath() != null;
                    p.HuePaired    = gate.Paired;
                    p.HueHold      = gate.Hold;
                    if (p.HuePaired && p.HueHold)
                    {
                        p.HueStartsWithWindows = HueSyncGate.HueSyncStartsWithWindows();
                        var reading = gate.LastReading;
                        if (reading == null)
                        {
                            var t = gate.ReadAsync();
                            if (t.Wait(1500)) reading = t.Result;
                        }
                        var area = reading?.ActiveArea;
                        string state = gate.HostState();
                        if (state == "syncing")
                            (p.HueValue, p.HueLine) = ("Syncing", area != null ? $"This PC · {area.Name}" : "This PC");
                        else if (area != null)
                            (p.HueValue, p.HueLine) = (state == "waiting" ? "Waiting" : "In use",
                                                       (area.OwnerDevice.Length > 0 ? area.OwnerDevice : "Another device") + $" · {area.Name}");
                        else
                            (p.HueValue, p.HueLine) = ("Free", "Starts here when no device is syncing");
                    }
                }
                catch { }

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
