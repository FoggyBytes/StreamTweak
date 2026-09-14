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
        /// When true and the data is downsampled to fit the canvas, a translucent
        /// min/max band is drawn behind the average line so isolated spikes (e.g.
        /// a single-frame RTT or host-latency lag) stay visible instead of being
        /// flattened by bucket averaging.
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

        /// <summary>
        /// Wall-clock mapping for the X axis. Null → the axis falls back to "0 → duration".
        /// </summary>
        public static readonly DependencyProperty TimeAxisProperty =
            DependencyProperty.Register(nameof(TimeAxis), typeof(ChartTimeAxis), typeof(SparklineControl),
                new PropertyMetadata(null, (d, e) => ((SparklineControl)d).Redraw()));

        public ChartTimeAxis? TimeAxis
        {
            get => (ChartTimeAxis?)GetValue(TimeAxisProperty);
            set => SetValue(TimeAxisProperty, value);
        }

        // Plot geometry captured on every Redraw so the hover readout can snap to a
        // sample without recomputing layout. Empty = nothing drawable, hover disabled.
        private readonly List<double> _plotX = new();
        private readonly List<(string Label, Color Color, IReadOnlyList<float> Values)> _plotSeries = new();
        private double _plotMargin, _plotChartH;
        private float  _plotMax = 1f;

        // Tick positions in 0..1, produced by DrawXAxis and consumed by DrawMarkers so
        // the scale lines land exactly under their labels instead of being recomputed.
        private readonly List<double> _tickFractions = new();

        // Dynamically-created polylines for multi-line mode (cleared each redraw).
        private readonly List<Microsoft.UI.Xaml.Shapes.Polyline> _extraLines = new();

        // Hover dots for series 2..n in multi-line mode; HoverDot serves series 1. Created on
        // demand and kept across redraws — HideHover collapses them, so a shorter series list
        // just leaves the surplus hidden.
        private readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> _extraDots = new();

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
            _plotX.Clear();
            _plotSeries.Clear();
            HideHover();
            DrawXAxis(w);
            DrawMarkers(w, h);

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

            // Bucket-average to the canvas width; when EnvelopeMode is on, also compute
            // the per-bucket min/max so spikes survive downsampling.
            List<float> pts;
            List<float>? bucketMin = null, bucketMax = null;
            bool envelope = EnvelopeMode && data.Count > (int)w;
            if (envelope)
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

            // Envelope band: a translucent min..max fill behind the average line.
            // Built as max-edge left→right then min-edge right→left.
            if (envelope && bucketMin != null && bucketMax != null && n >= 2)
            {
                double Xc(int i) => i * (w - 1) / (n - 1);
                double Yc(float v) => margin + chartH - (v / max) * chartH;
                for (int i = 0; i < n; i++) EnvelopeBand.Points.Add(new Point(Xc(i), Yc(bucketMax[i])));
                for (int i = n - 1; i >= 0; i--) EnvelopeBand.Points.Add(new Point(Xc(i), Yc(bucketMin[i])));
                var c = LineColor;
                EnvelopeBand.Fill = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
            }

            for (int i = 0; i < n; i++)
            {
                double x = i * (w - 1) / (n - 1);
                double y = margin + chartH - (pts[i] / max) * chartH;
                DataLine.Points.Add(new Point(x, y));
            }
            CapturePlot(pts, w, margin, chartH, max);
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

            // Hover reads every series at the pointed index, the way Chart.js "index"
            // mode does — one dot on each series, the popup lists them all.
            _plotMargin = margin; _plotChartH = chartH; _plotMax = max;
            _plotX.Clear();
            _plotSeries.Clear();
            for (int li = 0; li < drawable.Count; li++)
            {
                var pts = bucketed[li];
                if (pts.Count < 2) continue;
                if (_plotX.Count == 0)
                    for (int i = 0; i < pts.Count; i++) _plotX.Add(i * (w - 1) / (pts.Count - 1));
                _plotSeries.Add((drawable[li].Label, drawable[li].Color, pts));
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

        // ── Time axis, stream markers, hover readout ──────────────────────────

        private enum AxisAlign { Left, Center, Right }

        private void CapturePlot(IReadOnlyList<float> pts, double w, double margin, double chartH, float max)
        {
            _plotMargin = margin; _plotChartH = chartH; _plotMax = max;
            _plotX.Clear();
            _plotSeries.Clear();
            int n = pts.Count;
            for (int i = 0; i < n; i++) _plotX.Add(n > 1 ? i * (w - 1) / (n - 1) : 0);
            _plotSeries.Add((string.Empty, LineColor, pts));
        }

        private void DrawXAxis(double w)
        {
            if (XAxisCanvas == null) return;
            XAxisCanvas.Children.Clear();
            _tickFractions.Clear();
            if (w < 4) return;

            var axis = TimeAxis;
            if (axis is not { IsUsable: true })
            {
                // Legacy pair: elapsed time, no clock. It is also what historical sessions
                // get, having been recorded before stream spans existed. No tick fractions
                // are produced, so DrawMarkers draws no scale lines either — a grid with
                // nothing to index would be decoration, not a scale.
                AddAxisLabel("0", 0, w, AxisAlign.Left);
                if (!string.IsNullOrEmpty(DurationLabel))
                    AddAxisLabel(DurationLabel, w, w, AxisAlign.Right);
                return;
            }

            bool seconds = axis.ActiveDuration < TimeSpan.FromMinutes(10);
            string midFmt  = seconds ? "HH:mm:ss" : "HH:mm";
            string edgeFmt = "dd/MM " + midFmt;

            // Tick count is driven by the widest label each slot has to hold, not by a
            // fixed pixel step: the two edges carry the date and are far wider than the
            // intermediate times, so a step sized for the middles alone would push the
            // first intermediate underneath the date. Required step is whichever is
            // larger — one middle label, or an edge label plus half a middle.
            double wMid  = MeasureLabel(axis.TimeAt(0).ToString(midFmt,  CultureInfo.InvariantCulture));
            double wEdge = MeasureLabel(axis.TimeAt(0).ToString(edgeFmt, CultureInfo.InvariantCulture));
            const double gap = 12;
            double needed = Math.Max(wMid + gap, wEdge + gap + wMid / 2);
            int ticks = Math.Clamp((int)(w / Math.Max(needed, 1)) + 1, 2, 8);

            for (int i = 0; i < ticks; i++)
            {
                double t = (double)i / (ticks - 1);
                _tickFractions.Add(t);

                bool edge = i == 0 || i == ticks - 1;
                var align = i == 0 ? AxisAlign.Left
                          : i == ticks - 1 ? AxisAlign.Right
                          : AxisAlign.Center;
                string text = axis.TimeAt(t).ToString(edge ? edgeFmt : midFmt, CultureInfo.InvariantCulture);
                AddAxisLabel(text, t * w, w, align);
            }
        }

        private double MeasureLabel(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 9 };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width;
        }

        private void AddAxisLabel(string text, double x, double w, AxisAlign align)
        {
            var tb = new TextBlock { Text = text, FontSize = 9, Opacity = 0.55 };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = tb.DesiredSize.Width;
            double left = align switch
            {
                AxisAlign.Left  => 0,
                AxisAlign.Right => w - tw,
                _               => x - tw / 2
            };
            Canvas.SetLeft(tb, Math.Clamp(left, 0, Math.Max(0, w - tw)));
            XAxisCanvas.Children.Add(tb);
        }

        private void DrawMarkers(double w, double h)
        {
            if (MarkerCanvas == null) return;
            MarkerCanvas.Children.Clear();
            StreamLabelCanvas.Children.Clear();
            if (w < 4 || h < 4) return;

            // Scale lines first so the event markers draw on top of them. The two must
            // stay tellable apart: solid and faint is the scale, dashed and brighter is
            // "the stream restarted here". Same tone as the horizontal mid grid line.
            foreach (double t in _tickFractions)
            {
                MarkerCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = t * w, X2 = t * w, Y1 = 0, Y2 = h,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                });
            }

            var axis = TimeAxis;
            if (axis is not { IsUsable: true }) return;

            // One dashed line per boundary between live streams. The first start and the
            // last stop of the session are the chart edges, so they are not drawn.
            // Yellow, because it is the one hue no data series uses (orange, red, cyan,
            // purple, green, blue are all taken) and grey was lost against the scale lines.
            var gaps = axis.GapFractions().ToList();
            if (gaps.Count == 0) return;   // a single stream: nothing to tell apart

            foreach (double t in gaps)
            {
                MarkerCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = t * w, X2 = t * w, Y1 = 0, Y2 = h,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xB0, StreamMarker.R, StreamMarker.G, StreamMarker.B)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    IsHitTestVisible = false
                });
            }

            // A label at the START of each stream: S1 on the left edge, S2 on the first
            // boundary, and so on — N streams have only N-1 lines, so labelling lines alone
            // would leave the first stream nameless. Labels that would collide go to a second
            // row; if that is full too the label is skipped, the line stays, and the numbering
            // of the ones drawn stays true.
            var starts = new List<double>(gaps.Count + 1) { 0 };
            starts.AddRange(gaps);
            var labelBrush = new SolidColorBrush(StreamMarker);
            double[] rowEnd = { double.NegativeInfinity, double.NegativeInfinity };
            for (int i = 0; i < starts.Count; i++)
            {
                var label = new Border
                {
                    Background   = new SolidColorBrush(Color.FromArgb(0xC0, 0x16, 0x16, 0x16)),
                    CornerRadius = new CornerRadius(2),
                    Padding      = new Thickness(3, 0, 3, 0),
                    Child = new TextBlock
                    {
                        Text       = $"S{i + 1}",
                        FontSize   = 9,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = labelBrush,
                    },
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = label.DesiredSize.Width, lh = label.DesiredSize.Height;
                double left = Math.Clamp(starts[i] * w + 3, 0, Math.Max(0, w - lw));

                int row = left >= rowEnd[0] ? 0 : left >= rowEnd[1] ? 1 : -1;
                if (row < 0) continue;
                rowEnd[row] = left + lw + 2;

                Canvas.SetLeft(label, left);
                Canvas.SetTop(label, 2 + row * (lh + 2));
                StreamLabelCanvas.Children.Add(label);
            }
        }

        private static readonly Color StreamMarker = Color.FromArgb(0xFF, 0xFF, 0xEE, 0x58);

        private void HideHover()
        {
            if (HoverGuide == null) return;
            HoverGuide.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            HoverDot.Visibility   = Microsoft.UI.Xaml.Visibility.Collapsed;
            HoverPopup.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            foreach (var d in _extraDots) d.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private Microsoft.UI.Xaml.Shapes.Ellipse DotFor(int series)
        {
            if (series == 0) return HoverDot;
            while (_extraDots.Count < series)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = HoverDot.Width, Height = HoverDot.Height,
                    Stroke = HoverDot.Stroke, StrokeThickness = HoverDot.StrokeThickness,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    IsHitTestVisible = false,
                };
                Canvas.SetZIndex(dot, Canvas.GetZIndex(HoverDot));
                ChartCanvas.Children.Add(dot);
                _extraDots.Add(dot);
            }
            return _extraDots[series - 1];
        }

        private void ChartCanvas_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            => HideHover();

        private void ChartCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ChartCanvas == null || HoverGuide == null) return;
            if (_plotX.Count == 0 || _plotSeries.Count == 0) { HideHover(); return; }

            double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
            if (w < 4 || h < 4) { HideHover(); return; }
            double px = e.GetCurrentPoint(ChartCanvas).Position.X;

            // Snap on X alone, which is Chart.js "index" mode with intersect:false. The
            // pointer never has to find the line itself, and that is what makes it usable.
            int idx = 0; double best = double.MaxValue;
            for (int i = 0; i < _plotX.Count; i++)
            {
                double d = Math.Abs(_plotX[i] - px);
                if (d < best) { best = d; idx = i; }
            }

            double x = _plotX[idx];
            HoverGuide.X1 = HoverGuide.X2 = x;
            HoverGuide.Y1 = 0; HoverGuide.Y2 = h;
            HoverGuide.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

            // One dot per series. The popup sits above the topmost of them (smallest y).
            float v0 = 0f;
            double y = double.MaxValue;
            for (int si = 0; si < _plotSeries.Count; si++)
            {
                var s = _plotSeries[si];
                float v = idx < s.Values.Count ? s.Values[idx] : 0f;
                double sy = _plotMargin + _plotChartH - (v / _plotMax) * _plotChartH;
                if (si == 0) v0 = v;
                if (sy < y) y = sy;

                var dot = DotFor(si);
                dot.Fill = new SolidColorBrush(s.Color);
                Canvas.SetLeft(dot, x - dot.Width / 2);
                Canvas.SetTop(dot, sy - dot.Height / 2);
                dot.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }

            var axis = TimeAxis;
            double t = _plotX.Count > 1 ? (double)idx / (_plotX.Count - 1) : 0;
            HoverTime.Text = axis is { IsUsable: true }
                ? axis.TimeAt(t).ToString(
                      axis.ActiveDuration < TimeSpan.FromMinutes(10) ? "HH:mm:ss" : "HH:mm",
                      CultureInfo.InvariantCulture)
                : string.Empty;
            HoverTime.Visibility = string.IsNullOrEmpty(HoverTime.Text)
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;

            HoverValue.Text = _plotSeries.Count == 1
                ? FormatValue(v0)
                : string.Join("   ", _plotSeries.Select(s =>
                      $"{s.Label} {FormatValue(idx < s.Values.Count ? s.Values[idx] : 0f)}"));

            HoverPopup.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            HoverPopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double pw = HoverPopup.DesiredSize.Width, ph = HoverPopup.DesiredSize.Height;
            // Flip to the left of the guide rather than let the popup clip at the edge.
            double left = x + 8 + pw > w ? x - 8 - pw : x + 8;
            Canvas.SetLeft(HoverPopup, Math.Clamp(left, 0, Math.Max(0, w - pw)));
            Canvas.SetTop(HoverPopup, Math.Clamp(y - ph - 8, 0, Math.Max(0, h - ph)));
        }
    }

    /// <summary>
    /// Maps a position along a telemetry series to wall-clock time.
    /// Samples only accrue while a stream is live, so the axis walks the live spans and
    /// skips the idle gaps between them: the chart is compressed to active time and each
    /// gap is marked with a vertical line instead of being drawn as empty width.
    /// WARNING: pixel distance is therefore NOT proportional to wall-clock time across a
    /// gap. That is deliberate: the markers say where the jumps are, and the hover readout
    /// always reports the true timestamp of the sample under the pointer.
    /// </summary>
    public sealed class ChartTimeAxis
    {
        private readonly List<(DateTime Start, DateTime End, double Seconds)> _spans = new();

        public ChartTimeAxis(IEnumerable<StreamSpan>? spans, DateTime? fallbackEnd)
        {
            if (spans != null)
            {
                foreach (var s in spans)
                {
                    DateTime end = s.End ?? fallbackEnd ?? s.Start;
                    double secs = (end - s.Start).TotalSeconds;
                    if (secs > 0) _spans.Add((s.Start, end, secs));
                }
            }

            // No spans recorded: the session pre-dates the feature, and there is no way
            // to tell a single uninterrupted stream from a merged one after the fact.
            // Synthesising one span across StartTime..EndTime would be right for the
            // former and wrong for the latter, silently — the 11/09/2026 record held 11
            // streams with idle gaps, and every label after the first would be off by up
            // to ~110 s. So the axis stays unusable and the chart keeps "0 -> duration".

            ActiveSeconds = _spans.Sum(s => s.Seconds);
        }

        public double   ActiveSeconds  { get; }
        public TimeSpan ActiveDuration => TimeSpan.FromSeconds(ActiveSeconds);
        public bool     IsUsable       => _spans.Count > 0 && ActiveSeconds > 0;

        /// <summary>Wall-clock time at a 0..1 position across the series.</summary>
        public DateTime TimeAt(double t)
        {
            if (!IsUsable) return DateTime.MinValue;
            double target = Math.Clamp(t, 0, 1) * ActiveSeconds;
            double acc = 0;
            foreach (var s in _spans)
            {
                if (target <= acc + s.Seconds) return s.Start.AddSeconds(target - acc);
                acc += s.Seconds;
            }
            return _spans[^1].End;
        }

        /// <summary>Positions in 0..1 where one stream ended and the next began.</summary>
        public IEnumerable<double> GapFractions()
        {
            if (!IsUsable) yield break;
            double acc = 0;
            for (int i = 0; i < _spans.Count - 1; i++)
            {
                acc += _spans[i].Seconds;
                yield return acc / ActiveSeconds;
            }
        }
    }
}
