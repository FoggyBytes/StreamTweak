using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    public sealed partial class SettingsView : Page
    {
        public SettingsViewModel ViewModel { get; } = new SettingsViewModel();

        private Action? _bridgeClientsHandler;

        public SettingsView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();

            // Live-refresh the bridge client list while this page is open.
            var auth = AppStateService.Instance.BridgeAuth;
            if (auth != null)
            {
                _bridgeClientsHandler = () => DispatcherQueue.TryEnqueue(ViewModel.RefreshBridgeClients);
                auth.ClientsChanged += _bridgeClientsHandler;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            var auth = AppStateService.Instance.BridgeAuth;
            if (auth != null && _bridgeClientsHandler != null)
                auth.ClientsChanged -= _bridgeClientsHandler;
            _bridgeClientsHandler = null;
        }

        private void ApproveClient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string uid)
                ViewModel.ApproveBridgeClient(uid);
        }

        private void RevokeClient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string uid)
                ViewModel.RevokeBridgeClient(uid);
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenDataFolder();

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenLogFolder();

        private async void OpenLicense_Click(object sender, RoutedEventArgs e)
            => await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://www.gnu.org/licenses/gpl-3.0.html"));

        private async void OpenDonate_Click(object sender, RoutedEventArgs e)
            => await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://www.paypal.com/paypalme/foggypunk"));

        private async void OpenStreamTweakReleases_Click(object sender, RoutedEventArgs e)
            => await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/FoggyBytes/StreamTweak/releases"));

        private async void OpenStreamLightReleases_Click(object sender, RoutedEventArgs e)
            => await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/FoggyBytes/StreamLight/releases"));

        private async void OpenServerRepo_Click(object sender, RoutedEventArgs e)
        {
            string url = ViewModel.ServerRepoUrl;
            if (!string.IsNullOrEmpty(url))
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }

        private void OpenDebugLog_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenDebugLog();

        private void ClearSessions_Click(object sender, RoutedEventArgs e)
            => ViewModel.ClearSessions();

        private async void DebugModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool isOn = DebugModeToggle.IsOn;
            if (ViewModel.IsDebugModeActive == isOn) return;
            await ViewModel.ToggleDebugMode(isOn);
        }

        private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
            => ViewModel.HasStatus = false;
    }
}
