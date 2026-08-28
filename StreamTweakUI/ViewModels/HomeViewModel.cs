using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
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
    // ── Per-game cover shown in the Last Session strip ────────────────────────

    public sealed class SessionGameCover : ViewModelBase
    {
        public string GameName { get; }

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

        public bool HasNoCover => _coverImage == null;

        public SessionGameCover(string gameName) => GameName = gameName;
    }

    // ── One approved StreamLight client (Dashboard "Paired clients" list) ─────
    public sealed class HomeClientRow
    {
        public string Name         { get; init; } = "";
        public string LastSeenText { get; init; } = "";
        public string StatusText   { get; init; } = "Approved";
        public string StatusColorHex { get; init; } = "#4ade80";
        public string StatusBgHex     { get; init; } = "#1A4ade80";
        public string StatusBorderHex { get; init; } = "#404ade80";
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class HomeViewModel : ViewModelBase
    {
        private readonly DispatcherQueue _dispatcher;
        private System.Threading.Timer?  _nicSpeedTimer;

        // ── Version info ──────────────────────────────────────────────────────

        private string _versionText = string.Empty;
        public string VersionText
        {
            get => _versionText;
            private set => SetProperty(ref _versionText, value);
        }

        private string _buildDateText = string.Empty;
        public string BuildDateText
        {
            get => _buildDateText;
            private set => SetProperty(ref _buildDateText, value);
        }

        // Update-check infrastructure has moved to AppStateService (centralized,
        // fired once at app startup) and is surfaced by the sidebar and Settings.
        // The Home panel intentionally shows nothing about updates — this was
        // removed in 6.1.0 and the centralization in 6.2.0 keeps Home clean.

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
        public bool HasLastSession
        {
            get => _hasLastSession;
            private set
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

        private string _lastSessionDate = string.Empty;
        public string LastSessionDate
        {
            get => _lastSessionDate;
            private set => SetProperty(ref _lastSessionDate, value);
        }

        private string _lastSessionDuration = string.Empty;
        public string LastSessionDuration
        {
            get => _lastSessionDuration;
            private set => SetProperty(ref _lastSessionDuration, value);
        }

        private string _lastSessionStats = string.Empty;
        public string LastSessionStats
        {
            get => _lastSessionStats;
            private set
            {
                if (SetProperty(ref _lastSessionStats, value))
                    OnPropertyChanged(nameof(HasLastSessionStats));
            }
        }

        public bool HasLastSessionStats => !string.IsNullOrEmpty(_lastSessionStats);

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

        // ── Last session game covers ──────────────────────────────────────────

        public ObservableCollection<SessionGameCover> LastSessionCovers { get; } = new();

        /// <summary>"+2" when the session detected more games than the strip shows, else empty.</summary>
        private string _lastSessionCoversOverflow = string.Empty;
        public string LastSessionCoversOverflow
        {
            get => _lastSessionCoversOverflow;
            private set => SetProperty(ref _lastSessionCoversOverflow, value);
        }

        private bool _hasLastSessionCoversOverflow;
        public bool HasLastSessionCoversOverflow
        {
            get => _hasLastSessionCoversOverflow;
            private set => SetProperty(ref _hasLastSessionCoversOverflow, value);
        }

        private bool _hasLastSessionCovers;
        public bool HasLastSessionCovers
        {
            get => _hasLastSessionCovers;
            private set => SetProperty(ref _hasLastSessionCovers, value);
        }

        /// <summary>
        /// True when the process monitor ran but found no games (empty list, not null).
        /// Drives the "No games detected" fallback label.
        /// </summary>
        private bool _hasNoGamesDetected;
        public bool HasNoGamesDetected
        {
            get => _hasNoGamesDetected;
            private set => SetProperty(ref _hasNoGamesDetected, value);
        }

        // ── Live session ──────────────────────────────────────────────────────

        private const int LiveWindowSize = 30;

        // Rolling buffers — replaced by new List on each sample to trigger x:Bind redraw.
        private readonly List<float> _rttBuffer      = new();
        private readonly List<float> _bitrateBuffer  = new();
        private readonly List<int>   _dropsBuffer    = new();
        private readonly List<float> _fpsBuffer      = new();
        private readonly List<float> _hostLatBuffer  = new();
        private readonly List<float> _gpuBuffer      = new();
        private readonly List<float> _encBuffer      = new();
        private readonly List<float> _cpuBuffer      = new();

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

        private string _liveStartedAt = string.Empty;
        public string LiveStartedAt
        {
            get => _liveStartedAt;
            private set => SetProperty(ref _liveStartedAt, value);
        }

        private string _liveDropPct = "0.00%";
        public string LiveDropPct
        {
            get => _liveDropPct;
            private set => SetProperty(ref _liveDropPct, value);
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
        private string _liveRttValue = "—";
        public string LiveRttValue
        {
            get => _liveRttValue;
            private set => SetProperty(ref _liveRttValue, value);
        }

        private string _liveRttColorHex = "#808080";
        public string LiveRttColorHex
        {
            get => _liveRttColorHex;
            private set => SetProperty(ref _liveRttColorHex, value);
        }

        private Color _liveRttLineColor = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
        public Color LiveRttLineColor
        {
            get => _liveRttLineColor;
            private set => SetProperty(ref _liveRttLineColor, value);
        }

        // Bitrate current value (always cyan — color is fixed in XAML)
        private string _liveBitrateValue = "—";
        public string LiveBitrateValue
        {
            get => _liveBitrateValue;
            private set => SetProperty(ref _liveBitrateValue, value);
        }

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

        // GPU / Encoder / CPU overlaid (multi-line SparklineControl with legend).
        private IReadOnlyList<SparklineSeries> _liveComputeLines = Array.Empty<SparklineSeries>();
        public IReadOnlyList<SparklineSeries> LiveComputeLines
        {
            get => _liveComputeLines;
            private set => SetProperty(ref _liveComputeLines, value);
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
        public string ClientControlBgHex     => _clientControlText == "On" ? "#1F4ade80" : "#1Aef4444";
        public string ClientControlBorderHex => _clientControlText == "On" ? "#4D4ade80" : "#40ef4444";

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
        public string HdrBgHex     => _hdrText == "—" ? "#1A808080" : _hdrText == "On" ? "#1F4ade80" : "#1Aef4444";
        public string HdrBorderHex => _hdrText == "—" ? "#40808080" : _hdrText == "On" ? "#4D4ade80" : "#40ef4444";

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
                                               : _isSpatialAudioActivated ? "#1F4ade80" : "#1Af59e0b";
        public string SpatialAudioBorderHex => _spatialAudioText == "Off" ? "#40ef4444"
                                               : _isSpatialAudioActivated ? "#4D4ade80" : "#40f59e0b";

        private string _gameLibraryText = "—";
        public string GameLibraryText
        {
            get => _gameLibraryText;
            private set => SetProperty(ref _gameLibraryText, value);
        }

        private string _gameLibrarySyncText = string.Empty;
        public string GameLibrarySyncText
        {
            get => _gameLibrarySyncText;
            private set
            {
                if (SetProperty(ref _gameLibrarySyncText, value))
                    OnPropertyChanged(nameof(HasGameLibrarySyncText));
            }
        }

        public bool HasGameLibrarySyncText => !string.IsNullOrEmpty(_gameLibrarySyncText);

        private string _gameLibrarySyncValue = string.Empty;
        public string GameLibrarySyncValue
        {
            get => _gameLibrarySyncValue;
            private set => SetProperty(ref _gameLibrarySyncValue, value);
        }

        private string _autoHdrText = "—";
        public string AutoHdrText
        {
            get => _autoHdrText;
            private set
            {
                if (SetProperty(ref _autoHdrText, value))
                {
                    OnPropertyChanged(nameof(AutoHdrColorHex));
                    OnPropertyChanged(nameof(AutoHdrBgHex));
                    OnPropertyChanged(nameof(AutoHdrBorderHex));
                }
            }
        }

        public string AutoHdrColorHex  => _autoHdrText == "—" ? "#A8A49F"   : _autoHdrText == "On" ? "#4ade80"   : "#f87171";
        public string AutoHdrBgHex     => _autoHdrText == "—" ? "#1A808080" : _autoHdrText == "On" ? "#1F4ade80" : "#1Aef4444";
        public string AutoHdrBorderHex => _autoHdrText == "—" ? "#40808080" : _autoHdrText == "On" ? "#4D4ade80" : "#40ef4444";

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
            "On"    => "#1F4ade80",
            "Stuck" => "#1Ffbbf24",
            _       => "#1Aef4444",
        };
        public string NvSentinelAutoRestoreBorderHex => _nvSentinelAutoRestoreText switch
        {
            "On"    => "#4D4ade80",
            "Stuck" => "#4Dfbbf24",
            _       => "#40ef4444",
        };

        private string _nvSentinelBadgeText = "Off";
        public string NvSentinelBadgeText
        {
            get => _nvSentinelBadgeText;
            private set => SetProperty(ref _nvSentinelBadgeText, value);
        }

        private string _nvSentinelLastRestoreValue = "never";
        public string NvSentinelLastRestoreValue
        {
            get => _nvSentinelLastRestoreValue;
            private set => SetProperty(ref _nvSentinelLastRestoreValue, value);
        }

        // ── LOGS tile ─────────────────────────────────────────────────────────

        private string _logsSessionCount = "0";
        public string LogsSessionCount
        {
            get => _logsSessionCount;
            private set => SetProperty(ref _logsSessionCount, value);
        }

        private string _logsSessionCountColorHex = "#808080";
        public string LogsSessionCountColorHex
        {
            get => _logsSessionCountColorHex;
            private set => SetProperty(ref _logsSessionCountColorHex, value);
        }

        private string _logsTotalDuration = "—";
        public string LogsTotalDuration
        {
            get => _logsTotalDuration;
            private set => SetProperty(ref _logsTotalDuration, value);
        }

        // ── "This week" aggregate insight (last 7 days) ───────────────────────
        private bool _hasThisWeek;
        public bool HasThisWeek
        {
            get => _hasThisWeek;
            private set => SetProperty(ref _hasThisWeek, value);
        }

        private string _thisWeekSummary = "—";
        public string ThisWeekSummary
        {
            get => _thisWeekSummary;
            private set => SetProperty(ref _thisWeekSummary, value);
        }

        private string _thisWeekGradeLabel = "—";
        public string ThisWeekGradeLabel
        {
            get => _thisWeekGradeLabel;
            private set => SetProperty(ref _thisWeekGradeLabel, value);
        }

        private string _thisWeekGradeColorHex = "#808080";
        public string ThisWeekGradeColorHex
        {
            get => _thisWeekGradeColorHex;
            private set => SetProperty(ref _thisWeekGradeColorHex, value);
        }

        private string _thisWeekGradeBgHex = "#1A808080";
        public string ThisWeekGradeBgHex
        {
            get => _thisWeekGradeBgHex;
            private set => SetProperty(ref _thisWeekGradeBgHex, value);
        }

        private string _thisWeekGradeBorderHex = "#40808080";
        public string ThisWeekGradeBorderHex
        {
            get => _thisWeekGradeBorderHex;
            private set => SetProperty(ref _thisWeekGradeBorderHex, value);
        }

        private string _thisWeekRtt = "—";
        public string ThisWeekRtt
        {
            get => _thisWeekRtt;
            private set => SetProperty(ref _thisWeekRtt, value);
        }

        private string _thisWeekAvgLetter = "—";
        public string ThisWeekAvgLetter
        {
            get => _thisWeekAvgLetter;
            private set => SetProperty(ref _thisWeekAvgLetter, value);
        }

        private string _thisWeekBreakdown = "";
        public string ThisWeekBreakdown
        {
            get => _thisWeekBreakdown;
            private set => SetProperty(ref _thisWeekBreakdown, value);
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

        private string _hostHealthSub = "Armed — waiting for a client";
        public string HostHealthSub { get => _hostHealthSub; private set => SetProperty(ref _hostHealthSub, value); }

        private string _hostHealthColorHex = "#4ade80";
        public string HostHealthColorHex { get => _hostHealthColorHex; private set => SetProperty(ref _hostHealthColorHex, value); }

        private string _hostGpuTempText = "—";
        public string HostGpuTempText { get => _hostGpuTempText; private set => SetProperty(ref _hostGpuTempText, value); }
        private double _hostGpuTempBar;
        public double HostGpuTempBar { get => _hostGpuTempBar; private set => SetProperty(ref _hostGpuTempBar, value); }
        private string _hostGpuTempColorHex = "#4ade80";
        public string HostGpuTempColorHex { get => _hostGpuTempColorHex; private set => SetProperty(ref _hostGpuTempColorHex, value); }

        private string _hostGpuLoadText = "—";
        public string HostGpuLoadText { get => _hostGpuLoadText; private set => SetProperty(ref _hostGpuLoadText, value); }
        private double _hostGpuLoadBar;
        public double HostGpuLoadBar { get => _hostGpuLoadBar; private set => SetProperty(ref _hostGpuLoadBar, value); }
        private string _hostGpuLoadColorHex = "#4ade80";
        public string HostGpuLoadColorHex { get => _hostGpuLoadColorHex; private set => SetProperty(ref _hostGpuLoadColorHex, value); }

        private string _hostVramText = "—";
        public string HostVramText { get => _hostVramText; private set => SetProperty(ref _hostVramText, value); }
        private double _hostVramBar;
        public double HostVramBar { get => _hostVramBar; private set => SetProperty(ref _hostVramBar, value); }

        private string _hostCpuText = "—";
        public string HostCpuText { get => _hostCpuText; private set => SetProperty(ref _hostCpuText, value); }
        private double _hostCpuBar;
        public double HostCpuBar { get => _hostCpuBar; private set => SetProperty(ref _hostCpuBar, value); }
        private string _hostCpuColorHex = "#4ade80";
        public string HostCpuColorHex { get => _hostCpuColorHex; private set => SetProperty(ref _hostCpuColorHex, value); }

        private string _hostNetText = "—";
        public string HostNetText { get => _hostNetText; private set => SetProperty(ref _hostNetText, value); }
        private double _hostNetBar;
        public double HostNetBar { get => _hostNetBar; private set => SetProperty(ref _hostNetBar, value); }

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
                OnPropertyChanged(nameof(PerfPeriodLabel));
                // PerfPeriodAxisLabel is set by ComputePerformance — for "all time" it
                // depends on the data, so it can only be resolved once the sessions are read.
                _ = ReloadPerformanceAsync();
            }
        }

        /// <summary>Header caption for the Performance card ("last 90 days" / "all time").</summary>
        public string PerfPeriodLabel => _perfPeriodDays <= 0 ? "all time" : $"last {_perfPeriodDays} days";

        /// <summary>
        /// Compact span label drawn at the right edge of the trend chart's X axis ("90 days").
        /// Separate from PerfPeriodLabel so the chart corner stays short — the header already
        /// carries the "last …" wording. Filled by ComputePerformance rather than computed
        /// here, because for "all time" the span comes from the data (how far back the oldest
        /// recorded session actually goes), not from the selected period.
        /// </summary>
        private string _perfPeriodAxisLabel = "7 days";
        public string PerfPeriodAxisLabel
        {
            get => _perfPeriodAxisLabel;
            private set => SetProperty(ref _perfPeriodAxisLabel, value);
        }

        // ── Performance trend (Dashboard bottom-left chart) ───────────────────
        private IReadOnlyList<SparklineSeries> _weekPerfLines = Array.Empty<SparklineSeries>();
        public IReadOnlyList<SparklineSeries> WeekPerfLines
        {
            get => _weekPerfLines;
            private set => SetProperty(ref _weekPerfLines, value);
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
        private string _lastSessionAgo = "";
        public string LastSessionAgo
        {
            get => _lastSessionAgo;
            private set => SetProperty(ref _lastSessionAgo, value);
        }

        private string _lastSessionRttValue = "—";
        public string LastSessionRttValue
        {
            get => _lastSessionRttValue;
            private set => SetProperty(ref _lastSessionRttValue, value);
        }

        private string _lastSessionRttSub = "";
        public string LastSessionRttSub
        {
            get => _lastSessionRttSub;
            private set => SetProperty(ref _lastSessionRttSub, value);
        }

        private string _lastSessionHostLatency = "—";
        public string LastSessionHostLatency
        {
            get => _lastSessionHostLatency;
            private set => SetProperty(ref _lastSessionHostLatency, value);
        }

        private string _lastSessionDropsValue = "—";
        public string LastSessionDropsValue
        {
            get => _lastSessionDropsValue;
            private set => SetProperty(ref _lastSessionDropsValue, value);
        }

        // ── Spatial audio live activation status ──────────────────────────────

        private string _spatialAudioActivationText = string.Empty;
        /// <summary>
        /// Non-empty while Dolby/Sonic is activating or has just activated.
        /// Shown as a subtext in the Spatial Audio home tile.
        /// </summary>
        public string SpatialAudioActivationText
        {
            get => _spatialAudioActivationText;
            private set
            {
                if (SetProperty(ref _spatialAudioActivationText, value))
                    OnPropertyChanged(nameof(HasSpatialAudioActivationText));
            }
        }

        public bool HasSpatialAudioActivationText => !string.IsNullOrEmpty(_spatialAudioActivationText);

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

        // ── Constructor ───────────────────────────────────────────────────────

        public HomeViewModel()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            LoadVersionInfo();

            // Restore the Performance period before the view binds, so the ComboBox's
            // first write-back matches and doesn't trigger a redundant recompute.
            // An unrecognised stored value (e.g. from an older build) falls back to 7 days.
            int savedPeriod = ConfigService.GetInt("DashboardPerfPeriodDays", 7);
            _perfPeriodDays = Array.IndexOf(PerfPeriods, savedPeriod) >= 0 ? savedPeriod : 7;
            // Seed the chart's axis label from the restored period. It is normally filled by
            // ComputePerformance (for "all time" the span comes from the data), but without
            // this the card briefly claims "7 days" under any other saved period.
            _perfPeriodAxisLabel = _perfPeriodDays <= 0 ? "all time" : $"{_perfPeriodDays} days";

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
                        ? mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gbps" : $"{mbps} Mbps"
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
            // (gameName, coverImagePath?) pairs gathered from the last session's detected games
            List<(string Name, string? CoverPath)>? detectedGameCovers = null;

            // I/O-bound reads run off the UI thread; results marshalled back via dispatcher
            await Task.Run(() =>
            {
                // Last completed session
                try
                {
                    var sessions = SessionLogger.Load();

                    // LOGS tile aggregates
                    var completed = sessions.Where(s => s.EndTime != null).ToList();
                    int logsTotal = completed.Count;
                    var totalDur  = TimeSpan.FromSeconds(
                        completed.Sum(s => (s.EndTime!.Value - s.StartTime).TotalSeconds));
                    var graded = completed
                        .Where(s => s.Grade is QualityGrade.High or QualityGrade.Medium or QualityGrade.Low)
                        .ToList();
                    string logsColor = "#808080";
                    if (graded.Count > 0)
                    {
                        double avg = graded.Average(s => (int)s.Grade!.Value); // High=1, Med=2, Low=3
                        logsColor = avg < 1.5 ? "#4ade80" : avg < 2.5 ? "#fbbf24" : "#f87171";
                    }
                    _dispatcher.TryEnqueue(() =>
                    {
                        LogsSessionCount         = logsTotal.ToString();
                        LogsSessionCountColorHex = logsColor;
                        LogsTotalDuration        = FormatTotalDuration(totalDur);
                    });

                    // Performance aggregate over the user-selected window (7 or 30 days).
                    ComputePerformance(completed, _perfPeriodDays);
                    // Sessions are stored newest-first (Insert(0) in StartSession).
                    var last = sessions.FirstOrDefault(s => s.EndTime != null);
                    if (last != null)
                    {
                        string stats = last.QualityStats != null
                            ? $"RTT avg  {(int)last.QualityStats.RttAvgMs} ms   " +
                              $"Frame drops  {last.QualityStats.DropRatePct:0.#}%"
                            : string.Empty;

                        // Headline metric values for the mockup Last-Session card
                        var qs = last.QualityStats;
                        string lsAgo = last.EndTime is { } end ? FormatAgo(DateTime.Now - end) : "";
                        string lsRtt = qs != null ? $"{(int)qs.RttAvgMs}" : "—";
                        string lsRttSub = qs != null ? $"{(int)qs.RttMaxMs} peak" : "";
                        string lsHost = qs != null && qs.HostLatencyAvgMs >= 0 ? $"{qs.HostLatencyAvgMs:0.#}" : "—";
                        string lsDrops = qs != null ? $"{qs.DropRatePct:0.#}" : "—";
                        _dispatcher.TryEnqueue(() =>
                        {
                            LastSessionAgo          = lsAgo;
                            LastSessionRttValue     = lsRtt;
                            LastSessionRttSub       = lsRttSub;
                            LastSessionHostLatency  = lsHost;
                            LastSessionDropsValue   = lsDrops;
                        });

                        // Resolve cover paths for detected games (File.Exists — cheap, off UI thread).
                        // GamesDetected != null means monitor ran; [] means it ran but found nothing.
                        if (last.GamesDetected != null)
                        {
                            if (last.GamesDetected.Count > 0)
                            {
                                // Fallback map from live GameLibraryState (for old sessions
                                // that pre-date the GamesDetectedCoverPaths snapshot field).
                                var gameMap = GameLibraryState.Current.Games
                                    .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);

                                detectedGameCovers = last.GamesDetected
                                    .Select(name =>
                                    {
                                        string? path = null;

                                        // 1) Prefer the path snapshotted at session-end time —
                                        //    works even if the game was later removed from the library.
                                        if (last.GamesDetectedCoverPaths != null &&
                                            last.GamesDetectedCoverPaths.TryGetValue(name, out string? snap) &&
                                            File.Exists(snap))
                                        {
                                            path = snap;
                                        }
                                        // 2) Fall back to live GameLibraryState (old sessions).
                                        else if (gameMap.TryGetValue(name, out var gEntry))
                                        {
                                            path = gEntry.CoverImagePath;
                                        }

                                        return (name, path);
                                    })
                                    .ToList();
                            }
                            else
                            {
                                // Monitor ran but found no games (e.g. desktop session)
                                detectedGameCovers = new List<(string, string?)>(); // empty sentinel
                            }
                        }

                        _dispatcher.TryEnqueue(() =>
                        {
                            HasLastSession        = true;
                            LastSessionDate       = last.StartTimeDisplay;   // "dd/MM/yyyy  HH:mm"
                            LastSessionDuration   = last.DurationDisplay;
                            LastSessionStats      = stats;
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
                        ManagedAppsText = count > 0 ? count.ToString() : "—");
                }
                catch { _dispatcher.TryEnqueue(() => ManagedAppsText = "—"); }

                // Streaming server
                try
                {
                    var info = LogParser.FindStreamingAppInfo();
                    if (info != null)
                    {
                        streamHostExePath = info.ExePath;
                        _dispatcher.TryEnqueue(() => { StreamHostName = info.AppName; HasStreamHost = true; });
                    }
                }
                catch { }
            });

            // Populate the Last Session game cover strip
            LastSessionCovers.Clear();
            HasLastSessionCovers  = false;
            HasNoGamesDetected    = false;
            LastSessionCoversOverflow    = string.Empty;
            HasLastSessionCoversOverflow = false;

            if (detectedGameCovers != null)
            {
                // Three covers, and a "+N" for the rest.
                //
                // There was no limit here at all: a session accumulates every game it
                // recognises and survives one game closing and the next starting, so an
                // afternoon with four titles produced four covers. The strip is a plain
                // horizontal StackPanel with no scrolling and no wrapping, sharing the row
                // with a star-sized column, so each extra cover came straight out of the
                // width the figures had to draw in. Capping keeps the card the same shape
                // whatever the session did; nothing is lost, because Sessions still lists
                // every game the session detected.
                const int MaxCovers = 3;
                int overflow = Math.Max(0, detectedGameCovers.Count - MaxCovers);
                LastSessionCoversOverflow = overflow > 0 ? $"+{overflow}" : string.Empty;
                HasLastSessionCoversOverflow = overflow > 0;

                if (detectedGameCovers.Count > 0)
                {
                    int shown = Math.Min(MaxCovers, detectedGameCovers.Count);

                    for (int i = 0; i < shown; i++)
                        LastSessionCovers.Add(new SessionGameCover(detectedGameCovers[i].Name));
                    HasLastSessionCovers = true;

                    // Load cover bitmaps via StorageFile (same pattern as GameLibraryViewModel)
                    for (int i = 0; i < shown; i++)
                    {
                        string? path = detectedGameCovers[i].CoverPath;
                        if (path == null) continue;
                        try
                        {
                            var file = await StorageFile.GetFileFromPathAsync(path);
                            var bmp = new BitmapImage();
                            // 2× display width (67 px) → WIC Fant resampler, GPU renders 1:1
                            bmp.DecodePixelWidth = 134;
                            using var stream = await file.OpenReadAsync();
                            await bmp.SetSourceAsync(stream);
                            LastSessionCovers[i].CoverImage = bmp;
                        }
                        catch { /* non-fatal — fallback text already shown */ }
                    }
                }
                else
                {
                    // Monitor ran but found no games (desktop session, etc.)
                    HasNoGamesDetected = true;
                }
            }
            // else: detectedGameCovers == null → pre-feature session, show nothing

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
                    GameLibrarySyncText  = "Synced";
                    GameLibrarySyncValue = local.ToString("dd/MM/yyyy  HH:mm");
                }
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
            string wkLabel = "No grades yet", wkColor = "#808080", wkBg = "#1A808080", wkBdr = "#40808080";
            if (wkGraded.Count > 0)
            {
                double avg = wkGraded.Average(s => (int)s.Grade!.Value); // High=1, Med=2, Low=3
                if (avg < 1.5)      { wkLabel = "Excellent avg"; wkColor = "#4ade80"; wkBg = "#1A4ade80"; wkBdr = "#404ade80"; }
                else if (avg < 2.5) { wkLabel = "Good avg";      wkColor = "#fbbf24"; wkBg = "#1Af59e0b"; wkBdr = "#40f59e0b"; }
                else                { wkLabel = "Poor avg";      wkColor = "#f87171"; wkBg = "#1Aef4444"; wkBdr = "#40ef4444"; }
            }

            var wkStats = inPeriod.Where(s => s.QualityStats != null).ToList();
            string wkRtt = wkStats.Count > 0
                ? $"{(int)wkStats.Average(s => s.QualityStats!.RttAvgMs)} ms"
                : "—";
            string wkSummary = wkCount == 1 ? "1 session" : $"{wkCount} sessions";

            // Average-grade letter + breakdown counts
            string wkLetter = "—";
            if (wkGraded.Count > 0)
            {
                double avg = wkGraded.Average(s => (int)s.Grade!.Value);
                wkLetter = avg < 1.5 ? "A" : avg < 2.5 ? "B" : "C";
            }
            int hi = wkGraded.Count(s => s.Grade == QualityGrade.High);
            int md = wkGraded.Count(s => s.Grade == QualityGrade.Medium);
            int lo = wkGraded.Count(s => s.Grade == QualityGrade.Low);
            string wkBreak = wkGraded.Count > 0 ? $"{hi} excellent · {md} good · {lo} poor" : "";

            // Performance trend — one point per graded session (oldest → newest).
            // RTT is always present; host frame latency only for sessions recorded with
            // StreamLight ≥ 4.0.1, so that line is added only when there is real data.
            var ordered  = wkStats.OrderBy(s => s.StartTime).ToList();
            var rttData  = ordered.Select(s => (float)s.QualityStats!.RttAvgMs).ToList();
            var hostData = ordered
                .Select(s => s.QualityStats!.HostLatencyAvgMs >= 0 ? (float)s.QualityStats.HostLatencyAvgMs : 0f)
                .ToList();

            var perfLines = new List<SparklineSeries>
            {
                new() { Label = "RTT", Color = ComputeGpu, Data = rttData },
            };
            if (hostData.Any(v => v > 0f))
                perfLines.Add(new SparklineSeries { Label = "Host lat.", Color = PerfHostLat, Data = hostData });

            bool   hasPerf   = ordered.Count >= 2;
            string perfDrops = wkStats.Count > 0
                ? $"{wkStats.Average(s => s.QualityStats!.DropRatePct):0.#}%"
                : "—";
            var    wkDur     = TimeSpan.FromSeconds(
                inPeriod.Where(s => s.EndTime != null)
                        .Sum(s => (s.EndTime!.Value - s.StartTime).TotalSeconds));
            string perfStreamed = wkCount > 0 ? FormatTotalDuration(wkDur) : "—";

            // X-axis span label. For a fixed period it's the period itself; for "all time"
            // report how far back the recorded sessions actually reach, which is far more
            // informative than a generic "all time" — the chart then states its own scale.
            string axisLabel;
            if (days > 0)
            {
                axisLabel = $"{days} days";
            }
            else if (inPeriod.Count > 0)
            {
                var oldest  = inPeriod.Min(s => s.StartTime);
                int spanDay = Math.Max(1, (int)Math.Ceiling((DateTime.Now - oldest).TotalDays));
                axisLabel   = spanDay == 1 ? "1 day" : $"{spanDay} days";
            }
            else
            {
                axisLabel = "all time";   // nothing recorded yet — no span to report
            }

            _dispatcher.TryEnqueue(() =>
            {
                // Discard a result that the user has already scrolled past: LoadStatusAsync and
                // ReloadPerformanceAsync can be in flight at once, and without this the slower
                // one can land last and repaint the card with the previous period's data.
                if (days != _perfPeriodDays) return;

                WeekPerfLines      = perfLines;
                HasWeekPerf        = hasPerf;
                WeekPerfDrops      = perfDrops;
                WeekPerfStreamed   = perfStreamed;
                PerfPeriodAxisLabel = axisLabel;
            });

            _dispatcher.TryEnqueue(() =>
            {
                if (days != _perfPeriodDays) return;   // superseded — see above

                HasThisWeek            = wkCount > 0;
                ThisWeekSummary        = wkSummary;
                ThisWeekGradeLabel     = wkLabel;
                ThisWeekGradeColorHex  = wkColor;
                ThisWeekGradeBgHex     = wkBg;
                ThisWeekGradeBorderHex = wkBdr;
                ThisWeekRtt            = wkRtt;
                ThisWeekAvgLetter      = wkLetter;
                ThisWeekBreakdown      = wkBreak;
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
                            LastSeenText = seen,
                        });
                    }
                }
            }
            catch { /* non-fatal — the card simply shows the empty state */ }

            HasPairedClients     = PairedClients.Count > 0;
            PairedClientsSummary = PairedClients.Count switch
            {
                0 => "none yet",
                1 => "1 approved",
                _ => $"{PairedClients.Count} approved",
            };
        }

        private void RefreshNvSentinelTile()
        {
            var svc = AppStateService.Instance.NvidiaSentinel;
            IsNvSentinelAvailable = svc?.IsNvidiaAvailable == true;

            if (svc == null || !svc.IsNvidiaAvailable)
            {
                NvSentinelAutoRestoreText  = "Off";
                NvSentinelBadgeText        = "Off";
                NvSentinelLastRestoreValue = "never";
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

            NvSentinelLastRestoreValue = svc.LastRestoreAt is { } at
                ? at.ToLocalTime().ToString("dd/MM/yyyy  HH:mm")
                : "never";
        }

        public void RequestStopStream()
            => AppStateService.Instance.RequestStopStreamAction?.Invoke();

        public void OpenGitHub()
            => _ = Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/FoggyBytes/StreamTweak"));

        public void OpenPayPal()
            => _ = Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://paypal.me/foggybytes"));

        public void OpenLicense()
            => _ = Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/FoggyBytes/StreamTweak/blob/main/LICENSE"));

        private static string FormatTotalDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 60)  return $"{(int)t.TotalSeconds}s";
            if (t.TotalHours   < 1)   return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
            return $"{(int)t.TotalHours}h {t.Minutes:00}m";
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
                HostGpuTempBar      = Math.Clamp(s.GpuTemp, 0, 100);
                HostGpuTempColorHex = s.GpuTemp >= 85 ? "#f87171" : s.GpuTemp >= 75 ? "#fbbf24" : "#4ade80";
            }
            else { HostGpuTempText = "—"; HostGpuTempBar = 0; HostGpuTempColorHex = "#4ade80"; }

            // GPU 3D load
            if (s.Gpu >= 0)
            {
                HostGpuLoadText     = s.Gpu.ToString();
                HostGpuLoadBar      = Math.Clamp(s.Gpu, 0, 100);
                HostGpuLoadColorHex = s.Gpu >= 90 ? "#fbbf24" : "#4ade80";
            }
            else { HostGpuLoadText = "—"; HostGpuLoadBar = 0; HostGpuLoadColorHex = "#4ade80"; }

            // VRAM — "used / total GB" when the total is known, else just used.
            // Unit is rendered separately in the tile (like every other vital), so it
            // stays out of the 21px value text — baking " GB" in here made this the
            // widest tile and it overflowed into the CPU column at small window sizes.
            if (s.VramUsedMb >= 0 && s.VramTotalMb > 0)
            {
                HostVramText = $"{s.VramUsedMb / 1024.0:0.0}/{s.VramTotalMb / 1024.0:0}";
                HostVramBar  = Math.Clamp(s.VramUsedMb * 100.0 / s.VramTotalMb, 0, 100);
            }
            else if (s.VramUsedMb >= 0)
            {
                HostVramText = $"{s.VramUsedMb / 1024.0:0.0}";
                HostVramBar  = 0;
            }
            else { HostVramText = "—"; HostVramBar = 0; }

            // CPU
            if (s.Cpu >= 0)
            {
                HostCpuText     = s.Cpu.ToString();
                HostCpuBar      = Math.Clamp(s.Cpu, 0, 100);
                HostCpuColorHex = s.Cpu >= 90 ? "#fbbf24" : "#4ade80";
            }
            else { HostCpuText = "—"; HostCpuBar = 0; HostCpuColorHex = "#4ade80"; }

            // Network TX on the default-route interface. Bar is scaled against
            // 100 Mbps — enough headroom to read an idle host at a glance.
            if (s.NetTxMbps >= 0)
            {
                HostNetText = s.NetTxMbps.ToString();
                HostNetBar  = Math.Clamp(s.NetTxMbps, 0, 100);
            }
            else { HostNetText = "—"; HostNetBar = 0; }

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

            HostHealthSub = ClientControlText == "On"
                ? "Ready — clients may match the link"
                : "Waiting for a client";
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
            _gpuBuffer.Clear();
            _encBuffer.Clear();
            _cpuBuffer.Clear();
            _sessDrops    = 0;
            _sessRendered = 0;
            LiveRttSeries      = Array.Empty<float>();
            LiveBitrateSeries  = Array.Empty<float>();
            LiveHostLatSeries  = Array.Empty<float>();
            LiveComputeLines   = Array.Empty<SparklineSeries>();
            LiveDropPct        = "0.00%";
            LiveRttValue       = "—";
            LiveBitrateValue   = "—";
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
            LiveRttLineColor   = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

            var startTime     = SessionLogger.ActiveSessionStartTime;
            LiveStartedAt     = $"Started {startTime:dd/MM/yyyy  HH:mm}";
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
                };
            }
            _durationTimer.Start();
        }

        private void StopLiveSession()
        {
            _durationTimer?.Stop();
            _rttBuffer.Clear();
            _bitrateBuffer.Clear();
            _dropsBuffer.Clear();
            _fpsBuffer.Clear();
            _hostLatBuffer.Clear();
            _gpuBuffer.Clear();
            _encBuffer.Clear();
            _cpuBuffer.Clear();
        }

        // GPU / Encoder / CPU line colours (match the compute-chart legend).
        private static readonly Color ComputeGpu = Color.FromArgb(0xFF, 0x4a, 0xde, 0x80); // green
        private static readonly Color ComputeEnc = Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA); // purple
        private static readonly Color ComputeCpu = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B); // amber

        // Weekly performance chart — host frame latency line (cyan, distinct from RTT green).
        private static readonly Color PerfHostLat = Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8);

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
                Push(_gpuBuffer, (float)Math.Max(0, s.Gpu));
                Push(_encBuffer, (float)Math.Max(0, s.Enc));
                Push(_cpuBuffer, (float)Math.Max(0, s.Cpu));

                // Replace list references so x:Bind on SparklineControl.Data fires Redraw.
                LiveRttSeries     = _rttBuffer.ToList();
                LiveBitrateSeries = _bitrateBuffer.ToList();
                LiveHostLatSeries = _hostLatBuffer.ToList();
                LiveComputeLines  = new List<SparklineSeries>
                {
                    new() { Label = $"GPU {(int)LastOr(_gpuBuffer)}", Color = ComputeGpu, Data = _gpuBuffer.ToList() },
                    new() { Label = $"ENC {(int)LastOr(_encBuffer)}", Color = ComputeEnc, Data = _encBuffer.ToList() },
                    new() { Label = $"CPU {(int)LastOr(_cpuBuffer)}", Color = ComputeCpu, Data = _cpuBuffer.ToList() },
                };

                // Session-cumulative drop rate + frame count (stat card).
                _sessDrops    += Math.Max(0, s.Drops);
                _sessRendered += Math.Max(0f, s.FpsAvg);
                double totalFrames = _sessDrops + _sessRendered;
                float dropPct = totalFrames > 0 ? (float)(_sessDrops / totalFrames * 100.0) : 0f;
                LiveDropPct     = $"{dropPct:0.00}%";
                LiveDropsNumber = $"{dropPct:0.0}";
                LiveDropsSub    = $"{_sessDrops} of {FormatFrameCount(totalFrames)} frames";

                // RTT value + adaptive color (≤30 ms green, ≤80 ms amber, >80 ms red)
                LiveRttValue  = s.RttMs < 10f ? $"{s.RttMs:0.0} ms" : $"{(int)s.RttMs} ms";
                LiveRttNumber = s.RttMs < 10f ? $"{s.RttMs:0.0}" : $"{(int)s.RttMs}";
                float rttPeak = _rttBuffer.Count > 0 ? _rttBuffer.Max() : s.RttMs;
                LiveRttSub    = $"peak {(int)rttPeak} · jit {s.JitterMs:0.0}";
                if (s.RttMs <= 30f)
                {
                    LiveRttColorHex  = "#4ade80";
                    LiveRttLineColor = Color.FromArgb(0xFF, 0x4a, 0xde, 0x80);
                }
                else if (s.RttMs <= 80f)
                {
                    LiveRttColorHex  = "#fbbf24";
                    LiveRttLineColor = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B);
                }
                else
                {
                    LiveRttColorHex  = "#f87171";
                    LiveRttLineColor = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);
                }

                // Bitrate value
                LiveBitrateValue  = s.BitrateMbps >= 100f ? $"{s.BitrateMbps:0} Mbps" : $"{s.BitrateMbps:0.0} Mbps";
                LiveBitrateNumber = s.BitrateMbps >= 100f ? $"{s.BitrateMbps:0}"      : $"{s.BitrateMbps:0.0}";

                // Host frame latency (capture + encode); "—" until StreamLight reports it.
                LiveHostLatNumber = _hostLatBuffer.Count > 0 ? $"{_hostLatBuffer[^1]:0.0}" : "—";

                // Frame rate (live state box).
                LiveFpsNumber = s.FpsAvg > 0f ? $"{s.FpsAvg:0}" : "—";

                // Delivered vs configured ceiling — the comparison is the whole point,
                // and only the host can make it (the client sets the target, the host
                // sees what actually goes out).
                float target = AppStateService.Instance.CurrentTargetBitrateMbps;
                LiveBitrateSub = target > 0f ? $"of {target:0.#} Mbps target" : "live outbound";
            });
        }

        private static float LastOr(List<float> b) => b.Count > 0 ? b[^1] : 0f;

        private static string FormatFrameCount(double frames)
            => frames >= 1000 ? $"{frames / 1000.0:0.#}k" : $"{(int)frames}";

        private static void Push<T>(List<T> buffer, T value)
        {
            buffer.Add(value);
            if (buffer.Count > LiveWindowSize)
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

        private void LoadVersionInfo()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText = version != null
                ? $"Version {version.Major}.{version.Minor}.{version.Build}"
                : "Version 6.2.2";

            string location = Assembly.GetExecutingAssembly().Location;
            BuildDateText = File.Exists(location)
                ? $"Build: {File.GetLastWriteTime(location):dd MMM yyyy}"
                : string.Empty;
        }
    }
}
