using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using StreamTweak.Controls;
using StreamTweak.Nvidia;
using StreamTweak.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI;

namespace StreamTweak.ViewModels
{
    // ── One approved StreamLight client (Dashboard "Paired clients" list) ─────
    public sealed class HomeClientRow
    {
        public string Name         { get; init; } = "";
        public string Glyph        { get; init; } = DeviceKinds.UnsetGlyph;
        public string LastSeenText { get; init; } = "";
        public string StatusText   { get; init; } = "Approved";
        public string StatusColorHex { get; init; } = "#4ade80";
        public string StatusBgHex     { get; init; } = "#214ade80";
        public string StatusBorderHex { get; init; } = "#594ade80";
    }

    // ── One cover of the Dashboard "Recently streamed" shelf (9.0) ───────────
    public sealed class HomeShelfItem
    {
        public string  Name      { get; init; } = "";
        public string? CoverPath { get; init; }
        public string  MetaText  { get; init; } = "";
        public bool    HasNoCover => string.IsNullOrEmpty(CoverPath);
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class HomeViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _dispatcher;
        private System.Threading.Timer?  _nicSpeedTimer;

        // ── Session state ─────────────────────────────────────────────────────

        private bool _isSessionActive;
        public bool IsSessionActive
        {
            get => _isSessionActive;
            private set
            {
                if (SetProperty(ref _isSessionActive, value))
                {
                    OnPropertyChanged(nameof(ShowLastSession));
                    OnPropertyChanged(nameof(ShowEmptyState));
                }
            }
        }

        // ── Last session ──────────────────────────────────────────────────────

        private bool _hasLastSession;
        private bool HasLastSession
        {
            get => _hasLastSession;
            set
            {
                if (SetProperty(ref _hasLastSession, value))
                {
                    OnPropertyChanged(nameof(ShowLastSession));
                    OnPropertyChanged(nameof(ShowEmptyState));
                }
            }
        }

        /// <summary>True when there is a completed session to show. The Last-Session card is
        /// part of the fixed Dashboard layout and stays visible while streaming (8.0: only the
        /// top-left state box swaps between idle vitals and the live session).</summary>
        public bool ShowLastSession  => _hasLastSession;

        /// <summary>True when no session has ever been recorded.</summary>
        public bool ShowEmptyState   => !_hasLastSession;

        private string _lastSessionDuration = string.Empty;
        public string LastSessionDuration
        {
            get => _lastSessionDuration;
            private set => SetProperty(ref _lastSessionDuration, value);
        }

        private bool _lastSessionHasGrade;
        public bool LastSessionHasGrade
        {
            get => _lastSessionHasGrade;
            private set => SetProperty(ref _lastSessionHasGrade, value);
        }

        private string _lastSessionGrade = string.Empty;
        public string LastSessionGrade
        {
            get => _lastSessionGrade;
            private set => SetProperty(ref _lastSessionGrade, value);
        }

        private string _lastSessionGradeColorHex = "#808080";
        public string LastSessionGradeColorHex
        {
            get => _lastSessionGradeColorHex;
            private set => SetProperty(ref _lastSessionGradeColorHex, value);
        }

        private string _lastSessionGradeBgHex = "#1A808080";
        public string LastSessionGradeBgHex
        {
            get => _lastSessionGradeBgHex;
            private set => SetProperty(ref _lastSessionGradeBgHex, value);
        }

        private string _lastSessionGradeBorderHex = "#40808080";
        public string LastSessionGradeBorderHex
        {
            get => _lastSessionGradeBorderHex;
            private set => SetProperty(ref _lastSessionGradeBorderHex, value);
        }

        // ── Live session ──────────────────────────────────────────────────────

        private const int LiveWindowSize = 30;

        // Rolling buffers — replaced by new List on each sample to trigger x:Bind redraw.
        private readonly List<float> _rttBuffer      = new();
        private readonly List<float> _bitrateBuffer  = new();
        private readonly List<int>   _dropsBuffer    = new();
        private readonly List<float> _fpsBuffer      = new();
        private readonly List<float> _hostLatBuffer  = new();

        // Session-cumulative frame counters (for the drop-rate stat card).
        private long   _sessDrops;
        private double _sessRendered;

        private DispatcherQueueTimer? _durationTimer;

        private string _liveDuration = "0m 0s";
        public string LiveDuration
        {
            get => _liveDuration;
            private set => SetProperty(ref _liveDuration, value);
        }

        private IReadOnlyList<float> _liveRttSeries = Array.Empty<float>();
        public IReadOnlyList<float> LiveRttSeries
        {
            get => _liveRttSeries;
            private set => SetProperty(ref _liveRttSeries, value);
        }

        private IReadOnlyList<float> _liveBitrateSeries = Array.Empty<float>();
        public IReadOnlyList<float> LiveBitrateSeries
        {
            get => _liveBitrateSeries;
            private set => SetProperty(ref _liveBitrateSeries, value);
        }

        // RTT current value + adaptive color (thresholds: ≤30ms green / ≤80ms amber / >80ms red)

        private string _liveRttColorHex = "#808080";
        public string LiveRttColorHex
        {
            get => _liveRttColorHex;
            private set => SetProperty(ref _liveRttColorHex, value);
        }

        // Bitrate current value (always cyan — color is fixed in XAML)

        // ── Live cockpit (8.0 mockup): stat-card numbers/subs + extra series ─────

        private string _liveRttNumber = "—";
        public string LiveRttNumber { get => _liveRttNumber; private set => SetProperty(ref _liveRttNumber, value); }

        private string _liveRttSub = "peak — · jit —";
        public string LiveRttSub { get => _liveRttSub; private set => SetProperty(ref _liveRttSub, value); }

        private string _liveHostLatNumber = "—";
        public string LiveHostLatNumber { get => _liveHostLatNumber; private set => SetProperty(ref _liveHostLatNumber, value); }

        private string _liveBitrateNumber = "—";
        public string LiveBitrateNumber { get => _liveBitrateNumber; private set => SetProperty(ref _liveBitrateNumber, value); }

        private string _liveDropsNumber = "0.0";
        public string LiveDropsNumber { get => _liveDropsNumber; private set => SetProperty(ref _liveDropsNumber, value); }

        private string _liveDropsSub = "0 frames";
        public string LiveDropsSub { get => _liveDropsSub; private set => SetProperty(ref _liveDropsSub, value); }

        private string _liveFpsNumber = "—";
        public string LiveFpsNumber { get => _liveFpsNumber; private set => SetProperty(ref _liveFpsNumber, value); }

        // "of N Mbps target" once the client reports its ceiling (StreamLight 4.5.0+),
        // otherwise the plain "live outbound" caption used before the field existed.
        private string _liveBitrateSub = "live outbound";
        public string LiveBitrateSub { get => _liveBitrateSub; private set => SetProperty(ref _liveBitrateSub, value); }

        private IReadOnlyList<float> _liveHostLatSeries = Array.Empty<float>();
        public IReadOnlyList<float> LiveHostLatSeries
        {
            get => _liveHostLatSeries;
            private set => SetProperty(ref _liveHostLatSeries, value);
        }

        // ── Status tiles ──────────────────────────────────────────────────────

        private string _nicSpeedText = "—";
        public string NicSpeedText
        {
            get => _nicSpeedText;
            private set => SetProperty(ref _nicSpeedText, value);
        }

