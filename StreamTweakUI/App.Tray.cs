using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace StreamTweak
{
    // System-tray icon, its two-item context menu, and the tooltip.
    // Split out of App.xaml.cs. Operates on the tray fields declared in App.xaml.cs.
    //
    // The menu was cut back to Open + Exit in 8.1.2. What it used to carry:
    //   * "Speed:" / "Streaming:" status lines  -> the tooltip says both, and the icon
    //     already distinguishes the two session states.
    //   * "Restore link speed"                  -> the switch has been client-driven since
    //     8.1.0, so this was a third copy of a control the Network page offers with a proper
    //     enabled-state and a reason, and the client offers twice more.
    //   * HDR / Auto Spatial Audio toggles      -> both live on their own pages; the HDR one
    //     also went stale, since it was read once at startup and never refreshed while the
    //     streaming server toggles HDR by itself on every session.
    public partial class App
    {
        // ── Tray icon ─────────────────────────────────────────────────────────

        private void SetupTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText        = "StreamTweak",
                DoubleClickCommand = new SimpleCommand(ShowMainWindow),
                // ContextMenuMode is left at its default (PopupMenu), which turns the flyout
                // into a native Win32 HMENU. That is the point: there is no XAML popup window,
                // so none of the DPI sizing trouble that forced SecondWindow mode - and with it
                // two reflections into private H.NotifyIcon members - applies any more.
                //
                // ⚠️ PopupMenu mode runs Command and SILENTLY IGNORES Click. Every item below
                // must therefore use Command. An item that genuinely needs Click would require
                // SecondWindow mode back, and with it the first-open resize fix (see
                // ExpandTrayContextMenuWindow in the history before 8.1.2).
            };

            var flyout = new MenuFlyout();

            var openItem = new MenuFlyoutItem
            {
                Text    = "Open StreamTweak",
                Command = new SimpleCommand(() => { ShowMainWindow(); MainWindow?.NavigateTo("Home"); }),
            };
            flyout.Items.Add(openItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var exitItem = new MenuFlyoutItem
            {
                Text    = "Exit",
                Command = new SimpleCommand(ExitApp),
            };
            flyout.Items.Add(exitItem);

            _trayIcon.ContextFlyout = flyout;
            _trayIcon.ForceCreate(false);

            // Fill the tooltip immediately so a hover before the first tick is never blank,
            // then keep it fresh on a low-frequency timer.
            RefreshTrayTooltip();
            StartTraySpeedTimer();

            // UpdateIcon bypasses the XAML ImageSource → HICON pipeline and sets
            // the icon directly via native HICON handle — the only reliable path in
            // WinUI 3 unpackaged (SoftwareBitmapSource / BitmapImage both rendered blank).
            SetTrayIcon(sessionActive: false);
        }

        // Refreshes the tray tooltip on a low-frequency repeating timer. The NIC speed changes
        // rarely (and a stream-driven change already triggers PollForNicReconnectAsync), so a
        // few-second cadence keeps the tooltip effectively live at negligible cost.
        private void StartTraySpeedTimer()
        {
            if (_traySpeedTimer != null) return;
            _traySpeedTimer = _dispatcher.CreateTimer();
            _traySpeedTimer.Interval    = TimeSpan.FromMilliseconds(TRAY_SPEED_REFRESH_MS);
            _traySpeedTimer.IsRepeating = true;
            _traySpeedTimer.Tick += (_, _) => RefreshTrayTooltip();
            _traySpeedTimer.Start();
        }

        /// <summary>
        /// Rebuilds the whole tooltip: adapter speed plus session state. Both facts used to be
        /// non-clickable rows in the context menu; the tooltip is now their only textual home.
        /// </summary>
        private void RefreshTrayTooltip()
        {
            if (_trayIcon == null) return;

            var (mbps, connected) = GetCurrentSpeed();
            string speedText = !connected  ? "Unknown"
                : mbps <= 0                ? "Negotiating…"
                : mbps >= 1000             ? $"{mbps / 1000.0:0.##} Gbps"
                :                            $"{mbps} Mbps";

            // Windows caps a tray tooltip at 127 characters; three short lines stay well inside.
            _trayIcon.ToolTipText =
                $"StreamTweak\n{_adapterName}: {speedText}\n"
              + (_trayStreamingActive ? "Streaming: active" : "Streaming: inactive");
        }

        // Polls the NIC every second for up to 15 s after a speed change so the
        // tooltip reflects the new speed once the adapter reconnects.
        private async Task PollForNicReconnectAsync()
        {
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                var (_, connected) = GetCurrentSpeed();
                if (!connected) continue;
                _dispatcher.TryEnqueue(RefreshTrayTooltip);
                break;
            }
        }

        // ── Minimal ICommand for the tray menu and double-click ───────────────

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
