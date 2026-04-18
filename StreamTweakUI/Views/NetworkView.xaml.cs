using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    public sealed partial class NetworkView : Page
    {
        public NetworkViewModel ViewModel { get; } = new NetworkViewModel();

        public NetworkView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = ViewModel.InitializeAsync();
            ViewModel.StartSpeedRefresh();
            // Re-check Tailscale every time the page is opened in case
            // the user connected/disconnected since last visit.
            ViewModel.RefreshTailscale();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.StopSpeedRefresh();
        }

        private async void ToggleStreamingButton_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ToggleStreamingAsync();

        private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ApplySettingsAsync();

        private async void CopyTailscaleIp_Click(object sender, RoutedEventArgs e)
            => await ViewModel.CopyTailscaleIpAsync();
    }
}
