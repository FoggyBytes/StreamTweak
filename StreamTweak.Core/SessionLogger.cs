using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamTweak
{
    public class SessionEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string TriggerMode { get; set; } = "Auto"; // "Auto" | "Manual"
        public string OriginalSpeed { get; set; } = string.Empty;

        public string? EndReason { get; set; }

        // ── Telemetria qualità sessione (null se nessun dato client ricevuto) ──
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SessionQualityStats? QualityStats { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public QualityGrade? Grade { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? FpsTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? RttTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? DropsTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? BitrateTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? DecodeTimeSeries { get; set; }

        /// <summary>
        /// Display names of games detected as running during this session (process monitor).
        /// Null  → monitor never ran (session pre-dates this feature, or manual streaming mode).
        /// Empty → monitor ran but no matching game process was found (e.g. desktop session).
        /// Non-empty → one or more games were detected.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? GamesDetected { get; set; }

        /// <summary>True when the process monitor ran and detected at least one game.</summary>
        [JsonIgnore] public bool HasGamesDetected    => GamesDetected is { Count: > 0 };

        /// <summary>True when the process monitor ran but found no matching game processes.</summary>
        [JsonIgnore] public bool HasNoGamesDetected  => GamesDetected != null && GamesDetected.Count == 0;

        // ── Display properties ────────────────────────────────────────────────

        [JsonIgnore]
        public string DurationDisplay
        {
            get
            {
                if (EndTime == null)
                    return EndReason == "Interrupted" ? "—" : "Active";
                var d = EndTime.Value - StartTime;
                string duration = d.TotalMinutes >= 1
                    ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
                    : $"{d.Seconds}s";
                return EndReason == "Interrupted" ? $"{duration} ⚡" : duration;
            }
        }

        [JsonIgnore]
        public string StartTimeDisplay => StartTime.ToString("dd/MM/yyyy  HH:mm");

        [JsonIgnore]
        public string TelemetryDurationDisplay
        {
            get
            {
                if (EndTime == null) return DurationDisplay;
                var d = EndTime.Value - StartTime;
                int secs = (int)d.TotalSeconds;
                return secs >= 3600
                    ? $"{secs / 3600}h{(secs % 3600) / 60}m{secs % 60:00}s"
                    : secs >= 60
                        ? $"{secs / 60}m{secs % 60:00}s"
                        : $"{secs}s";
            }
        }

        [JsonIgnore]
        public int SessionDurationSeconds =>
            EndTime.HasValue ? (int)(EndTime.Value - StartTime).TotalSeconds : 0;

        [JsonIgnore]
        public bool NicThrottled => !string.IsNullOrEmpty(OriginalSpeed);

        [JsonIgnore]
        public string NicThrottleDisplay => string.IsNullOrEmpty(OriginalSpeed) ? "No" : "Yes";

        [JsonIgnore]
        public string OriginalNicSpeedDisplay => string.IsNullOrEmpty(OriginalSpeed) ? "N/A" : OriginalSpeed;

        [JsonIgnore]
        public string RttAvgDisplay =>
            QualityStats != null && QualityStats.RttAvgMs > 0
                ? $"{QualityStats.RttAvgMs:F0} ms"
                : "—";

        [JsonIgnore]
        public string DropRateDisplay =>
            QualityStats != null
                ? $"{QualityStats.DropRatePct:0.#}%"
                : "—";

        [JsonIgnore]
        public string NetTxAvgDisplay =>
            QualityStats?.HostNetTxAvg >= 0
                ? $"{QualityStats.HostNetTxAvg} Mbps"
                : "—";

        // ── UI helpers for WinUI 3 (no WPF converters available in Core) ─────

        [JsonIgnore]
        public bool HasGrade => Grade != null;

        [JsonIgnore]
        public string GradeShortLabel => Grade switch
        {
            QualityGrade.High   => "Excellent",
            QualityGrade.Medium => "Good",
            QualityGrade.Low    => "Poor",
            _                   => "—"
        };

        [JsonIgnore]
        public string GradeColorHex => Grade switch
        {
            QualityGrade.High   => "#FF4CAF50",
            QualityGrade.Medium => "#FFFFC107",
            QualityGrade.Low    => "#FFDC4632",
            _                   => "#FF808080"
        };
    }

    public static class SessionLogger
    {
        private const int MaxSessions = 10;
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "sessions.json");

        private static readonly object _fileLock = new();
        private static string? _activeSessionId = null;

        public static string? ActiveSessionId => _activeSessionId;

        public static void StartSession(string triggerMode, string originalSpeed)
        {
            try
            {
                var sessions = Load();
                var entry = new SessionEntry
                {
                    StartTime = DateTime.Now,
                    TriggerMode = triggerMode,
                    OriginalSpeed = originalSpeed
                };

                _activeSessionId = entry.Id;
                sessions.Insert(0, entry);

                if (sessions.Count > MaxSessions)
                    sessions = sessions.Take(MaxSessions).ToList();

                Save(sessions);
            }
            catch { }
        }

        public static void ClearAll()
        {
            try
            {
                var sessions = Load();
                // Preserve the currently active session so EndSession() can still close it properly
                var toKeep = sessions.Where(s => s.Id == _activeSessionId).ToList();
                Save(toKeep);
            }
            catch { }
        }

        public static void Initialize()
        {
            try
            {
                var sessions = Load();
                bool changed = false;
                foreach (var s in sessions.Where(s => s.EndTime == null && s.EndReason == null))
                {
                    s.EndReason = "Interrupted";
                    changed = true;
                }
                if (changed) Save(sessions);
            }
            catch { }
        }

        public static void UpdateSessionTelemetry(
            string sessionId,
            SessionQualityStats stats,
            QualityGrade grade,
            List<float> rttSeries,
            List<float> dropsSeries,
            List<float> bitrateSeries,
            List<float> decodeSeries)
        {
            try
            {
                var sessions = Load();
                var entry = sessions.FirstOrDefault(s => s.Id == sessionId);
                if (entry == null) return;

                entry.QualityStats      = stats;
                entry.Grade             = grade;
                entry.RttTimeSeries     = rttSeries.Count     > 0 ? rttSeries     : null;
                entry.DropsTimeSeries   = dropsSeries.Count   > 0 ? dropsSeries   : null;
                entry.BitrateTimeSeries = bitrateSeries.Count > 0 ? bitrateSeries : null;
                entry.DecodeTimeSeries  = decodeSeries.Count  > 0 ? decodeSeries  : null;
                Save(sessions);
            }
            catch { }
        }

        public static void EndSession(string endReason = "User", List<string>? gamesDetected = null)
        {
            // Atomically capture and clear the session ID so concurrent callers
            // (e.g. App_SessionEnding on the OS thread + HandleAutoStreamStop on the UI thread)
            // cannot both proceed past the null check.
            string? sessionId = System.Threading.Interlocked.Exchange(ref _activeSessionId, null);
            if (sessionId == null) return;
            try
            {
                var sessions = Load();
                var entry = sessions.FirstOrDefault(s => s.Id == sessionId);
                if (entry?.EndTime == null)
                {
                    entry!.EndTime = DateTime.Now;
                    entry.EndReason = endReason;
                    // Store even when empty: null means monitor never ran; [] means monitor ran but found nothing.
                    if (gamesDetected != null)
                        entry.GamesDetected = gamesDetected;
                    Save(sessions);
                }
            }
            catch { }
        }

        public static List<SessionEntry> Load()
        {
            lock (_fileLock)
            {
                try
                {
                    if (!File.Exists(LogPath)) return new List<SessionEntry>();
                    string json = File.ReadAllText(LogPath);
                    return JsonSerializer.Deserialize<List<SessionEntry>>(json) ?? new List<SessionEntry>();
                }
                catch { return new List<SessionEntry>(); }
            }
        }

        /// <summary>Persists a caller-supplied session list (e.g. after removing one entry).</summary>
        public static void SavePublic(List<SessionEntry> sessions) => Save(sessions);

        private static void Save(List<SessionEntry> sessions)
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.WriteAllText(LogPath, JsonSerializer.Serialize(sessions,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
