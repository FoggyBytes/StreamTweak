using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace StreamTweak
{
    /// <summary>
    /// Sparkline with Cartesian axes, Y-scale labels, and X-time labels.
    /// Renders entirely via DrawingContext — no third-party library required.
    /// </summary>
    public class SparklineControl : FrameworkElement
    {
        // ── Dependency properties ─────────────────────────────────────────────

        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<float>),
                typeof(SparklineControl),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string),
                typeof(SparklineControl),
                new FrameworkPropertyMetadata(string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeBrushProperty =
            DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush),
                typeof(SparklineControl),
                new FrameworkPropertyMetadata(Brushes.White,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MinValueProperty =
            DependencyProperty.Register(nameof(MinValue), typeof(float),
                typeof(SparklineControl),
                new FrameworkPropertyMetadata(0f,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(float),
                typeof(SparklineControl),
                new FrameworkPropertyMetadata(0f,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        // ── CLR wrappers ──────────────────────────────────────────────────────

        public IReadOnlyList<float>? Points
        {
            get => (IReadOnlyList<float>?)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public Brush StrokeBrush
        {
            get => (Brush)GetValue(StrokeBrushProperty);
            set => SetValue(StrokeBrushProperty, value);
        }

        public float MinValue
        {
            get => (float)GetValue(MinValueProperty);
            set => SetValue(MinValueProperty, value);
        }

        public float MaxValue
        {
            get => (float)GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }

        // ── Layout constants ──────────────────────────────────────────────────

        private const double YLabelW  = 28;  // left margin for Y-axis labels
        private const double XLabelH  = 15;  // bottom margin for X-axis labels
        private const double PadTop   = 4;   // top padding inside chart area

        // ── Rendering ─────────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double dpi      = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var    typeface = new Typeface("Segoe UI");
            var    lblBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            var    axBrush  = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            var    axPen    = new Pen(axBrush, 1);
            var    gridPen  = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);

            // ── Chart area ───────────────────────────────────────────────────
            double cx = YLabelW;         // left edge of chart
            double cy = PadTop;          // top edge of chart
            double cw = w - YLabelW;     // chart width
            double ch = h - XLabelH - PadTop; // chart height
            if (cw <= 0 || ch <= 0) return;

            // ── Background ──────────────────────────────────────────────────
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)),
                null, new Rect(cx, cy, cw, ch));

            // ── Axes ────────────────────────────────────────────────────────
            dc.DrawLine(axPen, new Point(cx, cy),      new Point(cx, cy + ch));  // Y axis
            dc.DrawLine(axPen, new Point(cx, cy + ch), new Point(cx + cw, cy + ch)); // X axis

            // ── X-axis time labels ───────────────────────────────────────────
            var pts = Points;
            if (pts != null && pts.Count > 1)
            {
                var t0Ft = MakeFt("0", typeface, 8.5, lblBrush, dpi);
                dc.DrawText(t0Ft, new Point(cx, cy + ch + 2));

                int secs = pts.Count;
                string durStr = secs >= 60
                    ? $"{secs / 60}m{secs % 60:00}s"
                    : $"{secs}s";
                var durFt = MakeFt(durStr, typeface, 8.5, lblBrush, dpi);
                dc.DrawText(durFt, new Point(cx + cw - durFt.Width, cy + ch + 2));
            }

            // ── Chart label (FPS / RTT ms) ───────────────────────────────────
            if (!string.IsNullOrEmpty(Label))
            {
                var lFt = MakeFt(Label, typeface, 10,
                    new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)), dpi);
                dc.DrawText(lFt, new Point(cx + 5, cy + 3));
            }

            if (pts == null || pts.Count < 2) return;

            // ── Y range ──────────────────────────────────────────────────────
            float yMin = MinValue, yMax = MaxValue;
            float displayMin, displayMax;
            bool autoScale = (yMin == 0f && yMax == 0f);
            if (autoScale)
            {
                yMin = float.MaxValue; yMax = float.MinValue;
                foreach (float v in pts) { if (v < yMin) yMin = v; if (v > yMax) yMax = v; }
                displayMin = yMin; displayMax = yMax;
                float margin = (yMax - yMin) * 0.1f;
                if (margin < 1f) margin = 1f;
                yMin = Math.Max(0f, yMin - margin);
                yMax += margin;
            }
            else { displayMin = yMin; displayMax = yMax; }

            float range = yMax - yMin;
            if (range <= 0f) range = 1f;

            // ── Y-axis labels ────────────────────────────────────────────────
            string maxStr = displayMax.ToString("F0");
            var maxFt = MakeFt(maxStr, typeface, 8.5, lblBrush, dpi);
            dc.DrawText(maxFt, new Point(cx - maxFt.Width - 3, cy));

            if (Math.Abs(displayMax - displayMin) > 0.5f)
            {
                string minStr = displayMin.ToString("F0");
                var minFt = MakeFt(minStr, typeface, 8.5, lblBrush, dpi);
                dc.DrawText(minFt, new Point(cx - minFt.Width - 3, cy + ch - minFt.Height));
            }

            // ── Midpoint gridline ─────────────────────────────────────────────
            dc.DrawLine(gridPen, new Point(cx + 1, cy + ch / 2), new Point(cx + cw, cy + ch / 2));

            // ── Midpoint Y label (right of axis) ─────────────────────────────
            float midVal = (displayMin + displayMax) / 2f;
            if (Math.Abs(displayMax - displayMin) > 1f)
            {
                var midFt = MakeFt(midVal.ToString("F0"), typeface, 8.5,
                    new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), dpi);
                dc.DrawText(midFt, new Point(cx - midFt.Width - 3, cy + ch / 2 - midFt.Height / 2));
            }

            // ── Polyline ─────────────────────────────────────────────────────
            double yNorm(float v) => cy + ch - (v - yMin) / range * ch;
            double xStep = cw / (pts.Count - 1);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(cx, yNorm(pts[0])), false, false);
                for (int i = 1; i < pts.Count; i++)
                    ctx.LineTo(new Point(cx + i * xStep, yNorm(pts[i])), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(StrokeBrush, 1.5), geometry);
        }

        private static FormattedText MakeFt(string text, Typeface typeface, double size,
            Brush brush, double dpi)
            => new FormattedText(text, CultureInfo.InvariantCulture,
                   FlowDirection.LeftToRight, typeface, size, brush, dpi);
    }
}
