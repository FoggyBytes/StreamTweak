using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    public sealed partial class HomeView : Page
    {
        public HomeViewModel ViewModel { get; } = new HomeViewModel();

        private static readonly SolidColorBrush ActiveBrush =
            new(Color.FromArgb(0xFF, 0x4a, 0xde, 0x80));  // #4ade80 — design token STSuccess
        private static readonly SolidColorBrush InactiveBrush =
            new(Color.FromArgb(0xFF, 0x44, 0x44, 0x44));  // #444 — design token dim

        public HomeView()
        {
            this.InitializeComponent();

            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.IsSessionActive))
                    UpdateSessionDot(ViewModel.IsSessionActive);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Reload status tiles whenever a tray toggle changes while this tab is active.
            AppStateService.Instance.SettingsChanged += OnSettingsChanged;
            UpdateSessionDot(ViewModel.IsSessionActive);
            _ = ViewModel.LoadStatusAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            AppStateService.Instance.SettingsChanged -= OnSettingsChanged;
            ViewModel.Unsubscribe();
        }

        private void OnSettingsChanged(object? sender, EventArgs e)
            => _ = ViewModel.LoadStatusAsync();

        private void UpdateSessionDot(bool active)
        {
            // The live cockpit (and its pulsing "Live" dot) only renders while active.
            if (active)
            {
                SessionDot.Fill = ActiveBrush;
                PulseStoryboard.Begin();
            }
            else
            {
                PulseStoryboard.Stop();
                SessionDot.Fill = InactiveBrush;
                SessionDot.Opacity = 1.0;
            }
        }

        private void StopStreamButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.RequestStopStream();

    }
}