        // Was "Auto" (the log-triggered auto-switch, removed in 8.1.0 — the badge was left
        // reading an orphaned config key and stuck on "Off" forever). Now it reports the one
        // thing the host still decides about the link: whether clients may change it.
        private string _clientControlText = "Off";
        public string ClientControlText
        {
            get => _clientControlText;
            private set
            {
                if (SetProperty(ref _clientControlText, value))
                {
                    OnPropertyChanged(nameof(ClientControlColorHex));
                    OnPropertyChanged(nameof(ClientControlBgHex));
                    OnPropertyChanged(nameof(ClientControlBorderHex));
                }
            }
        }

        public string ClientControlColorHex  => _clientControlText == "On" ? "#4ade80"   : "#f87171";
        public string ClientControlBgHex     => _clientControlText == "On" ? "#214ade80" : "#1Aef4444";
        public string ClientControlBorderHex => _clientControlText == "On" ? "#594ade80" : "#40ef4444";

        private string _hdrText = "—";
        public string HdrText
        {
            get => _hdrText;
            private set
            {
                if (SetProperty(ref _hdrText, value))
                {
                    OnPropertyChanged(nameof(HdrColorHex));
                    OnPropertyChanged(nameof(HdrBgHex));
                    OnPropertyChanged(nameof(HdrBorderHex));
                }
            }
        }

        // "—" means the state couldn't be read, not that it is off — so it must not borrow the
        // red of a disabled feature. Grey is the only honest colour for "don't know".
        public string HdrColorHex  => _hdrText == "—" ? "#A8A49F"   : _hdrText == "On" ? "#4ade80"   : "#f87171";
        public string HdrBgHex     => _hdrText == "—" ? "#1A808080" : _hdrText == "On" ? "#214ade80" : "#1Aef4444";
        public string HdrBorderHex => _hdrText == "—" ? "#40808080" : _hdrText == "On" ? "#594ade80" : "#40ef4444";

        private bool _isSpatialAudioActivated;
        private string _spatialAudioText = "Off";
        public string SpatialAudioText
        {
            get => _spatialAudioText;
            private set
            {
                if (SetProperty(ref _spatialAudioText, value))
                {
                    if (value == "Off") _isSpatialAudioActivated = false;
                    OnPropertyChanged(nameof(SpatialAudioColorHex));
                    OnPropertyChanged(nameof(SpatialAudioBgHex));
                    OnPropertyChanged(nameof(SpatialAudioBorderHex));
                }
            }
        }

        public string SpatialAudioColorHex  => _spatialAudioText == "Off" ? "#f87171"
                                               : _isSpatialAudioActivated ? "#4ade80" : "#fbbf24";
        public string SpatialAudioBgHex     => _spatialAudioText == "Off" ? "#1Aef4444"
                                               : _isSpatialAudioActivated ? "#214ade80" : "#1Af59e0b";
        public string SpatialAudioBorderHex => _spatialAudioText == "Off" ? "#40ef4444"
                                               : _isSpatialAudioActivated ? "#594ade80" : "#40f59e0b";

        private string _gameLibraryText = "—";
        public string GameLibraryText
        {
            get => _gameLibraryText;
            private set => SetProperty(ref _gameLibraryText, value);
        }

        private string _autoHdrText = "—";
        public string AutoHdrText
        {
            get => _autoHdrText;
            private set
            {
                if (SetProperty(ref _autoHdrText, value))
                {
                }
            }
        }

        // ── Tile subtitle text ────────────────────────────────────────────────

        private string _nicAdapterName = string.Empty;
        public string NicAdapterName
        {
            get => _nicAdapterName;
            private set => SetProperty(ref _nicAdapterName, value);
        }

        private string _hdrDisplayName = string.Empty;
        public string HdrDisplayName
        {
            get => _hdrDisplayName;
            private set => SetProperty(ref _hdrDisplayName, value);
        }

        private string _spatialAudioDeviceName = string.Empty;
        public string SpatialAudioDeviceName
        {
            get => _spatialAudioDeviceName;
            private set => SetProperty(ref _spatialAudioDeviceName, value);
        }

        // ── APPS tile ─────────────────────────────────────────────────────────

        private string _managedAppsText = "—";
        public string ManagedAppsText
        {
            get => _managedAppsText;
            private set => SetProperty(ref _managedAppsText, value);
        }

        // ── NVIDIA Sentinel tile ──────────────────────────────────────────────

        private bool _isNvSentinelAvailable;
        public bool IsNvSentinelAvailable
        {
            get => _isNvSentinelAvailable;
            private set => SetProperty(ref _isNvSentinelAvailable, value);
        }

        private string _nvSentinelAutoRestoreText = "Off";
        public string NvSentinelAutoRestoreText
        {
            get => _nvSentinelAutoRestoreText;
            private set
            {
                if (SetProperty(ref _nvSentinelAutoRestoreText, value))
                {
                    OnPropertyChanged(nameof(NvSentinelAutoRestoreColorHex));
                    OnPropertyChanged(nameof(NvSentinelAutoRestoreBgHex));
                    OnPropertyChanged(nameof(NvSentinelAutoRestoreBorderHex));
                }
            }
        }

        // Three states, not two: "Stuck" is armed-but-not-working, and showing it in the red of
        // "Off" would say the opposite of what is happening — the user turned it on. Amber.
        public string NvSentinelAutoRestoreColorHex  => _nvSentinelAutoRestoreText switch
        {
            "On"    => "#4ade80",
            "Stuck" => "#fbbf24",
            _       => "#f87171",
        };
        public string NvSentinelAutoRestoreBgHex     => _nvSentinelAutoRestoreText switch
        {
            "On"    => "#214ade80",
            "Stuck" => "#1Ffbbf24",
            _       => "#1Aef4444",
        };
        public string NvSentinelAutoRestoreBorderHex => _nvSentinelAutoRestoreText switch
        {
            "On"    => "#594ade80",
            "Stuck" => "#4Dfbbf24",
            _       => "#40ef4444",
        };

        private string _nvSentinelBadgeText = "Off";
        public string NvSentinelBadgeText
        {
            get => _nvSentinelBadgeText;
            private set => SetProperty(ref _nvSentinelBadgeText, value);
        }

        // ── "This week" aggregate insight (last 7 days) ───────────────────────

        private string _thisWeekSummary = "—";
        public string ThisWeekSummary
        {
            get => _thisWeekSummary;
            private set => SetProperty(ref _thisWeekSummary, value);
        }

        // ── Host vitals (idle "HOST · LIVE" box) ──────────────────────────────
        //
        // Replaces the old "host readiness" score. Instead of grading deliberate
        // user choices (Auto HDR off is a choice, not a fault), the idle box shows
        // objective, live hardware facts sampled by HostMetricsCollector — which
        // already runs every second from app boot to serve the STATS command.
        // Values are "—" and bars 0 when a metric is unavailable (-1).

        private DispatcherQueueTimer? _vitalsTimer;

        private string _hostHealthLabel = "Checking…";
        public string HostHealthLabel { get => _hostHealthLabel; private set => SetProperty(ref _hostHealthLabel, value); }

        private string _hostHealthColorHex = "#4ade80";
        public string HostHealthColorHex { get => _hostHealthColorHex; private set => SetProperty(ref _hostHealthColorHex, value); }

        private string _hostGpuTempText = "—";
        public string HostGpuTempText { get => _hostGpuTempText; private set => SetProperty(ref _hostGpuTempText, value); }
        private string _hostGpuTempColorHex = "#4ade80";
        public string HostGpuTempColorHex { get => _hostGpuTempColorHex; private set => SetProperty(ref _hostGpuTempColorHex, value); }

