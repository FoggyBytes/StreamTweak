using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Data;

namespace StreamTweak.Converters
{
    /// <summary>
    /// Presentation-only: shortens a verbose driver link-speed key
    /// (e.g. "1.0 Gbps Full Duplex", "2.5 Gbps Full Duplex", "100 Mbps Full Duplex")
    /// to a compact segmented-control label ("1 Gbps", "2.5 Gbps", "100 Mbps").
    /// The bound SelectedItem still carries the full key — this only affects display.
    /// </summary>
    public sealed partial class SpeedKeyToShortLabelConverter : IValueConverter
    {
        private static readonly Regex SpeedRx =
            new(@"(\d+(?:\.\d+)?)\s*(Mbps|Gbps)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string key = value as string ?? string.Empty;
            var m = SpeedRx.Match(key);
            if (!m.Success) return key;

            // Trim a trailing ".0" so "1.0 Gbps" reads as "1 Gbps"; keep "2.5 Gbps".
            string num = m.Groups[1].Value;
            if (num.EndsWith(".0")) num = num[..^2];

            // Normalise the unit's casing (Mbps / Gbps).
            string unit = m.Groups[2].Value;
            unit = char.ToUpperInvariant(unit[0]) + unit[1..].ToLowerInvariant();

            return $"{num} {unit}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value;
    }
}
