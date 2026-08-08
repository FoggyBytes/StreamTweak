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
    /// 8.0 consolidated "Tuning" page: a master-detail rail hosting the five host
    /// optimisations (Network / Display / Audio / NVIDIA Sentinel / Managed apps).
    /// Each panel reuses the existing, unchanged ViewModels — no business logic is
    /// duplicated here; the code-behind only recomposes the controls and forwards
    /// the same handler calls the standalone 7.x pages used.
    /// </summary>
    public sealed partial class TuningView : Page
    {
        public NetworkViewModel      Net    { get; } = new NetworkViewModel();
        public DisplayViewModel      Disp   { get; } = new DisplayViewModel();
        public AudioViewModel        Aud    { get; } = new AudioViewModel();
        public NvidiaProfileViewModel Nv    { get; } = new NvidiaProfileViewModel();
        public AppsViewModel         AppsVm { get; } = new AppsViewModel();

        // Guard: true while we programmatically set the audio toggles (see AudioView).
        private bool _updatingToggles;

        public TuningView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Each Host-setup subsection navigates here with its tag as the parameter.
            string section = e.Parameter as string ?? "TuneNet";

            _ = Net.InitializeAsync();
            Net.StartSpeedRefresh();
            Net.RefreshTailscale();

            _ = Disp.InitializeAsync();

            Aud.PropertyChanged += OnAudioPropertyChanged;
            _ = Aud.InitializeAsync();

            // NVIDIA Sentinel is NVIDIA-only (its sidebar item is hidden elsewhere too).
            bool nvOk = AppStateService.Instance.NvidiaSentinel?.IsNvidiaAvailable == true;
            if (nvOk) _ = Nv.InitializeAsync();

            AppsVm.Load();

            ShowSection(section);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            Net.StopSpeedRefresh();
            Net.Unsubscribe();
            Aud.PropertyChanged -= OnAudioPropertyChanged;
            Aud.Unsubscribe();
            Nv.Cleanup();
        }

        // ── Section (nav parameter) → panel switching ──────────────────────────
        private void ShowSection(string tag)
        {
            PanelNet.Visibility     = tag == "TuneNet"  ? Visibility.Visible : Visibility.Collapsed;
            PanelDispAud.Visibility = tag == "TuneAV"   ? Visibility.Visible : Visibility.Collapsed;
            PanelNv.Visibility      = tag == "TuneNv"   ? Visibility.Visible : Visibility.Collapsed;
            PanelApps.Visibility    = tag == "TuneApps" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Network ────────────────────────────────────────────────────────────
        private void RestoreNow_Click(object sender, RoutedEventArgs e) => Net.RestoreNow();

        private async void CopyTailscaleIp_Click(object sender, RoutedEventArgs e)
            => await Net.CopyTailscaleIpAsync();

        // ── Display ────────────────────────────────────────────────────────────
        private void RefreshDisplays_Click(object sender, RoutedEventArgs e)
            => _ = Disp.InitializeAsync();

        private void HdrToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch ts) return;
            if (ts.DataContext is not MonitorEntry entry) return;
            _ = Disp.ToggleMonitorHdrAsync(entry, ts.IsOn);
        }

        // ── Audio ──────────────────────────────────────────────────────────────
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

        // ── Managed apps ─────────────────────────────────────────────────────────
        private async void AddButton_Click(object sender, RoutedEventArgs e)
            => await AppsVm.AddAsync();

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