        private string _hostGpuLoadText = "—";
        public string HostGpuLoadText { get => _hostGpuLoadText; private set => SetProperty(ref _hostGpuLoadText, value); }

        private string _hostVramText = "—";
        public string HostVramText { get => _hostVramText; private set => SetProperty(ref _hostVramText, value); }

        private string _hostCpuText = "—";
        public string HostCpuText { get => _hostCpuText; private set => SetProperty(ref _hostCpuText, value); }

        private string _hostNetText = "—";
        public string HostNetText { get => _hostNetText; private set => SetProperty(ref _hostNetText, value); }

        // ── Performance period (7 or 30 days), persisted ──────────────────────
        //
        // Bound TwoWay to the ComboBox in the Performance card. The setter persists the
        // choice and recomputes only the Performance aggregates. On first bind the
        // ComboBox writes back the value the ctor already read from config, so
        // SetProperty short-circuits and no redundant reload happens.

        /// <summary>Selectable windows, Last.fm style. Index maps to the ComboBox order;
        /// <c>0</c> means "all time" (no lower bound).</summary>
        private static readonly int[] PerfPeriods = { 7, 30, 90, 180, 365, 0 };

        private int _perfPeriodDays = 7;

        public int PerfPeriodIndex
        {
            get
            {
                int i = Array.IndexOf(PerfPeriods, _perfPeriodDays);
                return i >= 0 ? i : 0;
            }
            set
            {
                if (value < 0 || value >= PerfPeriods.Length) return;
                int days = PerfPeriods[value];
                if (_perfPeriodDays == days) return;
                _perfPeriodDays = days;
                ConfigService.Set("DashboardPerfPeriodDays", days);
                OnPropertyChanged();
                _ = ReloadPerformanceAsync();
            }
        }

        // ── Performance trend (Dashboard bottom-left chart) ───────────────────
        private IReadOnlyList<TrendPoint> _perfPoints = Array.Empty<TrendPoint>();
        /// <summary>One point per session with telemetry in the period, oldest first (Controls/TrendChart).</summary>
        public IReadOnlyList<TrendPoint> PerfPoints
        {
            get => _perfPoints;
            private set => SetProperty(ref _perfPoints, value);
        }

        private bool _hasWeekPerf;
        public bool HasWeekPerf { get => _hasWeekPerf; private set => SetProperty(ref _hasWeekPerf, value); }

        private string _weekPerfDrops = "—";
        public string WeekPerfDrops { get => _weekPerfDrops; private set => SetProperty(ref _weekPerfDrops, value); }

        private string _weekPerfStreamed = "—";
        public string WeekPerfStreamed { get => _weekPerfStreamed; private set => SetProperty(ref _weekPerfStreamed, value); }

        // ── Paired StreamLight clients (Dashboard bottom-right) ───────────────
        public ObservableCollection<HomeClientRow> PairedClients { get; } = new();

        private bool _hasPairedClients;
        public bool HasPairedClients { get => _hasPairedClients; private set => SetProperty(ref _hasPairedClients, value); }

        private string _pairedClientsSummary = "none yet";
        public string PairedClientsSummary { get => _pairedClientsSummary; private set => SetProperty(ref _pairedClientsSummary, value); }

        // ── Last-session headline metrics (mockup card) ───────────────────────

        private string _lastSessionRttValue = "—";
        public string LastSessionRttValue
        {
            get => _lastSessionRttValue;
            private set => SetProperty(ref _lastSessionRttValue, value);
        }

        private string _lastSessionHostLatency = "—";
        public string LastSessionHostLatency
        {
            get => _lastSessionHostLatency;
            private set => SetProperty(ref _lastSessionHostLatency, value);
        }

        // ── Stream host ───────────────────────────────────────────────────────

        private string _streamHostName = string.Empty;
        public string StreamHostName
        {
            get => _streamHostName;
            private set => SetProperty(ref _streamHostName, value);
        }

        private bool _hasStreamHost;
        public bool HasStreamHost
        {
            get => _hasStreamHost;
            private set => SetProperty(ref _hasStreamHost, value);
        }

        private BitmapImage? _streamHostIcon;
        public BitmapImage? StreamHostIcon
        {
            get => _streamHostIcon;
            private set
            {
                if (SetProperty(ref _streamHostIcon, value))
                    OnPropertyChanged(nameof(HasStreamHostIcon));
            }
        }

        public bool HasStreamHostIcon => _streamHostIcon != null;

        // ── 9.0 Dashboard additions ───────────────────────────────────────────

        // The game on screen right now (from the session's process monitor) and its art.
        private string _liveGameName = string.Empty;
        public string LiveGameName { get => _liveGameName; private set => SetProperty(ref _liveGameName, value); }

        private bool _hasLiveGame;
        public bool HasLiveGame { get => _hasLiveGame; private set => SetProperty(ref _hasLiveGame, value); }

        private string? _liveGameCoverPath;
        public string? LiveGameCoverPath { get => _liveGameCoverPath; private set => SetProperty(ref _liveGameCoverPath, value); }

        private string _heroTitle = "Ready to stream";
        public string HeroTitle { get => _heroTitle; private set => SetProperty(ref _heroTitle, value); }

        private string _heroMeta = string.Empty;
        public string HeroMeta { get => _heroMeta; private set => SetProperty(ref _heroMeta, value); }

        private string _homeSubtitle = string.Empty;
        public string HomeSubtitle { get => _homeSubtitle; private set => SetProperty(ref _homeSubtitle, value); }

        // Trends for the live tiles that had no series before.
        private IReadOnlyList<float> _liveDropsSeries = Array.Empty<float>();
        public IReadOnlyList<float> LiveDropsSeries { get => _liveDropsSeries; private set => SetProperty(ref _liveDropsSeries, value); }

        private IReadOnlyList<float> _liveFpsSeries = Array.Empty<float>();
        public IReadOnlyList<float> LiveFpsSeries { get => _liveFpsSeries; private set => SetProperty(ref _liveFpsSeries, value); }

        // Idle host vitals, last minute (one point a second) — the tiles' sparklines.
        private const int VitalsWindow = 60;
        private readonly List<float> _vGpuTemp = new(), _vGpuLoad = new(), _vVram = new(), _vCpu = new(), _vNet = new();

        private IReadOnlyList<float> _hostGpuTempSeries = Array.Empty<float>();
        public IReadOnlyList<float> HostGpuTempSeries { get => _hostGpuTempSeries; private set => SetProperty(ref _hostGpuTempSeries, value); }
        private IReadOnlyList<float> _hostGpuLoadSeries = Array.Empty<float>();
        public IReadOnlyList<float> HostGpuLoadSeries { get => _hostGpuLoadSeries; private set => SetProperty(ref _hostGpuLoadSeries, value); }
        private IReadOnlyList<float> _hostVramSeries = Array.Empty<float>();
        public IReadOnlyList<float> HostVramSeries { get => _hostVramSeries; private set => SetProperty(ref _hostVramSeries, value); }
        private IReadOnlyList<float> _hostCpuSeries = Array.Empty<float>();
        public IReadOnlyList<float> HostCpuSeries { get => _hostCpuSeries; private set => SetProperty(ref _hostCpuSeries, value); }
        private IReadOnlyList<float> _hostNetSeries = Array.Empty<float>();
        public IReadOnlyList<float> HostNetSeries { get => _hostNetSeries; private set => SetProperty(ref _hostNetSeries, value); }

