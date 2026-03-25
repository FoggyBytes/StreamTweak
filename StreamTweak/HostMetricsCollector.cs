using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StreamTweak
{
    /// <summary>
    /// Snapshot of host metrics sampled from Windows PDH performance counters
    /// and, where available, vendor-specific GPU APIs.
    /// All values are -1 when the metric is unavailable.
    /// </summary>
    public struct HostMetricsSample
    {
        public int Gpu;        // GPU 3D engine utilization %
        public int GpuEnc;     // GPU VideoEncode engine utilization %
        public int GpuTemp;    // GPU temperature °C  (NVIDIA only via NVML; -1 on AMD/Intel)
        public int VramUsedMb;  // Dedicated GPU memory in use, MB
        public int VramTotalMb; // Total dedicated GPU memory, MB (NVIDIA only via NVML; -1 on AMD/Intel)
        public int Cpu;         // CPU utilization %
        public int NetTxMbps;   // Network outbound throughput, Mbps

        /// <summary>Serializes to the wire format expected by StreamLight's STATS command.</summary>
        public string ToJson() =>
            $"{{\"gpu\":{Gpu},\"gpu_enc\":{GpuEnc},\"gpu_temp\":{GpuTemp}," +
            $"\"vram_used\":{VramUsedMb},\"vram_total\":{VramTotalMb},\"cpu\":{Cpu},\"net_tx\":{NetTxMbps}}}";
    }

    /// <summary>
    /// Collects real-time host metrics every second on a background thread.
    ///
    /// GPU/CPU/VRAM/Network: Windows PDH PerformanceCounters — cross-vendor.
    /// GPU temperature: NVML P/Invoke (NVIDIA only). Returns -1 on AMD/Intel.
    ///
    /// Thread-safe: GetLatestSample() may be called from any thread.
    /// </summary>
    public sealed class HostMetricsCollector : IDisposable
    {
        // How often (in ticks = seconds) to re-enumerate GPU Engine instances.
        // GPU Engine instances are per-process and change as processes start/stop.
        private const int COUNTER_REFRESH_TICKS = 60;

        private readonly object _sampleLock = new();
        private HostMetricsSample _latestSample = new()
            { Gpu = -1, GpuEnc = -1, GpuTemp = -1, VramUsedMb = -1, VramTotalMb = -1, Cpu = -1, NetTxMbps = -1 };

        private Timer? _timer;
        private int _tickCount;
        private bool _disposed;

        // PDH counters — rebuilt every COUNTER_REFRESH_TICKS seconds
        private PerformanceCounter? _cpuCounter;
        private readonly List<PerformanceCounter> _gpuEngCounters = new();
        private readonly List<PerformanceCounter> _gpuEncCounters = new();
        private readonly List<PerformanceCounter> _vramCounters   = new();
        private readonly List<PerformanceCounter> _netTxCounters  = new();

        // NVML (NVIDIA GPU temperature)
        private bool   _nvmlInitialized;
        private IntPtr _nvmlDevice;

        public HostMetricsCollector()
        {
            // Initialize asynchronously to avoid blocking app startup.
            // Metrics will be -1 for the first second or two while the
            // background thread enumerates PDH counter instances.
            _ = System.Threading.Tasks.Task.Run(Initialize);
        }

        /// <summary>Returns the most recently sampled metric snapshot (thread-safe).</summary>
        public HostMetricsSample GetLatestSample()
        {
            lock (_sampleLock) { return _latestSample; }
        }

        // ── Initialization ────────────────────────────────────────────────────

        private void Initialize()
        {
            try
            {
                InitNvml();
                RebuildAllCounters();
                // Start the 1-second timer only after the first counter build is done.
                // Use dueTime=0 so the first sample fires immediately.
                // Guard against Dispose() being called before we get here.
                if (!_disposed)
                    _timer = new Timer(OnTimerTick, null, 0, 1000);
            }
            catch { }
        }

        private void InitNvml()
        {
            try
            {
                if (NvmlNative.nvmlInit() == 0 &&
                    NvmlNative.nvmlDeviceGetHandleByIndex(0, out _nvmlDevice) == 0)
                {
                    _nvmlInitialized = true;
                    DebugLogger.Log("HostMetricsCollector: NVIDIA GPU detected via NVML");
                }
            }
            catch (DllNotFoundException) { /* nvml.dll absent — not an NVIDIA system */ }
            catch { }
        }

        // ── Timer callback ────────────────────────────────────────────────────

        private void OnTimerTick(object? _)
        {
            if (_disposed) return;
            try
            {
                if (++_tickCount % COUNTER_REFRESH_TICKS == 0)
                    RebuildAllCounters();

                var sample = new HostMetricsSample
                {
                    Gpu         = SampleGpuUsage(),
                    GpuEnc      = SampleGpuEncUsage(),
                    GpuTemp     = SampleGpuTemp(),
                    VramUsedMb  = SampleVramUsedMb(),
                    VramTotalMb = SampleVramTotalMb(),
                    Cpu         = SampleCpuUsage(),
                    NetTxMbps   = SampleNetTx(),
                };

                lock (_sampleLock) { _latestSample = sample; }
            }
            catch { }
        }

        // ── Counter construction ──────────────────────────────────────────────

        private void RebuildAllCounters()
        {
            RebuildCpuCounter();
            RebuildGpuEngineCounters();
            RebuildCounterList(_vramCounters,  "GPU Process Memory", "Dedicated Usage");
            RebuildNetTxCounters();
        }

        private void RebuildCpuCounter()
        {
            try
            {
                _cpuCounter?.Dispose();
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
                _cpuCounter.NextValue(); // prime rate counter — first call always returns 0
            }
            catch { _cpuCounter = null; }
        }

        private void RebuildGpuEngineCounters()
        {
            DisposeAndClear(_gpuEngCounters);
            DisposeAndClear(_gpuEncCounters);

            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine")) return;

                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (string inst in cat.GetInstanceNames())
                {
                    bool is3D  = inst.Contains("engtype_3D",          StringComparison.OrdinalIgnoreCase);
                    bool isEnc = inst.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase);
                    if (!is3D && !isEnc) continue;

                    try
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                        c.NextValue(); // prime
                        (is3D ? _gpuEngCounters : _gpuEncCounters).Add(c);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void RebuildCounterList(List<PerformanceCounter> list, string category, string counter)
        {
            DisposeAndClear(list);
            try
            {
                if (!PerformanceCounterCategory.Exists(category)) return;

                var cat = new PerformanceCounterCategory(category);
                foreach (string inst in cat.GetInstanceNames())
                {
                    try
                    {
                        var c = new PerformanceCounter(category, counter, inst, readOnly: true);
                        c.NextValue(); // prime rate counters (no-op for raw counters)
                        list.Add(c);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void RebuildNetTxCounters()
        {
            DisposeAndClear(_netTxCounters);
            try
            {
                if (!PerformanceCounterCategory.Exists("Network Interface")) return;

                var cat = new PerformanceCounterCategory("Network Interface");
                foreach (string inst in cat.GetInstanceNames())
                {
                    if (inst.Contains("Loopback",  StringComparison.OrdinalIgnoreCase) ||
                        inst.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var c = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, readOnly: true);
                        c.NextValue(); // prime
                        _netTxCounters.Add(c);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ── Sampling ──────────────────────────────────────────────────────────

        private int SampleGpuUsage()
        {
            if (_gpuEngCounters.Count == 0) return -1;
            float v = SumCounters(_gpuEngCounters);
            return Math.Clamp((int)Math.Round(v), 0, 100);
        }

        private int SampleGpuEncUsage()
        {
            if (_gpuEncCounters.Count == 0) return -1;
            float v = SumCounters(_gpuEncCounters);
            return Math.Clamp((int)Math.Round(v), 0, 100);
        }

        private int SampleGpuTemp()
        {
            if (!_nvmlInitialized) return -1;
            try
            {
                if (NvmlNative.nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint temp) == 0)
                    return (int)temp;
            }
            catch { }
            return -1;
        }

        private int SampleVramUsedMb()
        {
            if (_vramCounters.Count == 0) return -1;
            // Use RawValue (int64) to avoid float precision loss on large VRAM values.
            long totalBytes = SumCountersRaw(_vramCounters);
            return (int)(totalBytes / (1024 * 1024));
        }

        private int SampleCpuUsage()
        {
            if (_cpuCounter == null) return -1;
            try { return Math.Clamp((int)Math.Round(_cpuCounter.NextValue()), 0, 100); }
            catch { _cpuCounter = null; return -1; }
        }

        private int SampleVramTotalMb()
        {
            if (!_nvmlInitialized) return -1;
            try
            {
                if (NvmlNative.nvmlDeviceGetMemoryInfo(_nvmlDevice, out var mem) == 0)
                    return (int)(mem.Total / (1024UL * 1024UL));
            }
            catch { }
            return -1;
        }

        private int SampleNetTx()
        {
            if (_netTxCounters.Count == 0) return -1;
            float bytesPerSec = SumCounters(_netTxCounters);
            return (int)Math.Round(bytesPerSec * 8f / 1_000_000f);
        }

        // ── Counter helpers ───────────────────────────────────────────────────

        /// <summary>Calls NextValue() on each counter, sums results, removes dead counters.</summary>
        private static float SumCounters(List<PerformanceCounter> counters)
        {
            float total = 0f;
            var dead = new List<PerformanceCounter>();

            foreach (var c in counters)
            {
                try { total += c.NextValue(); }
                catch { dead.Add(c); }
            }

            foreach (var c in dead)
            {
                try { c.Dispose(); } catch { }
                counters.Remove(c);
            }

            return total;
        }

        /// <summary>
        /// Returns the sum of RawValue (int64) across all counters.
        /// Suitable for instantaneous-value counters such as GPU Process Memory\Dedicated Usage.
        /// </summary>
        private static long SumCountersRaw(List<PerformanceCounter> counters)
        {
            long total = 0;
            var dead = new List<PerformanceCounter>();

            foreach (var c in counters)
            {
                try { total += c.NextSample().RawValue; }
                catch { dead.Add(c); }
            }

            foreach (var c in dead)
            {
                try { c.Dispose(); } catch { }
                counters.Remove(c);
            }

            return total;
        }

        private static void DisposeAndClear(List<PerformanceCounter> list)
        {
            foreach (var c in list) { try { c.Dispose(); } catch { } }
            list.Clear();
        }

        // ── NVML P/Invoke ─────────────────────────────────────────────────────

        private static class NvmlNative
        {
            // nvml.dll ships with all NVIDIA display drivers (found via PATH or System32).
            private const string DLL = "nvml.dll";

            [StructLayout(LayoutKind.Sequential)]
            public struct NvmlMemory
            {
                public ulong Total; // Total installed FB memory (bytes)
                public ulong Free;  // Unallocated FB memory (bytes)
                public ulong Used;  // Allocated FB memory (bytes)
            }

            [DllImport(DLL, EntryPoint = "nvmlInit_v2",                  ExactSpelling = true)] public static extern uint nvmlInit();
            [DllImport(DLL, EntryPoint = "nvmlShutdown",                 ExactSpelling = true)] public static extern uint nvmlShutdown();
            [DllImport(DLL, EntryPoint = "nvmlDeviceGetHandleByIndex_v2",ExactSpelling = true)] public static extern uint nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);
            [DllImport(DLL, EntryPoint = "nvmlDeviceGetTemperature",     ExactSpelling = true)] public static extern uint nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
            [DllImport(DLL, EntryPoint = "nvmlDeviceGetMemoryInfo",      ExactSpelling = true)] public static extern uint nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
            // sensorType = 0 → NVML_TEMPERATURE_GPU
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();
            _cpuCounter?.Dispose();
            DisposeAndClear(_gpuEngCounters);
            DisposeAndClear(_gpuEncCounters);
            DisposeAndClear(_vramCounters);
            DisposeAndClear(_netTxCounters);

            if (_nvmlInitialized)
            {
                try { NvmlNative.nvmlShutdown(); } catch { }
            }
        }
    }
}
