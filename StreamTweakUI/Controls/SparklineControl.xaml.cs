using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace StreamTweak.Controls
{
    /// <summary>One named, colored data series for <see cref="SparklineControl"/> multi-line mode.</summary>
    public sealed class SparklineSeries
    {
        public string Label { get; set; } = string.Empty;
        public Color Color { get; set; }
        public IReadOnlyList<float>? Data { get; set; }
    }

    public sealed partial class SparklineControl : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────────────

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(SparklineControl),
                new PropertyMetadata(string.Empty, (d, _) => ((SparklineControl)d).OnTitleChanged()));

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(IReadOnlyList<float>), typeof(SparklineControl),
                new PropertyMetadata(null, (d, _) => ((SparklineControl)d).Redraw()));

        public static readonly DependencyProperty DurationLabelProperty =
            DependencyProperty.Register(nameof(DurationLabel), typeof(string), typeof(SparklineControl),
                new PropertyMetadata(string.Empty, (d, _) => ((SparklineControl)d).Redraw()));

        public static readonly DependencyProperty LineColorProperty =
            DependencyProperty.Register(nameof(LineColor), typeof(Color), typeof(SparklineControl),
                new PropertyMetadata(Color.FromArgb(0xFF, 0x00, 0xB4, 0xD8),
                    (d, _) => ((SparklineControl)d).Redraw()));

        /// <summary>
        /// When > 0, the chart uses a fixed time scale with this many slots.
        /// Data fills from the right — new points appear at the right edge and
        /// older points scroll left, even while the buffer is not yet full.
        /// When 0 (default), data is stretched to fill the full canvas width.
        /// </summary>
        public static readonly DependencyProperty WindowSizeProperty =
            DependencyProperty.Register(nameof(WindowSize), typeof(int), typeof(SparklineControl),
                new PropertyMetadata(0, (d, _) => ((SparklineControl)d).Redraw()));

        /// <summary>
        /// When false, the "0" baseline label on the Y-axis is hidden.
        /// Default is true (visible). Set to false for live charts where
        /// the minimum value is always well above zero.
        /// </summary>
        public static readonly DependencyProperty ShowZeroLabelProperty =
            DependencyProperty.Register(nameof(ShowZeroLabel), typeof(bool), typeof(SparklineControl),
                new PropertyMetadata(true, (d, _) => ((SparklineControl)d).OnShowZeroLabelChanged()));

        /// <summary>
        /// When true and the data is downsampled to fit the canvas, a translucent
        /// min/max band is drawn behind the average line so isolated spikes (e.g.
        /// a single-frame RTT or host-latency lag) stay visible instead of being
        /// flattened by bucket averaging. Ignored in fixed-window (live) mode.
        /// </summary>
        public static readonly DependencyProperty EnvelopeModeProperty =
            DependencyProperty.Register(nameof(EnvelopeMode), typeof(bool), typeof(SparklineControl),
                new PropertyMetadata(false, (d, _) => ((SparklineControl)d).Redraw()));

        /// <summary>
        /// Multiple named series sharing one Y-scale, with a legend. When set
        /// (non-empty), it takes precedence over the single <see cref="Data"/> line.
        /// Used for the host compute chart (GPU / Encoder / CPU overlaid).
        /// </summary>
        public static readonly DependencyProperty LinesDataProperty =
            DependencyProperty.Register(nameof(LinesData), typeof(IReadOnlyList<SparklineSeries>), typeof(SparklineControl),
                new PropertyMetadata(null, (d, _) => ((SparklineControl)d).Redraw()));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public IReadOnlyList<float>? Data
        {
            get => (IReadOnlyList<float>?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public string DurationLabel
        {
            get => (string)GetValue(DurationLabelProperty);
            set => SetValue(DurationLabelProperty, value);
        }

        public Color LineColor
        {
            get => (Color)GetValue(LineColorProperty);
            set => SetValue(LineColorProperty, value);
        }

        public int WindowSize
        {
            get => (int)GetValue(WindowSizeProperty);
            set => SetValue(WindowSizeProperty, value);
        }

        public bool ShowZeroLabel
        {
            get => (bool)GetValue(ShowZeroLabelProperty);
            set => SetValue(ShowZeroLabelProperty, value);
        }

        public bool EnvelopeMode
        {
            get => (bool)GetValue(EnvelopeModeProperty);
            set => SetValue(EnvelopeModeProperty, value);
        }

        public IReadOnlyList<SparklineSeries>? LinesData
        {
            get => (IReadOnlyList<SparklineSeries>?)GetValue(LinesDataProperty);
            set => SetValue(LinesDataProperty, value);
        }

        // Dynamically-created polylines for multi-line mode (cleared each redraw).
        private readonly List<Microsoft.UI.Xaml.Shapes.Polyline> _extraLines = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public SparklineControl()
        {
            this.InitializeComponent();
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        private void OnTitleChanged()
        {
            if (TitleText == null) return;
            TitleText.Text       = Title;
            TitleText.Visibility = string.IsNullOrEmpty(Title)
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
        }

        private void OnShowZeroLabelChanged()
        {
            if (ZeroLabel == null) return;
            ZeroLabel.Visibility = ShowZeroLabel
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => Redraw();

        private void Redraw()
        {
            // Guard: controls may not be initialized yet during DP callbacks
            if (TitleText  == null) return;
            if (ChartCanvas == null) return;

            TitleText.Text = Title;

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            XEndLabel.Text = DurationLabel;

            // Multi-line mode takes precedence when LinesData has drawable series.
            var lines = LinesData;
            if (lines != null && lines.Any(s => s.Data != null && s.Data.Count >= 2))
            {
                RedrawMultiLine(lines, w, h);
                return;
            }
            ClearExtraLines();
            LegendPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            var data = Data;

            if (data == null || data.Count < 2 || w < 4 || h < 4)
            {
                DataLine.Points.Clear();
                EnvelopeBand.Points.Clear();
                MaxLabel.Text = string.Empty;
                MidLabel.Text = string.Empty;
                return;
            }

            int windowSize = WindowSize;

            // In fixed-window mode skip bucket averaging — points map 1:1 to time slots.
            // In stretch mode, bucket-average to the canvas width; when EnvelopeMode is
            // on, also compute the per-bucket min/max so spikes survive downsampling.
            List<float> pts;
            List<float>? bucketMin = null, bucketMax = null;
            bool envelope = EnvelopeMode && windowSize == 0 && data.Count > (int)w;
            if (windowSize > 0)
                pts = data.ToList();
            else if (envelope)
                (pts, bucketMin, bucketMax) = BucketStats(data, (int)w);
            else
                pts = BucketAverage(data, (int)w);

            // Y-scale tops out at the highest visible value — the band's max in
            // envelope mode, otherwise the average line's max.
            float max = (bucketMax != null && bucketMax.Count > 0 ? bucketMax.Max() : pts.Max());
            if (max <= 0f) max = 1f;

            MaxLabel.Text = FormatValue(max);
            MidLabel.Text = FormatValue(max / 2f);

            // Update line color
            DataLine.Stroke = new SolidColorBrush(LineColor);

            // Build polyline — slight top/bottom margin so the line is never
            // clipped by the border's edge
            const double margin = 2.0;
            double chartH = h - margin * 2;

            DataLine.Points.Clear();
            EnvelopeBand.Points.Clear();
            int n = pts.Count;

            // Envelope band (stretch mode only): a translucent min..max fill behind
            // the average line. Built as max-edge left→right then min-edge right→left.
            if (envelope && bucketMin != null && bucketMax != null && n >= 2)
            {
                double Xc(int i) => i * (w - 1) / (n - 1);
                double Yc(float v) => margin + chartH - (v / max) * chartH;
                for (int i = 0; i < n; i++) EnvelopeBand.Points.Add(new Point(Xc(i), Yc(bucketMax[i])));
                for (int i = n - 1; i >= 0; i--) EnvelopeBand.Points.Add(new Point(Xc(i), Yc(bucketMin[i])));
                var c = LineColor;
                EnvelopeBand.Fill = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
            }

            if (windowSize > 0)
            {
                // Fixed-time-scale: each of the windowSize slots occupies an equal
                // pixel interval. Data fills from the right — newer points on the
                // right, older ones scroll left as new samples arrive.
                double step   = (w - 1) / Math.Max(windowSize - 1, 1);
                int    offset = windowSize - n; // empty slots on the left
                for (int i = 0; i < n; i++)
                {
                    double x = (offset + i) * step;
                    double y = margin + chartH - (pts[i] / max) * chartH;
                    DataLine.Points.Add(new Point(x, y));
                }
            }
            else
            {
                // Stretch-to-fit (default)
                for (int i = 0; i < n; i++)
                {
                    double x = i * (w - 1) / (n - 1);
                    double y = margin + chartH - (pts[i] / max) * chartH;
                    DataLine.Points.Add(new Point(x, y));
                }
            }
        }

        // ── Multi-line rendering ──────────────────────────────────────────────

        private void RedrawMultiLine(IReadOnlyList<SparklineSeries> lines, double w, double h)
        {
            // Single-line elements are unused in this mode.
            DataLine.Points.Clear();
            EnvelopeBand.Points.Clear();
            ClearExtraLines();

            var drawable = lines.Where(s => s.Data != null && s.Data.Count >= 2).ToList();
            if (drawable.Count == 0 || w < 4 || h < 4)
            {
                LegendPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                MaxLabel.Text = MidLabel.Text = string.Empty;
                return;
            }

            // Bucket-average each series to the canvas width, then take a Y-scale
            // shared by all of them so the lines are directly comparable.
            var bucketed = drawable.Select(s => BucketAverage(s.Data!, (int)w)).ToList();
            float max = 0f;
            foreach (var b in bucketed)
                foreach (var v in b) if (v > max) max = v;
            if (max <= 0f) max = 1f;

            MaxLabel.Text = FormatValue(max);
            MidLabel.Text = FormatValue(max / 2f);

            const double margin = 2.0;
            double chartH = h - margin * 2;

            for (int li = 0; li < drawable.Count; li++)
            {
                var pts = bucketed[li];
                int n = pts.Count;
                if (n < 2) continue;

                var poly = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    StrokeThickness = 1.5,
                    StrokeLineJoin  = PenLineJoin.Round,
                    Stroke          = new SolidColorBrush(drawable[li].Color),
                    IsHitTestVisible = false,
                };
                for (int i = 0; i < n; i++)
                {
                    double x = i * (w - 1) / (n - 1);
                    double y = margin + chartH - (pts[i] / max) * chartH;
                    poly.Points.Add(new Point(x, y));
                }
                ChartCanvas.Children.Add(poly);
                _extraLines.Add(poly);
            }

            BuildLegend(drawable);
        }

        private void BuildLegend(IReadOnlyList<SparklineSeries> series)
        {
            LegendPanel.Children.Clear();
            foreach (var s in series)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                item.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 8, Height = 8, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(s.Color),
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                });
                item.Children.Add(new TextBlock
                {
                    Text = s.Label, FontSize = 9, Opacity = 0.7,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                });
                LegendPanel.Children.Add(item);
            }
            LegendPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }

        private void ClearExtraLines()
        {
            foreach (var p in _extraLines) ChartCanvas.Children.Remove(p);
            _extraLines.Clear();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<float> BucketAverage(IReadOnlyList<float> data, int maxBuckets)
        {
            int n = data.Count;
            if (n <= maxBuckets || maxBuckets <= 0) return data.ToList();

            var result = new List<float>(maxBuckets);
            double bucketSize = (double)n / maxBuckets;
            for (int b = 0; b < maxBuckets; b++)
            {
                int start = (int)(b * bucketSize);
                int end   = Math.Min((int)((b + 1) * bucketSize), n);
                if (start >= end) continue;
                float sum = 0;
                for (int i = start; i < end; i++) sum += data[i];
                result.Add(sum / (end - start));
            }
            return result;
        }

        /// <summary>
        /// Bucket-downsamples to maxBuckets returning the per-bucket average, min and
        /// max as three parallel lists. Used by envelope mode so the band reflects the
        /// real extremes within each bucket while the line shows the average.
        /// </summary>
        private static (List<float> Avg, List<float> Min, List<float> Max) BucketStats(IReadOnlyList<float> data, int maxBuckets)
        {
            int n = data.Count;
            if (n <= maxBuckets || maxBuckets <= 0)
            {
                var copy = data.ToList();
                return (copy, new List<float>(copy), new List<float>(copy));
            }

            var avg = new List<float>(maxBuckets);
            var min = new List<float>(maxBuckets);
            var mx  = new List<float>(maxBuckets);
            double bucketSize = (double)n / maxBuckets;
            for (int b = 0; b < maxBuckets; b++)
            {
                int start = (int)(b * bucketSize);
                int end   = Math.Min((int)((b + 1) * bucketSize), n);
                if (start >= end) continue;
                float sum = 0, lo = float.MaxValue, hi = float.MinValue;
                for (int i = start; i < end; i++)
                {
                    float v = data[i];
                    sum += v;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                avg.Add(sum / (end - start));
                min.Add(lo);
                mx.Add(hi);
            }
            return (avg, min, mx);
        }

        /// <summary>
        /// Formats a float value using at most 3 significant digits,
        /// no trailing zeros, invariant (dot) decimal separator.
        /// Examples: 2.0 → "2", 1.23 → "1.23", 81.8 → "81.8"
        /// </summary>
        private static string FormatValue(float v)
            => v == 0f ? "0" : v.ToString("G3", CultureInfo.InvariantCulture);
    }
}