        // Last session card
        private string _lastSessionId = string.Empty;
        public string LastSessionId { get => _lastSessionId; private set => SetProperty(ref _lastSessionId, value); }

        private string _lastSessionWhen = string.Empty;
        public string LastSessionWhen { get => _lastSessionWhen; private set => SetProperty(ref _lastSessionWhen, value); }

        private string _lastSessionGamesText = string.Empty;
        public string LastSessionGamesText { get => _lastSessionGamesText; private set => SetProperty(ref _lastSessionGamesText, value); }

        // Performance: grade split for the bar.
        private int _gradeExcellentCount, _gradeGoodCount, _gradePoorCount;
        public int GradeExcellentCount { get => _gradeExcellentCount; private set => SetProperty(ref _gradeExcellentCount, value); }
        public int GradeGoodCount      { get => _gradeGoodCount;      private set => SetProperty(ref _gradeGoodCount, value); }
        public int GradePoorCount      { get => _gradePoorCount;      private set => SetProperty(ref _gradePoorCount, value); }

        // The recently-streamed shelf.

        public ObservableCollection<HomeShelfItem> RecentGames { get; } = new();
        private bool _hasRecentGames;
        public bool HasRecentGames { get => _hasRecentGames; private set => SetProperty(ref _hasRecentGames, value); }

        // Host-setup tile captions
        private string _managedAppsSubText = "None set";
        public string ManagedAppsSubText { get => _managedAppsSubText; private set => SetProperty(ref _managedAppsSubText, value); }

        private string _libraryTileSub = "Never synced";
        public string LibraryTileSub { get => _libraryTileSub; private set => SetProperty(ref _libraryTileSub, value); }

        // ── Constructor ───────────────────────────────────────────────────────

        public HomeViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            // Restore the Performance period before the view binds, so the ComboBox's
            // first write-back matches and doesn't trigger a redundant recompute.
            // An unrecognised stored value (e.g. from an older build) falls back to 7 days.
            int savedPeriod = ConfigService.GetInt("DashboardPerfPeriodDays", 7);
            _perfPeriodDays = Array.IndexOf(PerfPeriods, savedPeriod) >= 0 ? savedPeriod : 7;

            IsSessionActive = AppStateService.Instance.IsSessionActive;
            AppStateService.Instance.SessionStateChanged       += OnSessionStateChanged;
            AppStateService.Instance.SpatialAudioStatusChanged += OnSpatialAudioStatusChanged;
            AppStateService.Instance.LiveTelemetrySample       += OnLiveSample;

            var sentinel = AppStateService.Instance.NvidiaSentinel;
            if (sentinel != null)
            {
                sentinel.AutoRestorePerformed    += OnNvAutoRestorePerformed;
                sentinel.AutoRestoreStateChanged += OnNvAutoRestorePerformed;
            }

            // Keep the Paired clients list live: approving or revoking a client on the
            // Clients page otherwise left the Dashboard showing the old list until the
            // next full status reload.
            var bridgeAuth = AppStateService.Instance.BridgeAuth;
            if (bridgeAuth != null)
                bridgeAuth.ClientsChanged += OnBridgeClientsChanged;

            if (IsSessionActive)
                StartLiveSession();

            // Populate initial status if Dolby is already running
            string initial = AppStateService.Instance.CurrentSpatialAudioStatus;
            if (!string.IsNullOrEmpty(initial))
                OnSpatialAudioStatusChanged(initial);

            // These two poll once or twice a second, so they run only while the window is
            // actually on screen. Minimising hides the window instead of navigating away,
            // so without this they kept polling for a dashboard nobody could see — most of
            // the idle CPU cost in issue #7.
            AppStateService.Instance.MainWindowVisibilityChanged += OnMainWindowVisibilityChanged;
            if (AppStateService.Instance.IsMainWindowVisible)
                StartPollingTimers();
        }

        private void OnMainWindowVisibilityChanged(object? sender, bool visible)
            => _dispatcher.TryEnqueue(() =>
            {
                if (visible) StartPollingTimers();
                else         StopPollingTimers();
            });

        private void StartPollingTimers()
        {
            // Poll NIC link speed every 2 s so the Home tile stays current in real time.
            _nicSpeedTimer ??= new System.Threading.Timer(_ => RefreshNicSpeed(),
                state: null, dueTime: 0, period: 2000);

            // Idle host vitals — 1 s tick, same cadence as the collector itself.
            // Reads an already-sampled snapshot, so this is a cheap struct copy.
            if (_vitalsTimer == null)
            {
                _vitalsTimer = _dispatcher.CreateTimer();
                _vitalsTimer.Interval    = TimeSpan.FromSeconds(1);
                _vitalsTimer.IsRepeating = true;
                _vitalsTimer.Tick       += (_, _) => RefreshHostVitals();
            }
            _vitalsTimer.Start();
            RefreshHostVitals();
        }

        private void StopPollingTimers()
        {
            _nicSpeedTimer?.Dispose();
            _nicSpeedTimer = null;
            _vitalsTimer?.Stop();
        }

        public void Unsubscribe()
        {
            StopPollingTimers();
            _vitalsTimer = null;
            AppStateService.Instance.MainWindowVisibilityChanged -= OnMainWindowVisibilityChanged;
            StopLiveSession();
            AppStateService.Instance.SessionStateChanged       -= OnSessionStateChanged;
            AppStateService.Instance.SpatialAudioStatusChanged -= OnSpatialAudioStatusChanged;
            AppStateService.Instance.LiveTelemetrySample       -= OnLiveSample;

            var sentinel = AppStateService.Instance.NvidiaSentinel;
            if (sentinel != null)
            {
                sentinel.AutoRestorePerformed    -= OnNvAutoRestorePerformed;
                sentinel.AutoRestoreStateChanged -= OnNvAutoRestorePerformed;
            }

            var bridgeAuth = AppStateService.Instance.BridgeAuth;
            if (bridgeAuth != null)
                bridgeAuth.ClientsChanged -= OnBridgeClientsChanged;
        }

        private void OnNvAutoRestorePerformed(object? sender, EventArgs e)
            => _dispatcher.TryEnqueue(RefreshNvSentinelTile);

        // Fired from the bridge's connection thread when a client is enrolled/approved/revoked.
        private void OnBridgeClientsChanged()
            => _dispatcher.TryEnqueue(RefreshPairedClients);

        private void RefreshNicSpeed()
        {
            try
            {
                string adapterName = ConfigService.Get("NetworkAdapterName", "Ethernet");
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
                string text = ni?.OperationalStatus == OperationalStatus.Up
                    ? (ni.Speed / 1_000_000) is long mbps && mbps > 0
                        ? mbps >= 1000 ? string.Create(CultureInfo.InvariantCulture, $"{mbps / 1000.0:0.#} Gbps") : string.Create(CultureInfo.InvariantCulture, $"{mbps} Mbps")
                        : "Negotiating…"
                    : "—";
                _dispatcher.TryEnqueue(() => NicSpeedText = text);
            }
            catch { }
        }

