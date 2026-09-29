using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace StreamTweak.Controls
{
    /// <summary>
    /// A small, axis-less trend line for the Dashboard vitals: an area, the line and a dot on
    /// the latest value. Scaled to its own min/max so a flat metric still shows its movement.
    /// Built in code (no XAML) because it is nothing but three shapes redrawn on change.
    /// </summary>
    public sealed class MiniSparkline : Grid
    {
        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(IReadOnlyList<float>), typeof(MiniSparkline),
                new PropertyMetadata(null, (d, _) => ((MiniSparkline)d).Redraw()));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(MiniSparkline),
                new PropertyMetadata(null, (d, _) => ((MiniSparkline)d).ApplyBrushes()));

        /// <summary>Lower bound of the scale; NaN = the data's own minimum.</summary>
        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(MiniSparkline),
                new PropertyMetadata(double.NaN, (d, _) => ((MiniSparkline)d).Redraw()));

        public IReadOnlyList<float>? Data
        {
            get => (IReadOnlyList<float>?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public Brush? Stroke
        {
            get => (Brush?)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        private readonly Polygon  _area = new() { StrokeThickness = 0, IsHitTestVisible = false };
        private readonly Polyline _line = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        private readonly Ellipse  _dot  = new() { Width = 4, Height = 4, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Canvas   _canvas = new() { IsHitTestVisible = false };

        private static readonly Color Mint = Color.FromArgb(0xFF, 0x4a, 0xde, 0x80);

        public MiniSparkline()
        {
            _canvas.Children.Add(_area);
            _canvas.Children.Add(_line);
            _canvas.Children.Add(_dot);
            Children.Add(_canvas);
            SizeChanged += (_, _) => Redraw();
            ApplyBrushes();
        }

        private void ApplyBrushes()
        {
            Color c = Stroke is SolidColorBrush sb ? sb.Color : Mint;
            var line = new SolidColorBrush(c);
            _line.Stroke = line;
            _dot.Fill = line;
            _area.Fill = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        }

        private void Redraw()
        {
            _line.Points.Clear();
            _area.Points.Clear();
            _dot.Visibility = Visibility.Collapsed;

            var data = Data;
            double w = ActualWidth, h = ActualHeight;
            if (data == null || data.Count < 2 || w < 4 || h < 4) return;

            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var v in data) { if (v < mn) mn = v; if (v > mx) mx = v; }
            if (!double.IsNaN(Minimum)) mn = (float)Math.Min(mn, Minimum);
            float range = mx - mn;
            if (range <= 0f) { range = Math.Max(1f, Math.Abs(mx)); mn = mx - range / 2f; }

            const double top = 3, bottom = 2;
            double chartH = h - top - bottom;
            int n = data.Count;
            Point last = default;
            for (int i = 0; i < n; i++)
            {
                double x = i * (w - 1) / (n - 1);
                double y = top + chartH - (data[i] - mn) / range * chartH;
                last = new Point(x, y);
                _line.Points.Add(last);
                _area.Points.Add(last);
            }
            _area.Points.Add(new Point(w - 1, h));
            _area.Points.Add(new Point(0, h));

            Canvas.SetLeft(_dot, last.X - _dot.Width / 2);
            Canvas.SetTop(_dot, last.Y - _dot.Height / 2);
            _dot.Visibility = Visibility.Visible;
        }
    }
}
