using StreamTweak.Services;

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
            // Our own Shell_NotifyIcon wrapper since 9.2.4 (Services/TrayIcon.cs), in place of
            // H.NotifyIcon. The menu is a native Win32 HMENU, as it already was in H.NotifyIcon's
            // PopupMenu mode since 8.1.2 — no XAML popup window, so none of the DPI sizing
            // trouble that once forced SecondWindow mode.
            try
            {
                _trayIcon = new TrayIcon(
                    toolTip: "StreamTweak",
                    onDoubleClick: ShowMainWindow,
                    menu:
                    [
                        new TrayIcon.MenuItem("Open StreamTweak", () => { ShowMainWindow(); MainWindow?.NavigateTo("Status"); }),
                        null,
                        new TrayIcon.MenuItem("Exit", ExitApp),
                    ]);
            }
            catch (Exception ex)
            {
                // No tray is a poorer app, not a broken one: every use of _trayIcon is null-guarded.
                DebugLogger.Log($"[Tray] not created: {ex.Message}");
                return;
            }

            // Fill the tooltip immediately so a hover before the first tick is never blank,
            // then keep it fresh on a low-frequency timer.
            RefreshTrayTooltip();
            StartTraySpeedTimer();

            // The icon is loaded from its .ico as a native HICON — the only reliable path in
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
    }
}