        private void OnSessionStateChanged(object? sender, bool active)
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsSessionActive = active;
                if (active)
                    StartLiveSession();
                else
                {
                    StopLiveSession();
                    _ = LoadStatusAsync();
                }
                RefreshHeroText();
            });
        }

        private void OnSpatialAudioStatusChanged(string status)
        {
            // Drive badge color: green when the format has been activated this session,
            // amber when configured but not yet active, red when Off (handled in setter).
            bool activated = status.StartsWith("✓");
            bool deactivated = status.Contains("waiting", StringComparison.OrdinalIgnoreCase)
                            || status.Contains("Ready",   StringComparison.OrdinalIgnoreCase)
                            || status == "Disabled.";

            if (!activated && !deactivated) return; // e.g. "Activating…" — keep current state

            _dispatcher.TryEnqueue(() =>
            {
                _isSpatialAudioActivated = activated;
                OnPropertyChanged(nameof(SpatialAudioColorHex));
                OnPropertyChanged(nameof(SpatialAudioBgHex));
                OnPropertyChanged(nameof(SpatialAudioBorderHex));
            });
        }

        // ── Public API ────────────────────────────────────────────────────────

        public async Task LoadStatusAsync()
        {
            string? streamHostExePath = null;

            // I/O-bound reads run off the UI thread; results marshalled back via dispatcher
            await Task.Run(() =>
            {
                // Last completed session
                try
                {
                    var sessions = SessionLogger.Load();

                    // Completed sessions feed the performance card and the recents
                    var completed = sessions.Where(s => s.EndTime != null).ToList();

                    // Performance aggregate over the user-selected window (7 or 30 days).
                    ComputePerformance(completed, _perfPeriodDays);
                    BuildRecents(completed);
                    // Sessions are stored newest-first (Insert(0) in StartSession).
                    var last = sessions.FirstOrDefault(s => s.EndTime != null);
                    if (last != null)
                    {
                        // Headline metric values for the mockup Last-Session card
                        var qs = last.QualityStats;
                        string lsRtt = qs != null ? $"{(int)qs.RttAvgMs}" : "—";
                        string lsHost = qs != null && qs.HostLatencyAvgMs >= 0 ? string.Create(CultureInfo.InvariantCulture, $"{qs.HostLatencyAvgMs:0.#}") : "—";
                        string lsId    = last.Id;
                        string lsWhen  = $"{GameStatsService.RelativeDay(last.StartTime)} · {last.StartTime:HH:mm} → {last.EndTime:HH:mm}";
                        string lsGames = last.GamesDetected is { Count: > 0 } g
                            ? string.Join(", ", g)
                            : last.GamesDetected != null ? "No game detected" : string.Empty;
                        _dispatcher.TryEnqueue(() =>
                        {
                            LastSessionId          = lsId;
                            LastSessionWhen        = lsWhen;
                            LastSessionGamesText   = lsGames;
                            LastSessionRttValue     = lsRtt;
                            LastSessionHostLatency  = lsHost;
                        });

                        _dispatcher.TryEnqueue(() =>
                        {
                            HasLastSession        = true;
                            LastSessionDuration   = last.DurationDisplay;
                            LastSessionHasGrade   = last.HasGrade;
                            LastSessionGrade      = last.GradeShortLabel;
                            LastSessionGradeColorHex  = last.GradeColorHex;
                            LastSessionGradeBgHex     = last.GradeBgHex;
                            LastSessionGradeBorderHex = last.GradeBorderHex;
                        });
                    }
                }
                catch { }

                // APPS tile — count managed apps from managedapps.json
                try
                {
                    string appsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "StreamTweak", "managedapps.json");
                    int count = 0;
                    if (File.Exists(appsPath))
                    {
                        string json = File.ReadAllText(appsPath);
                        var apps = JsonSerializer.Deserialize<List<ManagedApp>>(json);
                        count = apps?.Count ?? 0;
                    }
                    _dispatcher.TryEnqueue(() =>
                    {
                        ManagedAppsText    = count > 0 ? count.ToString() : "—";
                        ManagedAppsSubText = count switch
                        {
                            0 => "None set",
                            1 => "1 app closes when a stream starts",
                            _ => $"{count} apps close when a stream starts",
                        };
                    });
                }
                catch { _dispatcher.TryEnqueue(() => ManagedAppsText = "—"); }

                // Streaming server
                try
                {
                    var info = LogParser.FindStreamingAppInfo();
                    if (info != null)
                    {
                        streamHostExePath = info.ExePath;
                        _dispatcher.TryEnqueue(() =>
                        {
                            StreamHostName   = info.AppName;
                            HasStreamHost    = true;
                        });
                    }
                }
                catch { }
            });

            // Load streaming server EXE icon (WinRT async, must run after Task.Run)
            if (streamHostExePath != null)
            {
                var icon = await LoadExeIconAsync(streamHostExePath);
                if (icon != null) StreamHostIcon = icon;
            }

            // Config reads are fast — do on UI thread
            // Read through the manager, not the raw key: it owns the default (on) and is the
            // same value the Network page and the bridge act on.
            ClientControlText = (AppStateService.Instance.LinkSpeed?.AllowClientControl ?? false) ? "On" : "Off";

            NicAdapterName = ConfigService.Get("NetworkAdapterName", "Ethernet");

            bool audioEnabled = ConfigService.GetBool("AudioMonitorEnabled", false);
            SpatialAudioText = audioEnabled
                ? (ConfigService.Get("AudioSpatialFormat", "DolbyAtmos") == "WindowsSonic"
                    ? "Windows Sonic"
                    : "Dolby Atmos")
                : "Off";

            SpatialAudioDeviceName = AppStateService.Instance.CurrentAudioDeviceName;

            try
            {
                var state = GameLibraryState.Current;
                int count = state.Games?.Count ?? 0;
                GameLibraryText = count > 0 ? count.ToString() : "—";

                if (state.LastSyncUtc != null)
                {
                    var local = state.LastSyncUtc.Value.ToLocalTime();
                    LibraryTileSub = $"Synced {GameStatsService.RelativeDay(local).ToLowerInvariant()} at {local:HH:mm}";
                }
                else LibraryTileSub = state.SyncEnabled ? "Not synced yet" : "Sync is off";
            }
            catch { GameLibraryText = "—"; }

            // HDR state (async DisplayConfig query)
            try
            {
                var monitors = await HdrService.GetMonitorsAsync();

                // No monitors means the query couldn't be completed — the display topology was
                // changing under it, which is routine while a session starts or ends. Say so
                // instead of claiming Off: a wrong badge is worse than an honest dash.
                if (monitors.Count == 0)
                {
                    // ⚠️ Keep the last figure we actually read rather than falling back to a
                    // dash. On a host with a virtual display the topology is unreadable for
                    // stretches at a time — the retries inside HdrService can still come back
                    // empty — and a dash every time the Dashboard is opened is no more honest
                    // than a stale value: HDR does not turn itself off while nobody is looking.
                    // The dash is for the case where we have never managed to read it at all.
                    if (_hdrText != "On" && _hdrText != "Off") HdrText = "—";
                }
                else
                {
                    HdrText = monitors.Any(m => m.HdrEnabled && m.HdrSupported) ? "On" : "Off";
                    HdrDisplayName = monitors.FirstOrDefault(m => m.HdrSupported)?.FriendlyName
                                     ?? monitors.FirstOrDefault()?.FriendlyName
                                     ?? "Primary display";
                }
            }
            catch { HdrText = "—"; HdrDisplayName = "Primary display"; }

            try
            {
                AutoHdrText = await HdrService.GetAutoHdrAsync() ? "On" : "Off";
            }
            catch { AutoHdrText = "—"; }

            RefreshNvSentinelTile();
            RefreshPairedClients();
            RefreshHeroText();
        }

        /// <summary>
        /// The "Recently streamed" shelf. Runs on the background
        /// thread of <see cref="LoadStatusAsync"/>; only the collections are touched on the UI thread.
        /// </summary>
        private void BuildRecents(List<SessionEntry> completed)
        {
            var stats = GameStatsService.Compute(completed);
            var library = GameLibraryState.Current.Games
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var shelf = stats.Values
                .Where(g => g.LastPlayed != null)
                .OrderByDescending(g => g.LastPlayed)
                .Take(16)
                .Select(g =>
                {
                    string? cover = null;
                    if (library.TryGetValue(g.Name, out var entry) && File.Exists(entry.CoverImagePath)) cover = entry.CoverImagePath;
                    else
                    {
                        // A game no longer in the library still has the cover snapshotted with its session.
                        var snap = g.RecentSessions.Select(s => s.GamesDetectedCoverPaths)
                            .FirstOrDefault(m => m != null && m.ContainsKey(g.Name));
                        if (snap != null && File.Exists(snap[g.Name])) cover = snap[g.Name];
                    }
                    return new HomeShelfItem
                    {
                        Name      = g.Name,
                        CoverPath = cover,
                        MetaText  = $"{GameStatsService.RelativeDay(g.LastPlayed!.Value)} · {GameStatsService.FormatMinutes(g.Minutes)}",
                    };
                }).ToList();

            _dispatcher.TryEnqueue(() =>
            {
                RecentGames.Clear();
                foreach (var g in shelf) RecentGames.Add(g);
                HasRecentGames = RecentGames.Count > 0;
            });
        }

        /// <summary>The hero's title and caption and the page subtitle, for the current state.</summary>
        private void RefreshHeroText()
        {
            if (_isSessionActive)
            {
                HeroTitle = HasLiveGame ? LiveGameName : "Streaming";
                string server = HasStreamHost ? $" · {StreamHostName}" : string.Empty;
                HeroMeta  = $"{LiveDuration}{server}";
                HomeSubtitle = HasLiveGame
                    ? $"Streaming {LiveGameName} for {LiveDuration}"
                    : $"A stream has been running for {LiveDuration}";
            }
            else
            {
                HeroTitle = "Ready to stream";
                HeroMeta  = HasStreamHost ? $"{StreamHostName} is running · waiting for a client" : "Waiting for a client";
                int n = PairedClients.Count(c => c.StatusText != "Waiting");
                HomeSubtitle = n switch
                {
                    0 => "Nothing is streaming · no StreamLight device is approved yet",
                    1 => "Nothing is streaming · 1 approved StreamLight device",
                    _ => $"Nothing is streaming · {n} approved StreamLight devices",
                };
            }
        }

        /// <summary>
        /// Which game is on screen now, from the session's process monitor. Checked every
        /// second while streaming; the cover path is looked up only when the game changes.
        /// </summary>
        private void RefreshLiveGame()
        {
            string? name = SessionLogger.CurrentGameName(TimeSpan.FromSeconds(20));
            if (string.Equals(name ?? string.Empty, _liveGameName, StringComparison.Ordinal)) return;

            LiveGameName = name ?? string.Empty;
            HasLiveGame  = !string.IsNullOrEmpty(name);
            string? cover = null;
            if (HasLiveGame)
            {
                try
                {
                    var entry = GameLibraryState.Current.Games.FirstOrDefault(g =>
                        string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (entry?.CoverImagePath != null && File.Exists(entry.CoverImagePath))
                        cover = entry.CoverImagePath;
                }
                catch { }
            }
            LiveGameCoverPath = cover;
            RefreshHeroText();
        }

        /// <summary>
        /// Computes every Performance-card aggregate over the last <paramref name="days"/> days:
        /// session count, average grade + breakdown, average RTT/drops, streamed time, the grade
        /// bars and the RTT/host-latency trend lines. Runs on a background thread and marshals
        /// each group of properties to the UI thread.
        /// </summary>
        private void ComputePerformance(List<SessionEntry> completed, int days)
        {
            // days <= 0 means "all time" — no lower bound.
            var inPeriod = days <= 0
                ? completed
                : completed.Where(s => s.StartTime >= DateTime.Now.AddDays(-days)).ToList();
            int wkCount  = inPeriod.Count;

            var wkGraded = inPeriod
                .Where(s => s.Grade is QualityGrade.High or QualityGrade.Medium or QualityGrade.Low)
                .ToList();
            var wkStats = inPeriod.Where(s => s.QualityStats != null).ToList();
            string wkSummary = wkCount == 1 ? "1 session" : $"{wkCount} sessions";

            // Grade breakdown counts
            int hi = wkGraded.Count(s => s.Grade == QualityGrade.High);
            int md = wkGraded.Count(s => s.Grade == QualityGrade.Medium);
            int lo = wkGraded.Count(s => s.Grade == QualityGrade.Low);

            // Performance trend — one point per graded session (oldest → newest).
            // RTT is always present; host frame latency only for sessions recorded with
            // StreamLight ≥ 4.0.1, so that line is added only when there is real data.
            var ordered  = wkStats.OrderBy(s => s.StartTime).ToList();
            var perfPoints = ordered.Select(s => new TrendPoint(
                s.StartTime, s.Id,
                s.GamesDetected is { Count: > 0 } g ? string.Join(", ", g) : string.Empty,
                s.QualityStats!.RttAvgMs,
                s.QualityStats.HostLatencyAvgMs >= 0 ? s.QualityStats.HostLatencyAvgMs : -1)).ToList();
            bool   hasPerf   = ordered.Count >= 2;
            string perfDrops = wkStats.Count > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{wkStats.Average(s => s.QualityStats!.DropRatePct):0.#}%")
                : "—";
            var    wkDur     = TimeSpan.FromSeconds(
                inPeriod.Where(s => s.EndTime != null)
                        .Sum(s => (s.EndTime!.Value - s.StartTime).TotalSeconds));
            string perfStreamed = wkCount > 0 ? FormatTotalDuration(wkDur) : "—";

            _dispatcher.TryEnqueue(() =>
            {
                // Discard a result that the user has already scrolled past: LoadStatusAsync and
                // ReloadPerformanceAsync can be in flight at once, and without this the slower
                // one can land last and repaint the card with the previous period's data.
                if (days != _perfPeriodDays) return;

                GradeExcellentCount = hi;
                GradeGoodCount      = md;
                GradePoorCount      = lo;
                PerfPoints         = perfPoints;
                HasWeekPerf        = hasPerf;
                WeekPerfDrops      = perfDrops;
                WeekPerfStreamed   = perfStreamed;
            });

            _dispatcher.TryEnqueue(() =>
            {
                if (days != _perfPeriodDays) return;   // superseded — see above
                ThisWeekSummary = wkSummary;
            });
        }

        /// <summary>
        /// Re-runs only the Performance aggregates after the user switches the period,
        /// instead of the full LoadStatusAsync (which also re-reads HDR, covers, icons…).
        /// </summary>
        private async Task ReloadPerformanceAsync()
        {
            int days = _perfPeriodDays;
            await Task.Run(() =>
            {
                try
                {
                    var completed = SessionLogger.Load().Where(s => s.EndTime != null).ToList();
                    ComputePerformance(completed, days);
                }
                catch (Exception ex) { DebugLogger.Log($"ReloadPerformanceAsync failed: {ex}"); }
            });
        }

        /// <summary>
        /// Rebuilds the "Paired clients" list from the bridge client store. Only approved
        /// clients are listed — pending/denied enrollments belong to the Clients page, which
        /// is where the user acts on them.
        /// </summary>
        private void RefreshPairedClients()
        {
            PairedClients.Clear();
            try
            {
                var clients = AppStateService.Instance.BridgeAuth?.GetClients();
                if (clients != null)
                {
                    // Devices waiting for approval first: they are the ones that need a click.
                    foreach (var c in clients.Where(c => c.Status == "pending"))
                    {
                        PairedClients.Add(new HomeClientRow
                        {
                            Name            = string.IsNullOrWhiteSpace(c.Name) ? "StreamLight client" : c.Name,
                            Glyph           = DeviceKinds.Glyph(c.DeviceKind),
                            LastSeenText    = string.IsNullOrEmpty(c.Pin) ? "Asks for access" : $"Asks for access · PIN {c.Pin}",
                            StatusText      = "Waiting",
                            StatusColorHex  = "#fbbf24",
                            StatusBgHex     = "#21fbbf24",
                            StatusBorderHex = "#59fbbf24",
                        });
                    }
                    foreach (var c in clients.Where(c => c.Status == "approved"))
                    {
                        string seen = "never connected";
                        if (!string.IsNullOrEmpty(c.LastSeenUtc) &&
                            DateTime.TryParse(c.LastSeenUtc, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var last))
                        {
                            seen = $"Last connected {FormatAgo(DateTime.Now - last.ToLocalTime())}";
                        }

                        PairedClients.Add(new HomeClientRow
                        {
                            Name         = string.IsNullOrWhiteSpace(c.Name) ? "StreamLight client" : c.Name,
                            Glyph        = DeviceKinds.Glyph(c.DeviceKind),
                            LastSeenText = seen,
                        });
                    }
                }
            }
            catch { /* non-fatal — the card simply shows the empty state */ }

            HasPairedClients     = PairedClients.Count > 0;
            int approved = PairedClients.Count(c => c.StatusText != "Waiting");
            int waiting  = PairedClients.Count - approved;
            PairedClientsSummary = (approved switch
            {
                0 => "none approved yet",
                1 => "1 approved",
                _ => $"{approved} approved",
            }) + (waiting > 0 ? $" · {waiting} waiting" : string.Empty);
            RefreshHeroText();
        }

        private void RefreshNvSentinelTile()
        {
            var svc = AppStateService.Instance.NvidiaSentinel;
            IsNvSentinelAvailable = svc?.IsNvidiaAvailable == true;

            if (svc == null || !svc.IsNvidiaAvailable)
            {
                NvSentinelAutoRestoreText  = "Off";
                NvSentinelBadgeText        = "Off";
                return;
            }

            NvSentinelAutoRestoreText = !svc.AutoRestoreEnabled ? "Off"
                                      : svc.IsStuck            ? "Stuck"
                                      :                          "On";

            // The snapshot size is reported on its own neutral badge now, next to the
            // auto-restore state — so it's shown whether auto-restore is armed or not
            // (a saved profile exists either way; only its re-application is toggled).
            int n = 0;
            try
            {
                string? path = svc.SnapshotPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    n = NvidiaSentinelService.ReadSnapshot(path)?.Count ?? 0;
            }
            catch { n = 0; }
            NvSentinelBadgeText = n == 1 ? "1 saved" : $"{n} saved";

        }

        public void RequestStopStream()
            => AppStateService.Instance.RequestStopStreamAction?.Invoke();

        private static string FormatTotalDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 60)  return $"{(int)t.TotalSeconds}s";
            if (t.TotalHours   < 1)   return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}m {t.Seconds:00}s");
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes:00}m");
        }

        private static string FormatAgo(TimeSpan t)
        {
            if (t.TotalMinutes < 1)  return "just now";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m ago";
            if (t.TotalHours   < 24) return $"{(int)t.TotalHours}h ago";
            return $"{(int)t.TotalDays}d ago";
        }

        /// <summary>
        /// Refreshes the idle "HOST · LIVE" vitals from the metrics collector snapshot.
        /// Runs on the UI thread (DispatcherQueueTimer). Every field degrades to "—" with
        /// an empty bar when the metric is unavailable (-1 on non-NVIDIA / pre-WDDM hosts).
        /// </summary>
        private void RefreshHostVitals()
        {
            // While streaming, the state box shows session metrics instead of these vitals,
            // so refreshing them would only fire binding notifications nobody can see.
            if (_isSessionActive) return;

            var provider = AppStateService.Instance.HostMetricsProvider;
            if (provider == null) return;

            HostMetricsSample s;
            try { s = provider(); }
            catch { return; }

            // GPU temperature — the one vital with a real "too hot" threshold.
            if (s.GpuTemp >= 0)
            {
                HostGpuTempText     = s.GpuTemp.ToString();
                HostGpuTempColorHex = s.GpuTemp >= 85 ? "#f87171" : s.GpuTemp >= 75 ? "#fbbf24" : "#4ade80";
            }
            else { HostGpuTempText = "—"; HostGpuTempColorHex = "#4ade80"; }

            // GPU 3D load
            if (s.Gpu >= 0)
            {
                HostGpuLoadText     = s.Gpu.ToString();
            }
            else { HostGpuLoadText = "—"; }

            // VRAM — "used / total GB" when the total is known, else just used.
            // Unit is rendered separately in the tile (like every other vital), so it
            // stays out of the 21px value text — baking " GB" in here made this the
            // widest tile and it overflowed into the CPU column at small window sizes.
            if (s.VramUsedMb >= 0 && s.VramTotalMb > 0)
            {
                HostVramText = string.Create(CultureInfo.InvariantCulture, $"{s.VramUsedMb / 1024.0:0.0}/{s.VramTotalMb / 1024.0:0}");
            }
            else if (s.VramUsedMb >= 0)
            {
                HostVramText = string.Create(CultureInfo.InvariantCulture, $"{s.VramUsedMb / 1024.0:0.0}");
            }
            else { HostVramText = "—"; }

            // CPU
            if (s.Cpu >= 0)
            {
                HostCpuText     = s.Cpu.ToString();
            }
            else { HostCpuText = "—"; }

            // Network TX on the default-route interface. Bar is scaled against
            // 100 Mbps — enough headroom to read an idle host at a glance.
            if (s.NetTxMbps >= 0)
            {
                HostNetText = s.NetTxMbps.ToString();
            }
            else { HostNetText = "—"; }

            // One-minute trends for the tiles' sparklines.
            if (s.GpuTemp >= 0) Push(_vGpuTemp, (float)s.GpuTemp, VitalsWindow);
            if (s.Gpu >= 0)     Push(_vGpuLoad, (float)s.Gpu, VitalsWindow);
            if (s.VramUsedMb >= 0) Push(_vVram, (float)(s.VramUsedMb / 1024.0), VitalsWindow);
            if (s.Cpu >= 0)     Push(_vCpu, (float)s.Cpu, VitalsWindow);
            if (s.NetTxMbps >= 0) Push(_vNet, (float)s.NetTxMbps, VitalsWindow);
            HostGpuTempSeries = _vGpuTemp.ToList();
            HostGpuLoadSeries = _vGpuLoad.ToList();
            HostVramSeries    = _vVram.ToList();
            HostCpuSeries     = _vCpu.ToList();
            HostNetSeries     = _vNet.ToList();

            // Health headline — derived from measured facts only, never from
            // which optional automations the user chose to leave off.
            bool anyMetric = s.GpuTemp >= 0 || s.Gpu >= 0 || s.Cpu >= 0;
            if (!anyMetric)
            {
                HostHealthLabel    = "Host ready";
                HostHealthColorHex = "#4ade80";
            }
            else if (s.GpuTemp >= 85)
            {
                HostHealthLabel    = "GPU running hot";
                HostHealthColorHex = "#f87171";
            }
            else if (s.Gpu >= 80 || s.Cpu >= 80)
            {
                HostHealthLabel    = "Host under load";
                HostHealthColorHex = "#fbbf24";
            }
            else
            {
                HostHealthLabel    = "Idle";
                HostHealthColorHex = "#4ade80";
            }

        }

        // ── Live session helpers ──────────────────────────────────────────────

        private void StartLiveSession()
        {
            // Must run on UI thread (DispatcherQueueTimer requires it).
            _rttBuffer.Clear();
            _bitrateBuffer.Clear();
            _dropsBuffer.Clear();
            _fpsBuffer.Clear();
            _hostLatBuffer.Clear();
            _sessDrops    = 0;
            _sessRendered = 0;
            LiveRttSeries      = Array.Empty<float>();
            LiveBitrateSeries  = Array.Empty<float>();
            LiveHostLatSeries  = Array.Empty<float>();
            LiveDropsSeries    = Array.Empty<float>();
            LiveFpsSeries      = Array.Empty<float>();
            LiveRttNumber      = "—";
            LiveRttSub         = "peak — · jit —";
            LiveHostLatNumber  = "—";
            LiveBitrateNumber  = "—";
            LiveDropsNumber    = "0.0";
            LiveDropsSub       = "0 frames";
            // Reset these too, or the first seconds of a new session still show the
            // previous one's frame rate and bitrate target until the first sample lands.
            LiveFpsNumber      = "—";
            LiveBitrateSub     = "live outbound";
            LiveRttColorHex    = "#808080";

            var startTime     = SessionLogger.ActiveSessionStartTime;
            LiveDuration      = FormatDuration(startTime);

            if (_durationTimer == null)
            {
                _durationTimer = _dispatcher.CreateTimer();
                _durationTimer.Interval    = TimeSpan.FromSeconds(1);
                _durationTimer.IsRepeating = true;
                _durationTimer.Tick += (_, _) =>
                {
                    var t = SessionLogger.ActiveSessionStartTime;
                    if (t != default) LiveDuration = FormatDuration(t);
                    RefreshLiveGame();
                    RefreshHeroText();
                };
            }
            _durationTimer.Start();
            RefreshLiveGame();
            RefreshHeroText();
        }

        private void StopLiveSession()
        {
            _durationTimer?.Stop();
            LiveGameName = string.Empty;
            HasLiveGame = false;
            LiveGameCoverPath = null;
            _rttBuffer.Clear();
            _bitrateBuffer.Clear();
            _dropsBuffer.Clear();
            _fpsBuffer.Clear();
            _hostLatBuffer.Clear();
        }

        // GPU / Encoder / CPU line colours (match the compute-chart legend).

        private void OnLiveSample(AppStateService.LiveSample s)
        {
            // Fired on a background thread — marshal to UI thread for property updates.
            _dispatcher.TryEnqueue(() =>
            {
                Push(_rttBuffer,     s.RttMs);
                Push(_bitrateBuffer, s.BitrateMbps);
                Push(_dropsBuffer,   s.Drops);
                Push(_fpsBuffer,     s.FpsAvg);
                if (s.HostLatencyMs > 0f) Push(_hostLatBuffer, s.HostLatencyMs);

                // Replace list references so x:Bind on MiniSparkline.Data fires Redraw.
                LiveRttSeries     = _rttBuffer.ToList();
                LiveBitrateSeries = _bitrateBuffer.ToList();
                LiveHostLatSeries = _hostLatBuffer.ToList();
                LiveDropsSeries   = _dropsBuffer.Select(d => (float)d).ToList();
                LiveFpsSeries     = _fpsBuffer.ToList();

                // Session-cumulative drop rate + frame count (stat card).
                _sessDrops    += Math.Max(0, s.Drops);
                _sessRendered += Math.Max(0f, s.FpsAvg);
                double totalFrames = _sessDrops + _sessRendered;
                float dropPct = totalFrames > 0 ? (float)(_sessDrops / totalFrames * 100.0) : 0f;
                LiveDropsNumber = string.Create(CultureInfo.InvariantCulture, $"{dropPct:0.0}");
                LiveDropsSub    = $"{_sessDrops} of {FormatFrameCount(totalFrames)} frames";

                // RTT value + adaptive color (≤30 ms green, ≤80 ms amber, >80 ms red)
                LiveRttNumber = s.RttMs < 10f ? string.Create(CultureInfo.InvariantCulture, $"{s.RttMs:0.0}") : string.Create(CultureInfo.InvariantCulture, $"{(int)s.RttMs}");
                float rttPeak = _rttBuffer.Count > 0 ? _rttBuffer.Max() : s.RttMs;
                LiveRttSub    = string.Create(CultureInfo.InvariantCulture, $"peak {(int)rttPeak} · jit {s.JitterMs:0.0}");
                if (s.RttMs <= 30f)
                {
                    LiveRttColorHex  = "#4ade80";
                }
                else if (s.RttMs <= 80f)
                {
                    LiveRttColorHex  = "#fbbf24";
                }
                else
                {
                    LiveRttColorHex  = "#f87171";
                }

                // Bitrate value
                LiveBitrateNumber = s.BitrateMbps >= 100f ? string.Create(CultureInfo.InvariantCulture, $"{s.BitrateMbps:0}")      : string.Create(CultureInfo.InvariantCulture, $"{s.BitrateMbps:0.0}");

                // Host frame latency (capture + encode); "—" until StreamLight reports it.
                LiveHostLatNumber = _hostLatBuffer.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{_hostLatBuffer[^1]:0.0}") : "—";

                // Frame rate (live state box).
                LiveFpsNumber = s.FpsAvg > 0f ? string.Create(CultureInfo.InvariantCulture, $"{s.FpsAvg:0}") : "—";

                // Delivered vs configured ceiling — the comparison is the whole point,
                // and only the host can make it (the client sets the target, the host
                // sees what actually goes out).
                float target = AppStateService.Instance.CurrentTargetBitrateMbps;
                LiveBitrateSub = target > 0f ? string.Create(CultureInfo.InvariantCulture, $"of {target:0.#} Mbps target") : "live outbound";
            });
        }

        private static string FormatFrameCount(double frames)
            => frames >= 1000 ? string.Create(CultureInfo.InvariantCulture, $"{frames / 1000.0:0.#}k") : string.Create(CultureInfo.InvariantCulture, $"{(int)frames}");

        private static void Push<T>(List<T> buffer, T value) => Push(buffer, value, LiveWindowSize);

        private static void Push<T>(List<T> buffer, T value, int window)
        {
            buffer.Add(value);
            if (buffer.Count > window)
                buffer.RemoveAt(0);
        }

        private static string FormatDuration(DateTime startTime)
        {
            var d = DateTime.Now - startTime;
            return d.TotalMinutes >= 1
                ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
                : $"{d.Seconds}s";
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static async Task<BitmapImage?> LoadExeIconAsync(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
                if (thumbnail == null) return null;
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumbnail);
                return bmp;
            }
            catch { return null; }
        }
    }
}
