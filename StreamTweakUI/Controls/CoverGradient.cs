using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace StreamTweak.Controls
{
    /// <summary>
    /// 9.0 hero backdrop: the colours of a game's cover as a soft diagonal gradient. It replaced
    /// the cover itself decoded at 24 px and stretched, which showed its pixels. The cover is read
    /// once at 4×6 px (the decoder averages each cell) and three cells give the stops: bottom-left,
    /// right of centre, top. Measures to nothing, so it never sizes the card it sits in.
    /// </summary>
    public sealed class CoverGradient : Grid
    {
        public static readonly DependencyProperty CoverPathProperty =
            DependencyProperty.Register(nameof(CoverPath), typeof(string), typeof(CoverGradient),
                new PropertyMetadata(null, (d, _) => ((CoverGradient)d).Refresh()));

        public string? CoverPath { get => (string?)GetValue(CoverPathProperty); set => SetValue(CoverPathProperty, value); }

        // Per cover path, for the life of the app: the hero and the sheet show the same few covers.
        // Touched only on the UI thread.
        private static readonly Dictionary<string, Color[]> Cache = new(StringComparer.OrdinalIgnoreCase);

        private int _version;

        private async void Refresh()
        {
            int version = ++_version;
            string? path = CoverPath;
            if (string.IsNullOrEmpty(path)) { Background = null; return; }

            if (!Cache.TryGetValue(path, out var stops))
            {
                var read = await ReadStopsAsync(path);
                if (read == null) { if (version == _version) Background = null; return; }
                Cache[path] = stops = read;
            }
            if (version != _version) return;   // another cover arrived while this one was read

            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.2),
                EndPoint = new Point(1, 0.8),
                GradientStops =
                {
                    new GradientStop { Color = stops[0], Offset = 0 },
                    new GradientStop { Color = stops[1], Offset = 0.55 },
                    new GradientStop { Color = stops[2], Offset = 1 },
                },
            };
        }

        private static async Task<Color[]?> ReadStopsAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = 4, ScaledHeight = 6, InterpolationMode = BitmapInterpolationMode.Fant,
                };
                var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                byte[] px = data.DetachPixelData();

                Color At(int x, int y)
                {
                    int i = (y * 4 + x) * 4;
                    return Color.FromArgb(0xFF, px[i + 2], px[i + 1], px[i]);
                }
                return new[] { At(0, 5), At(3, 2), At(2, 0) };
            }
            catch
            {
                return null;   // no backdrop is better than a broken hero
            }
        }
    }
}
