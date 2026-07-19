using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;
using Windows.System;
using Windows.UI;

namespace StreamTweak.Views
{
    public sealed partial class LogsView : Page
    {
        // Chart line colors — match the old WPF app palette
        private static readonly Color ColorRtt     = Color.FromArgb(0xFF, 0xFF, 0xA7, 0x26); // amber
        private static readonly Color ColorDrops   = Color.FromArgb(0xFF, 0xEF, 0x53, 0x50); // red
        private static readonly Color ColorBitrate = Color.FromArgb(0xFF, 0x26, 0xC6, 0xDA); // cyan
        private static readonly Color ColorDecode  = Color.FromArgb(0xFF, 0xAB, 0x47, 0xBC); // purple
        private static readonly Color ColorHostLat = Color.FromArgb(0xFF, 0x66, 0xBB, 0x6A); // green

        public LogsViewModel ViewModel { get; } = new LogsViewModel();

        public LogsView()
        {
            this.InitializeComponent();
            this.KeyDown += OnPageKeyDown;
        }

        // Detail charts are stacked in a single scrollable column; their height grows
        // with the available window height so they stay readable from the minimum
        // window (floor 185 px) up to 4K (cap 280 px), without wasting space.
        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double h = e.NewSize.Height;
            ViewModel.DetailChartHeight = h < 900  ? 185
                                        : h < 1300 ? 225
                                        :            280;
        }

        private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                if (ViewModel.IsChartFullscreen)
                {
                    ViewModel.CloseFullscreenChart();
                    e.Handled = true;
                }
                else if (ViewModel.IsCompareVisible)
                {
                    ViewModel.CloseCompare();
                    e.Handled = true;
                }
                else if (ViewModel.IsDetailVisible)
                {
                    ViewModel.CloseDetail();
                    e.Handled = true;
                }
            }
        }

        // ── Compare ───────────────────────────────────────────────────────────

        private async void Compare_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenCompare();
            await ViewModel.LoadCompareCoversAsync();
        }

        private void CloseCompare_Click(object sender, RoutedEventArgs e)
            => ViewModel.CloseCompare();

        private async void CompareSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!ViewModel.IsCompareVisible) return; // ignore the initial population
            ViewModel.OnCompareSelectionChanged();
            await ViewModel.LoadCompareCoversAsync();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
        }

        // ── Session list ──────────────────────────────────────────────────────

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            // Browser-style "clear cache" chooser: pick a time window, then confirm.
            var options = new[] { "Last hour", "Last 24 hours", "Last 7 days", "Last 4 weeks", "All time" };
            var radios = new RadioButtons
            {
                ItemsSource   = options,
                SelectedIndex = 1,                       // default: Last 24 hours
                Margin        = new Thickness(0, 8, 0, 0),
            };

            // Selected dot in the app's green instead of the system accent color.
            var green     = new SolidColorBrush(Color.FromArgb(0xFF, 0x4a, 0xde, 0x80));
            var greenLite = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
            var greenDark = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
            radios.Resources["RadioButtonOuterEllipseCheckedFill"]              = green;
            radios.Resources["RadioButtonOuterEllipseCheckedFillPointerOver"]   = greenLite;
            radios.Resources["RadioButtonOuterEllipseCheckedFillPressed"]       = greenDark;
            radios.Resources["RadioButtonOuterEllipseCheckedStroke"]            = green;
            radios.Resources["RadioButtonOuterEllipseCheckedStrokePointerOver"] = greenLite;
            radios.Resources["RadioButtonOuterEllipseCheckedStrokePressed"]     = greenDark;

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text         = "Delete sessions recorded within:",
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = DmSans,
                Foreground   = new SolidColorBrush(DialogBodyText),
            });
            panel.Children.Add(radios);

            var dialog = new ContentDialog
            {
                Title             = "Clear history",
                Content           = panel,
                PrimaryButtonText = "Delete",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = this.XamlRoot,
            };
            ApplyDialogChrome(dialog);
            ApplyDangerAccentPalette(dialog);   // red Delete (Primary), grey Cancel

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            TimeSpan? window = radios.SelectedIndex switch
            {
                0 => TimeSpan.FromHours(1),
                1 => TimeSpan.FromHours(24),
                2 => TimeSpan.FromDays(7),
                3 => TimeSpan.FromDays(28),
                _ => null,                              // All time
            };
            ViewModel.ClearHistory(window);
        }

        // ── Dialog chrome (mirrors MainWindow's helpers) ──────────────────────
        private static readonly FontFamily DmSans =
            new("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
        private static readonly Color DialogBg       = Color.FromArgb(0xF2, 0x1d, 0x1b, 0x1a);
        private static readonly Color DialogBorder   = Color.FromArgb(0xFF, 0x2A, 0x27, 0x24);
        private static readonly Color DialogBodyText = Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8);
        private static readonly Color DangerColor    = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);

        private static void ApplyDialogChrome(ContentDialog dialog)
        {
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(DialogBg);
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(DialogBorder);
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
        }

        private static void ApplyDangerAccentPalette(ContentDialog dialog)
        {
            dialog.Resources["AccentButtonBackground"]             = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForeground"]             = new SolidColorBrush(DangerColor);
            dialog.Resources["AccentButtonBorderBrush"]            = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonBackgroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0x2D, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForegroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0xFF, 0xFC, 0xA5, 0xA5));
            dialog.Resources["AccentButtonBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonBackgroundPressed"]      = new SolidColorBrush(Color.FromArgb(0x12, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForegroundPressed"]      = new SolidColorBrush(DangerColor);
            dialog.Resources["AccentButtonBorderBrushPressed"]     = new SolidColorBrush(Color.FromArgb(0x44, 0xEF, 0x44, 0x44));
        }

        private async void DetailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SessionEntry entry)
            {
                ViewModel.OpenDetail(entry);
                await ViewModel.LoadDetailCoversAsync();
            }
        }

        private void DeleteSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SessionEntry entry)
                ViewModel.DeleteSession(entry);
        }

        // ── Detail overlay ────────────────────────────────────────────────────

        private void CloseDetail_Click(object sender, RoutedEventArgs e)
            => ViewModel.CloseDetail();

        // ── Chart tapped → open fullscreen ────────────────────────────────────

        private void RttChart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenFullscreenChart("RTT ms", ViewModel.RttSeries, ColorRtt);
            e.Handled = true;
        }

        private void DropsChart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenFullscreenChart("Frame Drops", ViewModel.DropsSeries, ColorDrops);
            e.Handled = true;
        }

        private void BitrateChart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenFullscreenChart("Bitrate Mbps", ViewModel.BitrateSeries, ColorBitrate);
            e.Handled = true;
        }

        private void DecodeChart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenFullscreenChart("Decode ms", ViewModel.DecodeSeries, ColorDecode);
            e.Handled = true;
        }

        private void HostLatencyChart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenFullscreenChart("Host latency ms", ViewModel.HostLatencySeries, ColorHostLat);
            e.Handled = true;
        }

        private void HostComputeChart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.OpenFullscreenChart("Host compute %", ViewModel.HostComputeLines);
            e.Handled = true;
        }

        // ── Fullscreen chart ──────────────────────────────────────────────────

        private void CloseFullscreen_Click(object sender, RoutedEventArgs e)
            => ViewModel.CloseFullscreenChart();
    }
}
