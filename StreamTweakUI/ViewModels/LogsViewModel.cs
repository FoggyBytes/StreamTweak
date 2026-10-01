using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    /// <summary>One metric row in the Compare view: the two sessions' values + a delta.</summary>
    public sealed class CompareMetric
    {
        public string Label      { get; set; } = "";
        public string ValueA     { get; set; } = "";
        public string ValueB     { get; set; } = "";
        public string Delta      { get; set; } = "";
        public string DeltaFgHex { get; set; } = "#C8CFCB";
        public string DeltaBgHex { get; set; } = "#0FFFFFFF";
    }

    /// <summary>One of the four checks behind a session's grade, as the Verdict card lists them.</summary>
    public sealed class CriterionRow
    {
        public string Name          { get; init; } = "";
        public string ValueText     { get; init; } = "";
        public string ThresholdText { get; init; } = "";
        public string GradeLabel    { get; init; } = "";
        public string GradeFgHex    { get; init; } = "";
        public string GradeBgHex    { get; init; } = "";
        public string GradeBorderHex { get; init; } = "";
    }

    /// <summary>
    /// Column visibility shared by every session row. The rows are DataTemplate items, so they
    /// cannot see the page; they bind to this one object instead, and the page flips it when
    /// the list gets too narrow for the metric columns (master–detail on a 1080p handheld).
    /// </summary>
    public sealed class SessionRowLayout : ViewModelBase
    {
        private Visibility _metrics = Visibility.Visible;
        public Visibility MetricsVisibility { get => _metrics; set => SetProperty(ref _metrics, value); }

        private Visibility _games = Visibility.Visible;
        public Visibility GamesVisibility { get => _games; set => SetProperty(ref _games, value); }
    }

    /// <summary>A row of the Sessions list; the first row of each day also carries the day header.</summary>
    public sealed class SessionRow : ViewModelBase
    {
        public SessionEntry Entry { get; init; } = null!;
        public string Id => Entry.Id;
        public SessionRowLayout Layout { get; init; } = null!;

        public bool ShowDay { get; init; }
        public Visibility DayVisibility => ShowDay ? Visibility.Visible : Visibility.Collapsed;
        public string DayText    { get; init; } = "";
        public string DaySubText { get; init; } = "";

        public string WhenText  { get; init; } = "";
        public string SubText   { get; init; } = "";
        public IReadOnlyList<GameCoverItem> Covers { get; init; } = Array.Empty<GameCoverItem>();
        public string GamesText { get; init; } = "";
        public string RttText   { get; init; } = "—";
        public string HostLatText { get; init; } = "—";
        public string DropsText { get; init; } = "—";

        public string GradeLabel     { get; init; } = "";
        public string GradeFgHex     { get; init; } = "";
        public string GradeBgHex     { get; init; } = "";
        public string GradeBorderHex { get; init; } = "";
        public string StripeHex      { get; init; } = "";

        public bool IsLive { get; init; }
        public Visibility PickVisibility => Entry.QualityStats != null && !IsLive ? Visibility.Visible : Visibility.Collapsed;

        internal Action<SessionRow>? CheckedChanged;
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (SetProperty(ref _isChecked, value)) CheckedChanged?.Invoke(this); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(RowBackgroundHex)); }
        }
        public string RowBackgroundHex => IsSelected ? "#144ade80" : IsLive ? "#0C4ade80" : "#00FFFFFF";
    }

    public sealed class LogsViewModel : ViewModelBase
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ═════════════════════════════ LIST ═════════════════════════════════

        private readonly List<SessionEntry> _all = new();
        public ObservableCollection<SessionRow> Rows { get; } = new();
        public SessionRowLayout RowLayout { get; } = new();

        private bool _hasSessions;
        public bool HasSessions { get => _hasSessions; private set => SetProperty(ref _hasSessions, value); }

        private bool _hasRows;
        public bool HasRows { get => _hasRows; private set => SetProperty(ref _hasRows, value); }

        private string _emptyText = "";
        public string EmptyText { get => _emptyText; private set => SetProperty(ref _emptyText, value); }

        public string[] PeriodOptions { get; } = { "Last 7 days", "Last 30 days", "Last 90 days", "All time" };

        private int _periodIndex = 3;
        public int PeriodIndex
        {
            get => _periodIndex;
            set { if (SetProperty(ref _periodIndex, value)) Rebuild(); }
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value ?? "")) RebuildRows(); }
        }

        /// <summary>-1 = all, otherwise the <see cref="QualityGrade"/> value to keep.</summary>
        private int _gradeFilter = -1;
        public int GradeFilter
        {
            get => _gradeFilter;
            set { if (SetProperty(ref _gradeFilter, value)) RebuildRows(); }
        }

        // ── Summary strip ─────────────────────────────────────────────────────

        private string _sumSessions = "0", _sumHours = "0", _sumRtt = "—", _sumHostLat = "—", _periodLabel = "";
        public string SummarySessions { get => _sumSessions; private set => SetProperty(ref _sumSessions, value); }
        public string SummaryHours    { get => _sumHours;    private set => SetProperty(ref _sumHours, value); }
        public string SummaryRtt      { get => _sumRtt;      private set => SetProperty(ref _sumRtt, value); }
        public string SummaryHostLat  { get => _sumHostLat;  private set => SetProperty(ref _sumHostLat, value); }
        public string PeriodLabel     { get => _periodLabel; private set => SetProperty(ref _periodLabel, value); }

        private int _cntExcellent, _cntGood, _cntPoor;
        public int CountExcellent { get => _cntExcellent; private set => SetProperty(ref _cntExcellent, value); }
        public int CountGood      { get => _cntGood;      private set => SetProperty(ref _cntGood, value); }
        public int CountPoor      { get => _cntPoor;      private set => SetProperty(ref _cntPoor, value); }

        private string _chipAll = "All", _chipExcellent = "Excellent", _chipGood = "Good", _chipPoor = "Poor";
        public string ChipAllText       { get => _chipAll;       private set => SetProperty(ref _chipAll, value); }
        public string ChipExcellentText { get => _chipExcellent; private set => SetProperty(ref _chipExcellent, value); }
        public string ChipGoodText      { get => _chipGood;      private set => SetProperty(ref _chipGood, value); }
        public string ChipPoorText      { get => _chipPoor;      private set => SetProperty(ref _chipPoor, value); }

        // ── Compare picks (ticked rows) ───────────────────────────────────────

        private readonly List<SessionEntry> _picked = new();

        private bool _isCompareBarVisible;
        public bool IsCompareBarVisible { get => _isCompareBarVisible; private set => SetProperty(ref _isCompareBarVisible, value); }

        private bool _canComparePicked;
        public bool CanComparePicked { get => _canComparePicked; private set => SetProperty(ref _canComparePicked, value); }

        private string _compareBarText = "";
        public string CompareBarText { get => _compareBarText; private set => SetProperty(ref _compareBarText, value); }

        // ── Load / filter ─────────────────────────────────────────────────────

        public void Load()
        {
            _all.Clear();
            _all.AddRange(SessionLogger.Load().OrderByDescending(s => s.StartTime));
            HasSessions = _all.Count > 0;
            // Picks that no longer exist (deleted, cleared) fall away.
            _picked.RemoveAll(p => _all.All(s => s.Id != p.Id));
            Rebuild();

            if (_selectedSession != null)
                SelectedSession = _all.FirstOrDefault(s => s.Id == _selectedSession.Id);
        }

        private IEnumerable<SessionEntry> InPeriod()
        {
            TimeSpan? window = _periodIndex switch
            {
                0 => TimeSpan.FromDays(7),
                1 => TimeSpan.FromDays(30),
                2 => TimeSpan.FromDays(90),
                _ => null,
            };
            if (window == null) return _all;
            var cutoff = DateTime.Now - window.Value;
            return _all.Where(s => s.StartTime >= cutoff);
        }

        private void Rebuild()
        {
            RebuildSummary();
            RebuildRows();
        }

        private void RebuildSummary()
        {
            var list = InPeriod().Where(s => s.EndTime != null).ToList();
            PeriodLabel = PeriodOptions[Math.Clamp(_periodIndex, 0, PeriodOptions.Length - 1)];
            SummarySessions = list.Count.ToString(Inv);
            double hours = list.Sum(s => (s.EndTime!.Value - s.StartTime).TotalHours);
            SummaryHours = hours >= 10 ? hours.ToString("0", Inv) : hours.ToString("0.#", Inv);

            var rtt = list.Where(s => s.QualityStats is { RttAvgMs: > 0 }).Select(s => s.QualityStats!.RttAvgMs).ToList();
            var hl  = list.Where(s => s.QualityStats is { HostLatencyAvgMs: >= 0 }).Select(s => s.QualityStats!.HostLatencyAvgMs).ToList();
            SummaryRtt     = rtt.Count > 0 ? rtt.Average().ToString("0.0", Inv) : "—";
            SummaryHostLat = hl.Count  > 0 ? hl.Average().ToString("0.0", Inv)  : "—";

            var graded = list.Where(s => !s.IsDebugSession).ToList();
            CountExcellent = graded.Count(s => s.Grade == QualityGrade.High);
            CountGood      = graded.Count(s => s.Grade == QualityGrade.Medium);
            CountPoor      = graded.Count(s => s.Grade == QualityGrade.Low);
            ChipAllText       = $"All  {list.Count}";
            ChipExcellentText = $"Excellent  {CountExcellent}";
            ChipGoodText      = $"Good  {CountGood}";
            ChipPoorText      = $"Poor  {CountPoor}";
        }

        private void RebuildRows()
        {
            string q = _searchText.Trim();
            var list = InPeriod().Where(s =>
                (_gradeFilter < 0 || (int?)s.Grade == _gradeFilter) &&
                (q.Length == 0 || (s.GamesDetected?.Any(g => g.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false)))
                .ToList();

            foreach (var r in Rows) r.CheckedChanged = null;
            Rows.Clear();

            var byDay = list.GroupBy(s => s.StartTime.Date).ToDictionary(g => g.Key, g => g.ToList());
            DateTime? lastDay = null;
            foreach (var s in list)
            {
                bool first = lastDay != s.StartTime.Date;
                lastDay = s.StartTime.Date;
                var day = byDay[s.StartTime.Date];
                var row = MakeRow(s, first, first ? DayTitle(s.StartTime) : "", first ? DaySub(day) : "");
                row.IsChecked = _picked.Any(p => p.Id == s.Id);
                row.IsSelected = _selectedSession?.Id == s.Id;
                row.CheckedChanged = OnRowChecked;
                Rows.Add(row);
            }

            HasRows = Rows.Count > 0;
            EmptyText = !HasSessions
                ? "No sessions yet. They appear here as soon as a client streams from this PC."
                : q.Length > 0 || _gradeFilter >= 0
                    ? "No sessions match this filter."
                    : $"No sessions in the period chosen ({PeriodLabel.ToLowerInvariant()}).";
            RefreshCompareBar();
        }

        private SessionRow MakeRow(SessionEntry s, bool showDay, string dayText, string daySub)
        {
            bool live = s.EndTime == null && s.Id == SessionLogger.ActiveSessionId;
            var q = s.QualityStats;

            (string label, string fg, string bg, string border) = s.IsDebugSession
                ? ("Debug", "#C8CFCB", "#0FFFFFFF", "#24FFFFFF")
                : live ? ("Live", "#4ade80", "#214ade80", "#594ade80")
                : GameStatsService.GradeColors(s.Grade);
            if (!live && !s.IsDebugSession && s.Grade is null or QualityGrade.NoData)
                label = q == null ? "No data" : "—";

            string end = live ? "now" : s.EndTime is { } e ? e.ToString("HH:mm", Inv) : "?";
            var parts = new List<string>();
            if (live) parts.Add("Live");
            else if (s.EndTime != null) parts.Add(FormatDuration(s.EndTime.Value - s.StartTime));
            int streams = s.StreamSpans?.Count ?? 0;
            if (streams > 1) parts.Add($"{streams} streams");
            if (s.EndReason == "Interrupted") parts.Add("interrupted");

            string games = s.GamesDetected is { Count: > 0 } g ? string.Join(", ", g)
                         : s.GamesDetected != null ? "Desktop" : "—";

            return new SessionRow
            {
                Entry = s,
                Layout = RowLayout,
                ShowDay = showDay, DayText = dayText, DaySubText = daySub,
                WhenText = $"{s.StartTime.ToString("HH:mm", Inv)} – {end}",
                SubText = string.Join(" · ", parts),
                Covers = s.GameCoversForDisplay,
                GamesText = games,
                RttText = q is { RttAvgMs: > 0 } ? q.RttAvgMs.ToString("0.0", Inv) : "—",
                HostLatText = q is { HostLatencyAvgMs: >= 0 } ? q.HostLatencyAvgMs.ToString("0.0", Inv) : "—",
                DropsText = q != null ? q.DropRatePct.ToString("0.00", Inv) : "—",
                GradeLabel = label, GradeFgHex = fg, GradeBgHex = bg, GradeBorderHex = border,
                StripeHex = live ? "#4ade80" : s.IsDebugSession ? "#646B67" : GameStatsService.GradeStripe(s.Grade),
                IsLive = live,
            };
        }

        private static string DayTitle(DateTime d)
        {
            string date = d.ToString(d.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", Inv);
            int days = (DateTime.Today - d.Date).Days;
            return days switch
            {
                0 => $"Today · {date}",
                1 => $"Yesterday · {date}",
                _ => $"{d.ToString("dddd", Inv)} {date}",
            };
        }

        private static string DaySub(List<SessionEntry> day)
        {
            double min = day.Where(s => s.EndTime != null).Sum(s => (s.EndTime!.Value - s.StartTime).TotalMinutes);
            string n = day.Count == 1 ? "1 session" : $"{day.Count} sessions";
            return min > 0 ? $"{n} · {GameStatsService.FormatMinutes(min)}" : n;
        }

        /// <summary>"2 h 05" / "45 min" / "50 s".</summary>
        public static string FormatDuration(TimeSpan d)
            => d.TotalMinutes >= 1 ? GameStatsService.FormatMinutes(d.TotalMinutes) : $"{Math.Max(0, (int)d.TotalSeconds)} s";

        // ── Compare picks ─────────────────────────────────────────────────────

        private void OnRowChecked(SessionRow row)
        {
            if (row.IsChecked)
            {
                if (_picked.All(p => p.Id != row.Id)) _picked.Add(row.Entry);
                // Two at most: ticking a third replaces the oldest pick.
                while (_picked.Count > 2)
                {
                    var drop = _picked[0];
                    _picked.RemoveAt(0);
                    var other = Rows.FirstOrDefault(r => r.Id == drop.Id);
                    if (other != null) { other.CheckedChanged = null; other.IsChecked = false; other.CheckedChanged = OnRowChecked; }
                }
            }
            else _picked.RemoveAll(p => p.Id == row.Id);
            RefreshCompareBar();
        }

        public void ClearPicks()
        {
            _picked.Clear();
            foreach (var r in Rows) { r.CheckedChanged = null; r.IsChecked = false; r.CheckedChanged = OnRowChecked; }
            RefreshCompareBar();
        }

        private void RefreshCompareBar()
        {
            IsCompareBarVisible = _picked.Count > 0;
            CanComparePicked = _picked.Count == 2;
            CompareBarText = _picked.Count == 1 ? "1 session selected · tick one more" : $"{_picked.Count} sessions selected";
        }

        // ── Maintenance ───────────────────────────────────────────────────────

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
                if (sessions.RemoveAll(s => s.Id == entry.Id) > 0)
                    SessionLogger.SavePublic(sessions);
            }
            catch { }
            if (_selectedSession?.Id == entry.Id) SelectedSession = null;
            _picked.RemoveAll(p => p.Id == entry.Id);
            Load();
        }

        // ═════════════════════════════ DETAIL ═══════════════════════════════

        private bool _isDetailVisible;
        /// <summary>Narrow windows: the detail covers the list. Wide windows show both.</summary>
        public bool IsDetailVisible { get => _isDetailVisible; private set => SetProperty(ref _isDetailVisible, value); }

        private SessionEntry? _selectedSession;
        public SessionEntry? SelectedSession
        {
            get => _selectedSession;
            private set
            {
                _selectedSession = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                RefreshDetail();
                foreach (var r in Rows) r.IsSelected = value != null && r.Id == value.Id;
            }
        }
        public bool HasSelection => _selectedSession != null;

        public void OpenDetail(SessionEntry entry)
        {
            SelectedSession = entry;
            IsDetailVisible = true;
        }

        public bool OpenDetailById(string id)
        {
            var s = _all.FirstOrDefault(x => x.Id == id);
            if (s == null) return false;
            // The filters could hide it: clear them so the list shows what the detail shows.
            if (!Rows.Any(r => r.Id == id))
            {
                _periodIndex = 3; OnPropertyChanged(nameof(PeriodIndex));
                _gradeFilter = -1; OnPropertyChanged(nameof(GradeFilter));
                _searchText = ""; OnPropertyChanged(nameof(SearchText));
                Rebuild();
            }
            OpenDetail(s);
            return true;
        }

        public void CloseDetail() => IsDetailVisible = false;

        // ── Detail: header ────────────────────────────────────────────────────

        private string _detailTitle = "", _detailSubtitle = "";
        public string DetailTitle    { get => _detailTitle;    private set => SetProperty(ref _detailTitle, value); }
        public string DetailSubtitle { get => _detailSubtitle; private set => SetProperty(ref _detailSubtitle, value); }

        private IReadOnlyList<GameCoverItem> _detailCovers = Array.Empty<GameCoverItem>();
        public IReadOnlyList<GameCoverItem> DetailCovers { get => _detailCovers; private set => SetProperty(ref _detailCovers, value); }

        private bool _hasDetailCovers;
        public bool HasDetailCovers { get => _hasDetailCovers; private set => SetProperty(ref _hasDetailCovers, value); }

        // ── Detail: verdict ───────────────────────────────────────────────────

        private string _verdictLabel = "", _verdictFg = "#C8CFCB", _verdictReason = "", _verdictNote = "";
        public string VerdictLabel  { get => _verdictLabel;  private set => SetProperty(ref _verdictLabel, value); }
        public string VerdictFgHex  { get => _verdictFg;     private set => SetProperty(ref _verdictFg, value); }
        public string VerdictReason { get => _verdictReason; private set => SetProperty(ref _verdictReason, value); }
        public string VerdictNote   { get => _verdictNote;   private set { SetProperty(ref _verdictNote, value); OnPropertyChanged(nameof(HasVerdictNote)); } }
        public bool HasVerdictNote => !string.IsNullOrEmpty(_verdictNote);

        public ObservableCollection<CriterionRow> Criteria { get; } = new();

        private bool _hasStats;
        public bool HasStats { get => _hasStats; private set => SetProperty(ref _hasStats, value); }

        // ── Detail: client ────────────────────────────────────────────────────

        private string _rtt = "—", _rttSub = "", _jit = "—", _jitSub = "", _drop = "—", _dropSub = "",
                       _br = "—", _brSub = "", _dec = "—", _decSub = "";
        public string DetailRtt       { get => _rtt;    private set => SetProperty(ref _rtt, value); }
        public string DetailRttSub    { get => _rttSub; private set => SetProperty(ref _rttSub, value); }
        public string DetailJitter    { get => _jit;    private set => SetProperty(ref _jit, value); }
        public string DetailJitterSub { get => _jitSub; private set => SetProperty(ref _jitSub, value); }
        public string DetailDrops     { get => _drop;   private set => SetProperty(ref _drop, value); }
        public string DetailDropsSub  { get => _dropSub; private set => SetProperty(ref _dropSub, value); }
        public string DetailBitrate   { get => _br;     private set => SetProperty(ref _br, value); }
        public string DetailBitrateSub { get => _brSub; private set => SetProperty(ref _brSub, value); }
        public string DetailDecode    { get => _dec;    private set => SetProperty(ref _dec, value); }
        public string DetailDecodeSub { get => _decSub; private set => SetProperty(ref _decSub, value); }

        // ── Detail: host ──────────────────────────────────────────────────────

        private string _hl = "—", _hlSub = "", _late = "—", _lateSub = "", _gpu = "—", _gpuSub = "",
                       _enc = "—", _encSub = "", _temp = "—", _tempSub = "", _cpu = "—", _cpuSub = "", _net = "—";
        public string DetailHostLat     { get => _hl;      private set => SetProperty(ref _hl, value); }
        public string DetailHostLatSub  { get => _hlSub;   private set => SetProperty(ref _hlSub, value); }
        public string DetailLate        { get => _late;    private set => SetProperty(ref _late, value); }
        public string DetailLateSub     { get => _lateSub; private set => SetProperty(ref _lateSub, value); }
        public string DetailGpu         { get => _gpu;     private set => SetProperty(ref _gpu, value); }
        public string DetailGpuSub      { get => _gpuSub;  private set => SetProperty(ref _gpuSub, value); }
        public string DetailEncoder     { get => _enc;     private set => SetProperty(ref _enc, value); }
        public string DetailEncoderSub  { get => _encSub;  private set => SetProperty(ref _encSub, value); }
        public string DetailTemp        { get => _temp;    private set => SetProperty(ref _temp, value); }
        public string DetailTempSub     { get => _tempSub; private set => SetProperty(ref _tempSub, value); }
        public string DetailCpu         { get => _cpu;     private set => SetProperty(ref _cpu, value); }
        public string DetailCpuSub      { get => _cpuSub;  private set => SetProperty(ref _cpuSub, value); }
        public string DetailNetTx       { get => _net;     private set => SetProperty(ref _net, value); }

        // 9.1.0 (§82): the codec(s) of the session, and the unit of the Encoder figure — empty when
        // the figure reads n/a because every stream bypassed the video-encode engine.
        private string _codec = "—", _codecSub = "", _encUnit = "%";
        public string DetailCodec       { get => _codec;    private set => SetProperty(ref _codec, value); }
        public string DetailCodecSub    { get => _codecSub; private set => SetProperty(ref _codecSub, value); }
        public string DetailEncoderUnit { get => _encUnit;  private set => SetProperty(ref _encUnit, value); }


        private void RefreshDetail()
        {
            var s = _selectedSession;
            Criteria.Clear();
            if (s == null)
            {
                DetailTitle = DetailSubtitle = VerdictLabel = VerdictReason = "";
                VerdictNote = "";
                DetailCovers = Array.Empty<GameCoverItem>();
                HasDetailCovers = false;
                HasStats = false;
                return;
            }

            var q = s.QualityStats;
            int fps = q?.TargetFps ?? 0;

            // ── Header
            DetailTitle = s.StartTime.ToString(s.StartTime.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", Inv);
            var sub = new List<string>();
            string end = s.EndTime is { } e ? e.ToString("HH:mm", Inv) : (s.Id == SessionLogger.ActiveSessionId ? "now" : "?");
            sub.Add($"{s.StartTime.ToString("HH:mm", Inv)} → {end}");
            if (s.EndTime != null) sub.Add(FormatDuration(s.EndTime.Value - s.StartTime));
            int streams = s.StreamSpans?.Count ?? 0;
            if (streams > 1) sub.Add($"{streams} streams, the client reconnected {(streams == 2 ? "once" : $"{streams - 1} times")}");
            else if (streams == 1) sub.Add("one stream");
            if (fps > 0) sub.Add($"{fps} fps");
            if (q is { TargetBitrateMbps: > 0 }) sub.Add($"{q.TargetBitrateMbps.ToString("0", Inv)} Mbps target");
            if (s.EndReason == "Interrupted") sub.Add("interrupted");
            if (s.IsDebugSession) sub.Add("debug session, synthetic data");
            DetailSubtitle = string.Join(" · ", sub);

            // ── Codec: from the streams' own records, so older sessions (no encoder) read unknown
            var enc = EncoderSummary.Of(s.StreamSpans, s.EndTime);
            DetailCodec    = enc.Primary ?? "—";
            DetailCodecSub = !enc.Known ? "not recorded"
                           : enc.Others.Count > 0 ? $"+ {string.Join(", ", enc.Others)} · {enc.PrimaryStreams} of {enc.TotalStreams} streams"
                           : enc.TotalStreams == 1 ? "one stream"
                           : enc.PrimaryStreams == enc.TotalStreams ? $"all {enc.TotalStreams} streams"
                           : $"{enc.PrimaryStreams} of {enc.TotalStreams} streams";

            DetailCovers = s.GameCoversForDisplay;
            HasDetailCovers = DetailCovers.Count > 0;

            // ── Verdict
            HasStats = q != null;
            if (q == null)
            {
                VerdictLabel = "No data";
                VerdictFgHex = "#C8CFCB";
                VerdictReason = "The client sent no telemetry for this session";
                VerdictNote = "StreamLight reports quality while it streams. Sessions from other clients, or ones that ended before the first report, have nothing to grade.";
                ClearMetrics();
                return;
            }

            var parts = QualityGradeCalculator.EvaluateParts(q, fps);
            var recorded = s.Grade is QualityGrade.High or QualityGrade.Medium or QualityGrade.Low ? s.Grade : null;
            var shown = recorded ?? (q.SampleCount >= 2 ? parts.Overall : (QualityGrade?)null);
            var gc = GameStatsService.GradeColors(shown);
            VerdictLabel = shown == null ? "No data" : gc.Label;
            VerdictFgHex = shown == null ? "#C8CFCB" : gc.Fg;

            float frame = QualityGradeCalculator.FramePeriodMs(fps);
            bool hlReported = q.HostLatencyAvgMs >= 0, lateReported = q.HostLatencyOverBudgetPct >= 0;
            var checks = new List<(string Name, QualityGrade G, bool Rated)>
            {
                ("drops", parts.Drops, true),
                ("RTT", parts.Rtt, q.RttAvgMs > 0),
                ("host frame latency", parts.HostLatency, hlReported),
                ("late frames", parts.LateFrames, lateReported),
            };
            var worst = checks.Where(c => c.Rated && c.G == parts.Overall && c.G != QualityGrade.High).Select(c => c.Name).ToList();
            VerdictReason = shown == null ? "Too few samples to grade"
                          : worst.Count == 0 ? "Every check passed"
                          : $"Held back by {string.Join(" and ", worst)}";

            var notes = new List<string>();
            if (recorded != null && q.SampleCount >= 2 && parts.Overall != recorded)
                notes.Add($"Graded {GameStatsService.GradeColors(recorded).Label} when it ended; with the current limits it would read {GameStatsService.GradeColors(parts.Overall).Label}.");
            if (fps <= 0)
                notes.Add("The frame rate was not recorded for this session: the frame-latency limits assume 60 fps.");
            VerdictNote = string.Join(" ", notes);

            string fpsAt = $" at {(fps > 0 ? fps : 60)} fps";
            Criteria.Add(Criterion("Drops", $"{Fmt(q.DropRatePct, 2)} %", parts.Drops, true,
                Limits(parts.Drops, "1 %", "2 %")));
            string rttLimits = Limits(parts.Rtt, "25 ms", "60 ms");
            if (q.RttMaxMs > 200) rttLimits += $" · a {Fmt(q.RttMaxMs, 0)} ms spike costs a level";
            Criteria.Add(Criterion("RTT", q.RttAvgMs > 0 ? $"{Fmt(q.RttAvgMs, 1)} ms avg" : "not reported", parts.Rtt, q.RttAvgMs > 0, rttLimits));
            string hlLimits = hlReported ? Limits(parts.HostLatency, $"{Fmt(frame * 0.6f, 1)} ms", $"{Fmt(frame, 1)} ms") + fpsAt : "not graded";
            if (hlReported && q.HostLatencyMaxMs > frame * 2.5f && q.HostLatencyOverBudgetPct >= 1f)
                hlLimits += $" · {Fmt(q.HostLatencyMaxMs, 1)} ms spikes cost a level";
            Criteria.Add(Criterion("Host frame latency", hlReported ? $"{Fmt(q.HostLatencyAvgMs, 1)} ms avg" : "not reported",
                parts.HostLatency, hlReported, hlLimits));
            Criteria.Add(Criterion("Late frames", lateReported ? $"{Fmt(q.HostLatencyOverBudgetPct, 2)} %" : "not reported",
                parts.LateFrames, lateReported, lateReported ? Limits(parts.LateFrames, "1 %", "5 %") : "not graded"));

            // ── Client
            DetailRtt = q.RttAvgMs > 0 ? Fmt(q.RttAvgMs, 1) : "—";
            DetailRttSub = q.RttAvgMs > 0 ? $"max {Fmt(q.RttMaxMs, 1)} ms" : "not reported";
            DetailJitter = q.JitterAvgMs > 0 ? Fmt(q.JitterAvgMs, 1) : "—";
            DetailJitterSub = q.JitterMaxMs > 0 ? $"max {Fmt(q.JitterMaxMs, 1)} ms" : "not reported";
            DetailDrops = Fmt(q.DropRatePct, 2);
            DetailDropsSub = q.TotalDrops == 1 ? "1 frame" : $"{q.TotalDrops.ToString("N0", Inv)} frames";
            DetailBitrate = Fmt(q.BitrateAvgMbps, 0);
            DetailBitrateSub = q.TargetBitrateMbps > 0
                ? $"of {Fmt(q.TargetBitrateMbps, 0)} target · {Fmt(q.BitrateAvgMbps / q.TargetBitrateMbps * 100, 0)} %"
                : "delivered, average";
            DetailDecode = Fmt(q.DecodeAvgMs, 1);
            DetailDecodeSub = "average";

            // ── Host
            DetailHostLat    = hlReported ? Fmt(q.HostLatencyAvgMs, 1) : "—";
            DetailHostLatSub = hlReported ? $"max {Fmt(q.HostLatencyMaxMs, 1)} ms" : "not reported";
            DetailLate       = lateReported ? Fmt(q.HostLatencyOverBudgetPct, 2) : "—";
            DetailLateSub    = lateReported ? $"over {Fmt(frame * QualityGradeCalculator.LateFrameMultiplier, 1)} ms (2 frames)" : "not reported";
            DetailGpu        = q.HostGpuAvg >= 0 ? q.HostGpuAvg.ToString(Inv) : "—";
            DetailGpuSub     = q.HostGpuAvg >= 0 ? $"peak {q.HostGpuPeak} %" : "not recorded";
            // The saved figure is unchanged — the video-encode engine's average over every sample.
            // What changes is how it is presented when some streams never used that engine (§82).
            var encUse = EncoderSummary.Of(s.StreamSpans, s.EndTime);
            if (q.HostGpuEncAvg < 0)
            {
                DetailEncoder = "—"; DetailEncoderUnit = "%"; DetailEncoderSub = "not recorded";
            }
            else if (encUse.AllBypass)
            {
                DetailEncoder = "n/a"; DetailEncoderUnit = "";
                DetailEncoderSub = $"{encUse.BypassLabel} · load in {encUse.BypassLoadIn}";
            }
            else
            {
                DetailEncoder = q.HostGpuEncAvg.ToString(Inv); DetailEncoderUnit = "%";
                DetailEncoderSub = encUse.Mixed ? $"{encUse.BypassLabel} streams read 0 · peak {q.HostGpuEncPeak} %"
                                                : $"peak {q.HostGpuEncPeak} %";
            }
            DetailTemp       = q.HostGpuTempAvg >= 0 ? q.HostGpuTempAvg.ToString(Inv) : "—";
            DetailTempSub    = q.HostGpuTempAvg >= 0 ? $"max {q.HostGpuTempMax} °C" : "not recorded";
            DetailCpu        = q.HostCpuAvg >= 0 ? q.HostCpuAvg.ToString(Inv) : "—";
            DetailCpuSub     = q.HostCpuAvg >= 0 ? $"peak {q.HostCpuPeak} %" : "not recorded";
            DetailNetTx      = q.HostNetTxAvg >= 0 ? q.HostNetTxAvg.ToString(Inv) : "—";
        }

        private static string Limits(QualityGrade g, string excellent, string good) => g switch
        {
            QualityGrade.High   => $"Excellent below {excellent}",
            QualityGrade.Medium => $"Good up to {good}",
            _                   => $"Poor above {good}",
        };

        private static CriterionRow Criterion(string name, string value, QualityGrade g, bool rated, string limits)
        {
            var c = rated ? GameStatsService.GradeColors(g) : ("n/a", "#929A96", "#0AFFFFFF", "#1AFFFFFF");
            return new CriterionRow
            {
                Name = name, ValueText = value, ThresholdText = limits,
                GradeLabel = c.Item1, GradeFgHex = c.Item2, GradeBgHex = c.Item3, GradeBorderHex = c.Item4,
            };
        }

        private void ClearMetrics()
        {
            DetailRtt = DetailJitter = DetailDrops = DetailBitrate = DetailDecode = "—";
            DetailRttSub = DetailJitterSub = DetailDropsSub = DetailBitrateSub = DetailDecodeSub = "";
            DetailHostLat = DetailLate = DetailGpu = DetailEncoder = DetailTemp = DetailCpu = DetailNetTx = "—";
            DetailEncoderUnit = "%";
            DetailHostLatSub = DetailLateSub = DetailGpuSub = DetailEncoderSub = DetailTempSub = DetailCpuSub = "";
        }

        private static string Fmt(float v, int dec) => v.ToString("F" + dec, Inv);

        // ═════════════════════════════ COMPARE ══════════════════════════════

        private const string GoodFg = "#4ade80", GoodBg = "#214ade80";
        private const string BadFg  = "#fca5a5", BadBg  = "#21f87171";
        private const string NeuFg  = "#C8CFCB", NeuBg  = "#0FFFFFFF";

        private enum Dir { LowerBetter, HigherBetter, Neutral }

        // Full candidate set, plus the two per-dropdown lists: A excludes whatever B
        // has selected and vice versa, so the same session can't be picked twice.
        private readonly List<SessionEntry> _allCandidates = new();
        public ObservableCollection<SessionEntry> CompareCandidatesA { get; } = new();
        public ObservableCollection<SessionEntry> CompareCandidatesB { get; } = new();
        public ObservableCollection<CompareMetric> CompareClientMetrics { get; } = new();
        public ObservableCollection<CompareMetric> CompareHostMetrics { get; } = new();

        private bool _isCompareVisible;
        public bool IsCompareVisible { get => _isCompareVisible; private set => SetProperty(ref _isCompareVisible, value); }

        private SessionEntry? _compareA, _compareB;
        public SessionEntry? CompareA { get => _compareA; set => SetProperty(ref _compareA, value); }
        public SessionEntry? CompareB { get => _compareB; set => SetProperty(ref _compareB, value); }

        private bool _hasCompareData;
        public bool HasCompareData { get => _hasCompareData; private set => SetProperty(ref _hasCompareData, value); }

        private bool _hasTwoCandidates;
        public bool HasTwoCandidates { get => _hasTwoCandidates; private set => SetProperty(ref _hasTwoCandidates, value); }

        private IReadOnlyList<GameCoverItem> _coversA = Array.Empty<GameCoverItem>(), _coversB = Array.Empty<GameCoverItem>();
        public IReadOnlyList<GameCoverItem> CompareCoversA { get => _coversA; private set => SetProperty(ref _coversA, value); }
        public IReadOnlyList<GameCoverItem> CompareCoversB { get => _coversB; private set => SetProperty(ref _coversB, value); }

        private string _cmpSubA = "", _cmpSubB = "";
        public string CompareSubA   { get => _cmpSubA;   private set => SetProperty(ref _cmpSubA, value); }
        public string CompareSubB   { get => _cmpSubB;   private set => SetProperty(ref _cmpSubB, value); }

        private string _gA = "", _gAFg = NeuFg, _gABg = NeuBg, _gB = "", _gBFg = NeuFg, _gBBg = NeuBg;
        public string CompareGradeA   { get => _gA;   private set => SetProperty(ref _gA, value); }
        public string CompareGradeAFg { get => _gAFg; private set => SetProperty(ref _gAFg, value); }
        public string CompareGradeABg { get => _gABg; private set => SetProperty(ref _gABg, value); }
        public string CompareGradeB   { get => _gB;   private set => SetProperty(ref _gB, value); }
        public string CompareGradeBFg { get => _gBFg; private set => SetProperty(ref _gBFg, value); }
        public string CompareGradeBBg { get => _gBBg; private set => SetProperty(ref _gBBg, value); }

        /// <summary>Opens Compare on the two ticked rows.</summary>
        public void ComparePicked()
        {
            if (_picked.Count < 2) return;
            // Session 1 is the one being judged, so the newer pick goes first, as in the list.
            var pair = _picked.OrderByDescending(s => s.StartTime).ToList();
            OpenCompare(pair[0], pair[1]);
        }

        /// <summary>Opens Compare with <paramref name="a"/> as session 1 and, if not given, the session before it.</summary>
        public void OpenCompare(SessionEntry? a = null, SessionEntry? b = null)
        {
            // Only sessions with telemetry can be compared.
            _allCandidates.Clear();
            _allCandidates.AddRange(_all.Where(s => s.QualityStats != null && s.EndTime != null));
            HasTwoCandidates = _allCandidates.Count >= 2;

            // Pre-fill both lists fully so the SelectedItem bindings resolve before we set
            // the selections (binding to an item absent from the source would null it).
            CompareCandidatesA.Clear();
            CompareCandidatesB.Clear();
            foreach (var s in _allCandidates) { CompareCandidatesA.Add(s); CompareCandidatesB.Add(s); }

            a = a != null ? _allCandidates.FirstOrDefault(s => s.Id == a.Id) : null;
            b = b != null ? _allCandidates.FirstOrDefault(s => s.Id == b.Id) : null;
            a ??= _allCandidates.FirstOrDefault();
            if (b == null || b == a)
            {
                int ia = a != null ? _allCandidates.IndexOf(a) : -1;
                b = _allCandidates.Skip(ia + 1).FirstOrDefault(s => s != a) ?? _allCandidates.FirstOrDefault(s => s != a);
            }
            CompareA = a;
            CompareB = b;

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
            for (int i = list.Count - 1; i >= 0; i--)
                if (ReferenceEquals(list[i], exclude) || !_allCandidates.Contains(list[i]))
                    list.RemoveAt(i);

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
            CompareCandidatesA.Clear();
            CompareCandidatesB.Clear();
            _allCandidates.Clear();
        }

        public void RebuildComparison()
        {
            CompareClientMetrics.Clear();
            CompareHostMetrics.Clear();
            DescribePick(_compareA, out var sA, out var cA, out var g1);
            DescribePick(_compareB, out var sB, out var cB, out var g2);
            CompareSubA = sA; CompareCoversA = cA;
            CompareSubB = sB; CompareCoversB = cB;
            (CompareGradeA, CompareGradeAFg, CompareGradeABg) = g1;
            (CompareGradeB, CompareGradeBFg, CompareGradeBBg) = g2;

            var a = _compareA?.QualityStats;
            var b = _compareB?.QualityStats;
            HasCompareData = a != null && b != null;
            if (!HasCompareData) return;

            CompareClientMetrics.Add(M("RTT avg",    a!.RttAvgMs,    b!.RttAvgMs,    1, " ms",   Dir.LowerBetter,  a.RttAvgMs    > 0, b.RttAvgMs    > 0));
            CompareClientMetrics.Add(M("RTT max",    a.RttMaxMs,     b.RttMaxMs,     1, " ms",   Dir.LowerBetter,  a.RttMaxMs    > 0, b.RttMaxMs    > 0));
            CompareClientMetrics.Add(M("Jitter avg", a.JitterAvgMs,  b.JitterAvgMs,  1, " ms",   Dir.LowerBetter,  a.JitterAvgMs > 0, b.JitterAvgMs > 0));
            CompareClientMetrics.Add(M("Drop rate",  a.DropRatePct,  b.DropRatePct,  2, " %",    Dir.LowerBetter));
            // Neutral, not LowerBetter: a raw count favours the shorter session, so
            // judging it green/red asserts something false when the two differ in
            // length. Drop rate carries the comparable signal.
            CompareClientMetrics.Add(M("Dropped frames", a.TotalDrops, b.TotalDrops, 0, "",      Dir.Neutral));
            CompareClientMetrics.Add(M("Bitrate avg", a.BitrateAvgMbps, b.BitrateAvgMbps, 1, " Mbps", Dir.HigherBetter));
            CompareClientMetrics.Add(M("Decode avg", a.DecodeAvgMs,  b.DecodeAvgMs,  1, " ms",   Dir.LowerBetter));

            // Load telemetry is neutral; only frame latency is quality-directional.
            CompareHostMetrics.Add(M("Frame latency avg", a.HostLatencyAvgMs, b.HostLatencyAvgMs, 1, " ms", Dir.LowerBetter, a.HostLatencyAvgMs >= 0, b.HostLatencyAvgMs >= 0));
            CompareHostMetrics.Add(M("Frame latency max", a.HostLatencyMaxMs, b.HostLatencyMaxMs, 1, " ms", Dir.LowerBetter, a.HostLatencyMaxMs >= 0, b.HostLatencyMaxMs >= 0));
            CompareHostMetrics.Add(M("Late frames",  a.HostLatencyOverBudgetPct, b.HostLatencyOverBudgetPct, 2, " %", Dir.LowerBetter, a.HostLatencyOverBudgetPct >= 0, b.HostLatencyOverBudgetPct >= 0));
            CompareHostMetrics.Add(M("GPU avg",      a.HostGpuAvg,     b.HostGpuAvg,     0, " %",   Dir.Neutral, a.HostGpuAvg    >= 0, b.HostGpuAvg    >= 0));
            // Encoder avg only compares like with like (§82): a PyroWave or software session's figure is
            // n/a, and a mixed one counts its PyroWave streams as 0, so neither is set against an NVENC one.
            var encA = EncoderSummary.Of(_compareA?.StreamSpans, _compareA?.EndTime);
            var encB = EncoderSummary.Of(_compareB?.StreamSpans, _compareB?.EndTime);
            var encRow = M("Encoder avg", a.HostGpuEncAvg, b.HostGpuEncAvg, 0, " %", Dir.Neutral,
                          a.HostGpuEncAvg >= 0 && !encA.AllBypass, b.HostGpuEncAvg >= 0 && !encB.AllBypass);
            if (encA.AllBypass) encRow.ValueA = $"n/a · {encA.BypassLabel}";
            if (encB.AllBypass) encRow.ValueB = $"n/a · {encB.BypassLabel}";
            if (!encA.EncFigureComparable || !encB.EncFigureComparable) encRow.Delta = "—";
            CompareHostMetrics.Add(encRow);
            CompareHostMetrics.Add(M("GPU temp avg", a.HostGpuTempAvg, b.HostGpuTempAvg, 0, " °C",  Dir.Neutral, a.HostGpuTempAvg>= 0, b.HostGpuTempAvg>= 0));
            CompareHostMetrics.Add(M("CPU avg",      a.HostCpuAvg,     b.HostCpuAvg,     0, " %",   Dir.Neutral, a.HostCpuAvg    >= 0, b.HostCpuAvg    >= 0));
            CompareHostMetrics.Add(M("Net TX avg",   a.HostNetTxAvg,   b.HostNetTxAvg,   0, " Mbps",Dir.Neutral, a.HostNetTxAvg  >= 0, b.HostNetTxAvg  >= 0));
        }

        private static void DescribePick(SessionEntry? s, out string sub,
                                         out IReadOnlyList<GameCoverItem> covers, out (string, string, string) grade)
        {
            if (s == null)
            {
                sub = ""; covers = Array.Empty<GameCoverItem>(); grade = ("", NeuFg, NeuBg);
                return;
            }
            var bits = new List<string>();
            if (s.EndTime != null) bits.Add(FormatDuration(s.EndTime.Value - s.StartTime));
            if (s.GamesDetected is { Count: > 0 } g) bits.Add(string.Join(", ", g));
            sub = string.Join(" · ", bits);
            covers = s.GameCoversForDisplay;
            var c = GameStatsService.GradeColors(s.Grade);
            grade = (c.Label, c.Fg, c.Bg);
        }

        private static CompareMetric M(string label, float a, float b, int dec, string unit, Dir dir, bool aOk = true, bool bOk = true)
        {
            string F(float v) => v.ToString("F" + dec, Inv) + unit;
            var m = new CompareMetric { Label = label, ValueA = aOk ? F(a) : "N/A", ValueB = bOk ? F(b) : "N/A" };
            if (!aOk || !bOk) { m.Delta = "—"; return m; }

            // Delta reads "Session 1 relative to Session 2": the left column is the
            // one being evaluated, so the sign answers "how does A differ from B".
            float d = a - b;
            float eps = 0.5f * (float)Math.Pow(10, -dec);
            string arrow = d > eps ? "▲ " : d < -eps ? "▼ " : "";
            m.Delta = arrow + Math.Abs(d).ToString("F" + dec, Inv) + unit;
            if (dir != Dir.Neutral && Math.Abs(d) >= eps)
            {
                bool improved = dir == Dir.LowerBetter ? d < 0 : d > 0;
                (m.DeltaFgHex, m.DeltaBgHex) = improved ? (GoodFg, GoodBg) : (BadFg, BadBg);
            }
            return m;
        }
    }
}
