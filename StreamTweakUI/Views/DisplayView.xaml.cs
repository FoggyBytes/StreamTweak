using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    public sealed partial class DisplayView : Page
    {
        public DisplayViewModel ViewModel { get; } = new DisplayViewModel();

        public DisplayView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = ViewModel.InitializeAsync();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
            => _ = ViewModel.InitializeAsync();

        private void HdrToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch ts) return;
            if (ts.DataContext is not MonitorEntry entry) return;
            _ = ViewModel.ToggleMonitorHdrAsync(entry, ts.IsOn);
        }
    }
}
