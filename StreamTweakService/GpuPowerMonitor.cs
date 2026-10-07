using StreamTweak.Nvidia;

namespace StreamTweakService;

/// <summary>
/// Reads the NVIDIA GPU's board power, the power limit in force and the driver's "held at
/// the limit" flag, for the session timeline's Host power lane (9.2.0). The temperature in
/// that lane comes from the host metrics' own D3DKMT reading, not from here. The
/// only place in StreamTweak that talks to NVML — see <see cref="Nvml"/> for why it lives in
/// the service.
///
/// Read-only by design. A first version also SET the limit (a card in NVIDIA Sentinel); it was
/// dropped on 07/10/2026 because NVIDIA App's own "Max power" slider does the same. The limit
/// read here is the one in force whoever set it — NVIDIA App, Afterburner, nvidia-smi
/// (verified by Marcello against nvidia-smi on 07/10).
///
/// The GPU is the NVIDIA one with the most memory: the rule the host metrics use to pick their
/// adapter (largest VRAM), so both describe the same card. All power figures are milliwatts.
/// </summary>
public sealed class GpuPowerMonitor
{
    private readonly object _lock = new();

    // NVML session — valid until an error says the driver went away (Nvml.NeedsReinit).
    private bool    _nvmlUp;
    private IntPtr  _device;

    /// <summary>The current readings. Cheap enough for once a second; under a lock, so NVML is
    /// never called from two pipe clients at once.</summary>
    public GpuPowerState GetState()
    {
        lock (_lock)
        {
            var st = new GpuPowerState();
            if (!EnsureDevice()) return st;

            st.Available = true;
            int r = Nvml.DeviceGetEnforcedPowerLimit(_device, out uint enforced);
            if (r == Nvml.SUCCESS) st.EnforcedMw = enforced; else Fail(r);
            if (_nvmlUp && Nvml.DeviceGetPowerUsage(_device, out uint usage) == Nvml.SUCCESS) st.PowerMw = (int)usage;
            if (_nvmlUp && Nvml.DeviceGetClocksEventReasons is { } reasons && reasons(_device, out ulong why) == Nvml.SUCCESS)
                st.PowerCapped = (why & Nvml.CLOCKS_EVENT_SW_POWER_CAP) != 0 ? 1 : 0;
            return st;
        }
    }

    private bool EnsureDevice()
    {
        if (_nvmlUp && _device != IntPtr.Zero) return true;
        if (!Nvml.Load()) return false;

        if (!_nvmlUp)
        {
            if (Nvml.Init() != Nvml.SUCCESS) return false;
            _nvmlUp = true;
        }

        _device = PickLargestGpu();
        return _device != IntPtr.Zero;
    }

    private IntPtr PickLargestGpu()
    {
        if (Nvml.DeviceGetCount(out uint count) != Nvml.SUCCESS || count == 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        ulong bestMem = 0;
        for (uint i = 0; i < count; i++)
        {
            if (Nvml.DeviceGetHandleByIndex(i, out IntPtr h) != Nvml.SUCCESS) continue;
            ulong mem = Nvml.DeviceGetMemoryInfo(h, out var m) == Nvml.SUCCESS ? m.Total : 0;
            if (best == IntPtr.Zero || mem > bestMem) { best = h; bestMem = mem; }
        }
        return best;
    }

    /// <summary>Records a failed call; after a driver reload/reset the session is torn down so
    /// the next call starts over with fresh handles.</summary>
    private void Fail(int result)
    {
        if (Nvml.NeedsReinit(result))
        {
            try { Nvml.Shutdown(); } catch { }
            _nvmlUp = false;
            _device = IntPtr.Zero;
        }
    }
}
