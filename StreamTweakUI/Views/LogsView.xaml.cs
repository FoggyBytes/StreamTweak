using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Controls;
using StreamTweak.Services;
using StreamTweak.ViewModels;
using Windows.System;
using Windows.UI;

namespace StreamTweak.Views
{
    public sealed partial class LogsView : Page
    {
        public LogsViewModel ViewModel { get; } = new LogsViewModel();

        /// <summary>Page width from which the list and the detail sit side by side.</summary>
        private const double MasterDetailWidth = 1400;

        private bool _wide;

        public LogsView()
        {
            this.InitializeComponent();
            this.KeyDown += OnPageKeyDown;
            Timeline.ZoomChanged += (_, _) =>
                ResetZoomButton.Visibility = Timeline.IsZoomed ? Visibility.Visible : Visibility.Collapsed;

            ViewModel.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(LogsViewModel.IsDetailVisible):
                        ApplyLayout();
                        break;
                    case nameof(LogsViewModel.SelectedSession):
                        DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
                        break;
                    case nameof(LogsViewModel.GradeFilter):
                        // The chips are checked by hand (GradeChip_Click); a deep link that
                        // clears the filter must move the check back to "All" as well.
                        string tag = ViewModel.GradeFilter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        foreach (var child in GradeChips.Children)
                            if (child is ToggleButton tb) tb.IsChecked = (tb.Tag as string) == tag;
                        break;
                }
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();

            // Deep link from the Dashboard ("Open session ›", a recent-session row).
            var pending = AppStateService.Instance.PendingSessionId;
            AppStateService.Instance.PendingSessionId = null;
            if (!string.IsNullOrEmpty(pending))
                ViewModel.OpenDetailById(pending);

            ApplyLayout();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width, h = e.NewSize.Height;
            _wide = w >= MasterDetailWidth;

            // Gutters grow with the window, capped so 4K gives the room to content, not margins.
            double side = Math.Clamp(Math.Round(w * 0.026), 16, 40);
            double listSide = _wide ? Math.Min(side, 28) : side;
            SessionList.Padding = new Thickness(listSide, 24, listSide, 96);
            CompareBar.Margin = new Thickness(listSide, 0, listSide, 16);
            DetailRoot.Padding = new Thickness(side, 24, side, 40);
            CompareRoot.Padding = new Thickness(side, 24, side, 40);

            // Lanes get taller with the window: readable on a 7" handheld, not sparse at 4K.
            Timeline.LaneHeight = h < 900 ? 64 : h < 1300 ? 78 : 94;

            ApplyLayout();
        }

        private void ApplyLayout()
        {
            double w = Root.ActualWidth;
            if (w <= 0) return;

            if (_wide)
            {
                // Master–detail: the list keeps ~40 % and never less than 560 px.
                ListCol.Width = new GridLength(Math.Max(560, w * 0.4));
                DetailCol.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(DetailLayer, 1);
                Grid.SetColumnSpan(DetailLayer, 1);
                DetailLayer.BorderThickness = new Thickness(1, 0, 0, 0);
                BackButton.Visibility = Visibility.Collapsed;
                ViewModel.EnsureSelection();
                DetailLayer.Visibility = ViewModel.HasSessions ? Visibility.Visible : Visibility.Collapsed;
                ListLayer.Visibility = Visibility.Visible;

                double listW = Math.Max(560, w * 0.4);
                ViewModel.RowLayout.MetricsVisibility = listW >= 760 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                ListCol.Width = new GridLength(1, GridUnitType.Star);
                DetailCol.Width = new GridLength(0);
                Grid.SetColumn(DetailLayer, 0);
                Grid.SetColumnSpan(DetailLayer, 2);
                DetailLayer.BorderThickness = new Thickness(0);
                BackButton.Visibility = Visibility.Visible;
                bool open = ViewModel.IsDetailVisible && ViewModel.HasSelection;
                DetailLayer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                // Hidden rather than covered, so keyboard focus and Narrator stay in the detail.
                ListLayer.Visibility = open ? Visibility.Collapsed : Visibility.Visible;

                ViewModel.RowLayout.MetricsVisibility = w >= 760 ? Visibility.Visible : Visibility.Collapsed;
            }
            ViewModel.RowLayout.GamesVisibility = Visibility.Visible;
        }

