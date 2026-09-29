using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    /// <summary>
    /// 9.0: device cards in a responsive grid, pending devices first with their PIN.
    /// 8.0 "Clients &amp; security" page — promotes the bridge-client approval list out of
    /// Settings into its own section. Reuses SettingsViewModel's bridge members (the list is
    /// sourced from the shared AppStateService.BridgeAuth, so it stays consistent with Settings).
    /// </summary>
    public sealed partial class ClientsView : Page
    {
        public SettingsViewModel ViewModel { get; } = new SettingsViewModel();

        private Action? _bridgeClientsHandler;

        public ClientsView()
        {
            this.InitializeComponent();
            SizeChanged += (_, e) =>
            {
                double side = Math.Clamp(Math.Round(e.NewSize.Width * 0.032), 16, 48);
                PageRoot.Padding = new Thickness(side, 24, side, 40);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();

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
    }
}
