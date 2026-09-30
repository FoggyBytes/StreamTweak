using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace StreamTweak.Controls
{
    public enum TimelineMode { Tracks, Focus }

    /// <summary>
    /// The 9.0 session timeline: every series of a session on one clock, one lane each.
    ///
    ///  • A crosshair runs through all lanes and every lane header reads its value under it,
    ///    so "what else happened when host latency spiked" is one hover away.
    ///  • Zoom: drag across the lanes (mouse) or across the overview strip below them
    ///    (mouse, pen or touch); Ctrl+wheel zooms around the cursor; double-tap resets.
    ///    Clicking a game in the top lane zooms to the time it was played.
    ///  • Focus mode shows one lane large, with a value axis, the grade thresholds as
    ///    dashed lines and the percentiles of what is in view.
    ///
    /// Everything is drawn in code on Canvases (no XAML template): the lane set depends on
    /// what the session recorded, and a redraw on every drag frame has to stay cheap. Series
    /// are stored as ≤ 600 points over the active stream time (SessionLogger), so position
    /// 0..1 along a series is the same instant for all of them; <see cref="ChartTimeAxis"/>
    /// turns that position into wall-clock time, skipping the idle gaps between streams.
    /// </summary>
    public sealed class TimelineChart : Grid
    {
        // ── Dependency properties ─────────────────────────────────────────────

        public static readonly DependencyProperty SessionProperty =
            DependencyProperty.Register(nameof(Session), typeof(SessionEntry), typeof(TimelineChart),
                new PropertyMetadata(null, (d, _) => ((TimelineChart)d).OnSessionChanged()));

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(TimelineMode), typeof(TimelineChart),
                new PropertyMetadata(TimelineMode.Tracks, (d, _) => ((TimelineChart)d).BuildLayout()));

        public static readonly DependencyProperty LaneHeightProperty =
            DependencyProperty.Register(nameof(LaneHeight), typeof(double), typeof(TimelineChart),
                new PropertyMetadata(72.0, (d, _) => ((TimelineChart)d).BuildLayout()));

        public SessionEntry? Session
        {
            get => (SessionEntry?)GetValue(SessionProperty);
            set => SetValue(SessionProperty, value);
        }

        public TimelineMode Mode
        {
            get => (TimelineMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        /// <summary>Height of one lane in the all-tracks view; Focus uses four times it.</summary>
        public double LaneHeight
        {
            get => (double)GetValue(LaneHeightProperty);
            set => SetValue(LaneHeightProperty, value);
        }

        /// <summary>True while the view shows part of the session rather than all of it.</summary>
        public bool IsZoomed => _z0 > 0.0005 || _z1 < 0.9995;

        /// <summary>Raised when the zoom window changes (the page shows "Whole session" then).</summary>
        public event EventHandler? ZoomChanged;

        /// <summary>True when the session has at least one series to draw.</summary>
        public bool HasData => _lanes.Count > 0;

        /// <summary>How many lane heights the chart stacks in the current mode (Focus: one lane
        /// four times as tall). Lets a host that wants the chart to fill a height size the lanes.</summary>
        public int LaneHeightUnits => Mode == TimelineMode.Focus ? 4 : Math.Max(1, _lanes.Count);

        public void ResetZoom() => SetZoom(0, 1);

        // ── Palette (validated for the dark surface, see the 9.0 mockup) ──────

        private static readonly Color S1 = C(0xFF, 0x39, 0x87, 0xe5);
        private static readonly Color S2 = C(0xFF, 0xd9, 0x59, 0x26);
        private static readonly Color S3 = C(0xFF, 0x19, 0x9e, 0x70);
        private static readonly Color GridLine = C(0x10, 0xFF, 0xFF, 0xFF);
        private static readonly Color GapLine  = C(0x55, 0xFF, 0xFF, 0xFF);
        private static readonly Color Stroke   = C(0x14, 0xFF, 0xFF, 0xFF);
        private static readonly Color Text2    = C(0xFF, 0xC8, 0xCF, 0xCB);
        private static readonly Color Text3    = C(0xFF, 0x92, 0x9A, 0x96);
        private static readonly Color Text4    = C(0xFF, 0x64, 0x6B, 0x67);
        private static readonly Color MintTh   = C(0xA0, 0x4a, 0xde, 0x80);
        private static readonly Color AmberTh  = C(0xB0, 0xfb, 0xbf, 0x24);
        private static readonly Color RedTh    = C(0xB0, 0xf8, 0x71, 0x71);
        private static readonly Color[] GamePalette =
        {
            C(0x66, 0x39, 0x87, 0xe5), C(0x66, 0x19, 0x9e, 0x70), C(0x66, 0xd9, 0x59, 0x26),
            C(0x66, 0x8b, 0x5c, 0xf6), C(0x66, 0xdb, 0x27, 0x77),
        };

        private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
        private static SolidColorBrush B(Color c) => new(c);

        // ── Model ─────────────────────────────────────────────────────────────

        private sealed class Series
        {
            public string Label = "";
            public Color Color;
            public IReadOnlyList<float> Data = Array.Empty<float>();
        }

        private sealed class Lane
        {
            public string Key = "", Name = "", Unit = "", Term = "", Tip = "";
            public int Decimals = 1;
            public readonly List<Series> Series = new();
            public bool Envelope, Bars, HigherIsBetter;
            public double FixedMax;                                    // 0 = auto
            public float Target;                                       // 0 = none
            public readonly List<(double V, string Label, Color Color)> Thresholds = new();
            public string Note = "";

            // UI, rebuilt by BuildLayout
            public TextBlock? Value, UnitText, Sub;
            public readonly List<TextBlock> LegendValues = new();
            public Canvas? Plot;
            public double Height;
        }

        private readonly List<Lane> _lanes = new();
        private ChartTimeAxis? _axis;
        private double _z0, _z1 = 1;
        private string _focusKey = "hl";

        // ── Visual tree (built once) ──────────────────────────────────────────

        private readonly StackPanel _focusBar = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
        private readonly Grid _lanesGrid = new();
        private readonly Canvas _hit = new() { Background = B(Colors.Transparent) };
        private readonly Canvas _overlay = new() { IsHitTestVisible = false };
        private readonly Rectangle _crosshair = new() { Width = 1, Fill = B(C(0x90, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
        private readonly Rectangle _selection = new() { Fill = B(C(0x1F, 0x4a, 0xde, 0x80)), Stroke = B(C(0x80, 0x4a, 0xde, 0x80)), StrokeThickness = 1, Visibility = Visibility.Collapsed };
        private readonly Border _timeTip = new()
        {
            Background = B(C(0xFF, 0x2a, 0x2e, 0x2e)), BorderBrush = B(C(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
            Visibility = Visibility.Collapsed,
        };
        private readonly TextBlock _timeTipText = new() { FontSize = 11.5 };
        private readonly Canvas _gameCanvas = new();
        private readonly Border _gameShell = new() { BorderBrush = B(Stroke), BorderThickness = new Thickness(0, 1, 0, 0) };
        private readonly Canvas _axisCanvas = new();
        private readonly Border _ovBorder = new()
        {
            Height = 46, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = B(C(0x08, 0xFF, 0xFF, 0xFF)), BorderBrush = B(Stroke), Margin = new Thickness(0, 10, 0, 0),
        };
        private readonly Canvas _ov = new() { Background = B(Colors.Transparent) };
        private readonly ResponsiveGrid _focusStats = new() { MinColumnWidth = 104, MaxColumns = 6, ColumnSpacing = 14, RowSpacing = 12, Margin = new Thickness(0, 16, 0, 0) };
        private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _empty = new()
        {
            Text = "This session recorded no telemetry to chart.", FontSize = 13, Margin = new Thickness(0, 12, 0, 12),
            Visibility = Visibility.Collapsed,
        };

        private double _labelW = 150;
        private double _plotW;
        private double _padL, _padR;         // value-axis room inside the plots (Focus only)
        private bool _redrawQueued;

        public TimelineChart()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // focus bar
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // lanes
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // focus stats
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // note
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // empty

            _timeTip.Child = _timeTipText;
            _timeTipText.Foreground = B(Text2);
            _note.Foreground = B(Text3);
            _empty.Foreground = B(Text3);
            if (Application.Current.Resources.TryGetValue("JetBrainsMono", out var mono) && mono is FontFamily ff)
                _timeTipText.FontFamily = ff;

            _lanesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_labelW) });
            _lanesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _overlay.Children.Add(_selection);
            _overlay.Children.Add(_crosshair);
            _overlay.Children.Add(_timeTip);

            _ovBorder.Child = _ov;
            _gameShell.Child = _gameCanvas;   // kept for the control's lifetime: a Canvas can have one parent
            _ov.ManipulationMode = ManipulationModes.TranslateX;   // a drag here zooms instead of scrolling the page

            SetRow(_focusBar, 0);
            SetRow(_lanesGrid, 1);
            SetRow(_focusStats, 2);
            SetRow(_note, 3);
            SetRow(_empty, 4);
            Children.Add(_focusBar);
            Children.Add(_lanesGrid);
            Children.Add(_focusStats);
            Children.Add(_note);
            Children.Add(_empty);

            _hit.PointerMoved       += Hit_PointerMoved;
            _hit.PointerExited      += (_, _) => { if (!_selecting) HideCursor(); };
            _hit.PointerPressed     += Hit_PointerPressed;
            _hit.PointerReleased    += Hit_PointerReleased;
            _hit.PointerCaptureLost += (_, _) => { _selecting = false; _selection.Visibility = Visibility.Collapsed; };
            _hit.PointerWheelChanged += Hit_PointerWheelChanged;
            _hit.DoubleTapped       += (_, e) => { ResetZoom(); e.Handled = true; };

            _ov.PointerPressed     += Ov_PointerPressed;
            _ov.PointerMoved       += Ov_PointerMoved;
            _ov.PointerReleased    += Ov_PointerReleased;
            _ov.PointerCaptureLost += (_, _) => _ovDragging = false;
            _ov.DoubleTapped       += (_, e) => { ResetZoom(); e.Handled = true; };

            _gameCanvas.PointerMoved  += (s, e) => ShowCursorAt(e.GetCurrentPoint(_gameCanvas).Position.X);
            _gameCanvas.PointerExited += (_, _) => HideCursor();

            SizeChanged += (_, e) =>
            {
                // 150 at least: "Host frame latency" and "Games & streams" with their ⓘ were cut
                // off in the detail beside the list.
                double lw = Math.Clamp(Math.Round(e.NewSize.Width * 0.15), 150, 190);
                if (Math.Abs(lw - _labelW) > 0.5)
                {
                    _labelW = lw;
                    _lanesGrid.ColumnDefinitions[0].Width = new GridLength(_labelW);
                }
                QueueRedraw();
            };
        }

        // ── Session → lanes ───────────────────────────────────────────────────

        private void OnSessionChanged()
        {
            _lanes.Clear();
            var s = Session;
            _z0 = 0; _z1 = 1;
            if (s != null)
            {
                _axis = new ChartTimeAxis(s.StreamSpans, s.EndTime);
                BuildLanes(s);
            }
            else _axis = null;

            if (_lanes.Count > 0 && _lanes.All(l => l.Key != _focusKey))
                _focusKey = _lanes[0].Key;
            BuildLayout();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BuildLanes(SessionEntry s)
        {
            var q = s.QualityStats;
            int fps = q?.TargetFps ?? 0;
            float frame = QualityGradeCalculator.FramePeriodMs(fps);
            string fpsText = fps > 0 ? $"{fps} fps" : "60 fps (assumed: this session did not record its frame rate)";

            if (s.RttTimeSeries is { Count: >= 2 })
            {
                var l = new Lane { Key = "rtt", Name = "RTT", Unit = "ms", Term = "RTT", Envelope = true,
                                   Tip = "Round-trip time between client and host." };
                l.Series.Add(new Series { Label = "RTT", Color = S1, Data = s.RttTimeSeries });
                l.Thresholds.Add((25, "Excellent below 25 ms", MintTh));
                l.Thresholds.Add((60, "Good up to 60 ms", AmberTh));
                l.Note = "Dashed lines, drawn when the data comes near them: 25 ms (Excellent below) and 60 ms (Good up to), on the average RTT. A single spike over 200 ms costs one grade level.";
                _lanes.Add(l);
            }
            if (s.HostLatencyTimeSeries is { Count: >= 2 })
            {
                var l = new Lane { Key = "hl", Name = "Host frame latency", Unit = "ms", Term = "Host Frame Latency", Envelope = true,
                                   Tip = "Time the host took to capture and encode each frame." };
                l.Series.Add(new Series { Label = "Host latency", Color = S1, Data = s.HostLatencyTimeSeries });
                l.Thresholds.Add((frame * 0.6, "0.6 frame", MintTh));
                l.Thresholds.Add((frame, "1 frame", AmberTh));
                l.Thresholds.Add((frame * QualityGradeCalculator.LateFrameMultiplier, "late", RedTh));
                l.Note = $"Frame budget at {fpsText}: {frame.ToString("0.0", CultureInfo.InvariantCulture)} ms. " +
                         "Dashed lines, drawn when the data comes near them: 0.6 frame (Excellent below), 1 frame (Good up to) and 2 frames, where a frame counts as late.";
                _lanes.Add(l);
            }
            if (s.DropsTimeSeries is { Count: >= 2 })
            {
                // A stored series longer than 600 samples is kept as bucket AVERAGES
                // (SessionTelemetry.Downsample), so adding it up undercounted a long session
                // several times over (317 shown for 1,795 dropped). Scaled to the recorded total,
                // each point is the frames dropped in its slice and the lane adds up again.
                IReadOnlyList<float> drops = s.DropsTimeSeries;
                float stored = s.DropsTimeSeries.Sum();
                if (q is { TotalDrops: > 0 } && stored > 0)
                {
                    float scale = q.TotalDrops / stored;
                    drops = s.DropsTimeSeries.Select(v => v * scale).ToList();
                }
                var l = new Lane { Key = "drops", Name = "Frame drops", Unit = "frames", Term = "Frame Drops", Bars = true, Decimals = 0,
                                   Tip = "Frames the client dropped in each slice of the session." };
                l.Series.Add(new Series { Label = "Drops", Color = S2, Data = drops });
                l.Note = "Frames dropped by the client in each slice of the session. The grade reads the drop rate over the whole session: Excellent below 1 %, Good up to 2 %.";
                _lanes.Add(l);
            }
            if (s.BitrateTimeSeries is { Count: >= 2 })
            {
                var l = new Lane { Key = "br", Name = "Bitrate", Unit = "Mbps", Term = "Bitrate", HigherIsBetter = true, Decimals = 0,
                                   Target = q?.TargetBitrateMbps ?? 0,
                                   Tip = "Bitrate the client actually received." };
                l.Series.Add(new Series { Label = "Bitrate", Color = S1, Data = s.BitrateTimeSeries });
                l.Note = l.Target > 0
                    ? $"Dashed line: the {l.Target.ToString("0", CultureInfo.InvariantCulture)} Mbps target the client asked for. Bitrate is context, not part of the grade."
                    : "Bitrate is context, not part of the grade. The client did not report its target for this session.";
                _lanes.Add(l);
            }
            if (s.DecodeTimeSeries is { Count: >= 2 })
            {
                var l = new Lane { Key = "dec", Name = "Decode", Unit = "ms", Term = "Decode Latency",
                                   Tip = "Time the client took to decode each frame." };
                l.Series.Add(new Series { Label = "Decode", Color = S1, Data = s.DecodeTimeSeries });
                l.Note = "Time the client's decoder spent on each frame. Context, not part of the grade.";
                _lanes.Add(l);
            }
            var compute = new Lane { Key = "compute", Name = "Host compute", Unit = "%", Term = "GPU", FixedMax = 100, Decimals = 0,
                                     Tip = "GPU, encoder and CPU load on this PC." };
            if (s.HostGpuTimeSeries is { Count: >= 2 }) compute.Series.Add(new Series { Label = "GPU", Color = S1, Data = s.HostGpuTimeSeries });
            if (s.HostEncTimeSeries is { Count: >= 2 }) compute.Series.Add(new Series { Label = "Enc", Color = S2, Data = s.HostEncTimeSeries });
            if (s.HostCpuTimeSeries is { Count: >= 2 }) compute.Series.Add(new Series { Label = "CPU", Color = S3, Data = s.HostCpuTimeSeries });
            if (compute.Series.Count > 0)
            {
                compute.Note = "Load on this PC. The encoder normally runs near its limit on demanding streams, so it is shown as context and not graded.";
                _lanes.Add(compute);
            }
        }

        private bool HasGameLane(SessionEntry s)
            => s.GamesDetected is { Count: > 0 } || (_axis?.StreamCount ?? 0) > 1;

        // ── Layout (rows, headers, canvases) ──────────────────────────────────

        private void BuildLayout()
        {
            _lanesGrid.Children.Clear();
            _lanesGrid.RowDefinitions.Clear();
            _focusBar.Children.Clear();
            foreach (var l in _lanes) { l.Plot = null; l.Value = l.UnitText = l.Sub = null; l.LegendValues.Clear(); }

            var s = Session;
            bool focus = Mode == TimelineMode.Focus;
            _empty.Visibility = s != null && _lanes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _lanesGrid.Visibility = _lanes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _focusBar.Visibility = focus && _lanes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _focusStats.Visibility = focus && _lanes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _note.Visibility = focus && _lanes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (s == null || _lanes.Count == 0) return;

            _padL = focus ? 40 : 0;
            _padR = focus ? 10 : 0;

            if (focus)
            {
                foreach (var l in _lanes)
                {
                    var chip = new ToggleButton { Content = l.Name, Tag = l.Key, IsChecked = l.Key == _focusKey };
                    if (Application.Current.Resources.TryGetValue("ST9_Chip", out var st) && st is Style chipStyle)
                        chip.Style = chipStyle;
                    chip.Click += (sender, _) =>
                    {
                        _focusKey = (string)((FrameworkElement)sender).Tag;
                        BuildLayout();
                    };
                    _focusBar.Children.Add(chip);
                }
            }

            int row = 0;
            int firstDataRow;

            // Games & streams
            if (HasGameLane(s))
            {
                AddRow(38);
                var head = LaneHeaderShell(row);
                var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(Label("Games & streams", 12.5, Text2, medium: true));
                name.Children.Add(new InfoHint { Term = "Timeline", Tip = "When each game ran; dashed lines mark a reconnect." });
                head.Child = name;
                SetRow(_gameShell, row); SetColumn(_gameShell, 1);
                _lanesGrid.Children.Add(head);
                _lanesGrid.Children.Add(_gameShell);
                row++;
            }
            firstDataRow = row;

            IEnumerable<Lane> shown = focus ? _lanes.Where(l => l.Key == _focusKey) : _lanes;
            foreach (var l in shown)
            {
                l.Height = focus ? Math.Max(260, LaneHeight * 4) : (l.Series.Count > 1 ? Math.Max(LaneHeight, 78) : LaneHeight);
                AddRow(l.Height);
                var head = LaneHeaderShell(row);
                head.Child = BuildHeader(l, focus);
                l.Plot = new Canvas();
                _lanesGrid.Children.Add(head);
                _lanesGrid.Children.Add(PlotShell(row, l.Plot));
                row++;
            }
            int lastDataRow = row - 1;

            // Time axis
            AddRow(24);
            SetRow(_axisCanvas, row); SetColumn(_axisCanvas, 1);
            _lanesGrid.Children.Add(_axisCanvas);
            row++;

            // Overview strip + its caption
            AddRow(56);
            var ovCaption = Label("Drag to zoom", 11.5, Text4);
            ovCaption.VerticalAlignment = VerticalAlignment.Center;
            ovCaption.Margin = new Thickness(0, 10, 12, 0);
            SetRow(ovCaption, row);
            SetRow(_ovBorder, row); SetColumn(_ovBorder, 1);
            _lanesGrid.Children.Add(ovCaption);
            _lanesGrid.Children.Add(_ovBorder);

            // Hit area over the data lanes; overlay (crosshair, selection, time tip) over lanes + axis
            SetRow(_hit, firstDataRow); SetColumn(_hit, 1);
            SetRowSpan(_hit, Math.Max(1, lastDataRow - firstDataRow + 1));
            _lanesGrid.Children.Add(_hit);
            SetRow(_overlay, 0); SetColumn(_overlay, 1);
            SetRowSpan(_overlay, lastDataRow + 2);
            _lanesGrid.Children.Add(_overlay);

            var fl = _lanes.FirstOrDefault(x => x.Key == _focusKey);
            _note.Text = fl?.Note ?? "";

            QueueRedraw();
        }

        private void AddRow(double h) => _lanesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });

        private Border LaneHeaderShell(int row)
        {
            var b = new Border { BorderBrush = B(Stroke), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 12, 6) };
            SetRow(b, row);
            return b;
        }

        private static Border PlotShell(int row, Canvas canvas)
        {
            canvas.Children.Clear();
            var b = new Border { BorderBrush = B(Stroke), BorderThickness = new Thickness(0, 1, 0, 0), Child = canvas };
            SetRow(b, row); SetColumn(b, 1);
            return b;
        }

        private UIElement BuildHeader(Lane l, bool focus)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(Label(l.Name, 12.5, Text2, medium: true));
            name.Children.Add(new InfoHint { Term = l.Term, Tip = l.Tip });
            sp.Children.Add(name);

            if (l.Series.Count > 1)
            {
                foreach (var se in l.Series)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    row.Children.Add(new Rectangle { Width = 10, Height = 2, Fill = B(se.Color), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(Label(se.Label, 11.5, Text3));
                    var v = Label("", 11.5, Text2, medium: true);
                    row.Children.Add(v);
                    l.LegendValues.Add(v);
                    sp.Children.Add(row);
                }
            }
            else
            {
                var vr = new StackPanel { Orientation = Orientation.Horizontal };
                l.Value = new TextBlock { FontSize = focus ? 24 : 17, FontWeight = FontWeights.SemiBold, Foreground = B(C(0xFF, 0xF2, 0xF5, 0xF3)) };
                if (Application.Current.Resources.TryGetValue("DMSansSemiBold", out var f) && f is FontFamily ff) l.Value.FontFamily = ff;
                l.UnitText = Label(l.Unit, 11.5, Text3, medium: true);
                l.UnitText.VerticalAlignment = VerticalAlignment.Bottom;
                l.UnitText.Margin = new Thickness(3, 0, 0, focus ? 4 : 2);
                vr.Children.Add(l.Value);
                vr.Children.Add(l.UnitText);
                sp.Children.Add(vr);
                l.Sub = Label("", 11.5, Text3);
                sp.Children.Add(l.Sub);
            }
            return sp;
        }

        private static TextBlock Label(string text, double size, Color color, bool medium = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = size, Foreground = B(color),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            };
            string key = medium ? "DMSansMedium" : "DMSans";
            if (Application.Current.Resources.TryGetValue(key, out var f) && f is FontFamily ff) t.FontFamily = ff;
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
            var s = Session;
            if (s == null || _lanes.Count == 0) return;
            _plotW = Math.Max(0, ActualWidth - _labelW);
            if (_plotW < 40) return;

            var ticks = Ticks(s);
            foreach (var l in _lanes)
                if (l.Plot != null) DrawLane(l, l.Plot, _plotW, l.Height, ticks);
            if (HasGameLane(s)) DrawGameLane(s);
            DrawAxis(ticks);
            DrawOverview(s);
            ShowDefaults();
            if (Mode == TimelineMode.Focus) BuildFocusStats();
        }

        private double X(double f) => _padL + (f - _z0) / (_z1 - _z0) * (_plotW - _padL - _padR);
        private double F(double x) => _z0 + Math.Clamp((x - _padL) / Math.Max(1, _plotW - _padL - _padR), 0, 1) * (_z1 - _z0);

        /// <summary>Visible index range of a series of n points, one extra either side.</summary>
        private (int I0, int I1) Visible(int n)
        {
            int i0 = Math.Max(0, (int)Math.Floor(_z0 * (n - 1)) - 1);
            int i1 = Math.Min(n - 1, (int)Math.Ceiling(_z1 * (n - 1)) + 1);
            return (i0, i1);
        }

        private readonly record struct Bucket(double F, float Avg, float Min, float Max);

        /// <summary>Averages the visible points into at most one bucket per 2 px, keeping min/max.</summary>
        private List<Bucket> Buckets(IReadOnlyList<float> d)
        {
            int n = d.Count;
            var (i0, i1) = Visible(n);
            int count = i1 - i0 + 1;
            int nb = Math.Max(2, (int)((_plotW - _padL - _padR) / 2));
            var outp = new List<Bucket>(Math.Min(count, nb));
            if (count <= nb)
            {
                for (int i = i0; i <= i1; i++)
                    outp.Add(new Bucket((double)i / (n - 1), d[i], d[i], d[i]));
                return outp;
            }
            double per = (double)count / nb;
            for (int b = 0; b < nb; b++)
            {
                int a = i0 + (int)Math.Floor(b * per);
                int e = Math.Min(i1, i0 + (int)Math.Floor((b + 1) * per) - 1);
                if (e < a) continue;
                float sum = 0, mn = float.MaxValue, mx = float.MinValue;
                for (int i = a; i <= e; i++) { sum += d[i]; if (d[i] < mn) mn = d[i]; if (d[i] > mx) mx = d[i]; }
                outp.Add(new Bucket(((a + e) / 2.0) / (n - 1), sum / (e - a + 1), mn, mx));
            }
            return outp;
        }

        private void DrawLane(Lane l, Canvas cv, double w, double h, List<(double F, string Label)> ticks)
        {
            cv.Children.Clear();
            cv.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
            bool big = Mode == TimelineMode.Focus;
            double top = big ? 12 : 6, bottom = big ? 6 : 4;

            var buckets = l.Series.Select(se => Buckets(se.Data)).ToList();

            // Y scale
            double ymax;
            List<(double V, string Label, Color Color)> ths;
            if (l.FixedMax > 0)
            {
                ymax = l.FixedMax;
                ths = new();
            }
            else
            {
                var tops = buckets[0].Select(b => (double)(l.Envelope || l.Bars ? b.Max : b.Avg)).ToList();
                double dmax = tops.Count == 0 ? 1 : l.Bars ? tops.Max() : Percentile(tops, 0.97) * 1.12;
                dmax = Math.Max(Math.Max(dmax, l.Target), 0.01);
                // A threshold is drawn only when the data comes near it: a 3 ms RTT lane
                // squashed flat under a 60 ms line would hide everything worth seeing.
                ths = l.Thresholds.Where(t => t.V <= dmax * 1.25).ToList();
                double m = Math.Max(dmax, ths.Count > 0 ? ths.Max(t => t.V) : 0);
                ymax = NiceMax(m * 1.08);
            }
            double Y(double v) => top + (1 - Math.Min(Math.Max(v, 0), ymax) / ymax) * (h - top - bottom);

            // Grid: time ticks, half and top value lines
            foreach (var (f, _) in ticks)
                cv.Children.Add(VLine(X(f), 0, h, GridLine, null));
            foreach (double fr in new[] { 0.5, 1.0 })
                cv.Children.Add(HLine(_padL, w - _padR, Y(ymax * fr), GridLine, null));
            if (big)
            {
                foreach (double fr in new[] { 0.0, 0.5, 1.0 })
                {
                    var t = Label(FormatValue(ymax * fr, ymax < 10 ? 1 : 0), 11, Text3);
                    t.Width = _padL - 6; t.TextAlignment = TextAlignment.Right;
                    Canvas.SetLeft(t, 0); Canvas.SetTop(t, Y(ymax * fr) - 8);
                    cv.Children.Add(t);
                }
            }

            // Stream gaps
            foreach (double g in _axis?.GapFractions() ?? Enumerable.Empty<double>())
                if (g > _z0 && g < _z1) cv.Children.Add(VLine(X(g), 0, h, GapLine, new DoubleCollection { 3, 3 }));

            // Thresholds and target
            foreach (var t in ths)
            {
                cv.Children.Add(HLine(_padL, w - _padR, Y(t.V), t.Color, new DoubleCollection { 4, 3 }));
                if (big) AddLineLabel(cv, t.Label, w - _padR, Y(t.V), t.Color);
            }
            if (l.Target > 0)
            {
                cv.Children.Add(HLine(_padL, w - _padR, Y(l.Target), C(0xB0, 0xFF, 0xFF, 0xFF), new DoubleCollection { 4, 3 }));
                if (big) AddLineLabel(cv, "target", w - _padR, Y(l.Target), Text2);
            }

            // Data
            for (int si = 0; si < l.Series.Count; si++)
            {
                var bs = buckets[si];
                if (bs.Count == 0) continue;
                var col = l.Series[si].Color;

                if (l.Bars)
                {
                    double bw = Math.Max(1.5, (_plotW - _padL - _padR) / Math.Max(1, bs.Count) - 1);
                    foreach (var b in bs)
                    {
                        if (b.Max <= 0) continue;
                        double y = Y(b.Max);
                        var r = new Rectangle { Width = bw, Height = Math.Max(1.5, h - bottom - y), Fill = B(col) };
                        Canvas.SetLeft(r, X(b.F) - bw / 2); Canvas.SetTop(r, y);
                        cv.Children.Add(r);
                    }
                    continue;
                }

                if (l.Envelope)
                {
                    var band = new Polygon { Fill = B(Color.FromArgb(0x40, col.R, col.G, col.B)) };
                    var pts = new PointCollection();
                    foreach (var b in bs) pts.Add(new Point(X(b.F), Y(b.Max)));
                    for (int i = bs.Count - 1; i >= 0; i--) pts.Add(new Point(X(bs[i].F), Y(bs[i].Min)));
                    band.Points = pts;
                    cv.Children.Add(band);
                }
                else if (l.Series.Count == 1)
                {
                    var area = new Polygon { Fill = B(Color.FromArgb(0x1C, col.R, col.G, col.B)) };
                    var pts = new PointCollection { new Point(X(bs[0].F), h - bottom) };
                    foreach (var b in bs) pts.Add(new Point(X(b.F), Y(b.Avg)));
                    pts.Add(new Point(X(bs[^1].F), h - bottom));
                    area.Points = pts;
                    cv.Children.Add(area);
                }

                var line = new Polyline { Stroke = B(col), StrokeThickness = big ? 2 : 1.5, StrokeLineJoin = PenLineJoin.Round };
                var lp = new PointCollection();
                foreach (var b in bs) lp.Add(new Point(X(b.F), Y(b.Avg)));
                line.Points = lp;
                cv.Children.Add(line);
            }
        }

        private static void AddLineLabel(Canvas cv, string text, double right, double y, Color color)
        {
            var t = Label(text, 11, color);
            t.Width = 120; t.TextAlignment = TextAlignment.Right;
            Canvas.SetLeft(t, right - 124); Canvas.SetTop(t, y - 16);
            cv.Children.Add(t);
        }

        private static Line VLine(double x, double y0, double y1, Color c, DoubleCollection? dash)
        {
            var ln = new Line { X1 = x, X2 = x, Y1 = y0, Y2 = y1, Stroke = B(c), StrokeThickness = 1 };
            if (dash != null) ln.StrokeDashArray = dash;
            return ln;
        }

        private static Line HLine(double x0, double x1, double y, Color c, DoubleCollection? dash)
        {
            var ln = new Line { X1 = x0, X2 = x1, Y1 = y, Y2 = y, Stroke = B(c), StrokeThickness = 1 };
            if (dash != null) ln.StrokeDashArray = dash;
            return ln;
        }

        // ── Games & streams lane ──────────────────────────────────────────────

        /// <summary>Position of a wall-clock time: on the stream clock when the session has one.</summary>
        private double FractionOf(SessionEntry s, DateTime t)
        {
            if (_axis is { IsUsable: true }) return _axis.FractionAt(t);
            var end = s.EndTime ?? DateTime.Now;
            double total = (end - s.StartTime).TotalSeconds;
            return total > 0 ? Math.Clamp((t - s.StartTime).TotalSeconds / total, 0, 1) : 0;
        }

        private void DrawGameLane(SessionEntry s)
        {
            var cv = _gameCanvas;
            cv.Children.Clear();
            double h = 38;
            cv.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _plotW, h) };
            var covers = s.GameCoversForDisplay.ToDictionary(c => c.Name, c => c.CoverPath, StringComparer.OrdinalIgnoreCase);
            var conv = new Converters.CoverPathToBitmapConverter { DecodeWidth = 34 };

            // Stream boundaries: dashed, with S1, S2… when there were several. The labels are
            // added after the games so they sit on top of them, and one that would land on the
            // previous is skipped: a reconnect seconds after the start drew S1 and S2 on top of
            // each other, under the game's cover.
            var streamLabels = new List<UIElement>();
            if (_axis is { StreamCount: > 1 })
            {
                var starts = new List<double> { 0 };
                starts.AddRange(_axis.GapFractions());
                double lastRight = double.NegativeInfinity;
                for (int i = 0; i < starts.Count; i++)
                {
                    double f = starts[i];
                    if (i > 0 && f > _z0 && f < _z1) cv.Children.Add(VLine(X(f), 0, h, GapLine, new DoubleCollection { 3, 3 }));
                    if (f >= _z0 - 1e-9 && f < _z1)
                    {
                        double left = Math.Max(0, X(f)) + 2;
                        if (left < lastRight + 4) continue;
                        var tag = new Border
                        {
                            Background = B(C(0xCC, 0x14, 0x16, 0x16)), CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(3, 0, 3, 1),
                            Child = Label($"S{i + 1}", 10.5, Text4, medium: true),
                        };
                        tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        lastRight = left + tag.DesiredSize.Width;
                        Canvas.SetLeft(tag, left); Canvas.SetTop(tag, 0);
                        streamLabels.Add(tag);
                    }
                }
            }

            if (s.GameSpans is { Count: > 0 } spans)
            {
                int idx = 0;
                foreach (var g in spans)
                {
                    double f0 = FractionOf(s, g.Start), f1 = FractionOf(s, g.End);
                    var color = GamePalette[idx++ % GamePalette.Length];
                    if (f1 <= _z0 || f0 >= _z1 || f1 - f0 <= 0) continue;
                    double x0 = Math.Max(0, X(f0)), x1 = Math.Min(_plotW, X(f1));
                    if (x1 - x0 < 2) continue;

                    var seg = new Border
                    {
                        Width = x1 - x0, Height = h - 12, CornerRadius = new CornerRadius(6),
                        Background = B(color), BorderBrush = B(Color.FromArgb(0x70, color.R, color.G, color.B)), BorderThickness = new Thickness(1),
                        Padding = new Thickness(3, 0, 8, 0),
                    };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (covers.TryGetValue(g.Name, out var path) && conv.Convert(path!, typeof(ImageSource), null!, "") is ImageSource src)
                        sp.Children.Add(new Border { Width = 15, Height = 20, CornerRadius = new CornerRadius(3), Child = new Image { Source = src, Stretch = Stretch.UniformToFill } });
                    sp.Children.Add(Label(g.Name, 12, C(0xFF, 0xFF, 0xFF, 0xFF)));
                    seg.Child = sp;
                    ToolTipService.SetToolTip(seg, $"{g.Name} · {g.Start:HH:mm} – {g.End:HH:mm} · click to zoom");
                    double pad = Math.Max(0.004, (f1 - f0) * 0.03);
                    seg.Tapped += (_, e) => { SetZoom(f0 - pad, f1 + pad); e.Handled = true; };
                    Canvas.SetLeft(seg, x0); Canvas.SetTop(seg, 8);
                    cv.Children.Add(seg);
                }
            }
            else if (s.GamesDetected is { Count: > 0 } games)
            {
                // Sessions before 9.0 know which games ran, not when.
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                foreach (var name in games)
                {
                    var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    if (covers.TryGetValue(name, out var path) && conv.Convert(path!, typeof(ImageSource), null!, "") is ImageSource src)
                        item.Children.Add(new Border { Width = 15, Height = 20, CornerRadius = new CornerRadius(3), Child = new Image { Source = src, Stretch = Stretch.UniformToFill } });
                    item.Children.Add(Label(name, 12, Text2));
                    sp.Children.Add(item);
                }
                sp.Children.Add(Label("· times not recorded for this session", 11.5, Text4));
                // Under the S1, S2… tags when there are any (they sit at the top of the lane),
                // so a tag cannot land on a game's name or on this note.
                Canvas.SetLeft(sp, 6); Canvas.SetTop(sp, streamLabels.Count > 0 ? 16 : 9);
                cv.Children.Add(sp);
            }

            foreach (var tag in streamLabels) cv.Children.Add(tag);
        }

        // ── Time axis ─────────────────────────────────────────────────────────

        private static readonly int[] StepSeconds = { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 3600, 7200, 10800, 21600 };

        private List<(double F, string Label)> Ticks(SessionEntry s)
        {
            var list = new List<(double, string)>();
            double usable = _plotW - _padL - _padR;
            bool clock = _axis is { IsUsable: true };
            double totalSec = clock ? _axis!.ActiveSeconds : Math.Max(1, s.SessionDurationSeconds);
            double windowSec = totalSec * (_z1 - _z0);
            double want = windowSec / Math.Max(2, usable / 110);
            int step = StepSeconds.FirstOrDefault(v => v >= want);
            if (step == 0) step = StepSeconds[^1];
            double lastX = double.NegativeInfinity;

            void Add(double f, string label)
            {
                if (f < _z0 || f > _z1) return;
                double x = X(f);
                if (x - lastX < 48) return;
                lastX = x;
                list.Add((f, label));
            }

            if (clock && s.StreamSpans != null)
            {
                string fmt = step < 60 ? "HH:mm:ss" : "HH:mm";
                long stepTicks = step * TimeSpan.TicksPerSecond;
                foreach (var sp in s.StreamSpans)
                {
                    var end = sp.End ?? s.EndTime ?? DateTime.Now;
                    var t = new DateTime((sp.Start.Ticks + stepTicks - 1) / stepTicks * stepTicks, sp.Start.Kind);
                    for (; t <= end; t = t.AddTicks(stepTicks))
                        Add(_axis!.FractionAt(t), t.ToString(fmt, CultureInfo.InvariantCulture));
                }
            }
            else
            {
                double first = Math.Ceiling(_z0 * totalSec / step) * step;
                for (double k = first; k <= _z1 * totalSec + 0.001; k += step)
                    Add(k / totalSec, FormatElapsed(k, step));
            }
            return list;
        }

        private static string FormatElapsed(double seconds, int step)
        {
            int s = (int)Math.Round(seconds);
            if (step < 60) return $"{s / 60}:{s % 60:00}";
            return s >= 3600 ? $"{s / 3600}h{(s % 3600) / 60:00}" : $"{s / 60}m";
        }

        private void DrawAxis(List<(double F, string Label)> ticks)
        {
            _axisCanvas.Children.Clear();
            foreach (var (f, label) in ticks)
            {
                var t = Label(label, 11, Text3);
                t.Width = 64; t.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(t, Math.Clamp(X(f) - 32, -8, _plotW - 56));
                Canvas.SetTop(t, 5);
                _axisCanvas.Children.Add(t);
            }
        }

        /// <summary>Wall-clock (or elapsed) text for a position, for the crosshair tip.</summary>
        private string TimeText(double f)
        {
            var s = Session;
            if (s == null) return "";
            if (_axis is { IsUsable: true }) return _axis.TimeAt(f).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            int sec = (int)Math.Round(f * Math.Max(1, s.SessionDurationSeconds));
            return sec >= 3600 ? $"{sec / 3600}:{(sec % 3600) / 60:00}:{sec % 60:00}" : $"{sec / 60}:{sec % 60:00}";
        }

        // ── Overview strip ────────────────────────────────────────────────────

        private void DrawOverview(SessionEntry s)
        {
            _ov.Children.Clear();
            double w = _plotW, h = 44;
            _ov.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };

            // The series that best summarises the session: host latency, else RTT, else the first.
            var lane = _lanes.FirstOrDefault(l => l.Key == "hl") ?? _lanes.FirstOrDefault(l => l.Key == "rtt") ?? _lanes[0];
            var d = lane.Series[0].Data;
            int nb = Math.Max(2, (int)(w / 3));
            var pts = new List<(double X, float V)>();
            int n = d.Count;
            double per = Math.Max(1, (double)n / nb);
            for (double a = 0; a < n; a += per)
            {
                int i0 = (int)a, i1 = Math.Min(n - 1, (int)(a + per) - 1);
                if (i1 < i0) i1 = i0;
                float mx = float.MinValue;
                for (int i = i0; i <= i1; i++) mx = Math.Max(mx, d[i]);
                pts.Add((((i0 + i1) / 2.0) / (n - 1) * w, mx));
            }
            double ymax = Math.Max(0.01, lane.FixedMax > 0 ? lane.FixedMax : Percentile(pts.Select(p => (double)p.V).ToList(), 0.98) * 1.15);
            var poly = new Polygon { Fill = B(C(0x33, 0x39, 0x87, 0xe5)), Stroke = B(C(0x99, 0x39, 0x87, 0xe5)), StrokeThickness = 1 };
            var pc = new PointCollection { new Point(0, h) };
            foreach (var p in pts) pc.Add(new Point(p.X, 4 + (1 - Math.Min(p.V, ymax) / ymax) * (h - 8)));
            pc.Add(new Point(w, h));
            poly.Points = pc;
            _ov.Children.Add(poly);

            foreach (double g in _axis?.GapFractions() ?? Enumerable.Empty<double>())
                _ov.Children.Add(VLine(g * w, 0, h, GapLine, new DoubleCollection { 3, 3 }));

            // Zoom window
            double wx0 = _z0 * w, wx1 = _z1 * w;
            if (IsZoomed)
            {
                _ov.Children.Add(new Rectangle { Width = Math.Max(0, wx0), Height = h, Fill = B(C(0x80, 0x0E, 0x0F, 0x0F)) });
                var right = new Rectangle { Width = Math.Max(0, w - wx1), Height = h, Fill = B(C(0x80, 0x0E, 0x0F, 0x0F)) };
                Canvas.SetLeft(right, wx1);
                _ov.Children.Add(right);
            }
            var win = new Rectangle
            {
                Width = Math.Max(2, wx1 - wx0), Height = h, Stroke = B(C(0xFF, 0x4a, 0xde, 0x80)), StrokeThickness = IsZoomed ? 2 : 0,
                Fill = B(C(IsZoomed ? (byte)0x1A : (byte)0x00, 0x4a, 0xde, 0x80)), RadiusX = 3, RadiusY = 3,
            };
            Canvas.SetLeft(win, wx0);
            _ov.Children.Add(win);
        }

        private bool _ovDragging;
        private double _ovAnchor, _ovAnchorX;

        private void Ov_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_plotW <= 0) return;
            _ovDragging = true;
            _ovAnchorX = e.GetCurrentPoint(_ov).Position.X;
            _ovAnchor = Math.Clamp(_ovAnchorX / _plotW, 0, 1);
            _ov.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void Ov_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_ovDragging) return;
            double x = e.GetCurrentPoint(_ov).Position.X;
            if (Math.Abs(x - _ovAnchorX) < 6) return;
            double f = Math.Clamp(x / _plotW, 0, 1);
            SetZoom(Math.Min(_ovAnchor, f), Math.Max(_ovAnchor, f), immediate: true);
            e.Handled = true;
        }

        private void Ov_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_ovDragging) return;
            _ovDragging = false;
            _ov.ReleasePointerCapture(e.Pointer);
            double x = e.GetCurrentPoint(_ov).Position.X;
            if (Math.Abs(x - _ovAnchorX) < 6 && IsZoomed)
            {
                // A tap moves the current window so it is centred where the strip was touched.
                double half = (_z1 - _z0) / 2, c = Math.Clamp(x / _plotW, half, 1 - half);
                SetZoom(c - half, c + half);
            }
            e.Handled = true;
        }

        // ── Zoom ──────────────────────────────────────────────────────────────

        private void SetZoom(double z0, double z1, bool immediate = false)
        {
            z0 = Math.Clamp(z0, 0, 1); z1 = Math.Clamp(z1, 0, 1);
            const double minSpan = 0.01;
            if (z1 - z0 < minSpan)
            {
                double c = (z0 + z1) / 2;
                z0 = Math.Clamp(c - minSpan / 2, 0, 1 - minSpan);
                z1 = z0 + minSpan;
            }
            if (Math.Abs(z0 - _z0) < 1e-6 && Math.Abs(z1 - _z1) < 1e-6) return;
            _z0 = z0; _z1 = z1;
            if (immediate) Redraw(); else QueueRedraw();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Hit_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // Ctrl+wheel only: a plain wheel keeps scrolling the page.
            if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0) return;
            int delta = e.GetCurrentPoint(_hit).Properties.MouseWheelDelta;
            double f = F(e.GetCurrentPoint(_hit).Position.X);
            double k = delta > 0 ? 0.8 : 1.25;
            SetZoom(f - (f - _z0) * k, f + (_z1 - f) * k);
            e.Handled = true;
        }

        // ── Crosshair, drag-to-zoom, readouts ─────────────────────────────────

        private bool _selecting;
        private double _selX0;

        private void Hit_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(_hit);
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            {
                ShowCursorAt(pt.Position.X);   // touch: a tap reads the values; zoom is on the strip
                return;
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            _selecting = true;
            _selX0 = pt.Position.X;
            _hit.CapturePointer(e.Pointer);
        }

        private void Hit_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            double x = e.GetCurrentPoint(_hit).Position.X;
            ShowCursorAt(x);
            if (!_selecting) return;
            double a = Math.Min(_selX0, x), b = Math.Max(_selX0, x);
            if (b - a < 4) { _selection.Visibility = Visibility.Collapsed; return; }
            _selection.Width = b - a;
            _selection.Height = Math.Max(0, _overlay.ActualHeight - 24);
            Canvas.SetLeft(_selection, a);
            _selection.Visibility = Visibility.Visible;
        }

        private void Hit_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_selecting) return;
            _selecting = false;
            _hit.ReleasePointerCapture(e.Pointer);
            _selection.Visibility = Visibility.Collapsed;
            double x = e.GetCurrentPoint(_hit).Position.X;
            if (Math.Abs(x - _selX0) >= 8)
                SetZoom(F(Math.Min(x, _selX0)), F(Math.Max(x, _selX0)));
        }

        private void ShowCursorAt(double x)
        {
            if (_plotW <= 0 || Session == null) return;
            x = Math.Clamp(x, _padL, _plotW - _padR);
            double f = F(x);
            double lanesH = Math.Max(0, _overlay.ActualHeight - 24);
            _crosshair.Height = lanesH;
            Canvas.SetLeft(_crosshair, x);
            _crosshair.Visibility = Visibility.Visible;

            _timeTipText.Text = TimeText(f);
            _timeTip.Visibility = Visibility.Visible;
            _timeTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = _timeTip.DesiredSize.Width;
            Canvas.SetLeft(_timeTip, Math.Clamp(x - tw / 2, 0, Math.Max(0, _plotW - tw)));
            Canvas.SetTop(_timeTip, lanesH + 2);

            foreach (var l in _lanes)
            {
                if (l.Plot == null) continue;
                if (l.Value != null)
                {
                    var d = l.Series[0].Data;
                    l.Value.Text = FormatValue(d[IndexAt(d.Count, f)], l.Decimals);
                }
                for (int i = 0; i < l.LegendValues.Count && i < l.Series.Count; i++)
                {
                    var d = l.Series[i].Data;
                    l.LegendValues[i].Text = FormatValue(d[IndexAt(d.Count, f)], l.Decimals) + l.Unit;
                }
            }
        }

        private static int IndexAt(int n, double f) => Math.Clamp((int)Math.Round(f * (n - 1)), 0, n - 1);

        private void HideCursor()
        {
            _crosshair.Visibility = Visibility.Collapsed;
            _timeTip.Visibility = Visibility.Collapsed;
            ShowDefaults();
        }

        /// <summary>Lane headers at rest: the average of what is in view, and its worst.</summary>
        private void ShowDefaults()
        {
            foreach (var l in _lanes)
            {
                if (l.Plot == null) continue;
                if (l.Value != null && l.Sub != null)
                {
                    var v = VisibleValues(l.Series[0].Data);
                    if (v.Count == 0) { l.Value.Text = "—"; l.Sub.Text = ""; continue; }
                    if (l.Bars)
                    {
                        l.Value.Text = FormatValue(v.Sum(), 0);
                        l.Sub.Text = IsZoomed ? "dropped in view" : "dropped in total";
                    }
                    else
                    {
                        l.Value.Text = FormatValue(v.Average(), l.Decimals);
                        l.Sub.Text = l.HigherIsBetter
                            ? (l.Target > 0 ? $"avg · target {FormatValue(l.Target, 0)}" : $"avg · low {FormatValue(v.Min(), l.Decimals)}")
                            : $"avg · max {FormatValue(v.Max(), l.Decimals)}";
                    }
                }
                for (int i = 0; i < l.LegendValues.Count && i < l.Series.Count; i++)
                {
                    var v = VisibleValues(l.Series[i].Data);
                    l.LegendValues[i].Text = v.Count > 0 ? FormatValue(v.Average(), 0) + l.Unit : "—";
                }
            }
        }

        private List<double> VisibleValues(IReadOnlyList<float> d)
        {
            int n = d.Count;
            int i0 = Math.Clamp((int)Math.Ceiling(_z0 * (n - 1) - 1e-9), 0, n - 1);
            int i1 = Math.Clamp((int)Math.Floor(_z1 * (n - 1) + 1e-9), 0, n - 1);
            var list = new List<double>(Math.Max(0, i1 - i0 + 1));
            for (int i = i0; i <= i1; i++) list.Add(d[i]);
            return list;
        }

        // ── Focus statistics ──────────────────────────────────────────────────

        private void BuildFocusStats()
        {
            _focusStats.Children.Clear();
            var l = _lanes.FirstOrDefault(x => x.Key == _focusKey);
            if (l == null) return;

            if (l.Series.Count > 1)
            {
                foreach (var se in l.Series)
                {
                    var v = VisibleValues(se.Data);
                    if (v.Count == 0) continue;
                    _focusStats.Children.Add(Stat($"{se.Label} average", FormatValue(v.Average(), 0), l.Unit));
                    _focusStats.Children.Add(Stat($"{se.Label} peak", FormatValue(v.Max(), 0), l.Unit));
                }
                return;
            }

            var vals = VisibleValues(l.Series[0].Data);
            if (vals.Count == 0) return;
            if (l.Bars)
            {
                int withDrops = vals.Count(v => v > 0);
                _focusStats.Children.Add(Stat("Dropped", FormatValue(vals.Sum(), 0), "frames"));
                _focusStats.Children.Add(Stat("Samples with drops", FormatValue(100.0 * withDrops / vals.Count, 1), "%"));
                _focusStats.Children.Add(Stat("Worst slice", FormatValue(vals.Max(), 0), "frames"));
                return;
            }
            var sorted = vals.OrderBy(v => v).ToList();
            int d = l.Decimals;
            _focusStats.Children.Add(Stat("Average", FormatValue(vals.Average(), d), l.Unit));
            _focusStats.Children.Add(Stat("Median", FormatValue(Percentile(sorted, 0.5, true), d), l.Unit));
            if (l.HigherIsBetter)
            {
                _focusStats.Children.Add(Stat("5th percentile", FormatValue(Percentile(sorted, 0.05, true), d), l.Unit));
                _focusStats.Children.Add(Stat("Lowest", FormatValue(sorted[0], d), l.Unit));
                _focusStats.Children.Add(Stat("Highest", FormatValue(sorted[^1], d), l.Unit));
            }
            else
            {
                _focusStats.Children.Add(Stat("95th percentile", FormatValue(Percentile(sorted, 0.95, true), d), l.Unit));
                _focusStats.Children.Add(Stat("99th percentile", FormatValue(Percentile(sorted, 0.99, true), d), l.Unit));
                _focusStats.Children.Add(Stat("Worst", FormatValue(sorted[^1], d), l.Unit));
            }
        }

        private static UIElement Stat(string label, string value, string unit)
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(Label(label, 12, Text3, medium: true));
            var vr = new StackPanel { Orientation = Orientation.Horizontal };
            var v = Label(value, 19, C(0xFF, 0xF2, 0xF5, 0xF3));
            if (Application.Current.Resources.TryGetValue("DMSansSemiBold", out var f) && f is FontFamily ff) v.FontFamily = ff;
            vr.Children.Add(v);
            var u = Label(unit, 12, Text3, medium: true);
            u.VerticalAlignment = VerticalAlignment.Bottom;
            u.Margin = new Thickness(3, 0, 0, 3);
            vr.Children.Add(u);
            sp.Children.Add(vr);
            return sp;
        }

        // ── Maths ─────────────────────────────────────────────────────────────

        private static double Percentile(List<double> values, double p, bool sorted = false)
        {
            if (values.Count == 0) return 0;
            var v = sorted ? values : values.OrderBy(x => x).ToList();
            double idx = p * (v.Count - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return v[lo] + (v[hi] - v[lo]) * (idx - lo);
        }

        private static double NiceMax(double v)
        {
            if (v <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
                if (v <= m * mag) return m * mag;
            return 10 * mag;
        }

        private static string FormatValue(double v, int decimals)
            => v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
