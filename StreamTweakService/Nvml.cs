using System.Runtime.InteropServices;

namespace StreamTweakService;

/// <summary>
/// The slice of NVIDIA's NVML that the GPU power readings need (read-only), bound at run time.
///
/// nvml.dll ships with every NVIDIA display driver: System32 first, the NVSMI folder of
/// older drivers second. It is loaded by full path and its entry points are bound one by
/// one, so a machine without it (AMD, Intel, no driver) simply reports <see cref="Load"/>
/// false — there is no DllImport that could throw at first call.
///
/// ⚠️ NVML runs in-process and can fault while the driver is in the middle of a TDR reset
/// (the crash Vibeshine hit; StreamTweak's host metrics moved to D3DKMT for the same reason
/// in 7.4.0). That is why the power readings are taken here and not in the UI process —
/// which keeps NVML only as the temperature fallback it never calls once D3DKMT works — and
/// why the installer registers the service to restart itself: a crash here costs a few
/// seconds of readings. All power values are milliwatts.
/// </summary>
internal static class Nvml
{
    public const int SUCCESS              = 0;
    public const int ERROR_UNINITIALIZED  = 1;
    public const int ERROR_DRIVER_NOT_LOADED = 9;
    public const int ERROR_GPU_IS_LOST    = 15;
    public const int ERROR_UNKNOWN        = 999;

    /// <summary>nvmlClocksEventReasonSwPowerCap: the driver is holding the clocks down to stay
    /// inside the power limit — the GPU is power-limited right now (9.2.0).</summary>
    public const ulong CLOCKS_EVENT_SW_POWER_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetCountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetHandleByIndexFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetUIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetMemoryFn(IntPtr device, out Memory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetULongFn(IntPtr device, out ulong value);

    public static InitFn               Init               = null!;
    public static ShutdownFn           Shutdown           = null!;
    public static GetCountFn           DeviceGetCount     = null!;
    public static GetHandleByIndexFn   DeviceGetHandleByIndex = null!;
    public static GetUIntFn            DeviceGetEnforcedPowerLimit    = null!;
    public static GetUIntFn            DeviceGetPowerUsage            = null!;
    public static GetMemoryFn          DeviceGetMemoryInfo            = null!;

    /// <summary>Why the clocks are below their maximum (bit mask). Optional: null on an NVML
    /// that has neither name — the power-cap flag then reads as unknown.</summary>
    public static GetULongFn?          DeviceGetClocksEventReasons;

    private static readonly object _loadLock = new();
    private static bool _loadTried;
    private static bool _loaded;

    /// <summary>Binds nvml.dll once. False on a machine without it, or with an NVML too old to
    /// have every function below — the feature is then simply absent.</summary>
    public static bool Load()
    {
        lock (_loadLock)
        {
            if (_loadTried) return _loaded;
            _loadTried = true;

            IntPtr lib = IntPtr.Zero;
            foreach (string path in Candidates())
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out lib)) break;
            if (lib == IntPtr.Zero) return false;

            try
            {
                Init                   = Bind<InitFn>(lib, "nvmlInit_v2");
                Shutdown               = Bind<ShutdownFn>(lib, "nvmlShutdown");
                DeviceGetCount         = Bind<GetCountFn>(lib, "nvmlDeviceGetCount_v2");
                DeviceGetHandleByIndex = Bind<GetHandleByIndexFn>(lib, "nvmlDeviceGetHandleByIndex_v2");
                DeviceGetEnforcedPowerLimit    = Bind<GetUIntFn>(lib, "nvmlDeviceGetEnforcedPowerLimit");
                DeviceGetPowerUsage            = Bind<GetUIntFn>(lib, "nvmlDeviceGetPowerUsage");
                DeviceGetMemoryInfo            = Bind<GetMemoryFn>(lib, "nvmlDeviceGetMemoryInfo");
                // Renamed from "ThrottleReasons" in newer NVML; the old name is still exported.
                DeviceGetClocksEventReasons    = TryBind<GetULongFn>(lib, "nvmlDeviceGetCurrentClocksEventReasons")
                                              ?? TryBind<GetULongFn>(lib, "nvmlDeviceGetCurrentClocksThrottleReasons");
                _loaded = true;
            }
            catch (EntryPointNotFoundException)
            {
                _loaded = false;
            }
            return _loaded;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvml.dll");
    }

    private static T Bind<T>(IntPtr lib, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    private static T? TryBind<T>(IntPtr lib, string name) where T : Delegate
        => NativeLibrary.TryGetExport(lib, name, out IntPtr fn) ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;

    /// <summary>Errors after which the handles are worthless: the driver was reloaded,
    /// reinstalled or reset. NVML has to be shut down and initialised again.</summary>
    public static bool NeedsReinit(int result)
        => result is ERROR_UNINITIALIZED or ERROR_DRIVER_NOT_LOADED or ERROR_GPU_IS_LOST or ERROR_UNKNOWN;
}
