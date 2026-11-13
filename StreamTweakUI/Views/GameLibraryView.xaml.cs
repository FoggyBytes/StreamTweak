using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.ViewModels;
using Windows.UI;

namespace StreamTweak.Views
{
    /// <summary>10.0 Library — see <see cref="GameLibraryViewModel"/>.</summary>
    public sealed partial class GameLibraryView : Page
    {
        public GameLibraryViewModel ViewModel { get; } = new GameLibraryViewModel();

        public GameLibraryView()
        {
            this.InitializeComponent();
            SizeChanged += (_, e) =>
            {
                // The same content column as every other page (Controls/PageLayout); padding and
                // not MaxWidth, so the wheel keeps scrolling the list over the side margins.
                var side = StreamTweak.Controls.PageLayout.Side(e.NewSize.Width);
                GameList.Padding = new Thickness(side, 0, side, 40);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Load();
            GameLibraryService.LibraryChanged += OnLibraryChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            GameLibraryService.LibraryChanged -= OnLibraryChanged;
            ViewModel.Stop();
        }

        // A sync this page did not start (StreamLight's Sync and restart, the start-up sync).
        private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(ViewModel.Reload);

        private static readonly SolidColorBrush Ok  = new(Color.FromArgb(0xFF, 0xC8, 0xCF, 0xCB));
        private static readonly SolidColorBrush Bad = new(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71));
        public SolidColorBrush JobBrush(bool failed) => failed ? Bad : Ok;

