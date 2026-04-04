using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace StreamTweak
{
    public enum SpatialAudioFormat { DolbyAtmos, WindowsSonic }

    internal sealed class DolbyAudioMonitor
    {
        private const int WaitSeconds      = 30;
        private const int RetryIntervalSec = 5;
        private const int MaxRetryAttempts = 36; // 36 × 5 s = 3 min of retrying after the initial 30 s wait

        private CancellationTokenSource? _cts;
        private bool _activatedThisSession;

        // ─── Configuration ────────────────────────────────────────────────────

        public string TargetDeviceName { get; set; } = "Steam Streaming Speakers";
        public SpatialAudioFormat SpatialFormat { get; set; } = SpatialAudioFormat.DolbyAtmos;

        public bool IsEnabled { get; private set; }

        public event Action<string>? StatusChanged;

        // ─── Lifecycle ────────────────────────────────────────────────────────

        public void Enable()
        {
            if (IsEnabled) return;
            IsEnabled = true;
            NotifyStatus("Ready — waiting for next stream…");
        }

        public void Disable()
        {
            if (!IsEnabled) return;
            IsEnabled = false;
            CancelPending();
            NotifyStatus("Disabled.");
        }

        // ─── Streaming events (called by App.xaml.cs) ─────────────────────────

        public void OnStreamingStarted(bool isRetrospective = false)
        {
            if (!IsEnabled || _activatedThisSession) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            int waitSeconds = isRetrospective ? 5 : WaitSeconds;
            _ = Task.Run(() => DelayedEnableSpatialAudioAsync(_cts.Token, waitSeconds));
            NotifyStatus(isRetrospective
                ? $"Active session detected — activating in {waitSeconds}s…"
                : $"Stream detected — waiting {waitSeconds}s…");
        }

        public void OnStreamingStopped()
        {
            CancelPending();
            _activatedThisSession = false;
            if (IsEnabled)
                NotifyStatus("Ready — waiting for next stream…");
        }

        private void CancelPending()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        // ─── Delayed activation ───────────────────────────────────────────────

        private async Task DelayedEnableSpatialAudioAsync(CancellationToken token, int waitSeconds = WaitSeconds)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token);
                if (token.IsCancellationRequested) return;

                // Retry loop — handles the case where the Windows Audio service is still
                // initializing when StreamTweak starts (e.g. early auto-login).
                // Only "device not found" is retried; permanent failures exit immediately.
                for (int attempt = 1; !token.IsCancellationRequested; attempt++)
                {
                    string? deviceId = await FindDeviceIdByNameAsync(TargetDeviceName);

                    if (deviceId != null)
                    {
                        await TryEnableSpatialAudioAsync(deviceId);
                        return;
                    }

                    if (attempt >= MaxRetryAttempts)
                    {
                        NotifyStatus($"Audio device '{TargetDeviceName}' not found.");
                        return;
                    }

                    NotifyStatus($"Audio not ready — retrying in {RetryIntervalSec}s… ({attempt}/{MaxRetryAttempts})");
                    await Task.Delay(TimeSpan.FromSeconds(RetryIntervalSec), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { NotifyStatus($"Audio error: {ex.Message}"); }
        }

        // ─── Device discovery ─────────────────────────────────────────────────

        private static async Task<string?> FindDeviceIdByNameAsync(string deviceName)
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                var match = devices.FirstOrDefault(d =>
                    d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
                return match?.Id;
            }
            catch { return null; }
        }

        // ─── Spatial audio activation ─────────────────────────────────────────

        // Windows Sonic for Headphones is Microsoft's built-in format and has no constant
        // in SpatialAudioFormatSubtype (which only lists third-party formats).
        // Windows Sonic is active when ActiveSpatialAudioFormat is empty/null.
        // Switching back to Windows Sonic is achieved by passing string.Empty to
        // SetDefaultSpatialAudioFormatAsync, which resets to the OS default.

        private async Task TryEnableSpatialAudioAsync(string deviceId)
        {
            try
            {
                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(deviceId);

                if (!config.IsSpatialAudioSupported)
                {
                    NotifyStatus("Spatial audio not supported on the selected output device.");
                    return;
                }

                if (SpatialFormat == SpatialAudioFormat.WindowsSonic)
                {
                    // Reset to OS default (Windows Sonic) by clearing the active format
                    await config.SetDefaultSpatialAudioFormatAsync(string.Empty);
                    string? activeAfter = config.ActiveSpatialAudioFormat;
                    if (string.IsNullOrEmpty(activeAfter))
                    {
                        _activatedThisSession = true;
                        NotifyStatus("✓ Windows Sonic for Headphones enabled.");
                    }
                    else
                    {
                        NotifyStatus($"Windows Sonic not confirmed by Windows (active: {activeAfter}).");
                    }
                }
                else
                {
                    string dolbyFormat = SpatialAudioFormatSubtype.DolbyAtmosForHeadphones;

                    if (!config.IsSpatialAudioFormatSupported(dolbyFormat))
                    {
                        NotifyStatus("Dolby Atmos for Headphones not available (Dolby Access not installed).");
                        return;
                    }

                    await config.SetDefaultSpatialAudioFormatAsync(dolbyFormat);
                    string? activeAfter = config.ActiveSpatialAudioFormat;
                    if (string.Equals(activeAfter, dolbyFormat, StringComparison.OrdinalIgnoreCase))
                    {
                        _activatedThisSession = true;
                        NotifyStatus("✓ Dolby Atmos for Headphones enabled.");
                    }
                    else
                    {
                        NotifyStatus($"Dolby not confirmed by Windows (active: {(string.IsNullOrEmpty(activeAfter) ? "none" : activeAfter)}).");
                    }
                }
            }
            catch (Exception ex)
            {
                NotifyStatus($"Failed: {ex.Message}");
            }
        }

        // ─── Static query methods ─────────────────────────────────────────────

        public static async Task<List<string>> GetAudioOutputDevicesAsync()
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                return devices.Select(d => d.Name).ToList();
            }
            catch { return new List<string>(); }
        }

        public static async Task<(bool dolby, bool sonic)> GetSpatialAudioCapabilitiesAsync(string deviceName)
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                var device = devices.FirstOrDefault(d =>
                    d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));

                if (device == null) return (false, false);

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(device.Id);
                if (!config.IsSpatialAudioSupported) return (false, false);

                bool dolby = config.IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.DolbyAtmosForHeadphones);
                // Windows Sonic is Microsoft's built-in format: available whenever spatial audio is supported
                bool sonic = config.IsSpatialAudioSupported;
                return (dolby, sonic);
            }
            catch { return (false, false); }
        }

        private void NotifyStatus(string message) => StatusChanged?.Invoke(message);
    }
}
