using System.Reflection;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using StreamTweak.Services;

namespace StreamTweak
{
    // System-tray icon, context menu, and the tray-tooltip / context-menu sizing helpers.
    // Split out of App.xaml.cs. Operates on the tray fields declared in App.xaml.cs.
    public partial class App
    {
        // ── Tray icon ─────────────────────────────────────────────────────────

        private void SetupTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText        = "StreamTweak",
                DoubleClickCommand = new SimpleCommand(ShowMainWindow),
                // SecondWindow mode renders the actual WinUI 3 MenuFlyout in a
                // transparent helper window, so all Click event handlers fire.
                // The default PopupMenu mode converts the flyout to a Win32 native
                // HMENU and only executes Command — Click is silently ignored.
                ContextMenuMode    = H.NotifyIcon.ContextMenuMode.SecondWindow,
            };

            var flyout = new MenuFlyout();

            // ── Open app (Home) ──────────────────────────────────────────────
            var openItem = new MenuFlyoutItem { Text = "StreamTweak" };
            openItem.Click += (_, _) => { ShowMainWindow(); MainWindow?.NavigateTo("Home"); };
            flyout.Items.Add(openItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Status: live NIC speed + streaming state (non-clickable) ───
            _traySpeedItem = new MenuFlyoutItem { Text = "Speed: …", IsEnabled = false };
            flyout.Items.Add(_traySpeedItem);

            _trayStreamingStatusItem = new MenuFlyoutItem { Text = "Streaming: Inactive", IsEnabled = false };
            flyout.Items.Add(_trayStreamingStatusItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Restore link speed ───────────────────────────────────────────
            // The switch itself is client-driven now, so there is nothing to start from here.
            // What remains is the escape hatch: put the adapter back when a client vanished
            // mid-session and left it switched.
            _trayStreamingModeItem = new MenuFlyoutItem { Text = "Restore link speed", MinWidth = 240 };
            _trayStreamingModeItem.Click += (_, _) =>
                AppStateService.Instance.LinkSpeed?.RestoreNow("tray");
            flyout.Items.Add(_trayStreamingModeItem);

            // "Auto-switch link speed" was removed in 8.1.0 along with the log-triggered
            // switch it controlled. The client decides now; the host only grants permission,
            // on the Network page.

            // ── Spatial Audio toggle ─────────────────────────────────────────
            _traySpatialAudioItem = new ToggleMenuFlyoutItem
            {
                Text      = "Auto Spatial Audio",
                IsChecked = _isAudioMonitorEnabled
            };
            _traySpatialAudioItem.Click += (_, _) =>
            {
                _isAudioMonitorEnabled = _traySpatialAudioItem.IsChecked;
                ConfigService.Set("AudioMonitorEnabled", _isAudioMonitorEnabled);
                if (_isAudioMonitorEnabled)
                    StartDolbyMonitor();
                else
                    _dolbyMonitor.Disable();
                AppStateService.Instance.RaiseSettingsChanged();
            };
            flyout.Items.Add(_traySpatialAudioItem);

            // ── HDR toggle (first HDR-capable monitor) ───────────────────────
            _trayHdrItem = new ToggleMenuFlyoutItem { Text = "HDR", IsChecked = false };
            _trayHdrItem.Click += async (_, _) =>
            {
                if (_trayHdrMonitor == null) return;
                bool newState = _trayHdrItem.IsChecked;
                try
                {
                    await HdrService.SetHdrAsync(_trayHdrMonitor.AdapterId, _trayHdrMonitor.TargetId, newState);
                    _trayHdrMonitor.HdrEnabled = newState;
                    AppStateService.Instance.RaiseSettingsChanged();
                }
                catch
                {
                    _trayHdrItem.IsChecked = !newState; // revert on failure
                }
            };
            flyout.Items.Add(_trayHdrItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // ── Exit ────────────────────────────────────────────────────────
            var exitItem = new MenuFlyoutItem { Text = "Exit" };
            exitItem.Click += (_, _) => ExitApp();
            flyout.Items.Add(exitItem);

            // Reduce font size slightly so the menu fits without a vertical scrollbar.
            foreach (var item in flyout.Items.OfType<Microsoft.UI.Xaml.Controls.Control>())
                item.FontSize = 12;

            _trayIcon.ContextFlyout = flyout;
            _trayIcon.ForceCreate(false);

            // H.NotifyIcon (SecondWindow mode) moves all items from our flyout into its own
            // private internal MenuFlyout (ContextMenuFlyout) during ForceCreate. Our original
            // flyout is left empty and is never shown — its Opening/Opened events never fire.
            //
            // The ONLY thing we still reach for via reflection is the Opened → resize fix
            // below, which has no public-API equivalent (see ExpandTrayContextMenuWindow for
            // the upstream-bug rationale). The speed-label refresh used to also hook the
            // internal flyout's Opening event; it now runs on an independent low-frequency
            // timer (StartTraySpeedTimer) so the visible speed value stays correct even if a
            // future H.NotifyIcon update renames the private member and the reflection breaks.
            var internalFlyoutProp = _trayIcon.GetType()
                .GetProperty("ContextMenuFlyout", BindingFlags.NonPublic | BindingFlags.Instance);
            if (internalFlyoutProp?.GetValue(_trayIcon) is MenuFlyout internalFlyout)
            {
                internalFlyout.Opened += (_, _) => ExpandTrayContextMenuWindow(internalFlyout);
            }

            // Initialize speed immediately so the first open never shows "…", then keep it
            // fresh on a timer (no reflection, no NIC polling cost beyond a cheap enumeration).
            RefreshTraySpeedItem();
            StartTraySpeedTimer();

            // UpdateIcon bypasses the XAML ImageSource → HICON pipeline and sets
            // the icon directly via native HICON handle — the only reliable path in
            // WinUI 3 unpackaged (SoftwareBitmapSource / BitmapImage both rendered blank).
            SetTrayIcon(sessionActive: false);

            // Load HDR/AutoHDR state in background and update toggle items when ready
            _ = LoadDisplayStateForTrayAsync();
        }

        // Refreshes the tray "Speed: …" label on a low-frequency repeating timer instead of
        // hooking H.NotifyIcon's internal flyout Opening event via reflection. The NIC speed
        // changes rarely (and stream-driven changes already trigger PollForNicReconnectAsync),
        // so a few-second cadence keeps the label effectively live with negligible cost.
        private void StartTraySpeedTimer()
        {
            if (_traySpeedTimer != null) return;
            _traySpeedTimer = _dispatcher.CreateTimer();
            _traySpeedTimer.Interval    = TimeSpan.FromMilliseconds(TRAY_SPEED_REFRESH_MS);
            _traySpeedTimer.IsRepeating = true;
            _traySpeedTimer.Tick += (_, _) => RefreshTraySpeedItem();
            _traySpeedTimer.Start();
        }

        private void RefreshTraySpeedItem()
        {
            if (_traySpeedItem == null) return;
            var (mbps, connected) = GetCurrentSpeed();
            string speedText = !connected ? "Unknown"
                : mbps <= 0   ? "Negotiating…"
                : mbps >= 1000 ? $"{mbps / 1000.0:0.##} Gbps"
                :                $"{mbps} Mbps";
            _traySpeedItem.Text = $"Speed: {speedText}";
            UpdateTrayTooltip(connected ? speedText : null);
        }

        private void UpdateTrayTooltip(string? speedText = null)
        {
            if (_trayIcon == null) return;
            if (speedText != null)
                _trayIcon.ToolTipText = $"StreamTweak\n{_adapterName}: {speedText}";
            else
                _trayIcon.ToolTipText = "StreamTweak";
        }

        // Polls the NIC every second for up to 15 s after a speed change so the
        // tooltip reflects the new speed once the adapter reconnects.
        private async Task PollForNicReconnectAsync()
        {
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                var (mbps, connected) = GetCurrentSpeed();
                if (!connected) continue;
                string speedText = mbps >= 1000
                    ? $"{mbps / 1000.0:0.##} Gbps"
                    : $"{mbps} Mbps";
                _dispatcher.TryEnqueue(() =>
                {
                    RefreshTraySpeedItem();   // updates both menu item and tooltip
                });
                break;
            }
        }

        private async Task LoadDisplayStateForTrayAsync()
        {
            try
            {
                var monitors = await HdrService.GetMonitorsAsync();
                var primary  = monitors.FirstOrDefault(m => m.HdrSupported && !m.IsVirtual);

                _dispatcher.TryEnqueue(() =>
                {
                    _trayHdrMonitor = primary;
                    if (_trayHdrItem != null)
                    {
                        _trayHdrItem.IsChecked = primary?.HdrEnabled ?? false;
                        _trayHdrItem.IsEnabled = primary != null;
                    }
                });
            }
            catch { }
        }

        // ── Tray context-menu scrollbar fix ──────────────────────────────────

        /// <summary>
        /// Ensures H.NotifyIcon's internal popup AppWindow is tall enough to show all menu
        /// items without a vertical scrollbar, at any DPI scaling factor.
        ///
        /// Root cause: H.NotifyIcon's MeasureFlyout() uses flyout.XamlRoot.RasterizationScale
        /// to convert logical→physical pixels when sizing the AppWindow. On the FIRST open,
        /// the internal Window has never been shown, so XamlRoot is null → scale falls back
        /// to 1.0 regardless of actual DPI → window can be 80-170 physical pixels too short
        /// at 125-150% DPI.  On subsequent opens the warm-up has run and XamlRoot is set,
        /// so only a 1-2 px rounding error remains.
        ///
        /// This is an UPSTREAM bug that is still unfixed as of H.NotifyIcon.WinUI 2.4.1
        /// (MeasureFlyout still does `XamlRoot?.RasterizationScale ?? 1.0`), so upgrading the
        /// package does not remove the need for this fix, and there is no public API exposing
        /// the popup AppWindow — hence the (guarded, best-effort) reflection below. If a future
        /// version renames ContextMenuFlyout / ContextMenuAppWindow this silently no-ops and
        /// the only consequence is a one-time scrollbar on the first open at high DPI.
        ///
        /// Fix: at Opened time (flyout shown, XamlRoot now valid) we re-run the same
        /// measurement H.NotifyIcon would have run with the correct scale, then resize the
        /// AppWindow to the correct physical height if it is currently undersized.
        /// </summary>
        private void ExpandTrayContextMenuWindow(MenuFlyout internalFlyout)
        {
            try
            {
                // Called from Opened: XamlRoot and DesiredSize are always valid at this point.
                if (internalFlyout.XamlRoot == null) return; // safety guard, should never trigger

                double scale = internalFlyout.XamlRoot.RasterizationScale;

                double totalHeight = 4.0;
                foreach (var item in internalFlyout.Items)
                    totalHeight += item.DesiredSize.Height;

                int neededHeight = (int)Math.Round(scale * totalHeight + 4.0) + 2;

                var appWindowProp = _trayIcon!.GetType()
                    .GetProperty("ContextMenuAppWindow",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                if (appWindowProp?.GetValue(_trayIcon) is Microsoft.UI.Windowing.AppWindow appWindow)
                {
                    var sz = appWindow.Size;
                    if (sz.Height > 0 && sz.Height < neededHeight)
                        appWindow.ResizeClient(
                            new Windows.Graphics.SizeInt32(sz.Width, neededHeight));
                }
            }
            catch { /* best-effort; never block the tray */ }
        }

        // ── Minimal ICommand for tray double-click ────────────────────────────

        private sealed class SimpleCommand(Action execute) : ICommand
        {
#pragma warning disable CS0067
            public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => execute();
        }
    }
}
