using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Controls;
using StreamTweak.Services;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    public sealed partial class GameLibraryView : Page
    {
        public GameLibraryViewModel ViewModel { get; } = new GameLibraryViewModel();

        private bool _syncingSheetToggle;

        // The hero follows the cover under the pointer or the focus, after a short pause so
        // it does not flicker through every cover the pointer crosses on its way.
        private DispatcherQueueTimer? _spotlightTimer;
        private ObservableGameEntry? _spotlightPending;

        public GameLibraryView()
        {
            this.InitializeComponent();
            ViewModel.StoreChips.CollectionChanged += StoreChips_CollectionChanged;
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GameLibraryViewModel.SelectedGame))
                    SyncSheetToggle();
            };
            SizeChanged += (_, e) => Sheet.OpenPaneLength = Math.Min(460, e.NewSize.Width);
            // The wrap panel only exists once the GridView has items; size it then too.
            LibraryGrid.Loaded += (_, _) => ApplyTileSize();
            ViewModel.FilteredGames.CollectionChanged += (_, _) =>
            {
                if (LibraryGrid.ItemsPanelRoot is ItemsWrapGrid { ItemWidth: 168 }) ApplyTileSize();
            };
            KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape && Sheet.IsPaneOpen)
                {
                    Sheet.IsPaneOpen = false;
                    e.Handled = true;
                }
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
            AppStateService.Instance.SessionStateChanged += OnSessionStateChanged;

            // Deep link from the Dashboard shelf: open that game's sheet.
            var pending = AppStateService.Instance.PendingGameName;
            AppStateService.Instance.PendingGameName = null;
            if (!string.IsNullOrEmpty(pending) && ViewModel.FindGame(pending) is { } g)
                OpenSheet(g);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            AppStateService.Instance.SessionStateChanged -= OnSessionStateChanged;
        }

        private void OnSessionStateChanged(object? sender, bool active)
            => DispatcherQueue.TryEnqueue(ViewModel.RefreshHero);

        // ── Layout ────────────────────────────────────────────────────────────

        private void LibraryGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSize();

        /// <summary>
        /// Cover size from the width: the tiles fill the row exactly (no ragged right edge),
        /// never narrower than a readable cover, in the shared content column (Controls/PageLayout).
        /// </summary>
        private void ApplyTileSize()
        {
            double w = LibraryGrid.ActualWidth;
            if (w <= 0) return;

            // The same content column as every other page (Controls/PageLayout). The header and
            // every tile sit 6 px inside the GridView's padding, so the padding is 6 less and the
            // visible edges line up with the other pages.
            double side = PageLayout.Side(w) - 6;
            LibraryGrid.Padding = new Thickness(side, 24, side, 40);
            double inner = w - side * 2 - 2;

            if (LibraryGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
            double min = inner < 520 ? 118 : 158;
            int cols = Math.Max(2, (int)(inner / min));
            double itemW = Math.Floor(inner / cols);
            panel.ItemWidth = itemW;
            // 2:3 cover + name + meta lines + paddings.
            panel.ItemHeight = Math.Round((itemW - 12) * 1.5) + 8 + 50;
        }

        private void HeroCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width;
            double coverW = Math.Clamp(Math.Round(w * 0.2), 96, 170);
            HeroCover.Width = coverW;
            HeroCover.Height = Math.Round(coverW * 1.5);
            HeroContent.Padding = w < 560 ? new Thickness(18) : new Thickness(24, 22, 24, 22);
            HeroContent.ColumnSpacing = w < 560 ? 16 : 24;
            HeroTitleText.FontSize = w < 560 ? 24 : w < 900 ? 28 : 34;
            HeroTitleText.LineHeight = HeroTitleText.FontSize + 4;
        }

        // ── Store chips (built here: their count and names depend on the library) ──

        private void StoreChips_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            StoreChipsPanel.Children.Clear();
            foreach (var chip in ViewModel.StoreChips)
            {
                // The store's badge in front of its name, as on the covers.
                object content = chip.Label;
                if (chip.IconUri != null)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                    row.Children.Add(new Image
                    {
                        Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
                        Source = new SvgImageSource(chip.IconUri),
                    });
                    row.Children.Add(new TextBlock { Text = chip.Label, VerticalAlignment = VerticalAlignment.Center });
                    content = row;
                }
                var b = new ToggleButton
                {
                    Content = content,
                    Tag = chip.Key,
                    IsChecked = chip.Key == ViewModel.StoreFilter,
                    Style = (Style)Application.Current.Resources["ST9_Chip"],
                };
                b.Click += StoreChip_Click;
                StoreChipsPanel.Children.Add(b);
            }
        }

        private void StoreChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked || clicked.Tag is not string key) return;
            foreach (var child in StoreChipsPanel.Children)
                if (child is ToggleButton tb) tb.IsChecked = ReferenceEquals(tb, clicked);
            ViewModel.StoreFilter = key;
        }

        // ── Tiles ─────────────────────────────────────────────────────────────

        private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ObservableGameEntry g }) QueueSpotlight(g);
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
            SetOverlay(sender, true);
        }

        /// <summary>Keyboard and controller: the hero follows the focused cover too.</summary>
        private void LibraryGrid_GotFocus(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewItem { Content: ObservableGameEntry g }) QueueSpotlight(g);
        }

        private void QueueSpotlight(ObservableGameEntry g)
        {
            _spotlightPending = g;
            if (_spotlightTimer == null)
            {
                _spotlightTimer = DispatcherQueue.CreateTimer();
                _spotlightTimer.Interval = TimeSpan.FromMilliseconds(140);
                _spotlightTimer.IsRepeating = false;
                _spotlightTimer.Tick += (_, _) => ViewModel.Spotlight(_spotlightPending);
            }
            _spotlightTimer.Stop();
            _spotlightTimer.Start();
        }

        private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => SetOverlay(sender, false);

        private static void SetOverlay(object sender, bool on)
        {
            if (sender is FrameworkElement fe && fe.FindName("Overlay") is UIElement overlay)
            {
                overlay.Opacity = on ? 1 : 0;
                overlay.IsHitTestVisible = on;
                // The badge sits where the action buttons appear.
                if (fe.FindName("StoreBadge") is UIElement badge) badge.Opacity = on ? 0 : 1;
            }
        }

        private void Game_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ObservableGameEntry g) OpenSheet(g);
        }

        private void TilePlay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ObservableGameEntry g }) ViewModel.LaunchGame(g);
        }

        private void TileFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ObservableGameEntry g }) ViewModel.OpenGameFolder(g);
        }

        private void TileDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ObservableGameEntry g }) OpenSheet(g);
        }

        // ── Hero ──────────────────────────────────────────────────────────────

        private void HeroPlay_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.HeroGame is { } g) ViewModel.LaunchGame(g);
        }

        private void HeroDetails_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.HeroGame is { } g) OpenSheet(g);
        }

        private void HeroLive_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("Home");

        // ── Sheet ─────────────────────────────────────────────────────────────

        private void OpenSheet(ObservableGameEntry g)
        {
            ViewModel.OpenSheet(g);
            Sheet.IsPaneOpen = true;
        }

        private void Sheet_PaneClosed(SplitView sender, object args) => ViewModel.CloseSheet();

        private void SheetClose_Click(object sender, RoutedEventArgs e) => Sheet.IsPaneOpen = false;

        private void SheetPlay_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedGame is { } g) ViewModel.LaunchGame(g);
        }

        private void SheetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedGame is { } g) ViewModel.OpenGameFolder(g);
        }

        private void SyncSheetToggle()
        {
            _syncingSheetToggle = true;
            SheetEnabledToggle.IsOn = ViewModel.SelectedGame?.Enabled ?? false;
            _syncingSheetToggle = false;
        }

        private void SheetEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingSheetToggle) return;
            if (ViewModel.SelectedGame is { } g) g.Enabled = SheetEnabledToggle.IsOn;
        }

        private void SheetSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string id })
            {
                AppStateService.Instance.PendingSessionId = id;
                App.MainWindow?.NavigateTo("Logs");
            }
        }

        private async void SheetRemove_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedGame is not { } g) return;
            if (!await ConfirmAsync($"Remove {g.Name}?",
                    g.IsManual
                        ? "The entry you added is removed from the library and from the streaming server's app list."
                        : "It is removed from the list. A game found by the store scan comes back at the next sync if it is still installed.",
                    "Remove")) return;
            Sheet.IsPaneOpen = false;
            await ViewModel.RemoveGameAsync(g);
        }

        // ── Toolbar actions ───────────────────────────────────────────────────

        private async void SyncNow_Click(object sender, RoutedEventArgs e)
            => await ViewModel.SyncNowAsync();

        private async void ClearSync_Click(object sender, RoutedEventArgs e)
        {
            if (!await ConfirmAsync("Clear sync?",
                    "Removes every entry StreamTweak added to the streaming server's app list and resets the game list here, manual entries included.",
                    "Clear")) return;
            await ViewModel.ClearSyncAsync();
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e)
            => await ViewModel.AddGameAsync();

        private async void ToggleHostAssets_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ToggleHostAssetsAsync();

        private void StatusInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
            => ViewModel.HasStatus = false;

        // ── Dialog ────────────────────────────────────────────────────────────

        private static readonly FontFamily DmSans = new("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");

        private async Task<bool> ConfirmAsync(string title, string body, string action)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = body, TextWrapping = TextWrapping.Wrap, FontFamily = DmSans,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                },
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xF2, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
            dialog.Resources["AccentButtonBackground"]        = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonForeground"]        = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
            dialog.Resources["AccentButtonBorderBrush"]       = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
    }
}
