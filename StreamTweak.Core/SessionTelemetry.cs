using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace StreamTweak
{
    // ── Enum ─────────────────────────────────────────────────────────────────

    public enum QualityGrade
    {
        NoData = 0,
        High   = 1,
        Medium = 2,
        Low    = 3
    }

    // ── DTOs (deserializzati dal payload SESSIONDATA) ─────────────────────────

    public sealed class ClientSample
    {
        [JsonPropertyName("fps_avg")]      public float FpsAvg         { get; set; }
        [JsonPropertyName("fps_min")]      public int   FpsMin         { get; set; }
        [JsonPropertyName("drops")]        public int   Drops          { get; set; }
        [JsonPropertyName("rtt_avg")]      public float RttAvg         { get; set; }
        [JsonPropertyName("rtt_max")]      public float RttMax         { get; set; }
        [JsonPropertyName("jitter_avg")]   public float JitterAvg      { get; set; }
        [JsonPropertyName("jitter_max")]   public float JitterMax      { get; set; }
        [JsonPropertyName("decode_ms")]    public float DecodeMs       { get; set; }
        [JsonPropertyName("bitrate_mbps")] public float BitrateAvgMbps { get; set; }
        // Host frame-processing latency (capture+encode) reported by the host in
        // each video frame header and surfaced by StreamLight 4.1.0+. 0 = the
        // client did not report it (older StreamLight, or no host timestamps).
        [JsonPropertyName("host_latency_avg")] public float HostLatencyAvg { get; set; }
        [JsonPropertyName("host_latency_max")] public float HostLatencyMax { get; set; }
    }

    public sealed class ClientBatch
    {
        [JsonPropertyName("session_id")]  public string             SessionId { get; set; } = "";
        [JsonPropertyName("target_fps")]  public int                TargetFps { get; set; }
        [JsonPropertyName("samples")]     public List<ClientSample> Samples   { get; set; } = new();

        /// <summary>
        /// Configured bitrate ceiling for the session, in Mbps (same unit as the per-sample
        /// bitrate). Sent by StreamLight 4.5.0+; stays 0 with older clients, which is how the
        /// UI knows to fall back to showing the delivered rate alone. Comparing the two is the
        /// point: neither side can do it on its own — the client sets the target, the host
        /// measures what actually goes out.
        /// </summary>
        [JsonPropertyName("target_bitrate_mbps")] public float TargetBitrateMbps { get; set; }
    }

    // ── Statistiche aggregate (persistite in sessions.json) ──────────────────

    public sealed class SessionQualityStats
    {
        // Client metrics
        public float FpsAvg          { get; set; }
        public int   FpsMin          { get; set; }
        public long  TotalDrops      { get; set; }
        public float DropRatePct     { get; set; }
        public float RttAvgMs        { get; set; }
        public float RttMaxMs        { get; set; }
        public float JitterAvgMs     { get; set; }
        public float JitterMaxMs     { get; set; }
        public float DecodeAvgMs     { get; set; }
        public float BitrateAvgMbps  { get; set; }

        // Host frame-processing latency (capture+encode), measured client-side from
        // the per-frame host timestamp. -1 = not reported by the client.
        public float HostLatencyAvgMs { get; set; } = -1f;
        public float HostLatencyMaxMs { get; set; } = -1f;

        // Host metrics (sampled at each batch interval, -1 = not available)
        public int   HostGpuAvg      { get; set; } = -1;
        public int   HostGpuPeak     { get; set; } = -1;
        public int   HostGpuEncAvg   { get; set; } = -1;
        public int   HostGpuEncPeak  { get; set; } = -1;
        public int   HostGpuTempAvg  { get; set; } = -1;
        public int   HostGpuTempMax  { get; set; } = -1;
        public int   HostCpuAvg      { get; set; } = -1;
        public int   HostCpuPeak     { get; set; } = -1;
        public int   HostNetTxAvg    { get; set; } = -1;

        public int   SampleCount     { get; set; }
    }

    // ── In-memory accumulator for the active session ──────────────────────────

    public sealed class TelemetryAccumulator
    {
        private readonly object _lock = new();

        // Client
        private readonly List<float> _fpsAvgSamples   = new();
        private readonly List<int>   _fpsMinSamples   = new();
        private readonly List<int>   _dropSamples     = new();
        private readonly List<float> _rttAvgSamples   = new();
        private readonly List<float> _rttMaxSamples   = new();
        private readonly List<float> _jitterSamples   = new();
        private readonly List<float> _jitterMaxSamples = new();
        private readonly List<float> _decodeSamples   = new();
        private readonly List<float> _bitrateSamples  = new();
        private readonly List<float> _hostLatencyAvgSamples = new();
        private readonly List<float> _hostLatencyMaxSamples = new();

        // Host (campionati una volta per batch, ogni ~10s)
        private readonly List<int>   _gpuSamples      = new();
        private readonly List<int>   _gpuEncSamples   = new();
        private readonly List<int>   _gpuTempSamples  = new();
        private readonly List<int>   _cpuSamples      = new();
        private readonly List<int>   _netTxSamples    = new();

        // Time series per le sparkline
        private readonly List<float> _rttTimeSeries     = new();
        private readonly List<float> _dropsTimeSeries   = new();
        private readonly List<float> _bitrateTimeSeries = new();
        private readonly List<float> _decodeTimeSeries  = new();
        private readonly List<float> _hostLatencyTimeSeries = new();

        private int  _targetFps;
        private long _totalFrames;
        private long _totalDrops;

        public void AddBatch(ClientBatch batch, HostMetricsSample host)
        {
            lock (_lock)
            {
                if (batch.Samples.Count == 0) return;

                _targetFps = batch.TargetFps;

                foreach (var s in batch.Samples)
                {
                    _fpsAvgSamples.Add(s.FpsAvg);
                    _fpsMinSamples.Add(s.FpsMin);
                    _dropSamples.Add(s.Drops);
                    _decodeSamples.Add(s.DecodeMs);
                    _bitrateSamples.Add(s.BitrateAvgMbps);

                    // RTT=0 means LiGetEstimatedRttInfo has not yet produced an
                    // estimate (typically the first tick after connect). Exclude
                    // from all RTT/jitter accumulators and the sparkline series to
                    // avoid pulling the average down artificially.
                    if (s.RttAvg > 0f)
                    {
                        _rttAvgSamples.Add(s.RttAvg);
                        _rttMaxSamples.Add(s.RttMax);
                        _jitterSamples.Add(s.JitterAvg);
                        _jitterMaxSamples.Add(s.JitterMax);
                    }

                    // Denominator = frames actually received (rendered + dropped),
                    // not target FPS — avoids inflating the rate when the decoder
                    // runs below target (throttling, VRR, static screens).
                    _totalFrames += (long)Math.Max(1f, s.FpsAvg + s.Drops);
                    _totalDrops  += s.Drops;

                    if (s.RttAvg > 0f)
                        _rttTimeSeries.Add(s.RttAvg);
                    _dropsTimeSeries.Add(s.Drops);
                    _bitrateTimeSeries.Add(s.BitrateAvgMbps);
                    _decodeTimeSeries.Add(s.DecodeMs);

                    // Host frame-processing latency — only when the client reported it
                    // (>0). Older StreamLight builds send 0; excluded from the average,
                    // the spike tracker and the sparkline so they read N/A, not zero.
                    if (s.HostLatencyAvg > 0f)
                    {
                        _hostLatencyAvgSamples.Add(s.HostLatencyAvg);
                        _hostLatencyMaxSamples.Add(s.HostLatencyMax);
                        _hostLatencyTimeSeries.Add(s.HostLatencyAvg);
                    }
                }

                // Host: un campione per batch (snapshot al momento della ricezione)
                if (host.Gpu     >= 0) _gpuSamples.Add(host.Gpu);
                if (host.GpuEnc  >= 0) _gpuEncSamples.Add(host.GpuEnc);
                if (host.GpuTemp >= 0) _gpuTempSamples.Add(host.GpuTemp);
                if (host.Cpu     >= 0) _cpuSamples.Add(host.Cpu);
                if (host.NetTxMbps >= 0) _netTxSamples.Add(host.NetTxMbps);
            }
        }

        public (SessionQualityStats Stats, List<float> RttSeries, List<float> DropsSeries, List<float> BitrateSeries, List<float> DecodeSeries, List<float> HostLatencySeries) Finalize()
        {
            lock (_lock)
            {
                int count = _fpsAvgSamples.Count;

                var stats = new SessionQualityStats
                {
                    SampleCount     = count,
                    FpsAvg          = count > 0 ? _fpsAvgSamples.Average()          : 0f,
                    FpsMin          = count > 0 ? _fpsMinSamples.Min()               : 0,
                    TotalDrops      = _totalDrops,
                    DropRatePct     = _totalFrames > 0
                                         ? (float)_totalDrops / _totalFrames * 100f
                                         : 0f,
                    RttAvgMs        = _rttAvgSamples.Count  > 0 ? _rttAvgSamples.Average()    : 0f,
                    RttMaxMs        = _rttMaxSamples.Count  > 0 ? _rttMaxSamples.Max()         : 0f,
                    JitterAvgMs     = _jitterSamples.Count  > 0 ? _jitterSamples.Average()     : 0f,
                    JitterMaxMs     = _jitterMaxSamples.Count > 0 ? _jitterMaxSamples.Max()    : 0f,
                    DecodeAvgMs     = count > 0 ? _decodeSamples.Average()           : 0f,
                    BitrateAvgMbps  = count > 0 ? _bitrateSamples.Average()          : 0f,

                    HostLatencyAvgMs = _hostLatencyAvgSamples.Count > 0 ? _hostLatencyAvgSamples.Average() : -1f,
                    HostLatencyMaxMs = _hostLatencyMaxSamples.Count > 0 ? _hostLatencyMaxSamples.Max()     : -1f,

                    HostGpuAvg      = _gpuSamples.Count     > 0 ? (int)_gpuSamples.Average()     : -1,
                    HostGpuPeak     = _gpuSamples.Count     > 0 ? _gpuSamples.Max()               : -1,
                    HostGpuEncAvg   = _gpuEncSamples.Count  > 0 ? (int)_gpuEncSamples.Average()  : -1,
                    HostGpuEncPeak  = _gpuEncSamples.Count  > 0 ? _gpuEncSamples.Max()            : -1,
                    HostGpuTempAvg  = _gpuTempSamples.Count > 0 ? (int)_gpuTempSamples.Average() : -1,
                    HostGpuTempMax  = _gpuTempSamples.Count > 0 ? _gpuTempSamples.Max()           : -1,
                    HostCpuAvg      = _cpuSamples.Count     > 0 ? (int)_cpuSamples.Average()     : -1,
                    HostCpuPeak     = _cpuSamples.Count     > 0 ? _cpuSamples.Max()               : -1,
                    HostNetTxAvg    = _netTxSamples.Count   > 0 ? (int)_netTxSamples.Average()   : -1,
                };

                const int MaxSeriesPoints = 600;
                return (stats,
                    Downsample(_rttTimeSeries,         MaxSeriesPoints),
                    Downsample(_dropsTimeSeries,       MaxSeriesPoints),
                    Downsample(_bitrateTimeSeries,     MaxSeriesPoints),
                    Downsample(_decodeTimeSeries,      MaxSeriesPoints),
                    Downsample(_hostLatencyTimeSeries, MaxSeriesPoints));
            }
        }

        /// <summary>
        /// Returns the per-batch host compute series (GPU %, Encoder %, CPU %),
        /// downsampled to the same 600-point cap as the client series. Each list is
        /// empty when that metric was never available. Call before <see cref="Reset"/>.
        /// </summary>
        public (List<float> Gpu, List<float> Enc, List<float> Cpu) GetHostSeries()
        {
            lock (_lock)
            {
                const int MaxSeriesPoints = 600;
                return (
                    Downsample(ToFloat(_gpuSamples),    MaxSeriesPoints),
                    Downsample(ToFloat(_gpuEncSamples), MaxSeriesPoints),
                    Downsample(ToFloat(_cpuSamples),    MaxSeriesPoints));
            }
        }

        private static List<float> ToFloat(List<int> src)
        {
            var r = new List<float>(src.Count);
            foreach (var v in src) r.Add(v);
            return r;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _fpsAvgSamples.Clear();
                _fpsMinSamples.Clear();
                _dropSamples.Clear();
                _rttAvgSamples.Clear();
                _rttMaxSamples.Clear();
                _jitterSamples.Clear();
                _jitterMaxSamples.Clear();
                _decodeSamples.Clear();
                _bitrateSamples.Clear();
                _hostLatencyAvgSamples.Clear();
                _hostLatencyMaxSamples.Clear();
                _gpuSamples.Clear();
                _gpuEncSamples.Clear();
                _gpuTempSamples.Clear();
                _cpuSamples.Clear();
                _netTxSamples.Clear();
                _rttTimeSeries.Clear();
                _dropsTimeSeries.Clear();
                _bitrateTimeSeries.Clear();
                _decodeTimeSeries.Clear();
                _hostLatencyTimeSeries.Clear();
                _targetFps   = 0;
                _totalFrames = 0;
                _totalDrops  = 0;
            }
        }

        public int TargetFps { get { lock (_lock) return _targetFps; } }

        // Reduces a time series to at most maxPoints by bucket-averaging.
        // Returns a copy; if the series is already within the limit, it is
        // returned as-is (new list) without any averaging.
        private static List<float> Downsample(List<float> src, int maxPoints)
        {
            if (src.Count <= maxPoints) return new List<float>(src);
            float step = (float)src.Count / maxPoints;
            var result = new List<float>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
            {
                int start = (int)(i * step);
                int end   = Math.Min((int)((i + 1) * step), src.Count);
                float sum = 0f;
                for (int j = start; j < end; j++) sum += src[j];
                result.Add(sum / (end - start));
            }
            return result;
        }
    }

    // ── Telemetry checkpoint on disk (recovery after abrupt shutdown) ─────────

    /// <summary>
    /// Written every 30 s during an active session to
    /// %LOCALAPPDATA%\StreamTweak\telemetry_checkpoint.json.
    /// On restart, <see cref="SessionLogger.Initialize"/> loads it to
    /// rebuild the quality stats of the "Interrupted" session.
    /// </summary>
    public sealed class TelemetryCheckpoint
    {
        public string   SessionId     { get; set; } = "";
        public DateTime Timestamp     { get; set; }
        public SessionQualityStats Stats { get; set; } = new();
        public int      Grade         { get; set; }   // cast di QualityGrade
        public List<float> RttSeries     { get; set; } = [];
        public List<float> DropsSeries   { get; set; } = [];
        public List<float> BitrateSeries { get; set; } = [];
        public List<float> DecodeSeries  { get; set; } = [];
        public List<float> HostLatencySeries { get; set; } = [];
        public List<float> HostGpuSeries { get; set; } = [];
        public List<float> HostEncSeries { get; set; } = [];
        public List<float> HostCpuSeries { get; set; } = [];

        /// <summary>
        /// Games detected by SessionProcessMonitor up to the last checkpoint write.
        /// Persisted here so they can be recovered if the session ends abruptly
        /// (host shutdown, crash) and SessionLogger.EndSession is never called.
        /// Null means the monitor had not detected any game yet at checkpoint time.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? GamesDetected { get; set; }
    }

    // ── Calcolo del grade ─────────────────────────────────────────────────────

    public static class QualityGradeCalculator
    {
        public static QualityGrade Evaluate(SessionQualityStats stats, int targetFps)
        {
            if (stats.SampleCount < 2)
                return QualityGrade.NoData;

            // FPS intentionally excluded: affected by static screens / loading screens,
            // which produce artificially low fps that doesn't reflect streaming quality.

            var gradeDrop = stats.DropRatePct < 1.0f  ? QualityGrade.High
                          : stats.DropRatePct <= 2.0f ? QualityGrade.Medium
                          :                             QualityGrade.Low;

            var gradeRtt  = stats.RttAvgMs < 25f      ? QualityGrade.High
                          : stats.RttAvgMs <= 60f     ? QualityGrade.Medium
                          :                             QualityGrade.Low;

            // A severe spike (>200ms) degrades the grade by one level even when
            // the average is good — the user will have felt that momentary lag.
            if (stats.RttMaxMs > 200f && gradeRtt < QualityGrade.Low)
                gradeRtt = (QualityGrade)((int)gradeRtt + 1);

            // GpuEnc: usa avg; non penalizza se metrica non disponibile (-1)
            var gradeEnc  = stats.HostGpuEncAvg < 0  ? QualityGrade.High
                          : stats.HostGpuEncAvg < 80 ? QualityGrade.High
                          : stats.HostGpuEncAvg < 90 ? QualityGrade.Medium
                          :                            QualityGrade.Low;

            // Host frame-processing latency (capture+encode, client-measured) is a
            // far more direct "the host couldn't keep up" signal than encoder
            // utilization %: a high latency means the host took too long to produce
            // the frame regardless of how busy the encoder looked. Only graded when
            // the client reported it (>= 0); otherwise it doesn't penalize.
            var gradeHostLat = stats.HostLatencyAvgMs < 0f  ? QualityGrade.High
                             : stats.HostLatencyAvgMs < 8f  ? QualityGrade.High
                             : stats.HostLatencyAvgMs <= 16f ? QualityGrade.Medium
                             :                                QualityGrade.Low;

            // A severe single-frame spike (>40 ms ≈ 2.5 frames at 60 Hz) drops the
            // grade by one level even when the average is good.
            if (stats.HostLatencyMaxMs > 40f && gradeHostLat < QualityGrade.Low)
                gradeHostLat = (QualityGrade)((int)gradeHostLat + 1);

            return (QualityGrade)Math.Max(
                Math.Max(Math.Max((int)gradeDrop, (int)gradeRtt), (int)gradeEnc),
                (int)gradeHostLat);
        }
    }
}
