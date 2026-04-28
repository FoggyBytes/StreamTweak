using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    public sealed partial class HomeView : Page
    {
        public HomeViewModel ViewModel { get; } = new HomeViewModel();

        private static readonly SolidColorBrush ActiveBrush =
            new(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));  // #22c55e — design token STSuccess
        private static readonly SolidColorBrush InactiveBrush =
            new(Color.FromArgb(0xFF, 0x44, 0x44, 0x44));  // #444 — design token dim

        public HomeView()
        {
            this.InitializeComponent();

            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.IsSessionActive))
                    UpdateSessionDot(ViewModel.IsSessionActive);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Reload status tiles whenever a tray toggle changes while this tab is active.
            AppStateService.Instance.SettingsChanged += OnSettingsChanged;
            UpdateSessionDot(ViewModel.IsSessionActive);
            _ = ViewModel.LoadStatusAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            AppStateService.Instance.SettingsChanged -= OnSettingsChanged;
            ViewModel.Unsubscribe();
        }

        private void OnSettingsChanged(object? sender, EventArgs e)
            => _ = ViewModel.LoadStatusAsync();

        private void UpdateSessionDot(bool active)
        {
            if (active)
            {
                SessionDot.Fill = ActiveBrush;
                SessionStatusText.Text = "Active";
                PulseStoryboard.Begin();
            }
            else
            {
                PulseStoryboard.Stop();
                SessionDot.Fill = InactiveBrush;
                SessionDot.Opacity = 1.0;
                SessionStatusText.Text = "Inactive";
            }
        }

        // ── Status tile navigation ──────────────────────────────────────────────

        private static void NavigateTo(string tag)
            => App.MainWindow?.NavigateTo(tag);

        private void NicSpeedTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => NavigateTo("Network");

        private void AutoStreamingTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => NavigateTo("Network");

        private void HdrTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => NavigateTo("Display");

        private void SpatialAudioTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => NavigateTo("Audio");

        private void GameLibraryTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => NavigateTo("GameLibrary");

        private void AutoHdrTile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => NavigateTo("Display");

        private void StopStreamButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.RequestStopStream();

        private void LicenseButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenLicense();

        private void GitHubButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenGitHub();

        private void PayPalButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenPayPal();
    }
}
