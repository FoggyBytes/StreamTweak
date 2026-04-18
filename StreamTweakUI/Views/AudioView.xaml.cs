using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;

namespace StreamTweak.Views
{
    public sealed partial class AudioView : Page
    {
        public AudioViewModel ViewModel { get; } = new AudioViewModel();

        // Guard flag: true while we are programmatically setting IsOn on the toggles.
        // WinUI3 fires Toggled synchronously when IsOn is set in code — this prevents
        // those programmatic changes from being treated as user gestures.
        private bool _updatingToggles;

        public AudioView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _ = ViewModel.InitializeAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.Unsubscribe();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModel.IsDolbyActive)
                               or nameof(ViewModel.IsSonicActive))
            {
                _updatingToggles = true;
                DolbyToggle.IsOn = ViewModel.IsDolbyActive;
                SonicToggle.IsOn = ViewModel.IsSonicActive;
                _updatingToggles = false;
            }
        }

        private async void DolbyToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_updatingToggles) return;
            if (sender is ToggleSwitch ts)
                await ViewModel.ToggleDolbyAsync(ts.IsOn);
        }

        private async void SonicToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_updatingToggles) return;
            if (sender is ToggleSwitch ts)
                await ViewModel.ToggleSonicAsync(ts.IsOn);
        }

    }
}
