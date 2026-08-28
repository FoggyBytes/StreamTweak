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
                : "v8.2.0";

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
                new SolidColorBrush(Color.FromArgb(0xFF, 0x4a, 0xde, 0x80));

            // Override the internal WinUI3 font used by NavigationViewItem labels.
            // FontFamily inheritance is ignored by the item template; the template
            // binds to ContentControlThemeFontFamily as a local resource key.
            NavView.Resources["ContentControlThemeFontFamily"] =
                new FontFamily("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");

            ConfigureTitleBar();    // sets up AppWindow.TitleBar colours + SetTitleBar()
            ConfigureWindowSize();
            RestoreSidebarState();  // remember the collapsed/expanded sidebar across runs
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
                    var dialog = new ContentDialog
                    {
                        Title   = "Quit StreamTweak",
                        Content = new TextBlock
                        {
                            Text         = "StreamTweak will stop monitoring streaming sessions.",
                            FontFamily   = DmSans,
                            FontSize     = 13,
                            Foreground   = new SolidColorBrush(DialogBodyText),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        PrimaryButtonText = "Quit",
                        CloseButtonText   = "Cancel",
                        DefaultButton     = ContentDialogButton.Primary,
                        XamlRoot          = this.Content.XamlRoot,
                    };

                    ApplyDialogChrome(dialog);
                    // Quit is the Primary button; WinUI3 ContentDialog styles it via the
                    // AccentButton* keys, so the red danger palette goes there.
                    ApplyDangerAccentPalette(dialog);

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
                {
                    ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE);
                    // Pages keep their timers running unless they are told the window is
                    // gone — navigation events never fire for a hide. See issue #7.
                    AppStateService.Instance.SetMainWindowVisible(false);
                }
            };

            // 8.0: NVIDIA Sentinel is now a panel inside the Tuning master-detail,
            // not a top-level nav entry — the runtime injection is disabled.
            // NVIDIA Sentinel is NVIDIA-only: hide its Host-setup nav item on AMD/Intel.
            if (AppStateService.Instance.NvidiaSentinel?.IsNvidiaAvailable != true)
                NavTuneNv.Visibility = Visibility.Collapsed;

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

        // ── Bridge client approval (7.1.0) ──────────────────────────────────────

        private bool _bridgeDialogOpen;
        private readonly Queue<BridgeClient> _pendingApprovals = new();

        /// <summary>
        /// Queues a trust-on-first-use approval prompt for a StreamLight client that
        /// just enrolled on the bridge. Safe to call from any thread's dispatch.
        /// </summary>
        public void ShowBridgeApproval(BridgeClient client)
        {
            _pendingApprovals.Enqueue(client);
            if (!_bridgeDialogOpen)
                _ = ShowNextBridgeApprovalAsync();
        }

        private async Task ShowNextBridgeApprovalAsync()
        {
            if (_bridgeDialogOpen) return;
            _bridgeDialogOpen = true;
            try
            {
                BringToFront(); // un-hide from tray so the user actually sees the prompt

                while (_pendingApprovals.Count > 0)
                {
                    var client = _pendingApprovals.Dequeue();

                    var content = new StackPanel { Spacing = 12 };
                    content.Children.Add(new TextBlock
                    {
                        Text         = $"{client.Name} wants to control StreamTweak — change the NIC speed, read host metrics and your game list. Allow it only if the PIN below matches the one shown on the device.",
                        FontFamily   = DmSans,
                        FontSize     = 13,
                        Foreground   = new SolidColorBrush(DialogBodyText),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text       = "PIN shown on the device",
                        FontFamily = DmSans,
                        FontSize   = 11,
                        Foreground = new SolidColorBrush(DialogMutedText),
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text             = string.IsNullOrEmpty(client.Pin) ? "————" : client.Pin,
                        // 8.0 font unification (was Consolas). The wide CharacterSpacing
                        // below is what keeps the 4 digits readable for the out-of-band
                        // comparison against the client's screen, not the typeface.
                        FontFamily       = DmSans,
                        FontSize         = 34,
                        FontWeight       = Microsoft.UI.Text.FontWeights.Bold,
                        CharacterSpacing = 240,
                        Foreground       = new SolidColorBrush(GreenText),
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text         = "Denying does not affect streaming — it only blocks StreamTweak's host metrics, NIC speed & Streaming Mode, store badges and session reports for this device.",
                        FontFamily   = DmSans,
                        FontSize     = 11,
                        Foreground   = new SolidColorBrush(DialogMutedText),
                        TextWrapping = TextWrapping.Wrap,
                    });

                    var dialog = new ContentDialog
                    {
                        Title             = "Allow StreamLight client?",
                        Content           = content,
                        PrimaryButtonText = "Allow",
                        CloseButtonText   = "Deny",
                        DefaultButton     = ContentDialogButton.Close,
                        XamlRoot          = this.Content.XamlRoot,
                    };
                    ApplyDialogChrome(dialog);
                    // Allow = green. As the non-default button, "Allow" (Primary) uses the
                    // regular Button* keys; "Deny" (the DefaultButton=Close) uses the
                    // AccentButton* keys — so we colour Button* green and AccentButton* red.
                    ApplyGreenButtonPalette(dialog);
                    ApplyDangerAccentPalette(dialog);

                    var auth = AppStateService.Instance.BridgeAuth;
                    try
                    {
                        var result = await dialog.ShowAsync();
                        if (result == ContentDialogResult.Primary) auth?.Approve(client.UniqueId);
                        else                                       auth?.Deny(client.UniqueId);
                    }
                    catch
                    {
                        // ShowAsync can throw if another dialog is mid-flight; leave the
                        // client pending so it can still be approved from Settings.
                    }
                }
            }
            finally { _bridgeDialogOpen = false; }
        }

        // ── Shared dialog styling ───────────────────────────────────────────────
        // StreamTweak's ContentDialogs (Quit, bridge approval) share the same dark
        // Mica-friendly chrome and the app-wide green/red button palettes. These were
        // previously duplicated brush-by-brush in each dialog; centralizing them keeps
        // a single source of truth and matches the ST_ButtonGreen / ST_ButtonDanger
        // hover/pressed shades defined in Themes/StreamTweakStyles.xaml.

        private static readonly FontFamily DmSans =
            new("ms-appx:///Resources/DMSans-Regular.ttf#DM Sans");

        private static readonly Color DialogBg        = Color.FromArgb(0xF2, 0x1d, 0x1b, 0x1a);
        private static readonly Color DialogBorder    = Color.FromArgb(0xFF, 0x2A, 0x27, 0x24);
        private static readonly Color DialogBodyText  = Color.FromArgb(0xFF, 0xC0, 0xBC, 0xB8);
        private static readonly Color DialogMutedText = Color.FromArgb(0xFF, 0x90, 0x8C, 0x88);
        private static readonly Color DangerColor     = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);
        private static readonly Color GreenColor      = Color.FromArgb(0xFF, 0x4a, 0xde, 0x80);
        private static readonly Color GreenText       = Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80);

        /// <summary>Dark background, border, and DM Sans font shared by all dialogs.</summary>
        private static void ApplyDialogChrome(ContentDialog dialog)
        {
            dialog.Resources["ContentDialogBackground"]       = new SolidColorBrush(DialogBg);
            dialog.Resources["ContentDialogBorderBrush"]      = new SolidColorBrush(DialogBorder);
            dialog.Resources["ContentControlThemeFontFamily"] = DmSans;
        }

        /// <summary>
        /// Red "danger" palette applied to the AccentButton* slot. WinUI3 ContentDialog
        /// styles its default/primary accent button with these keys, so this colours the
        /// Quit (Primary) button and the bridge Deny (Close/default) button red.
        /// </summary>
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

        /// <summary>
        /// Green "affirmative" palette applied to the regular Button* slot — used by the
        /// bridge Allow button (the non-default Primary button uses these keys).
        /// </summary>
        private static void ApplyGreenButtonPalette(ContentDialog dialog)
        {
            dialog.Resources["ButtonBackground"]             = new SolidColorBrush(Color.FromArgb(0x1F, 0x4a, 0xde, 0x80));
            dialog.Resources["ButtonForeground"]             = new SolidColorBrush(GreenText);
            dialog.Resources["ButtonBorderBrush"]            = new SolidColorBrush(Color.FromArgb(0x4D, 0x4a, 0xde, 0x80));
            dialog.Resources["ButtonBackgroundPointerOver"]  = new SolidColorBrush(Color.FromArgb(0x2D, 0x4a, 0xde, 0x80));
            dialog.Resources["ButtonForegroundPointerOver"]  = new SolidColorBrush(GreenText);
            dialog.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Color.FromArgb(0x66, 0x4a, 0xde, 0x80));
            dialog.Resources["ButtonBackgroundPressed"]      = new SolidColorBrush(Color.FromArgb(0x12, 0x4a, 0xde, 0x80));
            dialog.Resources["ButtonForegroundPressed"]      = new SolidColorBrush(GreenColor);
            dialog.Resources["ButtonBorderBrushPressed"]     = new SolidColorBrush(Color.FromArgb(0x44, 0x4a, 0xde, 0x80));
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

        // ── Sidebar collapsed/expanded state (restored across runs) ──────────────
        private const string KeySidebarPaneOpen = "SidebarPaneOpen";
        private bool _paneStateRestored;

        private void RestoreSidebarState()
        {
            // Apply early to reduce flicker. This assignment is usually overridden by the
            // NavigationView's first layout pass (in Expanded mode it auto-opens the pane),
            // so we re-apply it in Loaded below — which is the reliable point.
            NavView.IsPaneOpen = Services.ConfigService.GetBool(KeySidebarPaneOpen, true);

            NavView.Loaded += (_, _) =>
            {
                if (_paneStateRestored) return; // Loaded can fire more than once; restore only once
                _paneStateRestored = true;

                NavView.IsPaneOpen = Services.ConfigService.GetBool(KeySidebarPaneOpen, true);

                // Attach the save handlers ONLY AFTER restoring, so neither the restore itself
                // nor the control's auto-open during initial layout clobbers the user's choice.
                NavView.PaneOpening += (_, _) => Services.ConfigService.Set(KeySidebarPaneOpen, true);
                NavView.PaneClosing += (_, _) => Services.ConfigService.Set(KeySidebarPaneOpen, false);
            };
        }

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
                "GameLibrary"   => typeof(Views.GameLibraryView),
                "Logs"          => typeof(Views.LogsView),
                // Host-setup subsections — all resolve to the parametric TuningView,
                // which reads the tag to pick the panel (see TuningView.ShowSection).
                "TuneNet"       => typeof(Views.TuningView),
                "TuneAV"        => typeof(Views.TuningView),
                "TuneNv"        => typeof(Views.TuningView),
                "TuneApps"      => typeof(Views.TuningView),
                "Clients"       => typeof(Views.ClientsView),
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
            AppStateService.Instance.SetMainWindowVisible(true);
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
