using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

        public LogsViewModel ViewModel { get; } = new LogsViewModel();

        public LogsView()
        {
            this.InitializeComponent();
            this.KeyDown += OnPageKeyDown;
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
                else if (ViewModel.IsDetailVisible)
                {
                    ViewModel.CloseDetail();
                    e.Handled = true;
                }
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
        }

        // ── Session list ──────────────────────────────────────────────────────

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
            => ViewModel.ClearHistory();

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

        // ── Fullscreen chart ──────────────────────────────────────────────────

        private void CloseFullscreen_Click(object sender, RoutedEventArgs e)
            => ViewModel.CloseFullscreenChart();
    }
}
