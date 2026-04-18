using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace StreamTweak.Converters
{
    /// <summary>
    /// Converts a game display name (string) to a BitmapImage loaded from the cached cover PNG.
    /// Returns null when no cover is available — the bound Image control simply shows nothing.
    /// </summary>
    public sealed class GameNameToCoverConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string gameName || string.IsNullOrEmpty(gameName))
                return null;

            try
            {
                var entry = GameLibraryState.Current.Games
                    .FirstOrDefault(g => string.Equals(g.Name, gameName, StringComparison.OrdinalIgnoreCase));

                string? path = entry?.CoverImagePath;
                if (path == null) return null;

                // DecodePixelWidth = 2× display width (33 px in Logs) so WIC uses
                // its high-quality Fant resampler instead of GPU bilinear downscaling.
                var bmp = new BitmapImage(new Uri(path));
                bmp.DecodePixelWidth = 66;
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
