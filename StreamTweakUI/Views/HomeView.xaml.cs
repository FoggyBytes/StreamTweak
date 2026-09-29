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
            new(Color.FromArgb(0xFF, 0x4a, 0xde, 0x80));  // #4ade80 — ST9Mint
        private static readonly SolidColorBrush InactiveBrush =
            new(Color.FromArgb(0xFF, 0x64, 0x6B, 0x67));  // #646B67 — ST9Text4

        public HomeView()
        {
            this.InitializeComponent();

            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.IsSessionActive))
                    UpdateSessionDot(ViewModel.IsSessionActive);
            };

            SizeChanged += (_, e) => ApplyPagePadding(e.NewSize.Width);
            HeroCard.SizeChanged += (_, e) => ApplyHeroSize(e.NewSize.Width);
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
            PulseStoryboard.Stop();
            ViewModel.Unsubscribe();
        }

        private void OnSettingsChanged(object? sender, EventArgs e)
            => _ = ViewModel.LoadStatusAsync();

        // ── Layout ────────────────────────────────────────────────────────────

        /// <summary>
        /// Page gutters grow with the window: 16 px on a 7" handheld, ~32 at 1080p,
        /// capped at 48 on 4K so the cards — not the margins — take the extra room.
        /// </summary>
        private void ApplyPagePadding(double width)
        {
            double side = Math.Clamp(Math.Round(width * 0.032), 16, 48);
            double top = Math.Clamp(Math.Round(width * 0.02), 14, 28);
            PageRoot.Padding = new Thickness(side, top, side, 40);
        }

        /// <summary>A narrower hero keeps the cover but shrinks it so the title keeps its room.</summary>
        private void ApplyHeroSize(double width)
        {
            bool compact = width < 560;
            HeroArt.Width = compact ? 96 : 150;
            if (HeroArt.Child is Image img) img.Height = compact ? 144 : 225;
            HeroContent.Padding = compact ? new Thickness(18, 18, 18, 16) : new Thickness(24, 22, 24, 20);
            HeroContent.ColumnSpacing = compact ? 16 : 22;
        }

        private void UpdateSessionDot(bool active)
        {
            if (active)
            {
                SessionDot.Fill = ActiveBrush;
                PulseStoryboard.Begin();
            }
            else
            {
                PulseStoryboard.Stop();
                SessionDot.Fill = InactiveBrush;
                PulseRing.Opacity = 0;
            }
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void StopStreamButton_Click(object sender, RoutedEventArgs e)
            => ViewModel.RequestStopStream();

        /// <summary>Tiles and "›" links carry their destination page tag in Tag.</summary>
        private void NavTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } && !string.IsNullOrEmpty(tag))
                App.MainWindow?.NavigateTo(tag);
        }

        private void OpenLastSession_Click(object sender, RoutedEventArgs e)
            => OpenSession(ViewModel.LastSessionId);

        private void OpenSessionRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string id })
                OpenSession(id);
        }

        private static void OpenSession(string? id)
        {
            AppStateService.Instance.PendingSessionId = string.IsNullOrEmpty(id) ? null : id;
            App.MainWindow?.NavigateTo("Logs");
        }

        private void ShelfGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name } && !string.IsNullOrEmpty(name))
                AppStateService.Instance.PendingGameName = name;
            App.MainWindow?.NavigateTo("GameLibrary");
        }
    }
}