        private void VerdictGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Narrow card: the four checks go under the grade instead of beside it.
            bool stack = e.NewSize.Width < 520;
            Grid.SetRow(VerdictCriteria, stack ? 1 : 0);
            Grid.SetColumn(VerdictCriteria, stack ? 0 : 1);
            Grid.SetColumnSpan(VerdictCriteria, stack ? 2 : 1);
        }

        private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Escape) return;
            if (ViewModel.IsCompareVisible)
            {
                ViewModel.CloseCompare();
                e.Handled = true;
            }
            else if (!_wide && ViewModel.IsDetailVisible)
            {
                ViewModel.CloseDetail();
                e.Handled = true;
            }
        }

        // ── List ──────────────────────────────────────────────────────────────

        private void SessionList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not SessionRow row) return;
            if (row.IsLive)
            {
                // The live session has no detail yet: its numbers are on the Dashboard.
                App.MainWindow?.NavigateTo("Home");
                return;
            }
            ViewModel.OpenDetail(row.Entry);
        }

        private void GradeChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked || clicked.Tag is not string tag) return;
            int value = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var child in GradeChips.Children)
                if (child is ToggleButton tb) tb.IsChecked = ReferenceEquals(tb, clicked);
            ViewModel.GradeFilter = value;
        }

        private void ClearPicks_Click(object sender, RoutedEventArgs e) => ViewModel.ClearPicks();

        private void ComparePicked_Click(object sender, RoutedEventArgs e) => ViewModel.ComparePicked();

        private void RowCompare_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string id } &&
                ViewModel.Rows.FirstOrDefault(r => r.Id == id) is { } row)
                ViewModel.OpenCompare(row.Entry);
        }

        private async void RowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string id } &&
                ViewModel.Rows.FirstOrDefault(r => r.Id == id) is { } row)
                await ConfirmDeleteAsync(row.Entry);
        }

        // ── Detail ────────────────────────────────────────────────────────────

        private void CloseDetail_Click(object sender, RoutedEventArgs e) => ViewModel.CloseDetail();

        private void CompareWith_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedSession is { } s) ViewModel.OpenCompare(s);
        }

        private async void DeleteDetail_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedSession is { } s) await ConfirmDeleteAsync(s);
        }

        private async Task ConfirmDeleteAsync(SessionEntry entry)
        {
            var dialog = new ContentDialog
            {
                Title             = "Delete this session?",
                Content           = new TextBlock
                {
                    Text = $"{entry.StartTimeDisplay.Replace("  ", " ")} · {entry.DurationDisplay}. The session and its charts are removed from the history. This cannot be undone.",
                    TextWrapping = TextWrapping.Wrap, FontFamily = DmSans, Foreground = new SolidColorBrush(DialogBodyText),
                },
                PrimaryButtonText = "Delete",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.XamlRoot,
            };
            ApplyDialogChrome(dialog);
            ApplyDangerAccentPalette(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            ViewModel.DeleteSession(entry);
            if (_wide) ViewModel.EnsureSelection();
            else ViewModel.CloseDetail();
            ApplyLayout();
        }

        private void TimelineMode_Click(object sender, RoutedEventArgs e)
        {
            bool focus = ReferenceEquals(sender, FocusChip);
            TracksChip.IsChecked = !focus;
            FocusChip.IsChecked = focus;
            Timeline.Mode = focus ? TimelineMode.Focus : TimelineMode.Tracks;
        }

        private void ResetZoom_Click(object sender, RoutedEventArgs e) => Timeline.ResetZoom();

        // ── Compare ───────────────────────────────────────────────────────────

        private void CloseCompare_Click(object sender, RoutedEventArgs e) => ViewModel.CloseCompare();

        private void CompareSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!ViewModel.IsCompareVisible) return; // ignore the initial population
            ViewModel.OnCompareSelectionChanged();
        }

        // ── Clear history ─────────────────────────────────────────────────────

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            // Browser-style "clear cache" chooser: pick a time window, then confirm.
            var options = new[] { "Last hour", "Last 24 hours", "Last 7 days", "Last 4 weeks", "All time" };
            var radios = new RadioButtons
            {
                ItemsSource   = options,
                SelectedIndex = 1,                       // default: Last 24 hours
                Margin        = new Thickness(0, 8, 0, 0),
            };

            // Selected dot in the app's green instead of the system accent color.
            var green     = new SolidColorBrush(Color.FromArgb(0xFF, 0x4a, 0xde, 0x80));
            var greenDark = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
            radios.Resources["RadioButtonOuterEllipseCheckedFill"]              = green;
            radios.Resources["RadioButtonOuterEllipseCheckedFillPointerOver"]   = green;
            radios.Resources["RadioButtonOuterEllipseCheckedFillPressed"]       = greenDark;
            radios.Resources["RadioButtonOuterEllipseCheckedStroke"]            = green;
            radios.Resources["RadioButtonOuterEllipseCheckedStrokePointerOver"] = green;
            radios.Resources["RadioButtonOuterEllipseCheckedStrokePressed"]     = greenDark;

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text         = "Delete sessions recorded within:",
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = DmSans,
                Foreground   = new SolidColorBrush(DialogBodyText),
            });
            panel.Children.Add(radios);

            var dialog = new ContentDialog
            {
                Title             = "Clear history",
                Content           = panel,
                PrimaryButtonText = "Delete",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = this.XamlRoot,
            };
            ApplyDialogChrome(dialog);
            ApplyDangerAccentPalette(dialog);   // red Delete (Primary), grey Cancel

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            TimeSpan? window = radios.SelectedIndex switch
            {
                0 => TimeSpan.FromHours(1),
                1 => TimeSpan.FromHours(24),
                2 => TimeSpan.FromDays(7),
                3 => TimeSpan.FromDays(28),
                _ => null,                              // All time
            };
            ViewModel.ClearHistory(window);
            if (_wide) ViewModel.EnsureSelection();
            ApplyLayout();
        }

        // ── Dialog chrome (mirrors MainWindow's helpers) ──────────────────────
        private static readonly FontFamily DmSans =
            new("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
        private static readonly Color DialogBg       = Color.FromArgb(0xF2, 0x1d, 0x1b, 0x1a);
        private static readonly Color DialogBorder   = Color.FromArgb(0xFF, 0x2A, 0x27, 0x24);
        private static readonly Color DialogBodyText = Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8);
        private static readonly Color DangerColor    = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);

        private static void ApplyDialogChrome(ContentDialog dialog)
        {
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(DialogBg);
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(DialogBorder);
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
        }

        private static void ApplyDangerAccentPalette(ContentDialog dialog)
        {
            dialog.Resources["AccentButtonBackground"]             = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForeground"]             = new SolidColorBrush(DangerColor);
            dialog.Resources["AccentButtonBorderBrush"]            = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonBackgroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0x2D, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForegroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0xFF, 0xFC, 0xA5, 0xA5));
            dialog.Resources["AccentButtonBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonBackgroundPressed"]      = new SolidColorBrush(Color.FromArgb(0x12, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForegroundPressed"]      = new SolidColorBrush(DangerColor);
            dialog.Resources["AccentButtonBorderBrushPressed"]     = new SolidColorBrush(Color.FromArgb(0x44, 0xEF, 0x44, 0x44));
        }
    }
}
