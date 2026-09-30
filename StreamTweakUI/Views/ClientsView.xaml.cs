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
                // The same content column as every other page (Controls/PageLayout).
                PageRoot.Padding = StreamTweak.Controls.PageLayout.Padding(e.NewSize.Width);
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

        /// <summary>The device icon opens a menu of the five kinds; the current one is checked.</summary>
        private void DeviceKind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string uid } anchor) return;
            string? current = ViewModel.BridgeClients.FirstOrDefault(c => c.UniqueId == uid)?.DeviceKind;

            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
            foreach (var (key, label, glyph) in DeviceKinds.All)
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = label,
                    GroupName = "DeviceKind",
                    IsChecked = key == current,
                    Icon = new FontIcon { Glyph = glyph },
                };
                item.Click += (_, _) => ViewModel.SetDeviceKind(uid, key);
                menu.Items.Add(item);
            }
            menu.ShowAt(anchor);
        }

        private void RevokeClient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string uid)
                ViewModel.RevokeBridgeClient(uid);
        }
    }
}
