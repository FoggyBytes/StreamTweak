using Microsoft.UI.Xaml;

namespace StreamTweak.Controls
{
    /// <summary>
    /// One content column for every page, so moving between them nothing jumps sideways: the
    /// same gutters and, past <see cref="MaxContentWidth"/>, the same centred width. Every page
    /// takes its left/right padding from <see cref="Side"/> — padding and not MaxWidth, because
    /// on the Library and Sessions the page's own list is the scroller, and the mouse wheel must
    /// keep working over the side margins.
    /// </summary>
    public static class PageLayout
    {
        /// <summary>Widest the content column gets: roomy on 4K, without rows stretched edge to edge.</summary>
        public const double MaxContentWidth = 1344;

        /// <summary>Gutter of a page that is not yet at the maximum: 3.2 % of it, 16–48.</summary>
        public static double Gutter(double pageWidth) => Math.Clamp(Math.Round(pageWidth * 0.032), 16, 48);

        /// <summary>Left and right padding that puts the content column in place on a page this wide.</summary>
        public static double Side(double pageWidth)
            => Math.Max(Gutter(pageWidth), Math.Floor((pageWidth - MaxContentWidth) / 2));

        /// <summary>The page padding: <see cref="Side"/> on both sides, the usual top and bottom.</summary>
        public static Thickness Padding(double pageWidth, double top = 24, double bottom = 40)
        {
            double side = Side(pageWidth);
            return new Thickness(side, top, side, bottom);
        }
    }
}