        private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string tab) ViewModel.Tab = tab;
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryRow row) ViewModel.Play(row);
        }

        private async void Act_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryRow row) await ViewModel.ActAsync(row);
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e) => await ViewModel.AddGameAsync();

        // ── A game added by hand: picture and name (10.4.2) ──────────────────────────

        private async void Cover_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryRow row) await ViewModel.ChooseCoverAsync(row);
        }

        /// <summary>Rename: the name in a box; a refusal is said in the dialog, which stays open.</summary>
        private async void Name_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryRow row || !row.CanEditLook || !ViewModel.CanChange) return;

            var box = new TextBox { Header = "Name", Text = row.Name, MinWidth = 380 };
            var error = new TextBlock { FontFamily = DmSans, FontSize = 12.5, Foreground = Amber, TextWrapping = TextWrapping.Wrap,
                                        Visibility = Visibility.Collapsed };
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(box);
            body.Children.Add(new TextBlock { Text = $"{ViewModel.Server} shows the new name after Sync and restart.", FontFamily = DmSans,
                                              FontSize = 12.5, Foreground = Text3, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(error);

            var dialog = new ContentDialog
            {
                Title             = "Rename",
                Content           = body,
                PrimaryButtonText = "Save",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = this.XamlRoot,
            };
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
            void Validate() => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0 && box.Text.Trim() != row.Name;
            box.TextChanged += (_, _) => { error.Visibility = Visibility.Collapsed; Validate(); };
            box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            Validate();

            dialog.PrimaryButtonClick += async (d, args) =>
            {
                var deferral = args.GetDeferral();
                string? why = await ViewModel.RenameAsync(row, box.Text);
                if (why != null)
                {
                    error.Text = $"Not renamed: {why}.";
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
                deferral.Complete();
            };
            await dialog.ShowAsync();
        }

        // ── How a game starts (10.4.0, mockup A «Menu sul comando») ─────────────────

        private static readonly FontFamily DmSans    = new("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");
        private static readonly SolidColorBrush Text1 = new(Color.FromArgb(0xFF, 0xF2, 0xF5, 0xF3));
        private static readonly SolidColorBrush Text3 = new(Color.FromArgb(0xFF, 0x92, 0x9A, 0x96));
        private static readonly SolidColorBrush Text4 = new(Color.FromArgb(0xFF, 0x64, 0x6B, 0x67));
        private static readonly SolidColorBrush Mint  = new(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
        private static readonly SolidColorBrush Amber = new(Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24));

        /// <summary>
        /// The menu under the command: one entry per way the game can start, a title with a grey
        /// note and the program under it, the tick on the one in use. A Flyout of buttons rather
        /// than a MenuFlyout, whose items hold one line of text only.
        /// </summary>
        private void Command_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button anchor || anchor.DataContext is not LibraryRow row || !row.HasCommand) return;
            if (!ViewModel.CanChange) return;

            var options = ViewModel.LaunchOptions(row, out string? footnote);
            var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
            var panel = new StackPanel { Spacing = 1, MinWidth = Math.Max(380, anchor.ActualWidth), MaxWidth = 620,
                                         XYFocusKeyboardNavigation = Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled };

            foreach (var o in options)
            {
                if (o.Choice == GameLibraryEntry.ChoiceCustom)
                    panel.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4),
                                                    Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) });

                var title = new TextBlock { FontFamily = DmSans, FontSize = 13.5, Foreground = Text1 };
                title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = o.Title });
                if (o.Note.Length > 0)
                    title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "   " + o.Note, FontSize = 12, Foreground = Text3 });
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(title);
                if (o.Detail.Length > 0)
                    text.Children.Add(new TextBlock { Text = o.Detail, Style = (Style)Application.Current.Resources["ST9_Mono"],
                                                      FontSize = 11, Foreground = Text3, TextTrimming = TextTrimming.CharacterEllipsis });

                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (o.IsCurrent)
                    grid.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Foreground = Mint, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);

                var item = new Button
                {
                    Style = (Style)Application.Current.Resources["ST9_ButtonGhost"],
                    Content = grid, Padding = new Thickness(8, 6, 10, 6), MinHeight = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetName(item, o.Note.Length > 0 ? $"{o.Title}, {o.Note}" : o.Title);
                item.Click += async (_, _) =>
                {
                    flyout.Hide();
                    if (o.Choice == GameLibraryEntry.ChoiceCustom) await AskCustomCommandAsync(row);
                    else if (!o.IsCurrent) await ViewModel.SetLaunchAsync(row, o.Choice);
                };
                panel.Children.Add(item);
            }

            if (footnote != null)
                panel.Children.Add(new TextBlock { Text = footnote, FontFamily = DmSans, FontSize = 11.5, Foreground = Text4,
                                                   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 4, 10, 4) });

            flyout.Content = panel;
            flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(FlyoutPresenter.PaddingProperty, new Thickness(4)),
                    new Setter(FlyoutPresenter.MaxWidthProperty, 640.0),
                    new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)),
                },
            };
            flyout.ShowAt(anchor);
        }

        /// <summary>Custom command…: the command line and its folder, filled with what runs now.</summary>
        private async System.Threading.Tasks.Task AskCustomCommandAsync(LibraryRow row)
        {
            var e = row.Entry;
            var mono = (FontFamily)Application.Current.Resources["JetBrainsMono"];
            var cmdBox = new TextBox { Header = "Command", Text = e.CustomCommand ?? row.Command, FontFamily = mono, FontSize = 12.5,
                                       TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            var dirBox = new TextBox { Header = "Working folder", Text = e.CustomWorkingDir ?? row.WorkingDir, FontFamily = mono, FontSize = 12.5,
                                       PlaceholderText = "The program's own folder" };
            var body = new StackPanel { Spacing = 12, MinWidth = 460 };
            body.Children.Add(new TextBlock { Text = row.Name, FontFamily = DmSans, FontSize = 13, Foreground = Text3, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(cmdBox);
            body.Children.Add(dirBox);
            string warning = GameLibraryViewModel.BypassWarning(e);
            if (warning.Length > 0)
                body.Children.Add(new TextBlock { Text = warning, FontFamily = DmSans, FontSize = 12.5, Foreground = Amber, TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title             = "Custom command",
                Content           = body,
                PrimaryButtonText = "Save",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = this.XamlRoot,
            };
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
            cmdBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = cmdBox.Text.Trim().Length > 0;
            dialog.IsPrimaryButtonEnabled = cmdBox.Text.Trim().Length > 0;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.SetLaunchAsync(row, GameLibraryEntry.ChoiceCustom, cmdBox.Text, dirBox.Text);
        }

        /// <summary>Asked first: it empties the list and restarts the streaming server.</summary>
        private async void ClearLibrary_Click(object sender, RoutedEventArgs e)
        {
            string server = ViewModel.Server;
            string text = $"Every game StreamTweak listed in {server} leaves it — synced, added by hand and excluded — and "
                        + $"{server} restarts to show it. Its own entries, such as Desktop, stay.";
            if (ViewModel.SyncEnabled)
                text += " Sync when StreamTweak starts is on: the games come back at its next start.";

            var dialog = new ContentDialog
            {
                Title             = "Clear the library?",
                Content           = new TextBlock
                {
                    Text         = text,
                    FontFamily   = DmSans,
                    FontSize     = 13,
                    Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Clear and restart",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.XamlRoot,
            };
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.StartClearAndRestart();
        }

        private void SyncRestart_Click(object sender, RoutedEventArgs e) => ViewModel.StartSyncAndRestart();

        private async void HostTiles_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleHostAssetsAsync();

        private void ClearLegacy_Click(object sender, RoutedEventArgs e) => ViewModel.ClearLegacyExclusions();

        private void Status_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.HasStatus = false;
    }
}
