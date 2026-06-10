using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
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

        /// <summary>True when there is a completed session AND no session is currently active.</summary>
        public bool ShowLastSession  => _hasLastSession && !_isSessionActive;

        /// <summary>True when no session has ever been recorded and no session is active.</summary>
        public bool ShowEmptyState   => !_hasLastSession && !_isSessionActive;

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

        // ── Status tiles ──────────────────────────────────────────────────────

        private string _nicSpeedText = "—";
        public string NicSpeedText
        {
            get => _nicSpeedText;
            private set => SetProperty(ref _nicSpeedText, value);
        }

        private string _autoStreamingText = "Off";
        public string AutoStreamingText
        {
            get => _autoStreamingText;
            private set
            {
                if (SetProperty(ref _autoStreamingText, value))
                {
                    OnPropertyChanged(nameof(AutoStreamingColorHex));
                    OnPropertyChanged(nameof(AutoStreamingBgHex));
                    OnPropertyChanged(nameof(AutoStreamingBorderHex));
                }
            }
        }

        public string AutoStreamingColorHex  => _autoStreamingText == "On" ? "#22c55e"   : "#ef4444";
        public string AutoStreamingBgHex     => _autoStreamingText == "On" ? "#1F22c55e" : "#1Aef4444";
        public string AutoStreamingBorderHex => _autoStreamingText == "On" ? "#4D22c55e" : "#40ef4444";

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

        public string HdrColorHex  => _hdrText == "On" ? "#22c55e"   : "#ef4444";
        public string HdrBgHex     => _hdrText == "On" ? "#1F22c55e" : "#1Aef4444";
        public string HdrBorderHex => _hdrText == "On" ? "#4D22c55e" : "#40ef4444";

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

        public string SpatialAudioColorHex  => _spatialAudioText == "Off" ? "#ef4444"
                                               : _isSpatialAudioActivated ? "#22c55e" : "#f59e0b";
        public string SpatialAudioBgHex     => _spatialAudioText == "Off" ? "#1Aef4444"
                                               : _isSpatialAudioActivated ? "#1F22c55e" : "#1Af59e0b";
        public string SpatialAudioBorderHex => _spatialAudioText == "Off" ? "#40ef4444"
                                               : _isSpatialAudioActivated ? "#4D22c55e" : "#40f59e0b";

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

        public string AutoHdrColorHex  => _autoHdrText == "On" ? "#22c55e"   : "#ef4444";
        public string AutoHdrBgHex     => _autoHdrText == "On" ? "#1F22c55e" : "#1Aef4444";
        public string AutoHdrBorderHex => _autoHdrText == "On" ? "#4D22c55e" : "#40ef4444";

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

        public string NvSentinelAutoRestoreColorHex  => _nvSentinelAutoRestoreText == "On" ? "#22c55e"   : "#ef4444";
        public string NvSentinelAutoRestoreBgHex     => _nvSentinelAutoRestoreText == "On" ? "#1F22c55e" : "#1Aef4444";
        public string NvSentinelAutoRestoreBorderHex => _nvSentinelAutoRestoreText == "On" ? "#4D22c55e" : "#40ef4444";

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

            IsSessionActive = AppStateService.Instance.IsSessionActive;
            AppStateService.Instance.SessionStateChanged       += OnSessionStateChanged;
            AppStateService.Instance.SpatialAudioStatusChanged += OnSpatialAudioStatusChanged;
            AppStateService.Instance.LiveTelemetrySample       += OnLiveSample;

            var sentinel = AppStateService.Instance.NvidiaSentinel;
            if (sentinel != null)
                sentinel.AutoRestorePerformed += OnNvAutoRestorePerformed;

            if (IsSessionActive)
                StartLiveSession();

            // Populate initial status if Dolby is already running
            string initial = AppStateService.Instance.CurrentSpatialAudioStatus;
            if (!string.IsNullOrEmpty(initial))
                OnSpatialAudioStatusChanged(initial);

            // Poll NIC link speed every 2 s so the Home tile stays current in real time.
            _nicSpeedTimer = new System.Threading.Timer(_ => RefreshNicSpeed(),
                state: null, dueTime: 0, period: 2000);
        }

        public void Unsubscribe()
        {
            _nicSpeedTimer?.Dispose();
            _nicSpeedTimer = null;
            StopLiveSession();
            AppStateService.Instance.SessionStateChanged       -= OnSessionStateChanged;
            AppStateService.Instance.SpatialAudioStatusChanged -= OnSpatialAudioStatusChanged;
            AppStateService.Instance.LiveTelemetrySample       -= OnLiveSample;

            var sentinel = AppStateService.Instance.NvidiaSentinel;
            if (sentinel != null)
                sentinel.AutoRestorePerformed -= OnNvAutoRestorePerformed;
        }

        private void OnNvAutoRestorePerformed(object? sender, EventArgs e)
            => _dispatcher.TryEnqueue(RefreshNvSentinelTile);

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
                        logsColor = avg < 1.5 ? "#22c55e" : avg < 2.5 ? "#f59e0b" : "#ef4444";
                    }
                    _dispatcher.TryEnqueue(() =>
                    {
                        LogsSessionCount         = logsTotal.ToString();
                        LogsSessionCountColorHex = logsColor;
                        LogsTotalDuration        = FormatTotalDuration(totalDur);
                    });

                    // Sessions are stored newest-first (Insert(0) in StartSession).
                    var last = sessions.FirstOrDefault(s => s.EndTime != null);
                    if (last != null)
                    {
                        string stats = last.QualityStats != null
                            ? $"RTT avg  {(int)last.QualityStats.RttAvgMs} ms   " +
                              $"Frame drops  {last.QualityStats.DropRatePct:0.#}%"
                            : string.Empty;

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

            if (detectedGameCovers != null)
            {
                if (detectedGameCovers.Count > 0)
                {
                    foreach (var (name, _) in detectedGameCovers)
                        LastSessionCovers.Add(new SessionGameCover(name));
                    HasLastSessionCovers = true;

                    // Load cover bitmaps via StorageFile (same pattern as GameLibraryViewModel)
                    for (int i = 0; i < detectedGameCovers.Count; i++)
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
            AutoStreamingText = ConfigService.GetBool("AutoStreamingEnabled", false) ? "On" : "Off";

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
                HdrText = monitors.Any(m => m.HdrEnabled && m.HdrSupported) ? "On" : "Off";
                HdrDisplayName = monitors.FirstOrDefault(m => m.HdrSupported)?.FriendlyName
                                 ?? monitors.FirstOrDefault()?.FriendlyName
                                 ?? "Primary display";
            }
            catch { HdrText = "—"; HdrDisplayName = "Primary display"; }

            try
            {
                AutoHdrText = await HdrService.GetAutoHdrAsync() ? "On" : "Off";
            }
            catch { AutoHdrText = "—"; }

            RefreshNvSentinelTile();
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

            NvSentinelAutoRestoreText = svc.AutoRestoreEnabled ? "On" : "Off";

            if (svc.AutoRestoreEnabled)
            {
                int n = 0;
                try
                {
                    string? path = svc.SnapshotPath;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        n = NvidiaSentinelService.ReadSnapshot(path)?.Count ?? 0;
                }
                catch { n = 0; }
                NvSentinelBadgeText = n == 1 ? "1 setting saved" : $"{n} settings saved";
            }
            else
            {
                NvSentinelBadgeText = "Off";
            }

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

        // ── Live session helpers ──────────────────────────────────────────────

        private void StartLiveSession()
        {
            // Must run on UI thread (DispatcherQueueTimer requires it).
            _rttBuffer.Clear();
            _bitrateBuffer.Clear();
            _dropsBuffer.Clear();
            _fpsBuffer.Clear();
            LiveRttSeries      = Array.Empty<float>();
            LiveBitrateSeries  = Array.Empty<float>();
            LiveDropPct        = "0.00%";
            LiveRttValue       = "—";
            LiveBitrateValue   = "—";
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
        }

        private void OnLiveSample(float rttMs, float bitrateMbps, int drops, float fpsAvg)
        {
            // Fired on a background thread — marshal to UI thread for property updates.
            _dispatcher.TryEnqueue(() =>
            {
                Push(_rttBuffer,     rttMs);
                Push(_bitrateBuffer, bitrateMbps);
                Push(_dropsBuffer,   drops);
                Push(_fpsBuffer,     fpsAvg);

                // Replace list references so x:Bind on SparklineControl.Data fires Redraw.
                LiveRttSeries     = _rttBuffer.ToList();
                LiveBitrateSeries = _bitrateBuffer.ToList();

                // Drop % over the rolling window.
                int   totalDrops    = _dropsBuffer.Sum();
                float totalRendered = _fpsBuffer.Sum();
                float totalFrames   = totalDrops + totalRendered;
                LiveDropPct = totalFrames > 0
                    ? $"{totalDrops / totalFrames * 100f:0.00}%"
                    : "0.00%";

                // RTT value + adaptive color (≤30 ms green, ≤80 ms amber, >80 ms red)
                LiveRttValue = rttMs < 10f ? $"{rttMs:0.0} ms" : $"{(int)rttMs} ms";
                if (rttMs <= 30f)
                {
                    LiveRttColorHex  = "#22c55e";
                    LiveRttLineColor = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E);
                }
                else if (rttMs <= 80f)
                {
                    LiveRttColorHex  = "#f59e0b";
                    LiveRttLineColor = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B);
                }
                else
                {
                    LiveRttColorHex  = "#ef4444";
                    LiveRttLineColor = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);
                }

                // Bitrate value
                LiveBitrateValue = bitrateMbps >= 100f
                    ? $"{bitrateMbps:0} Mbps"
                    : $"{bitrateMbps:0.0} Mbps";
            });
        }

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
