using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace StreamTweakService;

/// <summary>
/// "Controllers off with the PC" (10.3.0): turns the Xbox controllers on the Xbox Wireless Adapter
/// off when this PC shuts down with Fast Startup — and, if asked, when it sleeps or hibernates —
/// as a full shut down and the console already do. Both are off until the user turns them on
/// (Settings → Xbox controllers in StreamTweak).
///
/// Why here and how:
/// - The service. The tray app has no right to the driver's admin device, and by the moment that
///   matters a Fast Startup shut down has already signed every user out.
/// - The moment is the suspend. With Fast Startup services are not stopped: no preshutdown, a
///   PBT_APMSUSPEND instead (measured by StreamLight's TV service, §102.3, §105.11). A full shut
///   down needs nothing from us — the driver turns the controllers off at S5 itself.
/// - ⚠️ Not ServiceBase.OnPowerEvent. .NET's ServiceBase answers SERVICE_CONTROL_POWEREVENT at
///   once and runs OnPowerEvent on the thread pool (ServiceBase.cs, DeferredPowerEvent), so
///   Windows would not wait for us and the turn-off would race the adapter powering down.
///   PowerRegisterSuspendResumeNotification calls back synchronously: Windows waits for it.
/// - A shut down is told from a sleep the way the TV service does it: event 1074 (a shut down or
///   restart was asked for) within 3 minutes before the suspend. A restart never suspends.
/// - Never in the way: the turn-off runs on a worker and the callback waits for it at most
///   <see cref="SuspendBudgetMs"/>, then lets Windows go on whatever happened. Every error is
///   logged and swallowed.
/// </summary>
public sealed class XboxControllerPower : IHostedService, IDisposable
{
    private const int SuspendBudgetMs = 700;
    private static readonly TimeSpan ShutdownWindow = TimeSpan.FromMinutes(3);

    private readonly ILogger<XboxControllerPower> _logger;
    private readonly IXboxGipControl _gip;
    private readonly object _lock = new();

    private EventLogWatcher? _shutdownWatcher;
    private bool _watcherUp;
    private long _shutdownAskedAt;     // Environment.TickCount64 of the last 1074, 0 = none

    private IntPtr _powerHandle;
    private IntPtr _recipient;
    private DeviceNotifyCallback? _callback;   // held: the native side keeps only a pointer

    private string _lastReason = "";
    private DateTime _lastAt;
    private int _lastOff, _lastFailed;

    public XboxControllerPower(ILogger<XboxControllerPower> logger, IXboxGipControl gip)
    {
        _logger = logger;
        _gip = gip;
    }

    // ── Settings ─────────────────────────────────────────────────────────────
    // In HKLM, not in the user's config.json: they must hold with nobody signed in. Only SYSTEM
    // and administrators may write there; the tray app changes them through the pipe.

    private const string SettingsKey = @"SOFTWARE\FoggyBytes\StreamTweak";
    private const string ShutdownValue = "XboxControllersOffAtShutdown";
    private const string SleepValue = "XboxControllersOffAtSleep";

    public static bool OffAtShutdown { get => ReadSetting(ShutdownValue); set => WriteSetting(ShutdownValue, value); }
    public static bool OffAtSleep    { get => ReadSetting(SleepValue);    set => WriteSetting(SleepValue, value); }

