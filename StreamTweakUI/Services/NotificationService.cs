namespace StreamTweak.Services
{
    /// <summary>
    /// Thin wrapper around ToastHelper that resolves the icon path for the
    /// WinUI 3 unpackaged deployment context (AppContext.BaseDirectory).
    ///
    /// Call Initialize() once in App.OnLaunched before any Show() calls.
    /// All Show() calls are fire-and-forget; failures are swallowed silently.
    /// </summary>
    public static class NotificationService
    {
        private static bool _initialized;

        /// <summary>
        /// Registers the AUMID and icon so Windows can route toast notifications.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Resolve the .ico file relative to the exe deployment directory.
            // In dev builds this is the bin/Debug or bin/Release output folder;
            // in production it is the InnoSetup install directory.
            string iconPath = Path.Combine(
                AppContext.BaseDirectory, "Resources", "streamtweak.ico");

            ToastHelper.Initialize("StreamTweak", iconPath);
        }

        // ── Toast presets matching the WPF version's call sites ──────────────

        public static void ShowStreamingDetected(string adapterName, string speed)
            => ToastHelper.Show(
                "Streaming Detected",
                $"{adapterName} set to {speed}. Reconnect within 30 seconds.");

        public static void ShowSpeedApplied(string adapterName, string speed)
            => ToastHelper.Show(
                "Speed Applied",
                $"{adapterName} is now at {speed}.");

        public static void ShowStreamingEnded(bool connectionLost = false)
            => ToastHelper.Show(
                "Streaming Ended",
                connectionLost
                    ? "Connection lost. Network speed restored."
                    : "Network speed restored to original.");

        public static void ShowBridgeReady()
            => ToastHelper.Show(
                "StreamTweak Ready",
                "Network set to 1 Gbps. Connect within 30 seconds or speed will be restored.");

        public static void ShowDolbyEnabled(string deviceName)
            => ToastHelper.Show(
                "Spatial Audio Active",
                $"Enabled on \"{deviceName}\".");

        public static void ShowDolbyDisabled()
            => ToastHelper.Show(
                "Spatial Audio Restored",
                "Original audio format restored.");

        /// <summary>Generic toast for ad-hoc notifications.</summary>
        public static void Show(string title, string message, string? attribution = null)
            => ToastHelper.Show(title, message, attribution);
    }
}
