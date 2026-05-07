using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace StreamTweak.Converters
{
    /// <summary>
    /// Converts an absolute cover file path (string?) to a BitmapImage.
    /// Returns null for null/empty input or on any I/O error.
    /// DecodePixelWidth=66: 2× the 33px display size so WIC uses its Fant resampler.
    /// </summary>
    public sealed class CoverPathToBitmapConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;
            try
            {
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
