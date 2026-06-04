using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    public sealed partial class NvidiaProfileView : Page
    {
        public NvidiaProfileViewModel ViewModel { get; } = new NvidiaProfileViewModel();

        public NvidiaProfileView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = ViewModel.InitializeAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Cleanup();
        }

        /// <summary>
        /// When Auto-restore is ON, saving would capture whatever the watcher/poll has
        /// just reverted to — almost certainly NOT the changes the user is currently
        /// tuning in NVIDIA App. Warn and recommend the correct workflow.
        /// </summary>
        private async void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.AutoRestoreEnabled)
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

                // Affirmative ("Save anyway") on the left as Primary, to match every other
                // StreamTweak dialog. Close (or X) = "Got it…" = cancel and let the user
                // disable auto-restore first; it stays the safe default button.
                if (result != ContentDialogResult.Primary) return;
            }

            _ = ViewModel.SaveCurrentAsync();
        }

        private void RestoreProfile_Click(object sender, RoutedEventArgs e)
            => _ = ViewModel.RestoreAsync();

        /// <summary>Confirmation before discarding the saved profile — destructive, no undo.</summary>
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
                await ViewModel.ClearAsync();
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
            => ViewModel.OpenLogFile();
    }
}
