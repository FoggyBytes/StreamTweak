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
        [JsonPropertyName("decode_ms")]    public float DecodeMs       { get; set; }
        [JsonPropertyName("bitrate_mbps")] public float BitrateAvgMbps { get; set; }
    }

    public sealed class ClientBatch
    {
        [JsonPropertyName("session_id")]  public string             SessionId { get; set; } = "";
        [JsonPropertyName("target_fps")]  public int                TargetFps { get; set; }
        [JsonPropertyName("samples")]     public List<ClientSample> Samples   { get; set; } = new();
    }

    // ── Statistiche aggregate (persistite in sessions.json) ──────────────────

    public sealed class SessionQualityStats
    {
        // Client metrics
        public float FpsAvg          { get; set; }
        public int   FpsMin          { get; set; }
        public float DropRatePct     { get; set; }
        public float RttAvgMs        { get; set; }
        public float RttMaxMs        { get; set; }
        public float DecodeAvgMs     { get; set; }
        public float BitrateAvgMbps  { get; set; }

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

    // ── Accumulatore in-memory per la sessione attiva ─────────────────────────

    public sealed class TelemetryAccumulator
    {
        private readonly object _lock = new();

        // Client
        private readonly List<float> _fpsAvgSamples   = new();
        private readonly List<int>   _fpsMinSamples   = new();
        private readonly List<int>   _dropSamples     = new();
        private readonly List<float> _rttAvgSamples   = new();
        private readonly List<float> _rttMaxSamples   = new();
        private readonly List<float> _decodeSamples   = new();
        private readonly List<float> _bitrateSamples  = new();

        // Host (campionati una volta per batch, ogni ~10s)
        private readonly List<int>   _gpuSamples      = new();
        private readonly List<int>   _gpuEncSamples   = new();
        private readonly List<int>   _gpuTempSamples  = new();
        private readonly List<int>   _cpuSamples      = new();
        private readonly List<int>   _netTxSamples    = new();

        // Time series per le sparkline
        private readonly List<float> _fpsTimeSeries     = new();
        private readonly List<float> _rttTimeSeries     = new();
        private readonly List<float> _dropsTimeSeries   = new();
        private readonly List<float> _bitrateTimeSeries = new();

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
                    _rttAvgSamples.Add(s.RttAvg);
                    _rttMaxSamples.Add(s.RttMax);
                    _decodeSamples.Add(s.DecodeMs);
                    _bitrateSamples.Add(s.BitrateAvgMbps);

                    _totalFrames += (long)(_targetFps > 0 ? _targetFps : s.FpsAvg);
                    _totalDrops  += s.Drops;

                    _fpsTimeSeries.Add(s.FpsAvg);
                    _rttTimeSeries.Add(s.RttAvg);
                    _dropsTimeSeries.Add(s.Drops);
                    _bitrateTimeSeries.Add(s.BitrateAvgMbps);
                }

                // Host: un campione per batch (snapshot al momento della ricezione)
                if (host.Gpu     >= 0) _gpuSamples.Add(host.Gpu);
                if (host.GpuEnc  >= 0) _gpuEncSamples.Add(host.GpuEnc);
                if (host.GpuTemp >= 0) _gpuTempSamples.Add(host.GpuTemp);
                if (host.Cpu     >= 0) _cpuSamples.Add(host.Cpu);
                if (host.NetTxMbps >= 0) _netTxSamples.Add(host.NetTxMbps);
            }
        }

        public (SessionQualityStats Stats, List<float> FpsSeries, List<float> RttSeries, List<float> DropsSeries, List<float> BitrateSeries) Finalize()
        {
            lock (_lock)
            {
                int count = _fpsAvgSamples.Count;

                var stats = new SessionQualityStats
                {
                    SampleCount     = count,
                    FpsAvg          = count > 0 ? _fpsAvgSamples.Average()         : 0f,
                    FpsMin          = count > 0 ? _fpsMinSamples.Min()              : 0,
                    DropRatePct     = _totalFrames > 0
                                         ? (float)_totalDrops / _totalFrames * 100f
                                         : 0f,
                    RttAvgMs        = count > 0 ? _rttAvgSamples.Average()         : 0f,
                    RttMaxMs        = count > 0 ? _rttMaxSamples.Max()              : 0f,
                    DecodeAvgMs     = count > 0 ? _decodeSamples.Average()          : 0f,
                    BitrateAvgMbps  = count > 0 ? _bitrateSamples.Average()         : 0f,

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

                return (stats,
                    new List<float>(_fpsTimeSeries),
                    new List<float>(_rttTimeSeries),
                    new List<float>(_dropsTimeSeries),
                    new List<float>(_bitrateTimeSeries));
            }
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
                _decodeSamples.Clear();
                _bitrateSamples.Clear();
                _gpuSamples.Clear();
                _gpuEncSamples.Clear();
                _gpuTempSamples.Clear();
                _cpuSamples.Clear();
                _netTxSamples.Clear();
                _fpsTimeSeries.Clear();
                _rttTimeSeries.Clear();
                _dropsTimeSeries.Clear();
                _bitrateTimeSeries.Clear();
                _targetFps   = 0;
                _totalFrames = 0;
                _totalDrops  = 0;
            }
        }

        public int TargetFps { get { lock (_lock) return _targetFps; } }
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

            var gradeDrop = stats.DropRatePct < 0.5f  ? QualityGrade.High
                          : stats.DropRatePct <= 2.0f ? QualityGrade.Medium
                          :                             QualityGrade.Low;

            var gradeRtt  = stats.RttAvgMs < 25f      ? QualityGrade.High
                          : stats.RttAvgMs <= 60f     ? QualityGrade.Medium
                          :                             QualityGrade.Low;

            // GpuEnc: usa avg; non penalizza se metrica non disponibile (-1)
            var gradeEnc  = stats.HostGpuEncAvg < 0  ? QualityGrade.High
                          : stats.HostGpuEncAvg < 80 ? QualityGrade.High
                          : stats.HostGpuEncAvg < 90 ? QualityGrade.Medium
                          :                            QualityGrade.Low;

            return (QualityGrade)Math.Max(
                Math.Max((int)gradeDrop, (int)gradeRtt),
                (int)gradeEnc);
        }
    }
}
