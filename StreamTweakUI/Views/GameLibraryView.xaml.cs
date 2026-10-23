using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    /// <summary>10.0 Library — see <see cref="GameLibraryViewModel"/>.</summary>
    public sealed partial class GameLibraryView : Page
    {
        public GameLibraryViewModel ViewModel { get; } = new GameLibraryViewModel();

        public GameLibraryView()
        {
            this.InitializeComponent();
            SizeChanged += (_, e) =>
            {
                // The same content column as every other page (Controls/PageLayout); padding and
                // not MaxWidth, so the wheel keeps scrolling the list over the side margins.
                var side = StreamTweak.Controls.PageLayout.Side(e.NewSize.Width);
                GameList.Padding = new Thickness(side, 0, side, 40);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
            GameLibraryService.LibraryChanged += OnLibraryChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            GameLibraryService.LibraryChanged -= OnLibraryChanged;
            ViewModel.Stop();
        }

        // A sync this page did not start (StreamLight's Sync and restart, the start-up sync).
        private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(ViewModel.Reload);

        private static readonly SolidColorBrush Ok  = new(Color.FromArgb(0xFF, 0xC8, 0xCF, 0xCB));
        private static readonly SolidColorBrush Bad = new(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71));
        public SolidColorBrush JobBrush(bool failed) => failed ? Bad : Ok;

        private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string tab) ViewModel.Tab = tab;
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryRow row) ViewModel.Play(row);
        }

        private async void Act_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryRow row) await ViewModel.ActAsync(row);
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e) => await ViewModel.AddGameAsync();

        /// <summary>Asked first: it empties the list and restarts the streaming server.</summary>
        private async void ClearLibrary_Click(object sender, RoutedEventArgs e)
        {
            string server = ViewModel.Server;
            string text = $"Every game StreamTweak listed in {server} leaves it — synced, added by hand and excluded — and "
                        + $"{server} restarts to show it. Its own entries, such as Desktop, stay.";
            if (ViewModel.SyncEnabled)
                text += " Sync when StreamTweak starts is on: the games come back at its next start.";

            var dmSans = new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
            var dialog = new ContentDialog
            {
                Title             = "Clear the library?",
                Content           = new TextBlock
                {
                    Text         = text,
                    FontFamily   = dmSans,
                    FontSize     = 13,
                    Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Clear and restart",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.XamlRoot,
            };
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"] = dmSans;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.StartClearAndRestart();
        }

        private void SyncRestart_Click(object sender, RoutedEventArgs e) => ViewModel.StartSyncAndRestart();

        private async void HostTiles_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleHostAssetsAsync();

        private void ClearLegacy_Click(object sender, RoutedEventArgs e) => ViewModel.ClearLegacyExclusions();

        private void Status_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.HasStatus = false;
    }
}
