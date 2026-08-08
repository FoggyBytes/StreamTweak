using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using StreamTweak.Controls;
using Windows.Storage;
using Windows.UI;

namespace StreamTweak.ViewModels
{
    /// <summary>One metric row in the Compare view: the two sessions' values + a delta.</summary>
    public sealed class CompareMetric
    {
        public string Label         { get; set; } = "";
        public string ValueA        { get; set; } = "";
        public string ValueB        { get; set; } = "";
        public string Delta         { get; set; } = "";
        public string DeltaColorHex { get; set; } = "#FF908C88";
    }

    /// <summary>One chart in the Compare view: title + the two sessions' lines.</summary>
    public sealed class CompareChart : ViewModelBase
    {
        public string Title { get; set; } = "";
        public IReadOnlyList<SparklineSeries> Lines { get; set; } = new List<SparklineSeries>();

        // Tracks LogsViewModel.DetailChartHeight so charts grow with the window.
        private double _height = 185;
        public double Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }
    }

    public sealed class LogsViewModel : ViewModelBase
    {
        // ── Session list ──────────────────────────────────────────────────────

        public ObservableCollection<SessionEntry> Sessions { get; } = new();

        private bool _hasSessions;
        public bool HasSessions
        {
            get => _hasSessions;
            private set => SetProperty(ref _hasSessions, value);
        }

        // ── Detail: header subtitle ("09/04/2026 21:58  ·  1h52m26s") ─────────

        private string _detailHeaderSubtitle = string.Empty;
        public string DetailHeaderSubtitle
        {
            get => _detailHeaderSubtitle;
            private set => SetProperty(ref _detailHeaderSubtitle, value);
        }

        // ── Detail overlay ────────────────────────────────────────────────────

        private bool _isDetailVisible;
        public bool IsDetailVisible
        {
            get => _isDetailVisible;
            private set => SetProperty(ref _isDetailVisible, value);
        }

        private SessionEntry? _selectedSession;
        public SessionEntry? SelectedSession
        {
            get => _selectedSession;
            private set
            {
                SetProperty(ref _selectedSession, value);
                RefreshDetailProperties();
            }
        }

        // ── Detail: grade ─────────────────────────────────────────────────────

        private string _gradeLabel = string.Empty;
        public string GradeLabel
        {
            get => _gradeLabel;
            private set => SetProperty(ref _gradeLabel, value);
        }

        private string _gradeColorHex = "#FF808080";
        public string GradeColorHex
        {
            get => _gradeColorHex;
            private set => SetProperty(ref _gradeColorHex, value);
        }

        // ── Detail: header ────────────────────────────────────────────────────

        private string _detailTitle = string.Empty;
        public string DetailTitle
        {
            get => _detailTitle;
            private set => SetProperty(ref _detailTitle, value);
        }

        private string _detailDuration = string.Empty;
        public string DetailDuration
        {
            get => _detailDuration;
            private set => SetProperty(ref _detailDuration, value);
        }

        // ── Detail: CLIENT stats ──────────────────────────────────────────────

        private string _detailRttAvg = "N/A";
        public string DetailRttAvg
        {
            get => _detailRttAvg;
            private set => SetProperty(ref _detailRttAvg, value);
        }

        private string _detailRttMax = "N/A";
        public string DetailRttMax
        {
            get => _detailRttMax;
            private set => SetProperty(ref _detailRttMax, value);
        }

        private string _detailJitterAvg = "N/A";
        public string DetailJitterAvg
        {
            get => _detailJitterAvg;
            private set => SetProperty(ref _detailJitterAvg, value);
        }

        private string _detailJitterMax = "N/A";
        public string DetailJitterMax
        {
            get => _detailJitterMax;
            private set => SetProperty(ref _detailJitterMax, value);
        }

        private string _detailDrops = "N/A";
        public string DetailDrops
        {
            get => _detailDrops;
            private set => SetProperty(ref _detailDrops, value);
        }

        private string _detailDropRate = "N/A";
        public string DetailDropRate
        {
            get => _detailDropRate;
            private set => SetProperty(ref _detailDropRate, value);
        }

        private string _detailDecodeAvg = "N/A";
        public string DetailDecodeAvg
        {
            get => _detailDecodeAvg;
            private set => SetProperty(ref _detailDecodeAvg, value);
        }

        private string _detailBitrateAvg = "N/A";
        public string DetailBitrateAvg
        {
            get => _detailBitrateAvg;
            private set => SetProperty(ref _detailBitrateAvg, value);
        }

        // ── Detail: HOST stats ────────────────────────────────────────────────

        private string _detailHostGpu = "N/A";
        public string DetailHostGpu
        {
            get => _detailHostGpu;
            private set => SetProperty(ref _detailHostGpu, value);
        }

        private string _detailHostEncoder = "N/A";
        public string DetailHostEncoder
        {
            get => _detailHostEncoder;
            private set => SetProperty(ref _detailHostEncoder, value);
        }

        private string _detailHostTemp = "N/A";
        public string DetailHostTemp
        {
            get => _detailHostTemp;
            private set => SetProperty(ref _detailHostTemp, value);
        }

        private string _detailHostCpu = "N/A";
        public string DetailHostCpu
        {
            get => _detailHostCpu;
            private set => SetProperty(ref _detailHostCpu, value);
        }

        private string _detailHostNetTx = "N/A";
        public string DetailHostNetTx
        {
            get => _detailHostNetTx;
            private set => SetProperty(ref _detailHostNetTx, value);
        }

        private string _detailHostLatency = "N/A";
        public string DetailHostLatency
        {
            get => _detailHostLatency;
            private set => SetProperty(ref _detailHostLatency, value);
        }

        // Secondary (peak / max) values for the HOST box — rendered dimmer next to the primary.
        private string _detailHostGpuSec = "";
        public string DetailHostGpuSec { get => _detailHostGpuSec; private set => SetProperty(ref _detailHostGpuSec, value); }
        private string _detailHostEncoderSec = "";
        public string DetailHostEncoderSec { get => _detailHostEncoderSec; private set => SetProperty(ref _detailHostEncoderSec, value); }
        private string _detailHostTempSec = "";
        public string DetailHostTempSec { get => _detailHostTempSec; private set => SetProperty(ref _detailHostTempSec, value); }
        private string _detailHostCpuSec = "";
        public string DetailHostCpuSec { get => _detailHostCpuSec; private set => SetProperty(ref _detailHostCpuSec, value); }
        private string _detailHostLatencySec = "";
        public string DetailHostLatencySec { get => _detailHostLatencySec; private set => SetProperty(ref _detailHostLatencySec, value); }

        private bool _hasHostStats;
        public bool HasHostStats
        {
            get => _hasHostStats;
            private set => SetProperty(ref _hasHostStats, value);
        }

        // ── Detail: sparkline series ──────────────────────────────────────────

        private bool _hasChartData;
        public bool HasChartData
        {
            get => _hasChartData;
            private set => SetProperty(ref _hasChartData, value);
        }

        private IReadOnlyList<float>? _rttSeries;
        public IReadOnlyList<float>? RttSeries
        {
            get => _rttSeries;
            private set => SetProperty(ref _rttSeries, value);
        }

        private IReadOnlyList<float>? _dropsSeries;
        public IReadOnlyList<float>? DropsSeries
        {
            get => _dropsSeries;
            private set => SetProperty(ref _dropsSeries, value);
        }

        private IReadOnlyList<float>? _bitrateSeries;
        public IReadOnlyList<float>? BitrateSeries
        {
            get => _bitrateSeries;
            private set => SetProperty(ref _bitrateSeries, value);
        }

        private IReadOnlyList<float>? _decodeSeries;
        public IReadOnlyList<float>? DecodeSeries
        {
            get => _decodeSeries;
            private set => SetProperty(ref _decodeSeries, value);
        }

        private IReadOnlyList<float>? _hostLatencySeries;
        public IReadOnlyList<float>? HostLatencySeries
        {
            get => _hostLatencySeries;
            private set => SetProperty(ref _hostLatencySeries, value);
        }

        // Host compute, overlaid as one multi-line chart (GPU / Encoder / CPU).
        private IReadOnlyList<SparklineSeries>? _hostComputeLines;
        public IReadOnlyList<SparklineSeries>? HostComputeLines
        {
            get => _hostComputeLines;
            private set => SetProperty(ref _hostComputeLines, value);
        }

        private bool _hasHostComputeChart;
        public bool HasHostComputeChart
        {
            get => _hasHostComputeChart;
            private set => SetProperty(ref _hasHostComputeChart, value);
        }

        // Per-chart height in the detail overlay. The charts are stacked in a single
        // scrollable column; this grows with the window height (set from the view's
        // SizeChanged) so charts stay readable from the minimum window up to 4K.
        private double _detailChartHeight = 185;
        public double DetailChartHeight
        {
            get => _detailChartHeight;
            set
            {
                if (SetProperty(ref _detailChartHeight, value))
                    foreach (var c in CompareCharts) c.Height = value;
            }
        }

        // ── Fullscreen chart ──────────────────────────────────────────────────

        private bool _isChartFullscreen;
        public bool IsChartFullscreen
        {
            get => _isChartFullscreen;
            private set => SetProperty(ref _isChartFullscreen, value);
        }

        private string _fullscreenChartTitle = string.Empty;
        public string FullscreenChartTitle
        {
            get => _fullscreenChartTitle;
            private set => SetProperty(ref _fullscreenChartTitle, value);
        }

        private IReadOnlyList<float>? _fullscreenChartData;
        public IReadOnlyList<float>? FullscreenChartData
        {
            get => _fullscreenChartData;
            private set => SetProperty(ref _fullscreenChartData, value);
        }

        private Color _fullscreenLineColor = Color.FromArgb(0xFF, 0x00, 0xB4, 0xD8);
        public Color FullscreenLineColor
        {
            get => _fullscreenLineColor;
            private set => SetProperty(ref _fullscreenLineColor, value);
        }

        // Multi-line variant (e.g. "Host compute %"): when set, SparklineControl.LinesData
        // takes precedence over the single FullscreenChartData line. Cleared on a single-line
        // open so a prior multi-line chart never leaks into the next fullscreen.
        private IReadOnlyList<SparklineSeries>? _fullscreenChartLines;
        public IReadOnlyList<SparklineSeries>? FullscreenChartLines
        {
            get => _fullscreenChartLines;
            private set => SetProperty(ref _fullscreenChartLines, value);
        }

        // ── Detail: game covers ───────────────────────────────────────────────

        public ObservableCollection<SessionGameCover> DetailGameCovers { get; } = new();

        private bool _hasDetailGameCovers;
        public bool HasDetailGameCovers
        {
            get => _hasDetailGameCovers;
            private set => SetProperty(ref _hasDetailGameCovers, value);
        }

        // (HasNoDetailGames / HasDetailGamesSection lived here to drive the detail overlay's
        //  GAMES box. That box went away in 7.4.0 when the covers moved into the header, and
        //  the two properties have been written but never read since.)

        // ── Public API ────────────────────────────────────────────────────────

        public void Load()
        {
            var list = SessionLogger.Load();
            Sessions.Clear();
            foreach (var s in list)
                Sessions.Add(s);
            HasSessions = Sessions.Count > 0;
        }

        /// <summary>
        /// Clears session history within a time window (browser-style). A null window
        /// clears everything. The active session, if any, is preserved.
        /// </summary>
        public void ClearHistory(TimeSpan? window)
        {
            DateTime cutoff = window.HasValue ? DateTime.Now - window.Value : DateTime.MinValue;
            SessionLogger.ClearSince(cutoff);
            Load();
        }

        public void DeleteSession(SessionEntry entry)
        {
            try
            {
                var sessions = SessionLogger.Load();
                int removed = sessions.RemoveAll(s => s.Id == entry.Id);
                if (removed > 0)
                {
                    SessionLogger.SavePublic(sessions);
                    Sessions.Remove(entry);
                    HasSessions = Sessions.Count > 0;
                }
            }
            catch { }
        }

        public void OpenDetail(SessionEntry entry)
        {
            if (entry.Grade == null) return;
            SelectedSession = entry;
            IsDetailVisible = true;
        }

        public void CloseDetail()
        {
            IsDetailVisible = false;
            SelectedSession = null;
            DetailGameCovers.Clear();
            HasDetailGameCovers  = false;
        }

        public async Task LoadDetailCoversAsync()
        {
            DetailGameCovers.Clear();
            HasDetailGameCovers   = false;

            var s = _selectedSession;
            if (s?.GamesDetected == null) return; // pre-feature session — nothing to show

            if (s.GamesDetected.Count == 0)
                return;

            HasDetailGameCovers = true;
            await LoadCoversForAsync(s, DetailGameCovers);
        }

        /// <summary>
        /// Populates <paramref name="target"/> with the detected-game covers of a session.
        /// Name-only entries appear immediately; images load asynchronously, snapshot-first
        /// then live GameLibraryState fallback. Shared by Session Detail and Compare.
        /// </summary>
        private static async Task LoadCoversForAsync(SessionEntry s, ObservableCollection<SessionGameCover> target)
        {
            target.Clear();
            if (s.GamesDetected == null || s.GamesDetected.Count == 0) return;

            foreach (var name in s.GamesDetected)
                target.Add(new SessionGameCover(name));

            var gameMap = GameLibraryState.Current.Games
                .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < s.GamesDetected.Count; i++)
            {
                string? path = null;
                if (s.GamesDetectedCoverPaths?.TryGetValue(s.GamesDetected[i], out var snap) == true
                    && File.Exists(snap))
                {
                    path = snap;
                }
                else if (gameMap.TryGetValue(s.GamesDetected[i], out var entry))
                {
                    path = entry.CoverImagePath;
                }
                if (path == null) continue;
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var bmp  = new BitmapImage { DecodePixelWidth = 134 };
                    using var stream = await file.OpenReadAsync();
                    await bmp.SetSourceAsync(stream);
                    target[i].CoverImage = bmp;
                }
                catch { /* non-fatal */ }
            }
        }

        public void OpenFullscreenChart(string title, IReadOnlyList<float>? data, Color lineColor)
        {
            if (data == null || data.Count < 2) return;
            FullscreenChartLines = null;          // single-line: drop any prior multi-line set
            FullscreenChartTitle = title;
            FullscreenChartData  = data;
            FullscreenLineColor  = lineColor;
            IsChartFullscreen    = true;
        }

        public void OpenFullscreenChart(string title, IReadOnlyList<SparklineSeries>? lines)
        {
            if (lines == null || lines.Count == 0) return;
            FullscreenChartData  = null;          // multi-line: LinesData takes precedence
            FullscreenChartTitle = title;
            FullscreenChartLines = lines;
            IsChartFullscreen    = true;
        }

        public void CloseFullscreenChart()
        {
            IsChartFullscreen = false;
        }

        // ── Compare ───────────────────────────────────────────────────────────

        // Reference colours: Session 1 = cyan, Session 2 = amber (equal-weight, neutral).
        public string CompareColorAHex => "#FF26C6DA";
        public string CompareColorBHex => "#FFFFA726";
        private static readonly Color CompareColorA = Color.FromArgb(0xFF, 0x26, 0xC6, 0xDA);
        private static readonly Color CompareColorB = Color.FromArgb(0xFF, 0xFF, 0xA7, 0x26);
        private const string GreenHex   = "#FF4ade80";
        private const string RedHex     = "#FFEF4444";
        private const string NeutralHex = "#FF908C88";

        private enum Dir { LowerBetter, HigherBetter, Neutral }

        // Full candidate set, plus the two per-dropdown lists: A excludes whatever B
        // has selected and vice versa, so the same session can't be picked twice.
        private readonly List<SessionEntry> _allCandidates = new();
        public ObservableCollection<SessionEntry> CompareCandidatesA { get; } = new();
        public ObservableCollection<SessionEntry> CompareCandidatesB { get; } = new();
        public ObservableCollection<CompareMetric> CompareClientMetrics { get; } = new();
        public ObservableCollection<CompareMetric> CompareHostMetrics { get; } = new();
        public ObservableCollection<CompareChart> CompareCharts { get; } = new();
        public ObservableCollection<SessionGameCover> CompareCoversA { get; } = new();
        public ObservableCollection<SessionGameCover> CompareCoversB { get; } = new();

        private bool _isCompareVisible;
        public bool IsCompareVisible
        {
            get => _isCompareVisible;
            private set => SetProperty(ref _isCompareVisible, value);
        }

        private SessionEntry? _compareA;
        public SessionEntry? CompareA
        {
            get => _compareA;
            set => SetProperty(ref _compareA, value);
        }

        private SessionEntry? _compareB;
        public SessionEntry? CompareB
        {
            get => _compareB;
            set => SetProperty(ref _compareB, value);
        }

        private bool _hasCompareData;
        public bool HasCompareData
        {
            get => _hasCompareData;
            private set => SetProperty(ref _hasCompareData, value);
        }

        private bool _hasCompareCoversA;
        public bool HasCompareCoversA
        {
            get => _hasCompareCoversA;
            private set => SetProperty(ref _hasCompareCoversA, value);
        }

        private bool _hasCompareCoversB;
        public bool HasCompareCoversB
        {
            get => _hasCompareCoversB;
            private set => SetProperty(ref _hasCompareCoversB, value);
        }

        private bool _hasCompareGames;
        public bool HasCompareGames
        {
            get => _hasCompareGames;
            private set => SetProperty(ref _hasCompareGames, value);
        }

        private bool _hasTwoCandidates;
        public bool HasTwoCandidates
        {
            get => _hasTwoCandidates;
            private set => SetProperty(ref _hasTwoCandidates, value);
        }

        public void OpenCompare()
        {
            // Only sessions with telemetry can be compared.
            _allCandidates.Clear();
            _allCandidates.AddRange(Sessions.Where(s => s.QualityStats != null));
            HasTwoCandidates = _allCandidates.Count >= 2;

            // Pre-fill both lists fully so the SelectedItem bindings resolve before we set
            // the selections (binding to an item absent from the source would null it).
            CompareCandidatesA.Clear();
            CompareCandidatesB.Clear();
            foreach (var s in _allCandidates) { CompareCandidatesA.Add(s); CompareCandidatesB.Add(s); }

            CompareA = _allCandidates.Count > 0 ? _allCandidates[0] : null;
            CompareB = _allCandidates.Count > 1 ? _allCandidates[1] : null;

            ReconcileCandidateLists();
            RebuildComparison();
            IsCompareVisible = true;
        }

        /// <summary>Called from the view when either dropdown selection changes.</summary>
        public void OnCompareSelectionChanged()
        {
            ReconcileCandidateLists();
            RebuildComparison();
        }

        /// <summary>
        /// Brings each dropdown's list to "all candidates except the other dropdown's
        /// selection", via minimal add/remove that preserves order and never touches the
        /// item that's currently selected — so this fires no reentrant SelectionChanged.
        /// </summary>
        private void ReconcileCandidateLists()
        {
            Reconcile(CompareCandidatesA, _compareB);
            Reconcile(CompareCandidatesB, _compareA);
        }

        private void Reconcile(ObservableCollection<SessionEntry> list, SessionEntry? exclude)
        {
            // Drop the excluded item (and anything stale).
            for (int i = list.Count - 1; i >= 0; i--)
                if (ReferenceEquals(list[i], exclude) || !_allCandidates.Contains(list[i]))
                    list.RemoveAt(i);

            // Insert any missing item at its position in the full ordering.
            int idx = 0;
            foreach (var s in _allCandidates)
            {
                if (ReferenceEquals(s, exclude)) continue;
                if (idx < list.Count && ReferenceEquals(list[idx], s)) { idx++; continue; }
                list.Insert(idx, s);
                idx++;
            }
        }

        public void CloseCompare()
        {
            IsCompareVisible = false;
            CompareClientMetrics.Clear();
            CompareHostMetrics.Clear();
            CompareCharts.Clear();
            CompareCoversA.Clear();
            CompareCoversB.Clear();
            CompareCandidatesA.Clear();
            CompareCandidatesB.Clear();
            _allCandidates.Clear();
        }

        public void RebuildComparison()
        {
            CompareClientMetrics.Clear();
            CompareHostMetrics.Clear();
            CompareCharts.Clear();

            var a = _compareA?.QualityStats;
            var b = _compareB?.QualityStats;
            HasCompareData = a != null && b != null;
            if (!HasCompareData) return;

            // CLIENT metrics
            CompareClientMetrics.Add(M("RTT avg",   a!.RttAvgMs,    b!.RttAvgMs,    1, " ms",   Dir.LowerBetter,  a.RttAvgMs    > 0, b.RttAvgMs    > 0));
            CompareClientMetrics.Add(M("RTT max",   a.RttMaxMs,     b.RttMaxMs,     1, " ms",   Dir.LowerBetter,  a.RttMaxMs    > 0, b.RttMaxMs    > 0));
            CompareClientMetrics.Add(M("Jitter avg",a.JitterAvgMs,  b.JitterAvgMs,  1, " ms",   Dir.LowerBetter,  a.JitterAvgMs > 0, b.JitterAvgMs > 0));
            CompareClientMetrics.Add(M("Drops",     a.TotalDrops,   b.TotalDrops,   0, "",      Dir.LowerBetter));
            CompareClientMetrics.Add(M("Drop rate", a.DropRatePct,  b.DropRatePct,  2, "%",     Dir.LowerBetter));
            CompareClientMetrics.Add(M("Decode avg",a.DecodeAvgMs,  b.DecodeAvgMs,  1, " ms",   Dir.LowerBetter));
            CompareClientMetrics.Add(M("Bitrate avg",a.BitrateAvgMbps, b.BitrateAvgMbps, 1, " Mbps", Dir.HigherBetter));

            // HOST metrics (load telemetry is neutral; only frame latency is quality-directional)
            CompareHostMetrics.Add(M("GPU avg",      a.HostGpuAvg,     b.HostGpuAvg,     0, "%",    Dir.Neutral, a.HostGpuAvg    >= 0, b.HostGpuAvg    >= 0));
            CompareHostMetrics.Add(M("Encoder avg",  a.HostGpuEncAvg,  b.HostGpuEncAvg,  0, "%",    Dir.Neutral, a.HostGpuEncAvg >= 0, b.HostGpuEncAvg >= 0));
            CompareHostMetrics.Add(M("GPU Temp avg", a.HostGpuTempAvg, b.HostGpuTempAvg, 0, " °C",  Dir.Neutral, a.HostGpuTempAvg>= 0, b.HostGpuTempAvg>= 0));
            CompareHostMetrics.Add(M("CPU avg",      a.HostCpuAvg,     b.HostCpuAvg,     0, "%",    Dir.Neutral, a.HostCpuAvg    >= 0, b.HostCpuAvg    >= 0));
            CompareHostMetrics.Add(M("Net TX avg",   a.HostNetTxAvg,   b.HostNetTxAvg,   0, " Mbps",Dir.Neutral, a.HostNetTxAvg  >= 0, b.HostNetTxAvg  >= 0));
            CompareHostMetrics.Add(M("Frame latency",a.HostLatencyAvgMs, b.HostLatencyAvgMs, 1, " ms", Dir.LowerBetter, a.HostLatencyAvgMs >= 0, b.HostLatencyAvgMs >= 0));

            // Charts — one per metric, each holding both sessions (only added if data exists)
            AddChart("RTT ms",                _compareA!.RttTimeSeries,         _compareB!.RttTimeSeries);
            AddChart("Frame Drops",           _compareA.DropsTimeSeries,        _compareB.DropsTimeSeries);
            AddChart("Bitrate Mbps",          _compareA.BitrateTimeSeries,      _compareB.BitrateTimeSeries);
            AddChart("Decode ms",             _compareA.DecodeTimeSeries,       _compareB.DecodeTimeSeries);
            AddChart("Host frame latency ms", _compareA.HostLatencyTimeSeries,  _compareB.HostLatencyTimeSeries);
            AddChart("Host GPU %",            _compareA.HostGpuTimeSeries,      _compareB.HostGpuTimeSeries);
            AddChart("Host Encoder %",        _compareA.HostEncTimeSeries,      _compareB.HostEncTimeSeries);
            AddChart("Host CPU %",            _compareA.HostCpuTimeSeries,      _compareB.HostCpuTimeSeries);
        }

        public async Task LoadCompareCoversAsync()
        {
            HasCompareCoversA = _compareA?.GamesDetected is { Count: > 0 };
            HasCompareCoversB = _compareB?.GamesDetected is { Count: > 0 };
            HasCompareGames   = HasCompareCoversA || HasCompareCoversB;
            CompareCoversA.Clear();
            CompareCoversB.Clear();
            if (_compareA != null) await LoadCoversForAsync(_compareA, CompareCoversA);
            if (_compareB != null) await LoadCoversForAsync(_compareB, CompareCoversB);
        }

        private void AddChart(string title, System.Collections.Generic.List<float>? a, System.Collections.Generic.List<float>? b)
        {
            var lines = new List<SparklineSeries>();
            if (a is { Count: >= 2 }) lines.Add(new SparklineSeries { Label = "Session 1", Color = CompareColorA, Data = a });
            if (b is { Count: >= 2 }) lines.Add(new SparklineSeries { Label = "Session 2", Color = CompareColorB, Data = b });
            if (lines.Count > 0)
                CompareCharts.Add(new CompareChart { Title = title, Lines = lines, Height = _detailChartHeight });
        }

        private static CompareMetric M(string label, float a, float b, int dec, string unit, Dir dir, bool aOk = true, bool bOk = true)
        {
            string Fmt(float v) => v.ToString("F" + dec, CultureInfo.InvariantCulture) + unit;
            string va = aOk ? Fmt(a) : "N/A";
            string vb = bOk ? Fmt(b) : "N/A";

            string delta, color;
            if (!aOk || !bOk)
            {
                delta = "—";
                color = NeutralHex;
            }
            else
            {
                float d   = b - a;
                float eps = 0.5f * (float)System.Math.Pow(10, -dec);
                string sign = d > 0 ? "+" : (d < 0 ? "−" : "");   // real minus sign
                delta = sign + System.Math.Abs(d).ToString("F" + dec, CultureInfo.InvariantCulture) + unit;
                if (dir == Dir.Neutral || System.Math.Abs(d) < eps)
                    color = NeutralHex;
                else
                {
                    bool improved = dir == Dir.LowerBetter ? d < 0 : d > 0;
                    color = improved ? GreenHex : RedHex;
                }
            }
            return new CompareMetric { Label = label, ValueA = va, ValueB = vb, Delta = delta, DeltaColorHex = color };
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void RefreshDetailProperties()
        {
            var s = _selectedSession;
            if (s == null)
            {
                GradeLabel = string.Empty;
                GradeColorHex = "#FF808080";
                DetailTitle = string.Empty;
                DetailDuration = string.Empty;
                DetailHeaderSubtitle = string.Empty;
                ClearClientStats();
                ClearHostStats();
                RttSeries = null;
                DropsSeries = null;
                BitrateSeries = null;
                DecodeSeries = null;
                HostLatencySeries = null;
                HostComputeLines = null;
                HasHostComputeChart = false;
                HasChartData = false;
                return;
            }

            DetailTitle    = s.StartTimeDisplay;
            DetailDuration = s.TelemetryDurationDisplay;
            DetailHeaderSubtitle = $"{s.StartTimeDisplay}  ·  {s.TelemetryDurationDisplay}";

            // Grade
            (GradeLabel, GradeColorHex) = s.Grade switch
            {
                QualityGrade.High   => ("Excellent", "#FF4ade80"),
                QualityGrade.Medium => ("Good",      "#FFFFC107"),
                QualityGrade.Low    => ("Poor",       "#FFDC4632"),
                _                   => ("—",          "#FF808080")
            };

            var q = s.QualityStats;
            if (q != null)
            {
                // Unified format: primary value + a dimmer "max/peak/count" secondary.
                DetailRttAvg    = $"{q.RttAvgMs:F1} ms";
                DetailRttMax    = $"max {q.RttMaxMs:F1} ms";
                DetailJitterAvg = q.JitterAvgMs > 0 ? $"{q.JitterAvgMs:F1} ms" : "N/A";
                DetailJitterMax = q.JitterMaxMs > 0 ? $"max {q.JitterMaxMs:F1} ms" : "";
                DetailDropRate  = $"{q.DropRatePct:F2} %";
                DetailDrops     = $"{q.TotalDrops} frames";
                DetailDecodeAvg = $"{q.DecodeAvgMs:F1} ms";
                DetailBitrateAvg = $"{q.BitrateAvgMbps:F1} Mbps";

                // HOST
                HasHostStats = q.HostGpuAvg >= 0 || q.HostCpuAvg >= 0;
                DetailHostGpu        = q.HostGpuAvg     >= 0 ? $"{q.HostGpuAvg} %"      : "N/A";
                DetailHostGpuSec     = q.HostGpuAvg     >= 0 ? $"peak {q.HostGpuPeak} %"    : "";
                DetailHostEncoder    = q.HostGpuEncAvg  >= 0 ? $"{q.HostGpuEncAvg} %"   : "N/A";
                DetailHostEncoderSec = q.HostGpuEncAvg  >= 0 ? $"peak {q.HostGpuEncPeak} %" : "";
                DetailHostTemp       = q.HostGpuTempAvg >= 0 ? $"{q.HostGpuTempAvg} °C" : "N/A";
                DetailHostTempSec    = q.HostGpuTempAvg >= 0 ? $"max {q.HostGpuTempMax} °C"  : "";
                DetailHostCpu        = q.HostCpuAvg     >= 0 ? $"{q.HostCpuAvg} %"      : "N/A";
                DetailHostCpuSec     = q.HostCpuAvg     >= 0 ? $"peak {q.HostCpuPeak} %"    : "";
                DetailHostNetTx      = q.HostNetTxAvg   >= 0 ? $"{q.HostNetTxAvg} Mbps" : "N/A";
                DetailHostLatency    = q.HostLatencyAvgMs >= 0 ? $"{q.HostLatencyAvgMs:F1} ms" : "N/A";
                DetailHostLatencySec = q.HostLatencyAvgMs >= 0 ? $"max {q.HostLatencyMaxMs:F1} ms" : "";
            }
            else
            {
                ClearClientStats();
                ClearHostStats();
            }

            RttSeries     = s.RttTimeSeries     as IReadOnlyList<float>;
            DropsSeries   = s.DropsTimeSeries   as IReadOnlyList<float>;
            BitrateSeries = s.BitrateTimeSeries as IReadOnlyList<float>;
            DecodeSeries  = s.DecodeTimeSeries  as IReadOnlyList<float>;
            HostLatencySeries = s.HostLatencyTimeSeries as IReadOnlyList<float>;

            // Host compute: overlay whichever of GPU / Encoder / CPU are present.
            var computeLines = new List<SparklineSeries>();
            if (s.HostGpuTimeSeries is { Count: >= 2 })
                computeLines.Add(new SparklineSeries { Label = "GPU", Color = Color.FromArgb(0xFF, 0x42, 0xA5, 0xF5), Data = s.HostGpuTimeSeries });
            if (s.HostEncTimeSeries is { Count: >= 2 })
                computeLines.Add(new SparklineSeries { Label = "Encoder", Color = Color.FromArgb(0xFF, 0xFF, 0xA7, 0x26), Data = s.HostEncTimeSeries });
            if (s.HostCpuTimeSeries is { Count: >= 2 })
                computeLines.Add(new SparklineSeries { Label = "CPU", Color = Color.FromArgb(0xFF, 0xAB, 0x47, 0xBC), Data = s.HostCpuTimeSeries });
            HostComputeLines    = computeLines.Count > 0 ? computeLines : null;
            HasHostComputeChart = computeLines.Count > 0;

            HasChartData  = _rttSeries != null || _dropsSeries != null
                         || _bitrateSeries != null || _decodeSeries != null
                         || _hostLatencySeries != null || _hasHostComputeChart;
        }

        private void ClearClientStats()
        {
            DetailRttAvg = DetailRttMax = DetailJitterAvg = DetailJitterMax =
            DetailDrops  = DetailDropRate = DetailDecodeAvg = DetailBitrateAvg = "N/A";
        }

        private void ClearHostStats()
        {
            HasHostStats = false;
            DetailHostGpu = DetailHostEncoder = DetailHostTemp =
            DetailHostCpu = DetailHostNetTx = DetailHostLatency = "N/A";
            DetailHostGpuSec = DetailHostEncoderSec = DetailHostTempSec =
            DetailHostCpuSec = DetailHostLatencySec = "";
        }
    }
}
