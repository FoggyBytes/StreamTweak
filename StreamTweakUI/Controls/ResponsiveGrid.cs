using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace StreamTweak.Controls
{
    /// <summary>
    /// 9.0 layout primitive: a grid that picks its column count from its OWN width and flows
    /// its children into rows. It is what lets every page fill the space it has, from a
    /// 1280×800 handheld to a 4K monitor, without a fixed maximum width.
    ///
    /// Two ways to choose the column count:
    /// <list type="bullet">
    /// <item><see cref="Breakpoints"/> + <see cref="Columns"/> — explicit widths
    /// ("0,700,1180" with "1,2,3"): the breakpoint index is also what the per-child
    /// <see cref="GetSpan"/> / <see cref="GetOrder"/> lists are indexed by.</item>
    /// <item><see cref="MinColumnWidth"/> &gt; 0 — as many columns of at least that width as
    /// fit, capped at <see cref="MaxColumns"/>; the index is then columns - 1.</item>
    /// </list>
    ///
    /// Per child, <c>Span</c> and <c>Order</c> are comma lists read at the current index (the
    /// last value repeats). A span of 0 hides the child at that size. Every child in a row is
    /// given the row's height, so cards side by side line up; when the next child does not fit,
    /// the last child of the row is widened to close the gap (<see cref="StretchLastInRow"/>).
    /// </summary>
    public sealed class ResponsiveGrid : Panel
    {
        // ── Panel properties ─────────────────────────────────────────────────────

        public static readonly DependencyProperty BreakpointsProperty =
            DependencyProperty.Register(nameof(Breakpoints), typeof(string), typeof(ResponsiveGrid),
                new PropertyMetadata(string.Empty, OnLayoutPropertyChanged));

        public static readonly DependencyProperty ColumnsProperty =
            DependencyProperty.Register(nameof(Columns), typeof(string), typeof(ResponsiveGrid),
                new PropertyMetadata("1", OnLayoutPropertyChanged));

        public static readonly DependencyProperty MinColumnWidthProperty =
            DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(ResponsiveGrid),
                new PropertyMetadata(0.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty MaxColumnsProperty =
            DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid),
                new PropertyMetadata(12, OnLayoutPropertyChanged));

        public static readonly DependencyProperty ColumnSpacingProperty =
            DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(ResponsiveGrid),
                new PropertyMetadata(16.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty RowSpacingProperty =
            DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(ResponsiveGrid),
                new PropertyMetadata(16.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty StretchLastInRowProperty =
            DependencyProperty.Register(nameof(StretchLastInRow), typeof(bool), typeof(ResponsiveGrid),
                new PropertyMetadata(true, OnLayoutPropertyChanged));

        /// <summary>Minimum widths (DIP) at which each layout starts, ascending, e.g. "0,700,1180".</summary>
        public string Breakpoints { get => (string)GetValue(BreakpointsProperty); set => SetValue(BreakpointsProperty, value); }

        /// <summary>Column count for each breakpoint, e.g. "1,2,3".</summary>
        public string Columns { get => (string)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

        /// <summary>When &gt; 0, ignore the breakpoints and fit as many columns of this width as possible.</summary>
        public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

        public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

        public double ColumnSpacing { get => (double)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }

        public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

        public bool StretchLastInRow { get => (bool)GetValue(StretchLastInRowProperty); set => SetValue(StretchLastInRowProperty, value); }

        /// <summary>Index of the layout in use (breakpoint index, or columns - 1 in MinColumnWidth mode).</summary>
        public int CurrentIndex { get; private set; } = -1;

        /// <summary>Number of columns in use.</summary>
        public int CurrentColumns { get; private set; } = 1;

        /// <summary>
        /// Raised (asynchronously, after layout) when the layout index changes, for pages that
        /// adjust something the panel itself cannot express.
        /// </summary>
        public event EventHandler<int>? LayoutIndexChanged;

        // ── Attached child properties ────────────────────────────────────────────

        public static readonly DependencyProperty SpanProperty =
            DependencyProperty.RegisterAttached("Span", typeof(string), typeof(ResponsiveGrid),
                new PropertyMetadata("1", OnChildPropertyChanged));

        public static readonly DependencyProperty OrderProperty =
            DependencyProperty.RegisterAttached("Order", typeof(string), typeof(ResponsiveGrid),
                new PropertyMetadata(string.Empty, OnChildPropertyChanged));

        public static string GetSpan(DependencyObject o) => (string)o.GetValue(SpanProperty);
        public static void SetSpan(DependencyObject o, string value) => o.SetValue(SpanProperty, value);

        public static string GetOrder(DependencyObject o) => (string)o.GetValue(OrderProperty);
        public static void SetOrder(DependencyObject o, string value) => o.SetValue(OrderProperty, value);

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((ResponsiveGrid)d).InvalidateMeasure();

        private static void OnChildPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe && fe.Parent is ResponsiveGrid g) g.InvalidateMeasure();
        }

        // ── Layout ───────────────────────────────────────────────────────────────

        private readonly struct Placement
        {
            public Placement(UIElement child, int row, int col, int span)
            { Child = child; Row = row; Col = col; Span = span; }
            public UIElement Child { get; }
            public int Row { get; }
            public int Col { get; }
            public int Span { get; }
        }

        private readonly List<Placement> _placements = new();
        private readonly List<double> _rowHeights = new();
        private readonly HashSet<UIElement> _hidden = new();
        private double _layoutWidth = -1;

        private static int[] ParseInts(string? s, int fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return new[] { fallback };
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var r = new List<int>(parts.Length);
            foreach (var p in parts) r.Add(int.TryParse(p, out int v) ? v : fallback);
            return r.Count > 0 ? r.ToArray() : new[] { fallback };
        }

        private static double[] ParseDoubles(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<double>();
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var r = new List<double>(parts.Length);
            foreach (var p in parts)
                if (double.TryParse(p, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v)) r.Add(v);
            return r.ToArray();
        }

        private static int At(int[] values, int index) => values[Math.Min(index, values.Length - 1)];

        private (int Index, int Columns) ResolveColumns(double width)
        {
            double gap = ColumnSpacing;
            if (MinColumnWidth > 0)
            {
                int cols = (int)Math.Floor((width + gap) / (MinColumnWidth + gap));
                cols = Math.Clamp(cols, 1, Math.Max(1, MaxColumns));
                return (cols - 1, cols);
            }

            var bps = ParseDoubles(Breakpoints);
            var cls = ParseInts(Columns, 1);
            int idx = 0;
            for (int i = 0; i < bps.Length; i++)
                if (width >= bps[i]) idx = i;
            int c = Math.Clamp(At(cls, idx), 1, Math.Max(1, MaxColumns));
            return (idx, c);
        }

        private void BuildLayout(double width)
        {
            _placements.Clear();
            _hidden.Clear();
            _layoutWidth = width;

            var (index, cols) = ResolveColumns(width);
            if (index != CurrentIndex)
            {
                CurrentIndex = index;
                int raised = index;
                DispatcherQueue?.TryEnqueue(() => LayoutIndexChanged?.Invoke(this, raised));
            }
            CurrentColumns = cols;

            // Visible children with their span and order at this size.
            var items = new List<(UIElement Child, int Span, int Order, int Index)>();
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child.Visibility == Visibility.Collapsed) { _hidden.Add(child); continue; }
                int span = At(ParseInts(GetSpan(child), 1), index);
                if (span <= 0) { _hidden.Add(child); continue; }
                string orderStr = GetOrder(child);
                int order = string.IsNullOrWhiteSpace(orderStr) ? i : At(ParseInts(orderStr, i), index);
                items.Add((child, Math.Min(span, cols), order, i));
            }
            items.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));

            // Flow into rows.
            int row = 0, col = 0;
            var rowItems = new List<Placement>();
            void CloseRow()
            {
                if (rowItems.Count == 0) return;
                if (StretchLastInRow && col < cols)
                {
                    var last = rowItems[^1];
                    rowItems[^1] = new Placement(last.Child, last.Row, last.Col, last.Span + (cols - col));
                }
                _placements.AddRange(rowItems);
                rowItems.Clear();
                row++;
                col = 0;
            }
            foreach (var it in items)
            {
                if (col + it.Span > cols) CloseRow();
                rowItems.Add(new Placement(it.Child, row, col, it.Span));
                col += it.Span;
            }
            CloseRow();
        }

        private double ColumnWidth(double width)
        {
            int cols = Math.Max(1, CurrentColumns);
            return Math.Max(0, (width - (cols - 1) * ColumnSpacing) / cols);
        }

        private double SlotWidth(double colW, int span) => span * colW + (span - 1) * ColumnSpacing;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
                ? 1200 : availableSize.Width;
            BuildLayout(width);

            double colW = ColumnWidth(width);
            _rowHeights.Clear();
            foreach (var p in _placements)
            {
                p.Child.Measure(new Size(SlotWidth(colW, p.Span), double.PositiveInfinity));
                while (_rowHeights.Count <= p.Row) _rowHeights.Add(0);
                _rowHeights[p.Row] = Math.Max(_rowHeights[p.Row], p.Child.DesiredSize.Height);
            }
            foreach (var h in _hidden) h.Measure(new Size(0, 0));

            double height = _rowHeights.Sum() + Math.Max(0, _rowHeights.Count - 1) * RowSpacing;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = finalSize.Width;
            if (Math.Abs(width - _layoutWidth) > 0.5)
            {
                // The final width differs from the measured one: re-flow and re-measure so the
                // row heights belong to the widths we are about to arrange at.
                BuildLayout(width);
                double cw = ColumnWidth(width);
                _rowHeights.Clear();
                foreach (var p in _placements)
                {
                    p.Child.Measure(new Size(SlotWidth(cw, p.Span), double.PositiveInfinity));
                    while (_rowHeights.Count <= p.Row) _rowHeights.Add(0);
                    _rowHeights[p.Row] = Math.Max(_rowHeights[p.Row], p.Child.DesiredSize.Height);
                }
            }

            double colW = ColumnWidth(width);
            var rowTops = new List<double>(_rowHeights.Count);
            double y = 0;
            foreach (var h in _rowHeights) { rowTops.Add(y); y += h + RowSpacing; }

            foreach (var p in _placements)
            {
                double x = p.Col * (colW + ColumnSpacing);
                p.Child.Arrange(new Rect(x, rowTops[p.Row], SlotWidth(colW, p.Span), _rowHeights[p.Row]));
            }
            foreach (var h in _hidden) h.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }
    }
}
