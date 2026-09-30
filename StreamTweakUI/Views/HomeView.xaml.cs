using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Controls;
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
                else if (e.PropertyName == nameof(ViewModel.HeroTitle))
                    DispatcherQueue.TryEnqueue(FitHeroTitle);   // after x:Bind has set the text
            };

            SizeChanged += (_, e) => ApplyPagePadding(e.NewSize.Width);
            Loaded += (_, _) => FitHeroTitle();
            PerfChart.SessionClicked += OpenSession;   // a point on the trend is a session
            HeroCard.SizeChanged += (_, e) => ApplyHeroSize(e.NewSize.Width);
            // Containers exist only after the ItemsControl has generated them: lay out then.
            ViewModel.RecentGames.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(LayoutShelf);
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

        /// <summary>The same content column as every other page (Controls/PageLayout).</summary>
        private void ApplyPagePadding(double width) => PageRoot.Padding = PageLayout.Padding(width);

        /// <summary>
        /// The hero is one band — state and five tiles side by side. Below 900 the tiles would
        /// be squeezed beside the state, so they drop to a row of their own. The row gap is
        /// only there when stacked: on one row an empty second row still took the gap, and
        /// the tiles sat above the band's middle.
        /// </summary>
        private void ApplyHeroSize(double width)
        {
            bool stacked = width < 900;
            foreach (var vitals in new FrameworkElement[] { LiveVitals, IdleVitals })
            {
                Grid.SetRow(vitals, stacked ? 1 : 0);
                Grid.SetColumn(vitals, stacked ? 0 : 1);
                Grid.SetColumnSpan(vitals, stacked ? 2 : 1);
            }
            HeroContent.RowSpacing = stacked ? 16 : 0;
        }

        private const double HeroTitleSize = 24, HeroTitleMinSize = 18;

        /// <summary>
        /// A game's name that does not fit the hero's text column at 24 px is set smaller, down to
        /// 18 px; only a name too long even at 18 is cut with an ellipsis (TextTrimming).
        /// </summary>
        private void FitHeroTitle()
        {
            double avail = HeroTextColumn.MaxWidth;
            if (string.IsNullOrEmpty(HeroTitleText.Text) || double.IsInfinity(avail)) return;

            HeroTitleText.FontSize = HeroTitleSize;
            HeroTitleText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double natural = HeroTitleText.DesiredSize.Width;
            if (natural <= avail) return;

            // Width grows in step with the size; the loop mops up what rounding leaves over.
            double size = Math.Max(HeroTitleMinSize, Math.Floor(HeroTitleSize * avail / natural * 2) / 2);
            HeroTitleText.FontSize = size;
            HeroTitleText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            while (HeroTitleText.DesiredSize.Width > avail && size > HeroTitleMinSize)
            {
                size = Math.Max(HeroTitleMinSize, size - 0.5);
                HeroTitleText.FontSize = size;
                HeroTitleText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            HeroTitleText.InvalidateMeasure();
        }

        private void ShelfItems_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutShelf();

        private void SetupInner_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutShelf();

        /// <summary>
        /// "Recently streamed": one row of whole covers; the rest are collapsed rather than cut
        /// off at the card's edge.
        ///
        /// On three columns the card shares its row with Host setup, whose height does not
        /// depend on anything here, and a row is as tall as its tallest card. So the covers take
        /// their height from Host setup: as tall as fills the card to the same height, as many
        /// as fit at that size, the spare width shared out between them. Otherwise (one or two
        /// columns) as many as fit at 124 wide, widened to fill the row.
        /// </summary>
        private void LayoutShelf()
        {
            double w = ShelfItems.ActualWidth;
            if (w <= 0) return;
            const double gap = 12, minW = 124;

            var shown = new List<Button>();
            for (int i = 0; i < ViewModel.RecentGames.Count; i++)
                if (ShelfItems.ContainerFromIndex(i) is FrameworkElement c &&
                    VisualTreeHelper.GetChildrenCount(c) > 0 && VisualTreeHelper.GetChild(c, 0) is Button b)
                    shown.Add(b);
            if (shown.Count == 0) return;

            int cols;
            double itemW, coverH, extra = 0;
            double labels = LabelsHeight(shown[0]);
            bool sideBySide = DashGrid.ActualWidth >= 1180 && labels > 0;
            // Everything in the card but the covers, then what Host setup leaves for them.
            double budget = SetupInner.ActualHeight - (RecentInner.ActualHeight - ShelfItems.ActualHeight) - labels;
            if (sideBySide && budget >= 96 * 1.5)
            {
                coverH = Math.Floor(Math.Min(budget, 170 * 1.5));
                itemW = Math.Floor(coverH / 1.5);
                cols = Math.Max(1, (int)((w + gap) / (itemW + gap)));
                extra = cols > 1 ? Math.Floor((w - cols * itemW - (cols - 1) * gap) / (cols - 1)) : 0;
            }
            else
            {
                cols = Math.Max(1, (int)((w + gap) / (minW + gap)));
                itemW = Math.Floor((w - (cols - 1) * gap) / cols);
                coverH = Math.Round(itemW * 1.5);
            }

            for (int i = 0; i < shown.Count; i++)
            {
                var b = shown[i];
                var container = VisualTreeHelper.GetParent(b) as FrameworkElement ?? b;
                container.Visibility = i < cols ? Visibility.Visible : Visibility.Collapsed;
                container.Margin = new Thickness(0, 0, i < cols - 1 ? extra : 0, 0);
                if (Math.Abs(b.Width - itemW) >= 1) b.Width = itemW;
                if (b.FindName("ShelfCover") is FrameworkElement cover && Math.Abs(cover.Height - coverH) >= 1)
                    cover.Height = coverH;
            }
        }

        /// <summary>Height of a shelf item below its cover (title and caption), once laid out.</summary>
        private static double LabelsHeight(Button b)
            => b.FindName("ShelfCover") is FrameworkElement cover && b.ActualHeight > 0
                ? b.ActualHeight - cover.ActualHeight
                : 0;

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
