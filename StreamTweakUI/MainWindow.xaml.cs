using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
using System.Runtime.InteropServices;
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

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            SidebarVersionText.Text = v != null
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v7.0.0";

            // Set NavigationView pane background via resource dictionary override.
            // PaneBackground does not exist as a XAML property on WinUI3 NavigationView;
            // the internal resource keys must be injected at runtime via code-behind.
            var sidebarBrush = new SolidColorBrush(Colors.Transparent);
            NavView.Resources["NavigationViewDefaultPaneBackground"]  = sidebarBrush;
            NavView.Resources["NavigationViewExpandedPaneBackground"] = sidebarBrush;
            NavView.Resources["NavigationViewTopPaneBackground"]      = sidebarBrush;
            // Clear the content-area background (right side) and the pane border/divider
            // so the content frame and empty sidebar space below nav items are transparent.
            NavView.Resources["NavigationViewContentBackground"]      = sidebarBrush;
            NavView.Resources["NavigationViewPaneBorderBrush"]        = sidebarBrush;
            // The faint hairline between pane and content is the ContentGridBorder
            // (template Border with BorderThickness="1,0,0,0"); WinUI3 binds its
            // BorderBrush to NavigationViewContentGridBorderBrush. Clear it too.
            NavView.Resources["NavigationViewContentGridBorderBrush"] = sidebarBrush;

            // Selection indicator (left accent bar on active item) — use system accent.
            // WinUI3 NavigationViewItem template binds the indicator Rectangle.Fill to
            // NavigationViewSelectionIndicatorForeground; default is AccentFillColorDefaultBrush
            // which may not match the exact SystemAccentColor used by button styles.
            NavView.Resources["NavigationViewSelectionIndicatorForeground"] =
                new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));

            // Override the internal WinUI3 font used by NavigationViewItem labels.
            // FontFamily inheritance is ignored by the item template; the template
            // binds to ContentControlThemeFontFamily as a local resource key.
            NavView.Resources["ContentControlThemeFontFamily"] =
                new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");

            ConfigureTitleBar();    // sets up AppWindow.TitleBar colours + SetTitleBar()
            ConfigureWindowSize();
            SubclassForMinSize(WindowNative.GetWindowHandle(this));

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

            // Persist window size on every resize.
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange) SaveWindowSize();
            };

            // Minimize button → hide window so it disappears from the taskbar.
            // The only way to reopen is via the tray icon.
            AppWindow.Changed += (_, _) =>
            {
                if (AppWindow.Presenter is OverlappedPresenter op &&
                    op.State == OverlappedPresenterState.Minimized)
                    ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE);
            };

            // Insert the "NVIDIA Sentinel" navigation entry at runtime, right after
            // Display. On AMD/Intel (or no working NVAPI) the entry is shown greyed
            // out and disabled rather than hidden.
            InsertNvidiaProfileNavItem();

            // Navigate to Home on startup
            NavView.SelectedItem = NavHome;
            ContentFrame.Navigate(typeof(Views.HomeView));

            // Sidebar update indicator: subscribe to AppStateService and refresh
            // immediately in case the boot-time check has already completed.
            AppStateService.Instance.UpdateAvailabilityChanged += OnUpdateAvailabilityChanged;
            RefreshUpdateIndicator();
        }

        private void InsertNvidiaProfileNavItem()
        {
            var svc = AppStateService.Instance.NvidiaSentinel;
            bool available = svc?.IsNvidiaAvailable == true;

            // Locate the Display item and insert the new entry right after it.
            int insertIndex = -1;
            for (int i = 0; i < NavView.MenuItems.Count; i++)
            {
                if (NavView.MenuItems[i] is NavigationViewItem nvi
                    && (nvi.Tag as string) == "Display")
                {
                    insertIndex = i + 1;
                    break;
                }
            }
            if (insertIndex < 0) insertIndex = NavView.MenuItems.Count;

            var item = new NavigationViewItem
            {
                Tag       = "NvidiaProfile",
                Content   = "NVIDIA Sentinel",
                Icon      = new ImageIcon
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
                        new Uri("ms-appx:///Resources/nvidia-eye.svg")),
                },
                // Greyed out + non-clickable on AMD/Intel or when NVAPI is unavailable.
                IsEnabled = available,
            };
            if (!available)
                ToolTipService.SetToolTip(item, "Requires an NVIDIA GPU");

            NavView.MenuItems.Insert(insertIndex, item);
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

        private const int MinLogicalWidth  = 1280;
        private const int MinLogicalHeight = 720;

        private void ConfigureWindowSize()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi / 96.0;

            // Restore last saved size, or fall back to the minimum.
            int logicalWidth  = Services.ConfigService.GetInt("WindowWidth",  MinLogicalWidth);
            int logicalHeight = Services.ConfigService.GetInt("WindowHeight", MinLogicalHeight);

            // Enforce minimum so the UI never becomes unusable.
            logicalWidth  = Math.Max(logicalWidth,  MinLogicalWidth);
            logicalHeight = Math.Max(logicalHeight, MinLogicalHeight);

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
                "Home"          => typeof(Views.HomeView),
                "Network"       => typeof(Views.NetworkView),
                "Audio"         => typeof(Views.AudioView),
                "Display"       => typeof(Views.DisplayView),
                "NvidiaProfile" => typeof(Views.NvidiaProfileView),
                "Apps"          => typeof(Views.AppsView),
                "GameLibrary"   => typeof(Views.GameLibraryView),
                "Store"         => typeof(Views.StoreView),
                "Logs"          => typeof(Views.LogsView),
                "Glossary"      => typeof(Views.GlossaryView),
                "Settings"      => typeof(Views.SettingsView),
                _               => null
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

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        // ── Minimum window size (WM_GETMINMAXINFO) ─────────────────────────────

        private const uint WM_GETMINMAXINFO = 0x0024;
        private const int  GWLP_WNDPROC     = -4;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate? _wndProcDelegate;
        private IntPtr           _oldWndProc = IntPtr.Zero;

        private void SubclassForMinSize(IntPtr hwnd)
        {
            _wndProcDelegate = WndProcHook;
            _oldWndProc      = GetWindowLongPtr(hwnd, GWLP_WNDPROC);
            SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }

        private IntPtr WndProcHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                uint   dpi   = GetDpiForWindow(hwnd);
                double scale = dpi / 96.0;
                var    mmi   = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.X = (int)(MinLogicalWidth  * scale);
                mmi.ptMinTrackSize.Y = (int)(MinLogicalHeight * scale);
                Marshal.StructureToPtr(mmi, lParam, false);
                return IntPtr.Zero;
            }
            return _oldWndProc != IntPtr.Zero
                ? CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam)
                : IntPtr.Zero;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
