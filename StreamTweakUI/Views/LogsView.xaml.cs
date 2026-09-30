using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

        /// <summary>The page's side padding (Controls/PageLayout) and its content width.</summary>
        private double _side, _contentWidth;

        /// <summary>Lane height in the page (follows the window height); full screen sets its own.</summary>
        private double _pageLaneHeight = 78;

        private bool _timelineFull;
        private int _fitPasses;

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
                    case nameof(LogsViewModel.IsCompareVisible):
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

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SetTimelineFull(false);   // never leave the window full screen behind another page
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width, h = e.NewSize.Height;

            // The same content column as every other page (Controls/PageLayout). Padding and not
            // MaxWidth: the list and the detail are the page's scrollers, and the wheel must work
            // over the side margins too.
            _side = PageLayout.Side(w);
            _contentWidth = w - 2 * _side;

            SessionList.Padding = new Thickness(_side, 24, _side, 96);
            CompareBar.Margin = new Thickness(_side, 0, _side, 16);
            DetailRoot.Padding = new Thickness(_side, 24, _side, 40);
            CompareRoot.Padding = new Thickness(_side, 24, _side, 40);

            // Lanes get taller with the window: readable on a 7" handheld, not sparse at 4K.
            _pageLaneHeight = h < 900 ? 64 : h < 1300 ? 78 : 94;
            if (!_timelineFull) Timeline.LaneHeight = _pageLaneHeight;

            ApplyLayout();
        }

        /// <summary>
        /// The list alone, or the chosen session over it — never both side by side: the detail's
        /// charts need the whole content column. Compare and the full-screen Timeline cover both.
        /// </summary>
        private void ApplyLayout()
        {
            if (Root.ActualWidth <= 0) return;
            double cw = _contentWidth;

            bool open = ViewModel.IsDetailVisible && ViewModel.HasSelection;
            DetailLayer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            // Hidden rather than covered, so keyboard focus and Narrator stay in the detail.
            ListLayer.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            if (!open && _timelineFull) SetTimelineFull(false);

            ViewModel.RowLayout.MetricsVisibility = cw >= 760 ? Visibility.Visible : Visibility.Collapsed;
            ViewModel.RowLayout.GamesVisibility = Visibility.Visible;

            // Search box (240) and the four grade chips need ~650 side by side.
            bool stackChips = cw < 660;
            Grid.SetRow(GradeChips, stackChips ? 1 : 0);
            Grid.SetColumn(GradeChips, stackChips ? 0 : 1);
            Grid.SetColumnSpan(GradeChips, stackChips ? 2 : 1);

            // Compare takes the whole page. Its layer is transparent (Mica shows through), so
            // the list and the detail must be hidden, not just covered: they drew through it.
            if (ViewModel.IsCompareVisible)
            {
                if (_timelineFull) SetTimelineFull(false);
                ListLayer.Visibility = Visibility.Collapsed;
                DetailLayer.Visibility = Visibility.Collapsed;
            }
            // The full-screen Timeline covers both for the same reason. Going full screen resizes
            // the page, which lands here: without this the detail came back behind the chart.
            else if (_timelineFull)
            {
                ListLayer.Visibility = Visibility.Collapsed;
                DetailLayer.Visibility = Visibility.Collapsed;
            }
        }

        // ── Timeline, full screen ─────────────────────────────────────────────

        private void TimelineFullScreen_Click(object sender, RoutedEventArgs e) => SetTimelineFull(!_timelineFull);

        /// <summary>
        /// Moves the Timeline card out of the detail into a layer over the whole page, and puts
        /// the window in full screen (MainWindow.SetImmersive); the lanes then grow to fill
        /// the height. Undone by the same button, Esc, the back button or leaving the page.
        /// </summary>
        private void SetTimelineFull(bool on)
        {
            if (on == _timelineFull) return;
            _timelineFull = on;
            if (on)
            {
                DetailBody.Children.Remove(TimelineCard);
                TimelineFullHost.Child = TimelineCard;
                TimelineFullLayer.Visibility = Visibility.Visible;
                DetailLayer.Visibility = Visibility.Collapsed;
                FullScreenButton.Content = "";
                ToolTipService.SetToolTip(FullScreenButton, "Leave full screen (Esc)");
                AutomationProperties.SetName(FullScreenButton, "Leave full screen");
                App.MainWindow?.SetImmersive(true);
                _fitPasses = 0;
                FitTimelineToScreen();
            }
            else
            {
                TimelineFullHost.Child = null;
                DetailBody.Children.Add(TimelineCard);
                TimelineFullLayer.Visibility = Visibility.Collapsed;
                FullScreenButton.Content = "";
                ToolTipService.SetToolTip(FullScreenButton, "Full screen (Esc to leave)");
                AutomationProperties.SetName(FullScreenButton, "Full screen");
                App.MainWindow?.SetImmersive(false);
                Timeline.LaneHeight = _pageLaneHeight;
                ApplyLayout();
            }
            FullScreenButton.Focus(FocusState.Programmatic);
        }

        private void TimelineFullLayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _fitPasses = 0;
            FitTimelineToScreen();
        }

        /// <summary>
        /// Grows or shrinks the lanes so the card fills the layer's height: measure what the card
        /// takes now, share the difference among the lane units. A couple of passes settle it
        /// (lanes with two series have a floor of their own).
        /// </summary>
        private void FitTimelineToScreen()
        {
            if (!_timelineFull) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_timelineFull || _fitPasses++ >= 8) return;
                // A new lane height rebuilds the chart; its size is only known after a layout
                // pass, which a queued callback can run ahead of. Settle it first.
                TimelineFullLayer.UpdateLayout();
                double avail = TimelineFullLayer.ActualHeight - TimelineFullHost.Padding.Top - TimelineFullHost.Padding.Bottom;
                // What the card needs, not what it was given: in the layer it is stretched to
                // the viewport, so its ActualHeight always equals what is available.
                double used = TimelineCard.DesiredSize.Height;
                // Not laid out in its new place yet: look again after the next pass.
                if (avail <= 0 || used <= 0) { FitTimelineToScreen(); return; }
                double lane = Math.Clamp(Timeline.LaneHeight + Math.Floor((avail - used) / Timeline.LaneHeightUnits), 56, 600);
                if (Math.Abs(lane - Timeline.LaneHeight) >= 1)
                {
                    Timeline.LaneHeight = lane;
                    FitTimelineToScreen();
                }
            });
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
            if (_timelineFull)
            {
                SetTimelineFull(false);
                e.Handled = true;
            }
            else if (ViewModel.IsCompareVisible)
            {
                ViewModel.CloseCompare();
                e.Handled = true;
            }
            else if (ViewModel.IsDetailVisible)
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

        private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) => SetRowHover(sender, true);

        private void Row_PointerExited(object sender, PointerRoutedEventArgs e) => SetRowHover(sender, false);

        private static void SetRowHover(object sender, bool on)
        {
            if (sender is FrameworkElement fe && fe.FindName("RowHover") is UIElement hover)
                hover.Opacity = on ? 1 : 0;
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
            ViewModel.CloseDetail();
            ApplyLayout();
        }

        private void TimelineMode_Click(object sender, RoutedEventArgs e)
        {
            bool focus = ReferenceEquals(sender, FocusChip);
            TracksChip.IsChecked = !focus;
            FocusChip.IsChecked = focus;
            Timeline.Mode = focus ? TimelineMode.Focus : TimelineMode.Tracks;
            _fitPasses = 0;
            FitTimelineToScreen();
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
            ViewModel.CloseDetail();
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
