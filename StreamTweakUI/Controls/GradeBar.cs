using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamTweak.Controls
{
    /// <summary>
    /// The share of Excellent / Good / Poor sessions as one proportional bar. The legend with
    /// the counts sits next to it in XAML, so the colours are never the only carrier of the
    /// information.
    /// </summary>
    public sealed class GradeBar : Grid
    {
        public static readonly DependencyProperty ExcellentProperty =
            DependencyProperty.Register(nameof(Excellent), typeof(int), typeof(GradeBar),
                new PropertyMetadata(0, (d, _) => ((GradeBar)d).Rebuild()));
        public static readonly DependencyProperty GoodProperty =
            DependencyProperty.Register(nameof(Good), typeof(int), typeof(GradeBar),
                new PropertyMetadata(0, (d, _) => ((GradeBar)d).Rebuild()));
        public static readonly DependencyProperty PoorProperty =
            DependencyProperty.Register(nameof(Poor), typeof(int), typeof(GradeBar),
                new PropertyMetadata(0, (d, _) => ((GradeBar)d).Rebuild()));

        public int Excellent { get => (int)GetValue(ExcellentProperty); set => SetValue(ExcellentProperty, value); }
        public int Good      { get => (int)GetValue(GoodProperty);      set => SetValue(GoodProperty, value); }
        public int Poor      { get => (int)GetValue(PoorProperty);      set => SetValue(PoorProperty, value); }

        private static readonly Color Green = Color.FromArgb(0xFF, 0x4a, 0xde, 0x80);
        private static readonly Color Amber = Color.FromArgb(0xFF, 0xfb, 0xbf, 0x24);
        private static readonly Color Red   = Color.FromArgb(0xFF, 0xf8, 0x71, 0x71);

        public GradeBar()
        {
            Height = 10;
            CornerRadius = new CornerRadius(5);
            Rebuild();
        }

        private void Rebuild()
        {
            Children.Clear();
            ColumnDefinitions.Clear();
            int total = Excellent + Good + Poor;
            if (total <= 0)
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                return;
            }
            Background = null;

            int col = 0;
            void Add(int count, Color c)
            {
                if (count <= 0) return;
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
                var b = new Border
                {
                    Background = new SolidColorBrush(c),
                    // 2 px surface gap between segments, none on the outer edges.
                    Margin = new Thickness(col == 0 ? 0 : 1, 0, 1, 0),
                };
                SetColumn(b, col++);
                Children.Add(b);
            }
            Add(Excellent, Green);
            Add(Good, Amber);
            Add(Poor, Red);
            if (Children.Count > 0 && Children[^1] is Border lastB)
                lastB.Margin = new Thickness(lastB.Margin.Left, 0, 0, 0);
        }
    }
}