    private static bool ReadSetting(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsKey);
            return key?.GetValue(name) is int v && v != 0;   // missing = off (the default)
        }
        catch { return false; }
    }

    private static void WriteSetting(string name, bool on)
    {
        using var key = Registry.LocalMachine.CreateSubKey(SettingsKey, writable: true);
        key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[(EventID=1074)]]");
            _shutdownWatcher = new EventLogWatcher(query);
            _shutdownWatcher.EventRecordWritten += (_, e) =>
            {
                e.EventRecord?.Dispose();
                Interlocked.Exchange(ref _shutdownAskedAt, Environment.TickCount64);
            };
            _shutdownWatcher.Enabled = true;
            _watcherUp = true;
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"[xbox] cannot watch event 1074, a suspend will count as a shut down — {ex.Message}");
            _logger.LogWarning(ex, "Xbox controllers: event 1074 watcher unavailable");
        }

        try
        {
            _callback = OnPowerNotification;
            var parameters = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                Context = IntPtr.Zero,
            };
            _recipient = Marshal.AllocHGlobal(Marshal.SizeOf<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>());
            Marshal.StructureToPtr(parameters, _recipient, false);
            uint err = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, _recipient, out _powerHandle);
            if (err != 0)
            {
                _powerHandle = IntPtr.Zero;
                ServiceLog.Write($"[xbox] suspend notifications refused (error {err}): controllers will not be turned off");
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"[xbox] suspend notifications unavailable — {ex.Message}");
            _logger.LogWarning(ex, "Xbox controllers: suspend notifications unavailable");
        }

        ServiceLog.Write($"[xbox] ready — off at shut down {OnOff(OffAtShutdown)}, off at sleep {OnOff(OffAtSleep)}, " +
                         $"adapter {(SafeAdapterPresent() ? "present" : "absent")}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { if (_powerHandle != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_powerHandle); } catch { }
        _powerHandle = IntPtr.Zero;
        if (_recipient != IntPtr.Zero) { Marshal.FreeHGlobal(_recipient); _recipient = IntPtr.Zero; }
        try { _shutdownWatcher?.Dispose(); } catch { }
        _shutdownWatcher = null;
    }

    // ── The suspend ──────────────────────────────────────────────────────────

    private uint OnPowerNotification(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type == PBT_APMSUSPEND)
                OnSuspend();
            else if (type == PBT_APMRESUMEAUTOMATIC)
                Interlocked.Exchange(ref _shutdownAskedAt, 0);   // that shut down or sleep is over
        }
        catch
        {
            // Nothing here may stop Windows from suspending.
        }
        return 0;
    }

    private void OnSuspend()
    {
        long asked = Interlocked.Read(ref _shutdownAskedAt);
        bool shutDown = !_watcherUp
                        || (asked != 0 && Environment.TickCount64 - asked < (long)ShutdownWindow.TotalMilliseconds);
        bool wanted = shutDown ? OffAtShutdown : OffAtSleep;
        string kind = shutDown ? (_watcherUp ? "Fast Startup shut down" : "suspend (kind unknown)") : "sleep or hibernation";
        if (!wanted)
        {
            if (OffAtShutdown || OffAtSleep)
                ServiceLog.Write($"[xbox] {kind}: its setting is off, controllers left on");
            return;
        }

        // TurnOffAll logs the outcome and its own time; this only caps how long Windows waits.
        Task work = Task.Run(() => TurnOffAll(kind));
        if (!work.Wait(SuspendBudgetMs))
            ServiceLog.Write($"[xbox] {kind}: still busy after {SuspendBudgetMs} ms, Windows goes on");
    }

    // ── Shared with the pipe ─────────────────────────────────────────────────

    /// <summary>Turns off every connected controller and logs the outcome. Never throws.</summary>
    public string TurnOffAll(string reason)
    {
        var watch = Stopwatch.StartNew();
        string outcome;
        int off = 0, failed = 0;
        try
        {
            IReadOnlyList<ulong> ids = _gip.ConnectedControllers();
            if (ids.Count == 0)
            {
                outcome = "no controller connected";
            }
            else
            {
                IReadOnlyList<XboxTurnOffResult> results = _gip.TurnOff(ids);
                off = results.Count(r => r.Error == 0);
                failed = results.Count - off;
                outcome = string.Join(", ", results.Select(r =>
                    $"{r.DeviceId:X16} {(r.Error == 0 ? "OK" : $"error {r.Error}")}"));
            }
        }
        catch (Exception ex)
        {
            outcome = $"failed — {ex.Message}";
            failed = 1;
        }

        ServiceLog.Write($"[xbox] {reason}: {outcome} ({watch.ElapsedMilliseconds} ms)");
        lock (_lock)
        {
            _lastReason = reason;
            _lastAt = DateTime.Now;
            _lastOff = off;
            _lastFailed = failed;
        }
        return outcome;
    }

    /// <summary>The state for StreamTweak's Settings page, as JSON.</summary>
    public string StateJson()
    {
        int connected = 0;
        try { connected = _gip.ConnectedControllers().Count; } catch { }
        string last; DateTime lastAt; int lastOff, lastFailed;
        lock (_lock) { last = _lastReason; lastAt = _lastAt; lastOff = _lastOff; lastFailed = _lastFailed; }
        return JsonSerializer.Serialize(new
        {
            offAtShutdown = OffAtShutdown,
            offAtSleep = OffAtSleep,
            adapter = SafeAdapterPresent(),
            connected,
            // The last turn-off since the service started — a Fast Startup boot keeps it, since
            // the service was only hibernated.
            last,
            lastAt = last.Length == 0 ? "" : lastAt.ToString("o"),
            lastOff,
            lastFailed,
        });
    }

    private bool SafeAdapterPresent()
    {
        try { return _gip.AdapterPresent(); } catch { return false; }
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint DEVICE_NOTIFY_CALLBACK = 2;
    private const uint PBT_APMSUSPEND = 0x0004;
    private const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);
}
