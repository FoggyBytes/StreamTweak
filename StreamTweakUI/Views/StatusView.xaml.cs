using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    /// <summary>10.0 Status — see <see cref="StatusViewModel"/>.</summary>
    public sealed partial class StatusView : Page
    {
        public StatusViewModel ViewModel { get; } = new StatusViewModel();

        public StatusView()
        {
            this.InitializeComponent();
            SizeChanged += (_, e) =>
            {
                // The same content column as every other page (Controls/PageLayout).
                PageRoot.Padding = StreamTweak.Controls.PageLayout.Padding(e.NewSize.Width);
            };
            ViewModel.CoversReady += BuildCoverWall;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Start();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Stop();
        }

        // ── Hero ─────────────────────────────────────────────────────────────────

        private const double CoverW = 96, CoverH = 144, Gap = 10;

        /// <summary>
        /// The hero's backdrop: five columns of the covers of the games on this PC, alternately
        /// raised, tilted by the Canvas. Fewer covers than places repeat; none leaves the gradient.
        /// Decoded at their drawn size, so fifteen covers cost what fifteen thumbnails cost.
        /// </summary>
        private void BuildCoverWall(IReadOnlyList<string> covers)
        {
            CoverWall.Children.Clear();
            if (covers.Count == 0) return;
            int i = 0;
            for (int col = 0; col < 6; col++)
            {
                double x = 20 + col * (CoverW + Gap);
                double y0 = -70 + (col % 2) * 64;
                for (int row = 0; row < 3; row++)
                {
                    var bmp = new BitmapImage { DecodePixelWidth = 192 };
                    try { bmp.UriSource = new Uri(covers[i++ % covers.Count]); } catch { continue; }
                    var tile = new Border
                    {
                        Width = CoverW, Height = CoverH, CornerRadius = new CornerRadius(8),
                        Background = new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill },
                    };
                    Canvas.SetLeft(tile, x);
                    Canvas.SetTop(tile, y0 + row * (CoverH + Gap));
                    CoverWall.Children.Add(tile);
                }
            }
        }

        /// <summary>The hero clips the tilted covers to its own rounded corners — a Grid's
        /// CornerRadius rounds its background, not what its children draw.</summary>
        private void Hero_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(Hero);
            var c = visual.Compositor;
            var shape = c.CreateRoundedRectangleGeometry();
            shape.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            shape.CornerRadius = new Vector2(14, 14);
            visual.Clip = c.CreateGeometricClip(shape);
        }

        // ── Getting started ──────────────────────────────────────────────────────

        private static readonly SolidColorBrush Done = new(Color.FromArgb(0xFF, 0x4a, 0xde, 0x80));
        private static readonly SolidColorBrush Todo = new(Color.FromArgb(0xFF, 0x64, 0x6b, 0x67));

        public string StepGlyph(bool done) => done ? "" : "";   // check mark · empty circle
        public SolidColorBrush StepBrush(bool done) => done ? Done : Todo;

        private void HideSteps_Click(object sender, RoutedEventArgs e) => ViewModel.HideSteps();

        // ── Needs you ────────────────────────────────────────────────────────────

        private void Approve_Click(object sender, RoutedEventArgs e)       => ViewModel.Approve(hostManagement: false);
        private void ApproveManage_Click(object sender, RoutedEventArgs e) => ViewModel.Approve(hostManagement: true);
        private void Deny_Click(object sender, RoutedEventArgs e)          => ViewModel.Deny();

        // ── Streaming now ────────────────────────────────────────────────────────

        /// <summary>Asked first, as the plan decided: it ends someone's game session.</summary>
        private async void ReturnToClient_Click(object sender, RoutedEventArgs e)
        {
            var dmSans = new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
            var dialog = new ContentDialog
            {
                Title             = "Return to the client?",
                Content           = new TextBlock
                {
                    Text         = "StreamLight ends the stream and goes back to its own screen. Other clients, such as Moonlight, do not answer this.",
                    FontFamily   = dmSans,
                    FontSize     = 13,
                    Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Return to client",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.XamlRoot,
            };
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"] = dmSans;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.ReturnToClient();
        }

        // ── Tiles ────────────────────────────────────────────────────────────────

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string target } && target.Length > 0)
                App.MainWindow?.NavigateTo(target);
        }

        private void RestoreLink_Click(object sender, RoutedEventArgs e) => ViewModel.RestoreLink();
    }
}
