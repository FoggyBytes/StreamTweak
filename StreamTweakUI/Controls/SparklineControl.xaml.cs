using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace StreamTweak.Controls
{
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

            var data = Data;
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;

            XEndLabel.Text = DurationLabel;

            if (data == null || data.Count < 2 || w < 4 || h < 4)
            {
                DataLine.Points.Clear();
                MaxLabel.Text = string.Empty;
                MidLabel.Text = string.Empty;
                return;
            }

            int windowSize = WindowSize;

            // In fixed-window mode skip bucket averaging — points map 1:1 to time slots
            var pts = windowSize > 0 ? data.ToList() : BucketAverage(data, (int)w);

            float max = pts.Max();
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
            int n = pts.Count;

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
        /// Formats a float value using at most 3 significant digits,
        /// no trailing zeros, invariant (dot) decimal separator.
        /// Examples: 2.0 → "2", 1.23 → "1.23", 81.8 → "81.8"
        /// </summary>
        private static string FormatValue(float v)
            => v == 0f ? "0" : v.ToString("G3", CultureInfo.InvariantCulture);
    }
}
