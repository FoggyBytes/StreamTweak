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

    public sealed class DolbyAudioMonitor
    {
        private const int WaitSeconds      = 10;
        private const int RetryIntervalSec = 5;
        private const int MaxRetryAttempts = 36; // 36 × 5 s = 3 min of retrying after the initial 10 s wait

        private CancellationTokenSource? _cts;
        private volatile bool _activatedThisSession;

        // ─── Configuration ────────────────────────────────────────────────────

        public string TargetDeviceName { get; set; } = "Steam Streaming Speakers";
        public SpatialAudioFormat SpatialFormat { get; set; } = SpatialAudioFormat.DolbyAtmos;

        public bool IsEnabled { get; private set; }

        public event Action<string>? StatusChanged;

        /// <summary>Fired after a successful auto-activation, carrying the format that was applied.</summary>
        public event Action<SpatialAudioFormat>? FormatActivated;

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

        // Verified via RunDiagnosticsAsync (Windows 10.0.26200.0, Corsair HS80 MAX):
        // Passing OffFormatGuid to SetDefaultSpatialAudioFormatAsync deactivates any active format.
        // Passing WindowsSonicGuid activates Windows Sonic for Headphones.
        // string.Empty and null are silently ignored (no-ops) by the API.
        private const string OffFormatGuid   = "{00000000-0000-0000-0000-000000000000}";
        private const string WindowsSonicGuid = "{B53D940C-B846-4831-9F76-D102B9B725A0}";

        /// <summary>
        /// Returns true when <paramref name="activeFormat"/> corresponds to Windows Sonic
        /// for Headphones being active.
        /// </summary>
        private static bool IsWindowsSonicActive(string? activeFormat)
            => string.Equals(activeFormat, WindowsSonicGuid, StringComparison.OrdinalIgnoreCase);

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
                    System.Diagnostics.Debug.WriteLine($"[Audio] TryEnable Sonic: SetDefaultSpatialAudioFormatAsync({WindowsSonicGuid})");
                    await config.SetDefaultSpatialAudioFormatAsync(WindowsSonicGuid);
                    string? activeAfter = config.ActiveSpatialAudioFormat;
                    System.Diagnostics.Debug.WriteLine($"[Audio] TryEnable Sonic after: '{activeAfter ?? "null"}'");
                    if (IsWindowsSonicActive(activeAfter))
                    {
                        _activatedThisSession = true;
                        FormatActivated?.Invoke(SpatialAudioFormat.WindowsSonic);
                        NotifyStatus("✓ Windows Sonic for Headphones enabled.");
                    }
                    else
                    {
                        NotifyStatus($"Windows Sonic not confirmed by Windows (active: '{activeAfter ?? "null"}').");
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
                        FormatActivated?.Invoke(SpatialAudioFormat.DolbyAtmos);
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

        /// <summary>
        /// Returns the display name of the spatial audio format currently active on the named
        /// device: "Dolby Atmos for Headphones", "Windows Sonic for Headphones", or null when
        /// the device is not found, spatial audio is unsupported, or an error occurs.
        /// </summary>
        public static async Task<string?> GetActiveFormatNameAsync(string deviceName)
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                var device = devices.FirstOrDefault(d =>
                    d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
                if (device == null) return null;

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(device.Id);
                if (!config.IsSpatialAudioSupported) return null;

                string? active = config.ActiveSpatialAudioFormat;
                System.Diagnostics.Debug.WriteLine($"[Audio] GetActiveFormat: raw='{active ?? "null"}'");

                if (string.Equals(active, SpatialAudioFormatSubtype.DolbyAtmosForHeadphones,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine("[Audio] GetActiveFormat: Dolby Atmos");
                    return "Dolby Atmos for Headphones";
                }
                if (IsWindowsSonicActive(active))
                {
                    System.Diagnostics.Debug.WriteLine("[Audio] GetActiveFormat: Windows Sonic");
                    return "Windows Sonic for Headphones";
                }
                System.Diagnostics.Debug.WriteLine($"[Audio] GetActiveFormat: unrecognized GUID '{active}' — treating as inactive");
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns true when <paramref name="format"/> is the currently active spatial audio
        /// format on the named device.
        /// </summary>
        public static async Task<bool> IsFormatActiveAsync(string deviceName, SpatialAudioFormat format)
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                var device = devices.FirstOrDefault(d =>
                    d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
                if (device == null) return false;

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(device.Id);
                if (!config.IsSpatialAudioSupported) return false;

                string? active = config.ActiveSpatialAudioFormat;
                return format == SpatialAudioFormat.DolbyAtmos
                    ? string.Equals(active, SpatialAudioFormatSubtype.DolbyAtmosForHeadphones,
                                    StringComparison.OrdinalIgnoreCase)
                    : IsWindowsSonicActive(active);
            }
            catch { return false; }
        }

        /// <summary>
        /// Immediately activates <paramref name="format"/> on <paramref name="deviceName"/>.
        /// Completely independent from IsEnabled and the streaming event system.
        /// Returns empty string on success, or a human-readable error message on failure.
        /// </summary>
        public static async Task<string> ActivateFormatAsync(string deviceName, SpatialAudioFormat format)
        {
            try
            {
                string? deviceId = await FindDeviceIdByNameAsync(deviceName);
                if (deviceId == null)
                    return $"Device '{deviceName}' not found.";

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(deviceId);

                if (!config.IsSpatialAudioSupported)
                    return "Spatial audio not supported on this device.";

                if (format == SpatialAudioFormat.WindowsSonic)
                {
                    System.Diagnostics.Debug.WriteLine($"[Audio] ActivateFormat Sonic: SetDefaultSpatialAudioFormatAsync({WindowsSonicGuid})");
                    await config.SetDefaultSpatialAudioFormatAsync(WindowsSonicGuid);
                    string? after = config.ActiveSpatialAudioFormat;
                    System.Diagnostics.Debug.WriteLine($"[Audio] ActivateFormat Sonic after: '{after ?? "null"}'");
                    if (!IsWindowsSonicActive(after))
                        return $"Windows Sonic not activated (active: '{after ?? "null"}').";
                }
                else
                {
                    string dolbyFormat = SpatialAudioFormatSubtype.DolbyAtmosForHeadphones;
                    if (!config.IsSpatialAudioFormatSupported(dolbyFormat))
                        return "Dolby Atmos for Headphones not available (Dolby Access not installed).";

                    System.Diagnostics.Debug.WriteLine($"[Audio] ActivateFormat Dolby: SetDefaultSpatialAudioFormatAsync({dolbyFormat})");
                    await config.SetDefaultSpatialAudioFormatAsync(dolbyFormat);
                    string? after = config.ActiveSpatialAudioFormat;
                    System.Diagnostics.Debug.WriteLine($"[Audio] ActivateFormat Dolby after: '{after ?? "null"}'");
                    if (!string.Equals(after, dolbyFormat, StringComparison.OrdinalIgnoreCase))
                        return $"Dolby not confirmed by Windows (active: '{after ?? "null"}').";
                }

                return string.Empty; // success
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Immediately activates the configured spatial format on the target device.
        /// Completely independent from IsEnabled and the streaming event system.
        /// Returns empty string on success, or an error message on failure.
        /// Must be called from a background thread (same context as TryEnableSpatialAudioAsync).
        /// </summary>
        public async Task<string> ForceActivateAsync()
        {
            try
            {
                string? deviceId = await FindDeviceIdByNameAsync(TargetDeviceName);
                if (deviceId == null)
                    return $"Device '{TargetDeviceName}' not found.";

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(deviceId);

                if (!config.IsSpatialAudioSupported)
                    return "Spatial audio not supported on this device.";

                if (SpatialFormat == SpatialAudioFormat.WindowsSonic)
                {
                    System.Diagnostics.Debug.WriteLine($"[Audio] ForceActivate Sonic: SetDefaultSpatialAudioFormatAsync({WindowsSonicGuid})");
                    await config.SetDefaultSpatialAudioFormatAsync(WindowsSonicGuid);
                    string? after = config.ActiveSpatialAudioFormat;
                    System.Diagnostics.Debug.WriteLine($"[Audio] ForceActivate Sonic after: '{after ?? "null"}'");
                    if (!IsWindowsSonicActive(after))
                        return $"Windows Sonic not confirmed by Windows (active: '{after ?? "null"}').";
                }
                else
                {
                    string dolbyFormat = SpatialAudioFormatSubtype.DolbyAtmosForHeadphones;
                    if (!config.IsSpatialAudioFormatSupported(dolbyFormat))
                        return "Dolby Atmos for Headphones not available (Dolby Access not installed).";

                    await config.SetDefaultSpatialAudioFormatAsync(dolbyFormat);
                    string? after = config.ActiveSpatialAudioFormat;
                    if (!string.Equals(after, dolbyFormat, StringComparison.OrdinalIgnoreCase))
                        return $"Dolby not confirmed by Windows (active: {(string.IsNullOrEmpty(after) ? "none" : after)}).";
                }

                _activatedThisSession = true;
                return string.Empty; // success
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Immediately deactivates the spatial audio format on the target device,
        /// resetting to the OS default (Windows Sonic / Off).
        /// Uses the same code path as activation so threading context matches.
        /// Returns empty string on success, or an error message on failure.
        /// </summary>
        public async Task<string> ForceDeactivateAsync()
        {
            try
            {
                string? deviceId = await FindDeviceIdByNameAsync(TargetDeviceName);
                if (deviceId == null)
                    return $"Device '{TargetDeviceName}' not found.";

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(deviceId);

                if (!config.IsSpatialAudioSupported)
                    return "Spatial audio not supported on this device.";

                string? activeBefore = config.ActiveSpatialAudioFormat;
                System.Diagnostics.Debug.WriteLine($"[Audio] ForceDeactivate before: '{activeBefore ?? "null"}'");

                System.Diagnostics.Debug.WriteLine($"[Audio] ForceDeactivate: SetDefaultSpatialAudioFormatAsync({OffFormatGuid})");
                await config.SetDefaultSpatialAudioFormatAsync(OffFormatGuid);
                string? activeAfter = config.ActiveSpatialAudioFormat;
                System.Diagnostics.Debug.WriteLine($"[Audio] ForceDeactivate after: '{activeAfter ?? "null"}'");

                if (!string.Equals(activeAfter, OffFormatGuid, StringComparison.OrdinalIgnoreCase))
                    return $"Deactivation not confirmed by Windows (still active: '{activeAfter ?? "null"}').";

                _activatedThisSession = false;
                return string.Empty; // success
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Resets the spatial audio format on the named device to the OS default.
        /// Returns null on success, or an error message string on failure.
        /// </summary>
        public static async Task<string?> DeactivateSpatialAudioAsync(string deviceName)
        {
            try
            {
                string selector = MediaDevice.GetAudioRenderSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);
                var device = devices.FirstOrDefault(d =>
                    d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
                if (device == null) return $"Device '{deviceName}' not found.";

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(device.Id);
                if (!config.IsSpatialAudioSupported) return "Spatial audio not supported on this device.";

                string? activeBefore = config.ActiveSpatialAudioFormat;
                System.Diagnostics.Debug.WriteLine($"[Audio] DeactivateSpatialAudio before: '{activeBefore ?? "null"}'");

                System.Diagnostics.Debug.WriteLine($"[Audio] DeactivateSpatialAudio: SetDefaultSpatialAudioFormatAsync({OffFormatGuid})");
                await config.SetDefaultSpatialAudioFormatAsync(OffFormatGuid);

                string? activeAfter = config.ActiveSpatialAudioFormat;
                System.Diagnostics.Debug.WriteLine($"[Audio] DeactivateSpatialAudio after: '{activeAfter ?? "null"}'");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private void NotifyStatus(string message) => StatusChanged?.Invoke(message);
    }
}
