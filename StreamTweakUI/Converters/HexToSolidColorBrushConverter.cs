using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamTweak.Converters
{
    /// <summary>
    /// Converts an ARGB hex string ("#AARRGGBB" or "#RRGGBB") to a SolidColorBrush.
    /// </summary>
    public sealed class HexToSolidColorBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string hex)
            {
                hex = hex.TrimStart('#');
                try
                {
                    if (hex.Length == 8)
                        return new SolidColorBrush(Color.FromArgb(
                            System.Convert.ToByte(hex[..2], 16),
                            System.Convert.ToByte(hex[2..4], 16),
                            System.Convert.ToByte(hex[4..6], 16),
                            System.Convert.ToByte(hex[6..8], 16)));

                    if (hex.Length == 6)
                        return new SolidColorBrush(Color.FromArgb(
                            0xFF,
                            System.Convert.ToByte(hex[..2], 16),
                            System.Convert.ToByte(hex[2..4], 16),
                            System.Convert.ToByte(hex[4..6], 16)));
                }
                catch { }
            }
            return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
