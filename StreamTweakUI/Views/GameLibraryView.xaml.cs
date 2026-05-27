using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    public sealed partial class GameLibraryView : Page
    {
        public GameLibraryViewModel ViewModel { get; } = new GameLibraryViewModel();

        public GameLibraryView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Unsubscribe();
        }

        private async void SyncNow_Click(object sender, RoutedEventArgs e)
            => await ViewModel.SyncNowAsync();

        private async void ClearSync_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ClearSyncAsync();

        private async void AddGame_Click(object sender, RoutedEventArgs e)
            => await ViewModel.AddGameAsync();

        private async void ToggleHostAssets_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ToggleHostAssetsAsync();

        private void PlayGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ObservableGameEntry entry)
                ViewModel.LaunchGame(entry);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ObservableGameEntry entry)
                ViewModel.OpenGameFolder(entry);
        }

        private async void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ObservableGameEntry entry)
                await ViewModel.RemoveGameAsync(entry);
        }

        private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
            => ViewModel.HasStatus = false;
    }
}
