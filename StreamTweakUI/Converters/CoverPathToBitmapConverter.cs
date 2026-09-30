using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace StreamTweak.Converters
{
    /// <summary>
    /// Converts an absolute cover file path (string?) to a BitmapImage.
    /// Returns null for null/empty input or on any I/O error.
    /// DecodePixelWidth defaults to 66 (2× the 33 px list thumbnail) so WIC uses its Fant
    /// resampler; 9.0 adds <see cref="DecodeWidth"/> for the larger Library tiles and hero covers.
    /// </summary>
    public sealed class CoverPathToBitmapConverter : IValueConverter
    {
        public int DecodeWidth { get; set; } = 66;

        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;
            try
            {
                var bmp = new BitmapImage(new Uri(path));
                bmp.DecodePixelWidth = DecodeWidth;
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
