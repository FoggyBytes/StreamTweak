using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        public int GpuTemp;    // GPU temperature °C  (D3DKMT cross-vendor; NVML fallback)
        public int VramUsedMb;  // Dedicated GPU memory in use, MB
        public int VramTotalMb; // Total dedicated GPU memory, MB (D3DKMT/DXGI cross-vendor)
        public int Cpu;         // CPU utilization %
        public int NetTxMbps;   // Network outbound throughput on the primary interface, Mbps

        /// <summary>Serializes to the wire format expected by StreamLight's STATS command.</summary>
        public string ToJson() =>
            $"{{\"gpu\":{Gpu},\"gpu_enc\":{GpuEnc},\"gpu_temp\":{GpuTemp}," +
            $"\"vram_used\":{VramUsedMb},\"vram_total\":{VramTotalMb},\"cpu\":{Cpu},\"net_tx\":{NetTxMbps}}}";
    }

    /// <summary>
    /// Collects real-time host metrics every second on a background thread.
    ///
    /// GPU usage / encoder / VRAM-used / CPU: Windows PDH PerformanceCounters — cross-vendor.
    /// GPU temperature + VRAM total: D3DKMT (kernel-mediated, cross-vendor, TDR-safe);
    ///   NVML (NVIDIA only) is used only as a fallback when D3DKMT has never worked.
    /// Network TX: measured on the single interface that carries the default route,
    ///   so virtual/VPN/Tailscale adapters don't inflate the figure.
    ///
    /// Thread-safe: GetLatestSample() may be called from any thread.
    /// </summary>
    public sealed class HostMetricsCollector : IDisposable
    {
        private const int TICK_MS = 1000;

        // Sampling stops once nothing has read a sample for this long. This is a pull
        // model — GetLatestSample() is the only consumer path — so a reader arms the
        // timer and silence disarms it. With the app in the tray and no client polling
        // STATS, nothing is sampled at all.
        private const int IDLE_STOP_MS = 10_000;

        private static readonly HostMetricsSample Unavailable = new()
            { Gpu = -1, GpuEnc = -1, GpuTemp = -1, VramUsedMb = -1, VramTotalMb = -1, Cpu = -1, NetTxMbps = -1 };

        private readonly object _sampleLock = new();
        private HostMetricsSample _latestSample = Unavailable;

        // Timer lifecycle. Lock order is always _timerLock → _sampleLock, never the reverse.
        private readonly object _timerLock = new();
        private Timer? _timer;
        private volatile bool _running;      // the tick timer is armed
        private bool _initialized;           // one-time discovery finished
        private bool _wantRunning;           // a read arrived before discovery finished
        private long _lastReadTicks;
        private volatile bool _disposed;

        // GPU: one batched read of the whole PDH category per tick — see GpuCategoryReader.
        private readonly GpuCategoryReader _gpu = new();
        private PerformanceCounter? _cpuCounter;

        // D3DKMT GPU telemetry (temperature + VRAM, cross-vendor, TDR-safe)
        private readonly D3dkmtGpu _kmt = new();

        // Network: primary (default-route) interface sampler
        private readonly PrimaryNetSampler _net = new();

        // NVML (NVIDIA GPU temperature) — fallback only
        private bool   _nvmlInitialized;
        private IntPtr _nvmlDevice;

        public HostMetricsCollector()
        {
            // One-time device discovery only, off the startup thread. The sampling
            // timer stays off until somebody actually reads a sample.
            _ = System.Threading.Tasks.Task.Run(Initialize);
        }

        /// <summary>
        /// Returns the most recently sampled metric snapshot (thread-safe), and marks the
        /// collector as in demand: sampling starts here if it was idle, and keeps going for
        /// as long as reads keep arriving.
        /// </summary>
        /// <remarks>
        /// The first read after an idle period returns all -1 — the rate counters need two
        /// reads a second apart before they mean anything, and reporting the pre-idle sample
        /// would be reporting a stale figure as a live one. Consumers already treat all -1 as
        /// "unavailable" (StreamLight hides the host metrics section), so the values simply
        /// appear one tick into a session instead of instantly.
        /// </remarks>
        public HostMetricsSample GetLatestSample()
        {
            Volatile.Write(ref _lastReadTicks, Environment.TickCount64);
            EnsureRunning();
            lock (_sampleLock) { return _latestSample; }
        }

        // ── Initialization ────────────────────────────────────────────────────

        private void Initialize()
        {
            try
            {
                // D3DKMT first — it provides cross-vendor GPU temp + VRAM total and
                // is the preferred source. NVML stays as a fallback for systems
                // where D3DKMT perfdata is unavailable (pre-WDDM 2.4).
                _kmt.Discover();
                InitNvml();
                RebuildCpuCounter();
                _net.Refresh();
            }
            catch { }
            finally
            {
                lock (_timerLock)
                {
                    _initialized = true;
                    // A reader may have arrived while discovery was still running.
                    if (_wantRunning && !_disposed) StartTimerLocked();
                }
            }
        }

        // ── Start / stop on demand ────────────────────────────────────────────

        private void EnsureRunning()
        {
            if (_running || _disposed) return;
            lock (_timerLock)
            {
                if (_running || _disposed) return;
                _wantRunning = true;
                if (_initialized) StartTimerLocked();
            }
        }

        /// <summary>Arms the tick timer. Caller must hold <see cref="_timerLock"/>.</summary>
        private void StartTimerLocked()
        {
            if (_running) return;
            _running = true;
            _gpu.Reset();   // rate counters start from a fresh baseline
            _timer ??= new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
            // dueTime 0: the first tick primes the baseline, the second carries real rates.
            _timer.Change(0, TICK_MS);
        }

        /// <summary>Disarms the tick timer and drops the sample, which is now stale.</summary>
        private void Stop()
        {
            lock (_timerLock)
            {
                if (!_running) return;

                // A reader can arrive between the idle test in OnTimerTick and this lock;
                // EnsureRunning() would have seen _running still true and done nothing, so
                // its request would be lost and the metrics would blink for a tick. Re-test
                // under the lock and let the reader win.
                if (Environment.TickCount64 - Volatile.Read(ref _lastReadTicks) <= IDLE_STOP_MS)
                    return;

                _running    = false;
                _wantRunning = false;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                _gpu.Reset();
                lock (_sampleLock) { _latestSample = Unavailable; }
            }
        }

        private void InitNvml()
        {
            try
            {
                if (NvmlNative.nvmlInit() == 0 &&
                    NvmlNative.nvmlDeviceGetHandleByIndex(0, out _nvmlDevice) == 0)
                {
                    _nvmlInitialized = true;
                    DebugLogger.Log("HostMetricsCollector: NVIDIA GPU detected via NVML (fallback path)");
                }
            }
            catch (DllNotFoundException) { /* nvml.dll absent — not an NVIDIA system */ }
            catch { }
        }

        // ── Timer callback ────────────────────────────────────────────────────

        private void OnTimerTick(object? _)
        {
            if (_disposed) return;

            // Nobody has read a sample in a while — go back to sleep.
            if (Environment.TickCount64 - Volatile.Read(ref _lastReadTicks) > IDLE_STOP_MS)
            {
                Stop();
                return;
            }

            try
            {
                // One read of each PDH category, covering every instance at once.
                _gpu.Sample();

                var sample = new HostMetricsSample
                {
                    Gpu         = SampleGpuUsage(),
                    GpuEnc      = SampleGpuEncUsage(),
                    GpuTemp     = SampleGpuTemp(),
                    VramUsedMb  = SampleVramUsedMb(),
                    VramTotalMb = SampleVramTotalMb(),
                    Cpu         = SampleCpuUsage(),
                    NetTxMbps   = _net.SampleTxMbps(),
                };

                lock (_sampleLock) { _latestSample = sample; }
            }
            catch { }
        }

        // ── Counter construction ──────────────────────────────────────────────

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

        // ── Sampling ──────────────────────────────────────────────────────────

        private int SampleGpuUsage()    => _gpu.Gpu;

        private int SampleGpuEncUsage() => _gpu.GpuEnc;

        private int SampleGpuTemp()
        {
            // Primary: D3DKMT ADAPTERPERFDATA (same source as Task Manager,
            // cross-vendor, fails gracefully during a driver TDR instead of
            // faulting in-process the way NVML can).
            int temp = _kmt.GpuTempC();
            if (temp >= 0) return temp;

            // Fallback: NVML — but only on systems where D3DKMT perfdata has
            // never worked. Once D3DKMT has produced a temperature we never call
            // NVML again, because an in-process NVML call can AV while the driver
            // is mid-TDR-reset.
            if (!_kmt.PerfDataWorks && _nvmlInitialized)
            {
                try
                {
                    if (NvmlNative.nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint t) == 0)
                        return (int)t;
                }
                catch { }
            }
            return -1;
        }

        private int SampleVramUsedMb()
        {
            // Primary: PDH GPU Process Memory\Dedicated Usage — cross-vendor.
            long totalBytes = _gpu.VramUsedBytes;
            if (totalBytes > 0)
                return (int)(totalBytes / (1024 * 1024));

            // Fallback: D3DKMT segment residency (cross-vendor, TDR-safe).
            long kmtUsed = _kmt.VramUsedBytes();
            if (kmtUsed > 0)
                return (int)(kmtUsed / (1024 * 1024));

            return -1;
        }

        private int SampleVramTotalMb()
        {
            // Primary: D3DKMT / DXGI total (cross-vendor, cached at discovery).
            long total = _kmt.VramTotalBytes;
            if (total > 0)
                return (int)(total / (1024L * 1024L));

            // Fallback: NVML (NVIDIA only).
            if (!_kmt.StatisticsWork && _nvmlInitialized)
            {
                try
                {
                    if (NvmlNative.nvmlDeviceGetMemoryInfo(_nvmlDevice, out var mem) == 0)
                        return (int)(mem.Total / (1024UL * 1024UL));
                }
                catch { }
            }
            return -1;
        }

        private int SampleCpuUsage()
        {
            if (_cpuCounter == null) return -1;
            try { return Math.Clamp((int)Math.Round(_cpuCounter.NextValue()), 0, 100); }
            catch { _cpuCounter = null; return -1; }
        }

        // ── Counter helpers ───────────────────────────────────────────────────

        /// <summary>Calls NextValue() on each counter, sums results, removes dead counters.</summary>
        // ── Batched PDH reader ────────────────────────────────────────────────

        /// <summary>
        /// Reads the GPU PDH categories one whole category at a time.
        ///
        /// The obvious implementation — one <see cref="PerformanceCounter"/> per instance,
        /// <c>NextValue()</c> on each — is a trap: every single NextValue() re-reads and
        /// re-parses the *entire* category blob out of HKEY_PERFORMANCE_DATA. "GPU Engine"
        /// has one instance per process per engine type, so on an ordinary desktop that was
        /// ~240 full reads of a 600-instance category every second: ~100 ms of CPU and 33 MB
        /// of garbage per tick, and it degrades as the machine gets busier (issue #7).
        ///
        /// ReadCategory() pays for exactly one read per category and yields the same numbers
        /// — verified against the per-counter path, identical to the last decimal, at 0.7 ms
        /// per tick instead of ~100. Rates are computed with CounterSample.Calculate over the
        /// previous tick's samples, which is what NextValue() did internally anyway.
        ///
        /// Re-enumerating instances is now free (each read carries the current set), so the
        /// old 60-second counter rebuild — another 400 ms spike — is gone with it.
        /// </summary>
        private sealed class GpuCategoryReader
        {
            private Dictionary<string, CounterSample> _prev3d  = new(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, CounterSample> _prevEnc = new(StringComparer.OrdinalIgnoreCase);
            private bool _engineUnavailable;
            private bool _memoryUnavailable;

            /// <summary>3D engine utilization %, or -1 when unavailable.</summary>
            public int  Gpu           { get; private set; } = -1;
            /// <summary>VideoEncode engine utilization %, or -1 when unavailable.</summary>
            public int  GpuEnc        { get; private set; } = -1;
            /// <summary>Dedicated GPU memory in use (bytes), or -1 when unavailable.</summary>
            public long VramUsedBytes { get; private set; } = -1;

            /// <summary>Drops the rate baseline and the last values. Called when sampling stops
            /// or restarts, so a resumed collector never reports a rate spanning the gap.</summary>
            public void Reset()
            {
                _prev3d.Clear();
                _prevEnc.Clear();
                Gpu = GpuEnc = -1;
                VramUsedBytes = -1;
                // Give a category that was missing (or erroring) another chance on resume.
                _engineUnavailable = false;
                _memoryUnavailable = false;
            }

            public void Sample()
            {
                SampleEngine();
                SampleMemory();
            }

            private void SampleEngine()
            {
                if (_engineUnavailable) { Gpu = GpuEnc = -1; return; }

                try
                {
                    var util = new PerformanceCounterCategory("GPU Engine")
                        .ReadCategory()["Utilization Percentage"];
                    if (util == null) { _engineUnavailable = true; Gpu = GpuEnc = -1; return; }

                    var next3d  = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
                    var nextEnc = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
                    float sum3d = 0f, sumEnc = 0f;

                    foreach (System.Collections.DictionaryEntry entry in util)
                    {
                        if (entry.Key is not string inst || entry.Value is not InstanceData data) continue;

                        bool is3D  = inst.Contains("engtype_3D",          StringComparison.OrdinalIgnoreCase);
                        bool isEnc = inst.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase);
                        if (!is3D && !isEnc) continue;

                        var sample = data.Sample;
                        var target = is3D ? next3d : nextEnc;
                        var prior  = is3D ? _prev3d : _prevEnc;
                        target[inst] = sample;

                        // An instance seen for the first time (or the first tick after a
                        // resume) has no baseline yet, so it contributes nothing this tick.
                        if (!prior.TryGetValue(inst, out var previous)) continue;

                        try
                        {
                            float v = CounterSample.Calculate(previous, sample);
                            if (is3D) sum3d += v; else sumEnc += v;
                        }
                        catch { }
                    }

                    bool hadBaseline = _prev3d.Count > 0 || _prevEnc.Count > 0;
                    _prev3d  = next3d;
                    _prevEnc = nextEnc;

                    if (!hadBaseline) { Gpu = GpuEnc = -1; return; }   // priming tick

                    Gpu    = next3d.Count  > 0 ? Math.Clamp((int)Math.Round(sum3d),  0, 100) : -1;
                    GpuEnc = nextEnc.Count > 0 ? Math.Clamp((int)Math.Round(sumEnc), 0, 100) : -1;
                }
                catch
                {
                    // No such category on this machine (or the provider is broken): stop
                    // asking until the next resume, rather than throwing every second.
                    _engineUnavailable = true;
                    Gpu = GpuEnc = -1;
                }
            }

            private void SampleMemory()
            {
                if (_memoryUnavailable) { VramUsedBytes = -1; return; }

                try
                {
                    var dedicated = new PerformanceCounterCategory("GPU Process Memory")
                        .ReadCategory()["Dedicated Usage"];
                    if (dedicated == null) { _memoryUnavailable = true; VramUsedBytes = -1; return; }

                    long total = 0;
                    foreach (System.Collections.DictionaryEntry entry in dedicated)
                        if (entry.Value is InstanceData data) total += data.RawValue;

                    VramUsedBytes = total;
                }
                catch
                {
                    _memoryUnavailable = true;
                    VramUsedBytes = -1;
                }
            }
        }

        // ── NVML P/Invoke (fallback only) ─────────────────────────────────────

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

        // ── D3DKMT GPU telemetry ──────────────────────────────────────────────

        /// <summary>
        /// Cross-vendor GPU temperature + VRAM via the D3DKMT kernel interface
        /// (the same source Windows Task Manager uses). All calls are mediated by
        /// dxgkrnl, so a hung or mid-TDR GPU returns an NTSTATUS error instead of
        /// faulting the process — unlike an in-process NVML call.
        ///
        /// The adapter LUID is discovered once via D3DKMTEnumAdapters2 (the discrete
        /// adapter is chosen by largest dedicated VRAM). After a full driver restart
        /// the LUID can go stale; repeated query failures trigger a re-enumeration.
        /// </summary>
        private sealed class D3dkmtGpu : IDisposable
        {
            private const uint QAITYPE_ADAPTERPERFDATA = 62;
            private const uint QUERYSTATISTICS_ADAPTER = 0;
            private const uint QUERYSTATISTICS_SEGMENT = 3;

            private bool   _available;
            private bool   _haveLuid;
            private LUID   _luid;
            private uint   _adapter;          // open handle for perfdata queries
            private long   _vramTotalBytes;
            private bool   _perfDataOkOnce;
            private bool   _statsOkOnce;
            private DateTime _lastReenum = DateTime.MinValue;

            public bool PerfDataWorks  => _perfDataOkOnce;
            public bool StatisticsWork => _statsOkOnce;
            public long VramTotalBytes => _vramTotalBytes;

            public void Discover()
            {
                try
                {
                    // Probe availability — EnumAdapters2 throws EntryPointNotFound on
                    // a platform without the export (won't happen on Win10 19041+).
                    if (!ReenumerateAdapter())
                        return;
                    _available = true;
                }
                catch (EntryPointNotFoundException) { _available = false; }
                catch (DllNotFoundException)        { _available = false; }
                catch { _available = false; }
            }

            /// <summary>Returns GPU temperature in °C, or -1 on failure.</summary>
            public int GpuTempC()
            {
                if (!_available || !_haveLuid) return -1;
                try
                {
                    if (!EnsureAdapter()) { OnQueryFailed(); return -1; }

                    IntPtr buf = Marshal.AllocHGlobal(0x40);
                    try
                    {
                        // Zero the buffer (PhysicalAdapterIndex = 0 for a single GPU).
                        for (int i = 0; i < 0x40; i += 8) Marshal.WriteInt64(buf, i, 0);

                        var q = new QueryAdapterInfo
                        {
                            hAdapter = _adapter,
                            Type     = QAITYPE_ADAPTERPERFDATA,
                            Data     = buf,
                            DataSize = 0x40,
                        };
                        if (D3DKMTQueryAdapterInfo(ref q) < 0)
                        {
                            // Stale handle (TDR / driver restart) — drop it so the
                            // next sample reopens, and flag for possible re-enum.
                            CloseAdapter();
                            OnQueryFailed();
                            return -1;
                        }

                        uint deciC = unchecked((uint)Marshal.ReadInt32(buf, 0x38));
                        if (deciC == 0) return -1;
                        _perfDataOkOnce = true;
                        return (int)Math.Round(deciC / 10.0);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { return -1; }
            }

            /// <summary>Returns dedicated VRAM in use (bytes), or 0 on failure.</summary>
            public long VramUsedBytes()
            {
                var (used, _) = QueryVram();
                return used;
            }

            // ── internals ──

            private (long used, long total) QueryVram()
            {
                if (!_available || !_haveLuid) return (0, 0);
                try
                {
                    var adapterQuery = new QueryStatistics
                    {
                        Type        = QUERYSTATISTICS_ADAPTER,
                        AdapterLuid = _luid,
                    };
                    if (D3DKMTQueryStatistics(ref adapterQuery) < 0) { OnQueryFailed(); return (0, 0); }

                    uint segs = Math.Min(adapterQuery.NbSegments, 16u);
                    long used = 0, total = 0;
                    for (uint s = 0; s < segs; s++)
                    {
                        var segQuery = new QueryStatistics
                        {
                            Type        = QUERYSTATISTICS_SEGMENT,
                            AdapterLuid = _luid,
                            SegmentId   = s,
                        };
                        if (D3DKMTQueryStatistics(ref segQuery) < 0) continue;
                        if (segQuery.Aperture != 0) continue; // skip shared/aperture segments
                        used  += (long)segQuery.BytesResident;
                        total += (long)segQuery.CommitLimit;
                    }
                    if (used > 0 || total > 0) _statsOkOnce = true;
                    return (used, total);
                }
                catch { return (0, 0); }
            }

            /// <summary>
            /// Enumerate adapters, pick the one with the largest dedicated VRAM,
            /// and cache its LUID + total. Returns false if no adapter was found.
            /// </summary>
            private bool ReenumerateAdapter()
            {
                var enumStruct = new EnumAdapters2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
                // First call: query the count.
                if (D3DKMTEnumAdapters2(ref enumStruct) < 0) return false;
                uint count = enumStruct.NumAdapters;
                if (count == 0) return false;

                int stride = Marshal.SizeOf<AdapterInfo>();
                IntPtr buf = Marshal.AllocHGlobal(stride * (int)count);
                try
                {
                    enumStruct.pAdapters = buf;
                    if (D3DKMTEnumAdapters2(ref enumStruct) < 0) return false;

                    bool found = false;
                    LUID bestLuid = default;
                    long bestTotal = -1;

                    for (uint i = 0; i < enumStruct.NumAdapters; i++)
                    {
                        var info = Marshal.PtrToStructure<AdapterInfo>(buf + (int)i * stride);

                        // Query this adapter's VRAM total to rank discrete vs integrated.
                        long total = QueryAdapterTotal(info.Luid);
                        if (total > bestTotal)
                        {
                            bestTotal = total;
                            bestLuid  = info.Luid;
                            found     = true;
                        }

                        // Close the handle EnumAdapters2 opened for us.
                        if (info.hAdapter != 0)
                        {
                            var close = new CloseAdapterArg { hAdapter = info.hAdapter };
                            try { D3DKMTCloseAdapter(ref close); } catch { }
                        }
                    }

                    if (!found) return false;

                    SetLuid(bestLuid);
                    if (bestTotal > 0) _vramTotalBytes = bestTotal;
                    return true;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            private long QueryAdapterTotal(LUID luid)
            {
                try
                {
                    var adapterQuery = new QueryStatistics { Type = QUERYSTATISTICS_ADAPTER, AdapterLuid = luid };
                    if (D3DKMTQueryStatistics(ref adapterQuery) < 0) return 0;
                    uint segs = Math.Min(adapterQuery.NbSegments, 16u);
                    long total = 0;
                    for (uint s = 0; s < segs; s++)
                    {
                        var segQuery = new QueryStatistics { Type = QUERYSTATISTICS_SEGMENT, AdapterLuid = luid, SegmentId = s };
                        if (D3DKMTQueryStatistics(ref segQuery) < 0) continue;
                        if (segQuery.Aperture != 0) continue;
                        total += (long)segQuery.CommitLimit;
                    }
                    return total;
                }
                catch { return 0; }
            }

            private void SetLuid(LUID luid)
            {
                if (_haveLuid && luid.LowPart == _luid.LowPart && luid.HighPart == _luid.HighPart) return;
                CloseAdapter();
                _luid = luid;
                _haveLuid = luid.LowPart != 0 || luid.HighPart != 0;
            }

            private bool EnsureAdapter()
            {
                if (_adapter != 0) return true;
                var open = new OpenAdapterFromLuid { Luid = _luid, hAdapter = 0 };
                if (D3DKMTOpenAdapterFromLuid(ref open) < 0) return false;
                _adapter = open.hAdapter;
                return true;
            }

            private void CloseAdapter()
            {
                if (_adapter != 0)
                {
                    var close = new CloseAdapterArg { hAdapter = _adapter };
                    try { D3DKMTCloseAdapter(ref close); } catch { }
                    _adapter = 0;
                }
            }

            // A full driver restart (not just a TDR) gives the adapter a new LUID,
            // leaving every query aimed at a stale one. On repeated failures,
            // re-enumerate occasionally and re-target the fresh LUID.
            private void OnQueryFailed()
            {
                var now = DateTime.UtcNow;
                if (now - _lastReenum < TimeSpan.FromSeconds(30)) return;
                _lastReenum = now;
                try { ReenumerateAdapter(); } catch { }
            }

            public void Dispose() => CloseAdapter();

            // ── flat structs (no COM) ──

            [StructLayout(LayoutKind.Sequential)]
            private struct LUID { public uint LowPart; public int HighPart; }

            [StructLayout(LayoutKind.Sequential)]
            private struct EnumAdapters2 { public uint NumAdapters; public IntPtr pAdapters; }

            [StructLayout(LayoutKind.Sequential)]
            private struct AdapterInfo
            {
                public uint hAdapter;
                public LUID Luid;
                public uint NumOfSources;
                public int  bPrecisePresentRegionsPreferred;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct OpenAdapterFromLuid { public LUID Luid; public uint hAdapter; }

            [StructLayout(LayoutKind.Sequential)]
            private struct CloseAdapterArg { public uint hAdapter; }

            [StructLayout(LayoutKind.Sequential)]
            private struct QueryAdapterInfo
            {
                public uint   hAdapter;
                public uint   Type;
                public IntPtr Data;
                public uint   DataSize;
            }

            // D3DKMT_QUERYSTATISTICS — x64 layout pinned to the Windows SDK definition
            // (the union'd result fields overlap by design; offsets match the header).
            [StructLayout(LayoutKind.Explicit, Size = 0x328)]
            private struct QueryStatistics
            {
                [FieldOffset(0x00)] public uint  Type;
                [FieldOffset(0x08)] public LUID  AdapterLuid;
                [FieldOffset(0x10)] public IntPtr Process;
                // QUERYSTATISTICS_ADAPTER result prefix:
                [FieldOffset(0x18)] public uint  NbSegments;
                // QUERYSTATISTICS_SEGMENT result fields (overlap the adapter result):
                [FieldOffset(0x18)] public ulong CommitLimit;
                [FieldOffset(0x28)] public ulong BytesResident;
                [FieldOffset(0x40)] public uint  Aperture;
                // Input segment id:
                [FieldOffset(0x320)] public uint SegmentId;
            }

            [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 p);
            [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid p);
            [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTCloseAdapter(ref CloseAdapterArg p);
            [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo p);
            [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int D3DKMTQueryStatistics(ref QueryStatistics p);
        }

        // ── Primary-interface network sampler ─────────────────────────────────

        /// <summary>
        /// Samples outbound throughput on the single interface that carries the
        /// default route to the internet (resolved via GetBestInterfaceEx toward
        /// 8.8.8.8), so virtual switches, VPNs and Tailscale adapters never inflate
        /// the figure the way summing every interface does. The interface is
        /// re-resolved every 30 s to adapt to a VPN coming up or a cable change.
        /// </summary>
        private sealed class PrimaryNetSampler
        {
            private NetworkInterface? _iface;
            private long     _lastBytes;
            private DateTime _lastSampleUtc;
            private DateTime _lastResolveUtc = DateTime.MinValue;
            private bool     _haveBaseline;

            public void Refresh() => ResolvePrimary();

            public int SampleTxMbps()
            {
                var now = DateTime.UtcNow;
                if (_iface == null || now - _lastResolveUtc > TimeSpan.FromSeconds(30))
                    ResolvePrimary();

                if (_iface == null) { _haveBaseline = false; return -1; }

                long bytes;
                try { bytes = _iface.GetIPv4Statistics().BytesSent; }
                catch { _iface = null; _haveBaseline = false; return -1; }

                if (!_haveBaseline)
                {
                    _lastBytes     = bytes;
                    _lastSampleUtc = now;
                    _haveBaseline  = true;
                    return -1;
                }

                double secs  = (now - _lastSampleUtc).TotalSeconds;
                long   delta = bytes - _lastBytes;
                _lastBytes     = bytes;
                _lastSampleUtc = now;

                if (secs <= 0 || delta < 0) return -1; // counter wrap / clock anomaly
                double mbps = delta * 8.0 / 1_000_000.0 / secs;
                return (int)Math.Round(mbps);
            }

            private void ResolvePrimary()
            {
                _lastResolveUtc = DateTime.UtcNow;
                NetworkInterface? previous = _iface;
                _iface = null;

                try
                {
                    int bestIndex = GetDefaultRouteIfIndex();
                    var candidates = NetworkInterface.GetAllNetworkInterfaces();

                    // Prefer the interface that actually carries the default route.
                    if (bestIndex > 0)
                    {
                        foreach (var ni in candidates)
                        {
                            if (ni.OperationalStatus != OperationalStatus.Up) continue;
                            try
                            {
                                var p = ni.GetIPProperties().GetIPv4Properties();
                                if (p != null && p.Index == bestIndex) { _iface = ni; break; }
                            }
                            catch { }
                        }
                    }

                    // Fallback: first up, non-loopback/tunnel interface that has a gateway.
                    _iface ??= candidates.FirstOrDefault(ni =>
                        ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        HasGateway(ni));
                }
                catch { _iface = null; }

                // If the chosen interface changed, drop the byte baseline so the
                // next sample doesn't report a spurious spike across interfaces.
                if (!ReferenceEquals(previous, _iface))
                    _haveBaseline = false;
            }

            private static bool HasGateway(NetworkInterface ni)
            {
                try { return ni.GetIPProperties().GatewayAddresses.Count > 0; }
                catch { return false; }
            }

            private static int GetDefaultRouteIfIndex()
            {
                // sockaddr_in for 8.8.8.8 — a public destination; the chosen route is
                // the one the host would actually use to reach the internet.
                var dst = new SockAddrIn
                {
                    sin_family = (short)AddressFamily.InterNetwork,
                    sin_port   = 0,
                    sin_addr   = 0x08080808, // already network byte order (8.8.8.8)
                };
                try
                {
                    if (GetBestInterfaceEx(ref dst, out uint ifIndex) == 0)
                        return (int)ifIndex;
                }
                catch { }
                return 0;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct SockAddrIn
            {
                public short sin_family;
                public ushort sin_port;
                public uint  sin_addr;
                public ulong sin_zero; // 8 bytes padding to sizeof(sockaddr) = 16
            }

            [DllImport("iphlpapi.dll", SetLastError = false)]
            private static extern int GetBestInterfaceEx(ref SockAddrIn dest, out uint bestIfIndex);
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_timerLock)
            {
                _running     = false;
                _wantRunning = false;
                _timer?.Dispose();
                _timer = null;
            }

            _cpuCounter?.Dispose();
            _kmt.Dispose();

            if (_nvmlInitialized)
            {
                try { NvmlNative.nvmlShutdown(); } catch { }
            }
        }
    }
}
