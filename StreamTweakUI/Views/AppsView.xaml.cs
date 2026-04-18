using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    public sealed partial class AppsView : Page
    {
        public AppsViewModel ViewModel { get; } = new AppsViewModel();

        public AppsView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
            => await ViewModel.AddAsync();

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.RemoveSelected();

        private void KillButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.KillSelected();

        private async void RestartButton_Click(object sender, RoutedEventArgs e)
            => await ViewModel.RestartSelectedAsync();

        private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
            => ViewModel.HasStatus = false;
    }
}
