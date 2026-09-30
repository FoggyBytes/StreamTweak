using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace StreamTweak.Controls
{
    /// <summary>One session on the Dashboard's performance trend.</summary>
    /// <param name="HostLatency">Negative when the session did not record it.</param>
    public sealed record TrendPoint(DateTime When, string SessionId, string Title, double Rtt, double HostLatency);

    /// <summary>
    /// The Dashboard's performance trend, drawn the way the Sessions timeline is: one lane per
    /// series with its name, value and "avg · max" in a label column, the same colours, grid
    /// and borders, and a crosshair that makes every lane header read the session under the
    /// pointer while a tip on the axis names it. One point per session, oldest to newest,
    /// evenly spaced; the axis carries the sessions' dates. A click opens that session.
    ///
    /// Not a TimelineChart: that one is built around a single session's clock (streams, games,
    /// zoom). The look is copied from it, not shared — keep the two in step when one changes.
    /// </summary>
    public sealed class TrendChart : Grid
    {
        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<TrendPoint>), typeof(TrendChart),
                new PropertyMetadata(null, (d, _) => ((TrendChart)d).BuildLayout()));

        public IReadOnlyList<TrendPoint>? Points
        {
            get => (IReadOnlyList<TrendPoint>?)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        /// <summary>Raised with the session id when a point is clicked.</summary>
        public event Action<string>? SessionClicked;

        // ── Palette (TimelineChart's) ─────────────────────────────────────────

        private static readonly Color S1       = C(0xFF, 0x39, 0x87, 0xe5);
        private static readonly Color GridLine = C(0x10, 0xFF, 0xFF, 0xFF);
        private static readonly Color Stroke   = C(0x14, 0xFF, 0xFF, 0xFF);
        private static readonly Color Text2    = C(0xFF, 0xC8, 0xCF, 0xCB);
        private static readonly Color Text3    = C(0xFF, 0x92, 0x9A, 0x96);
        private static readonly Color MintTh   = C(0xA0, 0x4a, 0xde, 0x80);
        private static readonly Color AmberTh  = C(0xB0, 0xfb, 0xbf, 0x24);

        private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
        private static SolidColorBrush B(Color c) => new(c);

        private sealed class Lane
        {
            public string Name = "", Term = "", Tip = "";
            public Func<TrendPoint, double> Value = _ => 0;
            public readonly List<(double V, Color Color)> Thresholds = new();
            public TextBlock? ValueText, Sub;
            public readonly Canvas Plot = new();
        }

        private readonly List<Lane> _lanes = new();
        private readonly Grid _lanesGrid = new();
        private readonly Canvas _hit = new() { Background = B(Colors.Transparent) };
        private readonly Canvas _overlay = new() { IsHitTestVisible = false };
        private readonly Canvas _axisCanvas = new();
        private readonly Rectangle _crosshair = new() { Width = 1, Fill = B(C(0x90, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
        private readonly Border _tip = new()
        {
            Background = B(C(0xFF, 0x2a, 0x2e, 0x2e)), BorderBrush = B(C(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
            Visibility = Visibility.Collapsed,
        };
        private readonly TextBlock _tipText = new() { FontSize = 11.5 };

        private const double AxisH = 24, MinLaneH = 56;
        private double _labelW = 150, _plotW;
        private int _hover = -1;
        private bool _redrawQueued;

        public TrendChart()
        {
            _tip.Child = _tipText;
            _tipText.Foreground = B(Text2);
            if (Application.Current.Resources.TryGetValue("JetBrainsMono", out var mono) && mono is FontFamily ff)
                _tipText.FontFamily = ff;

            _lanesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_labelW) });
            _lanesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _overlay.Children.Add(_crosshair);
            _overlay.Children.Add(_tip);
            Children.Add(_lanesGrid);

            _hit.PointerMoved  += (_, e) => ShowCursorAt(e.GetCurrentPoint(_hit).Position.X);
            _hit.PointerExited += (_, _) => HideCursor();
            _hit.PointerPressed += (_, e) =>
            {
                ShowCursorAt(e.GetCurrentPoint(_hit).Position.X);
                if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch && _hover >= 0 && Points is { } p)
                    SessionClicked?.Invoke(p[_hover].SessionId);
            };

            SizeChanged += (_, e) =>
            {
                double lw = Math.Clamp(Math.Round(e.NewSize.Width * 0.15), 150, 190);
                if (Math.Abs(lw - _labelW) > 0.5)
                {
                    _labelW = lw;
                    _lanesGrid.ColumnDefinitions[0].Width = new GridLength(_labelW);
                }
                QueueRedraw();
            };
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void BuildLayout()
        {
            _lanesGrid.Children.Clear();
            _lanesGrid.RowDefinitions.Clear();
            _lanes.Clear();
            var pts = Points;
            if (pts == null || pts.Count < 2) return;

            var rtt = new Lane { Name = "RTT", Term = "RTT", Tip = "Average round-trip time of each session.", Value = p => p.Rtt };
            rtt.Thresholds.Add((25, MintTh));   // the grade's own limits, as on the Sessions timeline
            rtt.Thresholds.Add((60, AmberTh));
            _lanes.Add(rtt);
            if (pts.Any(p => p.HostLatency > 0))
                _lanes.Add(new Lane { Name = "Host frame latency", Term = "Host Frame Latency",
                                      Tip = "Average time the host took to capture and encode a frame, per session.",
                                      Value = p => p.HostLatency });

            int row = 0;
            foreach (var l in _lanes)
            {
                // Star rows: the lanes share whatever height the card gives the chart.
                _lanesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = MinLaneH });
                var head = new Border { BorderBrush = B(Stroke), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 12, 6), Child = Header(l) };
                SetRow(head, row);
                var shell = new Border { BorderBrush = B(Stroke), BorderThickness = new Thickness(0, 1, 0, 0), Child = l.Plot };
                SetRow(shell, row); SetColumn(shell, 1);
                _lanesGrid.Children.Add(head);
                _lanesGrid.Children.Add(shell);
                row++;
            }
            _lanesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(AxisH) });
            SetRow(_axisCanvas, row); SetColumn(_axisCanvas, 1);
            _lanesGrid.Children.Add(_axisCanvas);

            SetRow(_hit, 0); SetColumn(_hit, 1); SetRowSpan(_hit, _lanes.Count);
            _lanesGrid.Children.Add(_hit);
            SetRow(_overlay, 0); SetColumn(_overlay, 1); SetRowSpan(_overlay, _lanes.Count + 1);
            _lanesGrid.Children.Add(_overlay);

            _hover = -1;
            QueueRedraw();
        }

        private static UIElement Header(Lane l)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(Label(l.Name, 12.5, Text2, medium: true));
            name.Children.Add(new InfoHint { Term = l.Term, Tip = l.Tip });
            sp.Children.Add(name);

            var vr = new StackPanel { Orientation = Orientation.Horizontal };
            l.ValueText = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = B(C(0xFF, 0xF2, 0xF5, 0xF3)) };
            if (Application.Current.Resources.TryGetValue("DMSansSemiBold", out var f) && f is FontFamily ff) l.ValueText.FontFamily = ff;
            var unit = Label("ms", 11.5, Text3, medium: true);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(3, 0, 0, 2);
            vr.Children.Add(l.ValueText);
            vr.Children.Add(unit);
            sp.Children.Add(vr);
            l.Sub = Label("", 11.5, Text3);
            sp.Children.Add(l.Sub);
            return sp;
        }

        private static TextBlock Label(string text, double size, Color color, bool medium = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = B(color), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            if (Application.Current.Resources.TryGetValue(medium ? "DMSansMedium" : "DMSans", out var f) && f is FontFamily ff) t.FontFamily = ff;
            return t;
        }

        // ── Drawing ───────────────────────────────────────────────────────────

        private void QueueRedraw()
        {
            if (_redrawQueued) return;
            _redrawQueued = true;
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _redrawQueued = false;
                Redraw();
            });
        }

        private void Redraw()
        {
            var pts = Points;
            if (pts == null || pts.Count < 2 || _lanes.Count == 0) return;
            _plotW = Math.Max(0, ActualWidth - _labelW);
            if (_plotW < 40) return;

            var ticks = Ticks(pts);
            foreach (var l in _lanes) DrawLane(l, pts, ticks);
            DrawAxis(ticks);
            if (_hover >= 0) ShowIndex(_hover); else ShowDefaults();
        }

        private double X(int i, int n) => n <= 1 ? 0 : (double)i / (n - 1) * _plotW;

        private void DrawLane(Lane l, IReadOnlyList<TrendPoint> pts, List<(int I, string Label)> ticks)
        {
            var cv = l.Plot;
            cv.Children.Clear();
            double w = _plotW, h = cv.ActualHeight;
            if (h <= 0) return;
            cv.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
            const double top = 6, bottom = 4;
            int n = pts.Count;

            var vals = pts.Select(l.Value).ToList();
            var real = vals.Where(v => v >= 0).ToList();
            double dmax = real.Count == 0 ? 1 : real.Max() * 1.12;
            // Grade limits only when the data comes near them, as on the timeline.
            var ths = l.Thresholds.Where(t => t.V <= dmax * 1.25).ToList();
            double ymax = NiceMax(Math.Max(dmax, ths.Count > 0 ? ths.Max(t => t.V) : 0) * 1.08);
            double Y(double v) => top + (1 - Math.Min(Math.Max(v, 0), ymax) / ymax) * (h - top - bottom);

            foreach (var (i, _) in ticks) cv.Children.Add(VLine(X(i, n), 0, h, GridLine));
            foreach (double fr in new[] { 0.5, 1.0 }) cv.Children.Add(HLine(0, w, Y(ymax * fr), GridLine, null));
            foreach (var t in ths) cv.Children.Add(HLine(0, w, Y(t.V), t.Color, new DoubleCollection { 4, 3 }));

            // A session that did not record the series breaks the line instead of dropping to 0.
            var run = new List<Point>();
            void Flush()
            {
                if (run.Count == 0) return;
                var area = new Polygon { Fill = B(Color.FromArgb(0x1C, S1.R, S1.G, S1.B)) };
                var ap = new PointCollection { new Point(run[0].X, h - bottom) };
                foreach (var p in run) ap.Add(p);
                ap.Add(new Point(run[^1].X, h - bottom));
                area.Points = ap;
                cv.Children.Add(area);
                var line = new Polyline { Stroke = B(S1), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
                var lp = new PointCollection();
                foreach (var p in run) lp.Add(p);
                line.Points = lp;
                cv.Children.Add(line);
                run.Clear();
            }
            for (int i = 0; i < n; i++)
            {
                if (vals[i] < 0) { Flush(); continue; }
                run.Add(new Point(X(i, n), Y(vals[i])));
            }
            Flush();
        }

        /// <summary>A date under the points, about one per 110 px, never two of the same day.</summary>
        private List<(int I, string Label)> Ticks(IReadOnlyList<TrendPoint> pts)
        {
            var list = new List<(int, string)>();
            int n = pts.Count;
            double lastX = double.NegativeInfinity;
            string lastDay = "";
            for (int i = 0; i < n; i++)
            {
                double x = X(i, n);
                string day = pts[i].When.ToString("d MMM", CultureInfo.InvariantCulture);
                if (x - lastX < 110 || day == lastDay) continue;
                list.Add((i, day));
                lastX = x;
                lastDay = day;
            }
            return list;
        }

        private void DrawAxis(List<(int I, string Label)> ticks)
        {
            _axisCanvas.Children.Clear();
            int n = Points?.Count ?? 0;
            foreach (var (i, label) in ticks)
            {
                var t = Label(label, 11, Text3);
                t.Width = 64; t.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(t, Math.Clamp(X(i, n) - 32, -8, _plotW - 56));
                Canvas.SetTop(t, 5);
                _axisCanvas.Children.Add(t);
            }
        }

        private static Line VLine(double x, double y0, double y1, Color c)
            => new() { X1 = x, X2 = x, Y1 = y0, Y2 = y1, Stroke = B(c), StrokeThickness = 1 };

        private static Line HLine(double x0, double x1, double y, Color c, DoubleCollection? dash)
        {
            var ln = new Line { X1 = x0, X2 = x1, Y1 = y, Y2 = y, Stroke = B(c), StrokeThickness = 1 };
            if (dash != null) ln.StrokeDashArray = dash;
            return ln;
        }

        // ── Crosshair and readouts ────────────────────────────────────────────

        private void ShowCursorAt(double x)
        {
            var pts = Points;
            if (pts == null || pts.Count < 2 || _plotW <= 0) return;
            int i = Math.Clamp((int)Math.Round(Math.Clamp(x, 0, _plotW) / _plotW * (pts.Count - 1)), 0, pts.Count - 1);
            ShowIndex(i);
        }

        private void ShowIndex(int i)
        {
            var pts = Points;
            if (pts == null || i < 0 || i >= pts.Count) return;
            _hover = i;
            double x = X(i, pts.Count);
            double lanesH = Math.Max(0, _overlay.ActualHeight - AxisH);
            _crosshair.Height = lanesH;
            Canvas.SetLeft(_crosshair, x);
            _crosshair.Visibility = Visibility.Visible;

            var p = pts[i];
            _tipText.Text = $"{p.When.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}" + (string.IsNullOrEmpty(p.Title) ? "" : $" · {p.Title}");
            _tip.Visibility = Visibility.Visible;
            _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = _tip.DesiredSize.Width;
            Canvas.SetLeft(_tip, Math.Clamp(x - tw / 2, 0, Math.Max(0, _plotW - tw)));
            Canvas.SetTop(_tip, lanesH + 2);

            foreach (var l in _lanes)
            {
                if (l.ValueText == null || l.Sub == null) continue;
                double v = l.Value(p);
                l.ValueText.Text = v >= 0 ? Fmt(v) : "—";
                l.Sub.Text = "this session · click to open";
            }
        }

        private void HideCursor()
        {
            _hover = -1;
            _crosshair.Visibility = Visibility.Collapsed;
            _tip.Visibility = Visibility.Collapsed;
            ShowDefaults();
        }

        /// <summary>Lane headers at rest: the average over the period, and its worst session.</summary>
        private void ShowDefaults()
        {
            var pts = Points;
            if (pts == null) return;
            foreach (var l in _lanes)
            {
                if (l.ValueText == null || l.Sub == null) continue;
                var v = pts.Select(l.Value).Where(x => x >= 0).ToList();
                if (v.Count == 0) { l.ValueText.Text = "—"; l.Sub.Text = ""; continue; }
                l.ValueText.Text = Fmt(v.Average());
                l.Sub.Text = $"avg · max {Fmt(v.Max())}";
            }
        }

        private static double NiceMax(double v)
        {
            if (v <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
                if (v <= m * mag) return m * mag;
            return 10 * mag;
        }

        private static string Fmt(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
