using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StreamTweak.Services;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace StreamTweak
{
    public sealed partial class MainWindow : Window
    {
        private bool _quitDialogOpen = false;

        public MainWindow()
        {
            this.InitializeComponent();
            AppWindow.SetIcon(System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "Resources", "streamtweak.ico"));

            // Set NavigationView pane background via resource dictionary override.
            // PaneBackground does not exist as a XAML property on WinUI3 NavigationView;
            // the internal resource keys must be injected at runtime via code-behind.
            var sidebarBrush = new SolidColorBrush(Colors.Transparent);
            NavView.Resources["NavigationViewDefaultPaneBackground"]  = sidebarBrush;
            NavView.Resources["NavigationViewExpandedPaneBackground"] = sidebarBrush;
            NavView.Resources["NavigationViewTopPaneBackground"]      = sidebarBrush;

            // Selection indicator (left accent bar on active item) — use system accent.
            // WinUI3 NavigationViewItem template binds the indicator Rectangle.Fill to
            // NavigationViewSelectionIndicatorForeground; default is AccentFillColorDefaultBrush
            // which may not match the exact SystemAccentColor used by button styles.
            var accentBrush = new SolidColorBrush(
                (Color)Application.Current.Resources["SystemAccentColor"]);
            NavView.Resources["NavigationViewSelectionIndicatorForeground"] = accentBrush;

            // Override the internal WinUI3 font used by NavigationViewItem labels.
            // FontFamily inheritance is ignored by the item template; the template
            // binds to ContentControlThemeFontFamily as a local resource key.
            NavView.Resources["ContentControlThemeFontFamily"] =
                new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");

            ConfigureTitleBar();    // sets up AppWindow.TitleBar colours + SetTitleBar()
            ConfigureWindowSize();

            // Clicking X shows a confirmation dialog instead of silently hiding.
            // If the user confirms, ExitApp() terminates the process.
            // If the user cancels, the window stays visible.
            AppWindow.Closing += async (_, args) =>
            {
                args.Cancel = true;
                if (_quitDialogOpen) return;
                _quitDialogOpen = true;
                try
                {
                    var dmSans = new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");

                    var dialog = new ContentDialog
                    {
                        Title   = "Quit StreamTweak",
                        Content = new TextBlock
                        {
                            Text         = "StreamTweak will stop monitoring streaming sessions.",
                            FontFamily   = dmSans,
                            FontSize     = 13,
                            Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8)),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        PrimaryButtonText = "Quit",
                        CloseButtonText   = "Cancel",
                        DefaultButton     = ContentDialogButton.Primary,
                        XamlRoot          = this.Content.XamlRoot,
                    };

                    // Background, border, font.
                    dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(Color.FromArgb(0xE6, 0x1d, 0x1b, 0x1a));
                    dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x27, 0x24));
                    dialog.Resources["ContentControlThemeFontFamily"] = dmSans;

                    // Primary button (Quit) — override AccentButtonStyle resources,
                    // which is what WinUI3 ContentDialog actually applies to the primary button.
                    var dangerFg  = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
                    var dangerBg  = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
                    var dangerBdr = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
                    dialog.Resources["AccentButtonBackground"]             = dangerBg;
                    dialog.Resources["AccentButtonForeground"]             = dangerFg;
                    dialog.Resources["AccentButtonBorderBrush"]            = dangerBdr;
                    dialog.Resources["AccentButtonBackgroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
                    dialog.Resources["AccentButtonForegroundPointerOver"]  = dangerFg;
                    dialog.Resources["AccentButtonBorderBrushPointerOver"] = dangerBdr;
                    dialog.Resources["AccentButtonBackgroundPressed"]      = new SolidColorBrush(Color.FromArgb(0x55, 0xEF, 0x44, 0x44));
                    dialog.Resources["AccentButtonForegroundPressed"]      = dangerFg;
                    dialog.Resources["AccentButtonBorderBrushPressed"]     = dangerBdr;
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                        ((App)Application.Current).ExitApp();
                }
                finally { _quitDialogOpen = false; }
            };

            // Persist window size whenever it changes so it survives process restarts.
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange)
                    SaveWindowSize();
            };

            // Minimize button → hide window so it disappears from the taskbar.
            // The only way to reopen is via the tray icon.
            AppWindow.Changed += (_, _) =>
            {
                if (AppWindow.Presenter is OverlappedPresenter op &&
                    op.State == OverlappedPresenterState.Minimized)
                    ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE);
            };

            // Navigate to Home on startup
            NavView.SelectedItem = NavHome;
            ContentFrame.Navigate(typeof(Views.HomeView));

            // Sidebar update indicator: subscribe to AppStateService and refresh
            // immediately in case the boot-time check has already completed.
            AppStateService.Instance.UpdateAvailabilityChanged += OnUpdateAvailabilityChanged;
            RefreshUpdateIndicator();
        }

        private void OnUpdateAvailabilityChanged(object? sender, EventArgs e)
        {
            // The event can fire from the HTTP continuation on a thread-pool thread —
            // marshal to the UI thread before touching XAML.
            DispatcherQueue.TryEnqueue(RefreshUpdateIndicator);
        }

        private void RefreshUpdateIndicator()
        {
            var state = AppStateService.Instance;
            if (state.UpdateAvailable && !string.IsNullOrEmpty(state.LatestVersion))
            {
                SidebarUpdateLink.Content    = $"↑ Update to v{state.LatestVersion}";
                SidebarUpdateLink.Visibility = Visibility.Visible;
            }
            else
            {
                SidebarUpdateLink.Visibility = Visibility.Collapsed;
            }
        }

        private void SidebarUpdateLink_Click(object sender, RoutedEventArgs e)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/FoggyBytes/StreamTweak/releases/latest"));
        }

        // ── Title bar ───────────────────────────────────────────────────────────

        private void ConfigureTitleBar()
        {
            // Extend WinUI content into the title bar area so NavigationView's
            // pane header fills the full height flush with the window edge.
            ExtendsContentIntoTitleBar = true;

            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;

            // Make caption buttons (min/max/close) blend with dark Mica background.
            titleBar.ButtonBackgroundColor         = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor         = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor    = Colors.White;
            titleBar.ButtonHoverBackgroundColor    = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor  = Colors.White;
            titleBar.ButtonPressedBackgroundColor  = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);

            // Register the AppTitleBar Grid as the drag region.
            // This makes the area between the pane and caption buttons draggable
            // and tells WinUI where interactive elements live.
            this.SetTitleBar(AppTitleBar);
        }

        private void ConfigureWindowSize()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi / 96.0;

            // Restore last saved size, or fall back to the default 800×640.
            int logicalWidth  = Services.ConfigService.GetInt("WindowWidth",  800);
            int logicalHeight = Services.ConfigService.GetInt("WindowHeight", 640);

            // Clamp to a sensible minimum so the UI never becomes unusable.
            logicalWidth  = Math.Max(logicalWidth,  620);
            logicalHeight = Math.Max(logicalHeight, 480);

            int physicalWidth  = (int)(logicalWidth  * scale);
            int physicalHeight = (int)(logicalHeight * scale);

            AppWindow.Resize(new SizeInt32(physicalWidth, physicalHeight));

            // Always center on the primary display (WorkArea is in physical pixels).
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var x = (display.WorkArea.Width  - physicalWidth)  / 2;
            var y = (display.WorkArea.Height - physicalHeight) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }

        private void SaveWindowSize()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi / 96.0;

            // Persist logical (DIP) dimensions so they can be correctly scaled
            // on any DPI when the process restarts.
            int logicalWidth  = (int)(AppWindow.Size.Width  / scale);
            int logicalHeight = (int)(AppWindow.Size.Height / scale);

            Services.ConfigService.Set("WindowWidth",  logicalWidth);
            Services.ConfigService.Set("WindowHeight", logicalHeight);
        }

        // ── Navigation ──────────────────────────────────────────────────────────

        private void NavView_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is not NavigationViewItem item) return;
            string? tag = item.Tag as string;

            Type? pageType = tag switch
            {
                "Home"        => typeof(Views.HomeView),
                "Network"     => typeof(Views.NetworkView),
                "Audio"       => typeof(Views.AudioView),
                "Display"     => typeof(Views.DisplayView),
                "Apps"        => typeof(Views.AppsView),
                "GameLibrary" => typeof(Views.GameLibraryView),
                "Store"       => typeof(Views.StoreView),
                "Logs"        => typeof(Views.LogsView),
                "Glossary"    => typeof(Views.GlossaryView),
                "Settings"    => typeof(Views.SettingsView),
                _             => null
            };

            if (pageType != null)
                ContentFrame.Navigate(pageType, tag);
        }

        // ── Public helpers ──────────────────────────────────────────────────────

        /// <summary>Selects the navigation item with the given tag, triggering page navigation.</summary>
        public void NavigateTo(string tag)
        {
            var allItems = NavView.MenuItems
                .Concat(NavView.FooterMenuItems)
                .OfType<NavigationViewItem>();
            var item = allItems.FirstOrDefault(i => i.Tag as string == tag);
            if (item != null)
                NavView.SelectedItem = item;
        }

        public void BringToFront()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_RESTORE);   // un-hide if window was hidden via minimize
            SetForegroundWindow(hwnd);
        }

        private const int SW_HIDE    = 0;
        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
