using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.UI;

namespace StreamTweak.ViewModels
{
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

        // ── Detail: game covers ───────────────────────────────────────────────

        public ObservableCollection<SessionGameCover> DetailGameCovers { get; } = new();

        private bool _hasDetailGameCovers;
        public bool HasDetailGameCovers
        {
            get => _hasDetailGameCovers;
            private set => SetProperty(ref _hasDetailGameCovers, value);
        }

        private bool _hasNoDetailGames;
        public bool HasNoDetailGames
        {
            get => _hasNoDetailGames;
            private set => SetProperty(ref _hasNoDetailGames, value);
        }

        /// <summary>True when GamesDetected != null (monitor ran) — shows the Games box.</summary>
        private bool _hasDetailGamesSection;
        public bool HasDetailGamesSection
        {
            get => _hasDetailGamesSection;
            private set => SetProperty(ref _hasDetailGamesSection, value);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Load()
        {
            var list = SessionLogger.Load();
            Sessions.Clear();
            foreach (var s in list)
                Sessions.Add(s);
            HasSessions = Sessions.Count > 0;
        }

        public void ClearHistory()
        {
            SessionLogger.ClearAll();
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
            HasNoDetailGames     = false;
            HasDetailGamesSection = false;
        }

        public async Task LoadDetailCoversAsync()
        {
            DetailGameCovers.Clear();
            HasDetailGameCovers   = false;
            HasNoDetailGames      = false;

            var s = _selectedSession;
            if (s?.GamesDetected == null) return; // pre-feature session — section already hidden

            if (s.GamesDetected.Count == 0)
            {
                HasNoDetailGames = true;
                return;
            }

            // Populate with name-only covers first so the strip appears immediately
            foreach (var name in s.GamesDetected)
                DetailGameCovers.Add(new SessionGameCover(name));
            HasDetailGameCovers = true;

            // Load images asynchronously — snapshot-first, then live state fallback
            var gameMap = GameLibraryState.Current.Games
                .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < s.GamesDetected.Count; i++)
            {
                // GamesDetectedCoverPaths snapshot survives game uninstall (cover files never deleted)
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
                    var bmp  = new BitmapImage();
                    // 2× display width (67 px) so WIC uses its Fant resampler
                    bmp.DecodePixelWidth = 134;
                    using var stream = await file.OpenReadAsync();
                    await bmp.SetSourceAsync(stream);
                    DetailGameCovers[i].CoverImage = bmp;
                }
                catch { /* non-fatal */ }
            }
        }

        public void OpenFullscreenChart(string title, IReadOnlyList<float>? data, Color lineColor)
        {
            if (data == null || data.Count < 2) return;
            FullscreenChartTitle = title;
            FullscreenChartData  = data;
            FullscreenLineColor  = lineColor;
            IsChartFullscreen    = true;
        }

        public void CloseFullscreenChart()
        {
            IsChartFullscreen = false;
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
                HasChartData = false;
                return;
            }

            DetailTitle    = s.StartTimeDisplay;
            DetailDuration = s.TelemetryDurationDisplay;
            DetailHeaderSubtitle = $"{s.StartTimeDisplay}  ·  {s.TelemetryDurationDisplay}";

            // Grade
            (GradeLabel, GradeColorHex) = s.Grade switch
            {
                QualityGrade.High   => ("Excellent", "#FF4CAF50"),
                QualityGrade.Medium => ("Good",      "#FFFFC107"),
                QualityGrade.Low    => ("Poor",       "#FFDC4632"),
                _                   => ("—",          "#FF808080")
            };

            var q = s.QualityStats;
            if (q != null)
            {
                DetailRttAvg    = $"{q.RttAvgMs:F1} ms";
                DetailRttMax    = $"{q.RttMaxMs:F1} ms";
                DetailJitterAvg = q.JitterAvgMs > 0 ? $"{q.JitterAvgMs:F1} ms" : "N/A";
                DetailJitterMax = q.JitterMaxMs > 0 ? $"{q.JitterMaxMs:F1} ms" : "N/A";
                DetailDrops     = q.TotalDrops.ToString();
                DetailDropRate  = $"{q.DropRatePct:F2}%";
                DetailDecodeAvg = $"{q.DecodeAvgMs:F1} ms";
                DetailBitrateAvg = $"{q.BitrateAvgMbps:F1} Mbps";

                // HOST
                HasHostStats = q.HostGpuAvg >= 0 || q.HostCpuAvg >= 0;
                DetailHostGpu     = q.HostGpuAvg     >= 0 ? $"{q.HostGpuAvg} % (peak {q.HostGpuPeak} %)"   : "N/A";
                DetailHostEncoder = q.HostGpuEncAvg  >= 0 ? $"{q.HostGpuEncAvg} % (peak {q.HostGpuEncPeak} %)" : "N/A";
                DetailHostTemp    = q.HostGpuTempAvg >= 0 ? $"{q.HostGpuTempAvg} °C (max {q.HostGpuTempMax} °C)" : "N/A";
                DetailHostCpu     = q.HostCpuAvg     >= 0 ? $"{q.HostCpuAvg} % (peak {q.HostCpuPeak} %)"   : "N/A";
                DetailHostNetTx   = q.HostNetTxAvg   >= 0 ? $"{q.HostNetTxAvg} Mbps" : "N/A";
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
            HasChartData  = _rttSeries != null || _dropsSeries != null
                         || _bitrateSeries != null || _decodeSeries != null;

            HasDetailGamesSection = s.GamesDetected != null;
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
            DetailHostCpu = DetailHostNetTx = "N/A";
        }
    }
}
