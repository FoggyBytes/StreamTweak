using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    /// <summary>
    /// 10.0 Setup: what is set once on this host — Clients, Network, Apps, Spatial audio,
    /// NVIDIA Sentinel — one sidebar item each, all on this page. It replaces the 8.0 Tuning page
    /// (Display &amp; audio went: HDR is the streaming servers' job; Network is back as it was in
    /// 9.2.5) and the Clients page. Each panel reuses
    /// its ViewModel unchanged; the code-behind only picks the panel and forwards the handlers.
    /// </summary>
    public sealed partial class SetupView : Page
    {
        public SettingsViewModel      Clients { get; } = new SettingsViewModel();
        public NetworkViewModel       Net     { get; } = new NetworkViewModel();
        public AudioViewModel         Aud     { get; } = new AudioViewModel();
        public NvidiaProfileViewModel Nv      { get; } = new NvidiaProfileViewModel();
        public AppsViewModel          AppsVm  { get; } = new AppsViewModel();

        // Guard: true while we programmatically set the audio toggles.
        private bool _updatingToggles;
        private Action? _bridgeClientsHandler;

        public SetupView()
        {
            this.InitializeComponent();
            SizeChanged += (_, e) =>
            {
                // The same content column as every other page (Controls/PageLayout).
                PanelsHost.Padding = StreamTweak.Controls.PageLayout.Padding(e.NewSize.Width);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Clients.Load();
            var auth = AppStateService.Instance.BridgeAuth;
            if (auth != null)
            {
                _bridgeClientsHandler = () => DispatcherQueue.TryEnqueue(Clients.RefreshBridgeClients);
                auth.ClientsChanged += _bridgeClientsHandler;
            }

            _ = Net.InitializeAsync();
            Net.StartSpeedRefresh();
            Net.RefreshTailscale();

            Aud.PropertyChanged += OnAudioPropertyChanged;
            _ = Aud.InitializeAsync();

            // NVIDIA Sentinel is NVIDIA-only (MainWindow hides its item on AMD/Intel).
            if (AppStateService.Instance.NvidiaSentinel?.IsNvidiaAvailable == true) _ = Nv.InitializeAsync();

            AppsVm.Load();

            // The sidebar item's tag: Clients, Network, Apps, Audio or Sentinel.
            ShowTab(e.Parameter as string ?? "Clients");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            var auth = AppStateService.Instance.BridgeAuth;
            if (auth != null && _bridgeClientsHandler != null)
                auth.ClientsChanged -= _bridgeClientsHandler;
            _bridgeClientsHandler = null;

            Net.StopSpeedRefresh();
            Net.Unsubscribe();
            Aud.PropertyChanged -= OnAudioPropertyChanged;
            Aud.Unsubscribe();
            Nv.Cleanup();
            AppsVm.Unsubscribe();
        }

        // ── Which page ───────────────────────────────────────────────────────────

        /// <summary>Shows one page: Clients, Network, Apps, Audio or Sentinel.</summary>
        public void ShowTab(string tab)
        {
            PageTitle.Text = tab switch
            {
                "Network"  => "Network",
                "Apps"     => "Apps",
                "Audio"    => "Spatial audio",
                "Sentinel" => "NVIDIA Sentinel",
                _          => "Clients",
            };
            PanelClients.Visibility = tab is not ("Network" or "Apps" or "Audio" or "Sentinel") ? Visibility.Visible : Visibility.Collapsed;
            PanelNet.Visibility     = tab == "Network"  ? Visibility.Visible : Visibility.Collapsed;
            PanelApps.Visibility    = tab == "Apps"     ? Visibility.Visible : Visibility.Collapsed;
            PanelAudio.Visibility   = tab == "Audio"    ? Visibility.Visible : Visibility.Collapsed;
            PanelNv.Visibility      = tab == "Sentinel" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Network ──────────────────────────────────────────────────────────────

        private void RestoreNow_Click(object sender, RoutedEventArgs e) => Net.RestoreNow();

        private async void CopyTailscaleIp_Click(object sender, RoutedEventArgs e)
            => await Net.CopyTailscaleIpAsync();

        private async void CopyLanIp_Click(object sender, RoutedEventArgs e)
            => await Net.CopyLanIpAsync();

        // ── Clients ──────────────────────────────────────────────────────────────

        private void ApproveClient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string uid)
                Clients.ApproveBridgeClient(uid);
        }

        /// <summary>The device icon opens a menu of the five kinds; the current one is checked.</summary>
        private void DeviceKind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string uid } anchor) return;
            string? current = Clients.BridgeClients.FirstOrDefault(c => c.UniqueId == uid)?.DeviceKind;

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
                item.Click += (_, _) => Clients.SetDeviceKind(uid, key);
                menu.Items.Add(item);
            }
            menu.ShowAt(anchor);
        }

        /// <summary>10.0.0. The row is rebuilt on every refresh and a ToggleSwitch raises Toggled
        /// when its first value is bound, so only a real change goes through: the same value
        /// would otherwise clear the upgrade notice the moment the page opens.</summary>
        private void HostManagement_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch { Tag: string uid } ts) return;
            var item = Clients.BridgeClients.FirstOrDefault(c => c.UniqueId == uid);
            if (item == null || item.HostManagement == ts.IsOn) return;
            Clients.SetHostManagement(uid, ts.IsOn);
        }

        private void HostManagementNotice_Close(InfoBar sender, object args)
            => Clients.DismissHostManagementNotice();

        private void RevokeClient_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string uid)
                Clients.RevokeBridgeClient(uid);
        }

        // ── Spatial audio ────────────────────────────────────────────────────────

        private void OnAudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Aud.IsDolbyActive) or nameof(Aud.IsSonicActive))
            {
                _updatingToggles = true;
                DolbyToggle.IsOn = Aud.IsDolbyActive;
                SonicToggle.IsOn = Aud.IsSonicActive;
                _updatingToggles = false;
            }
        }

        private async void DolbyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingToggles) return;
            if (sender is ToggleSwitch ts)
                await Aud.ToggleDolbyAsync(ts.IsOn);
        }

        private async void SonicToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingToggles) return;
            if (sender is ToggleSwitch ts)
                await Aud.ToggleSonicAsync(ts.IsOn);
        }

        // ── NVIDIA Sentinel ──────────────────────────────────────────────────────

        private async void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (Nv.AutoRestoreEnabled)
            {
                var dmSans = new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
                var body = new TextBlock
                {
                    Text =
                        "Saving while Auto-restore is enabled may capture the previously restored profile instead of your latest changes — the watcher reverts any external DRS modification within a few seconds.\n\n" +
                        "Recommended workflow:\n" +
                        "  1. Turn off \"Auto-restore\" below\n" +
                        "  2. Configure your desired settings in NVIDIA App\n" +
                        "  3. Come back here and click \"Save current as my profile\"\n" +
                        "  4. Turn Auto-restore back on",
                    FontFamily   = dmSans,
                    FontSize     = 13,
                    Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                    TextWrapping = TextWrapping.Wrap,
                };
                var dialog = new ContentDialog
                {
                    Title             = "Auto-restore is active",
                    Content           = body,
                    PrimaryButtonText = "Save anyway",
                    CloseButtonText   = "Got it, I'll turn it off first",
                    DefaultButton     = ContentDialogButton.Close,
                    XamlRoot          = this.XamlRoot,
                };
                dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
                dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
                dialog.Resources["ContentControlThemeFontFamily"] = dmSans;

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;
            }

            _ = Nv.SaveCurrentAsync();
        }

        private void RestoreProfile_Click(object sender, RoutedEventArgs e)
            => _ = Nv.RestoreAsync();

        private async void ClearProfile_Click(object sender, RoutedEventArgs e)
        {
            var dmSans = new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
            var body = new TextBlock
            {
                Text =
                    "This will discard your saved NVIDIA profile. After clearing:\n\n" +
                    "  • Auto-restore will have nothing to re-apply and will sit idle\n" +
                    "  • NVIDIA App keeps whatever it currently has — StreamTweak won't touch it\n\n" +
                    "You can capture a new profile anytime with “Save current as my profile”.\n\n" +
                    "Continue?",
                FontFamily   = dmSans,
                FontSize     = 13,
                Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                TextWrapping = TextWrapping.Wrap,
            };
            var dialog = new ContentDialog
            {
                Title             = "Clear saved NVIDIA profile?",
                Content           = body,
                PrimaryButtonText = "Clear profile",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.XamlRoot,
            };
            var dangerFg  = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
            var dangerBg  = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
            var dangerBdr = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
            dialog.Resources["ContentDialogBackground"]            = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]           = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"]      = dmSans;
            dialog.Resources["AccentButtonBackground"]             = dangerBg;
            dialog.Resources["AccentButtonForeground"]             = dangerFg;
            dialog.Resources["AccentButtonBorderBrush"]            = dangerBdr;
            dialog.Resources["AccentButtonBackgroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForegroundPointerOver"]  = dangerFg;
            dialog.Resources["AccentButtonBorderBrushPointerOver"] = dangerBdr;
            dialog.Resources["AccentButtonBackgroundPressed"]      = new SolidColorBrush(Color.FromArgb(0x55, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForegroundPressed"]      = dangerFg;
            dialog.Resources["AccentButtonBorderBrushPressed"]     = dangerBdr;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await Nv.ClearAsync();
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
            => Nv.OpenLogFile();

        // ── Apps ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Add app's menu, built each time it opens (plan 10.0, Apps): the programs running now
        /// first — one click adds one — then "Browse for a program…" for anything else.
        /// </summary>
        private void AddMenu_Opening(object sender, object e)
        {
            AddMenu.Items.Clear();
            var running = AppsVm.RunningPrograms();

            if (running.Count == 0)
                AddMenu.Items.Add(new MenuFlyoutItem { Text = "No other program is running", IsEnabled = false });

            foreach (var prog in running)
            {
                var icon = new ImageIcon();
                var item = new MenuFlyoutItem { Text = prog.Name, Icon = icon };
                ToolTipService.SetToolTip(item, prog.Path);
                item.Click += async (_, _) => await AppsVm.AddAsync(prog.Path, prog.Name);
                AddMenu.Items.Add(item);
                _ = SetIconAsync(icon, prog.Path);
            }

            AddMenu.Items.Add(new MenuFlyoutSeparator());
            var browse = new MenuFlyoutItem { Text = "Browse for a program…", Icon = new FontIcon { Glyph = "" } };
            browse.Click += async (_, _) => await AppsVm.BrowseAsync();
            AddMenu.Items.Add(browse);
        }

        private static async Task SetIconAsync(ImageIcon icon, string path)
        {
            var bmp = await AppsViewModel.IconFor(path);
            if (bmp != null) icon.Source = bmp;
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
            => AppsVm.RemoveSelected();

        private void KillButton_Click(object sender, RoutedEventArgs e)
            => AppsVm.KillSelected();

        private async void RestartButton_Click(object sender, RoutedEventArgs e)
            => await AppsVm.RestartSelectedAsync();

        private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
            => AppsVm.HasStatus = false;
    }
}
