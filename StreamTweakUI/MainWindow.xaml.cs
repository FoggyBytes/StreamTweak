using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
                    var dialog = new ContentDialog
                    {
                        Title             = "Quit StreamTweak",
                        Content           = "StreamTweak will stop monitoring streaming sessions.",
                        PrimaryButtonText = "Quit",
                        CloseButtonText   = "Cancel",
                        DefaultButton     = ContentDialogButton.Primary,
                        XamlRoot          = this.Content.XamlRoot,
                    };
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
