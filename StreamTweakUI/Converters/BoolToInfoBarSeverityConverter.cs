using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace StreamTweak.Converters
{
    /// <summary>
    /// true (error) → InfoBarSeverity.Error, false → InfoBarSeverity.Informational
    /// </summary>
    public sealed class BoolToInfoBarSeverityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
