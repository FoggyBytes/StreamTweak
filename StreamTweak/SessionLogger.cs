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
                int secs = FpsTimeSeries?.Count ?? RttTimeSeries?.Count ?? 0;
                if (secs == 0) return DurationDisplay;
                return secs >= 60
                    ? $"{secs / 60}m {secs % 60}s"
                    : $"{secs}s";
            }
        }

        [JsonIgnore]
        public string NicThrottleDisplay => string.IsNullOrEmpty(OriginalSpeed) ? "No" : "Yes";

        [JsonIgnore]
        public string OriginalNicSpeedDisplay => string.IsNullOrEmpty(OriginalSpeed) ? "N/A" : OriginalSpeed;
    }

    public static class SessionLogger
    {
        private const int MaxSessions = 10;
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "sessions.json");

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
            List<float> fpsSeries,
            List<float> rttSeries,
            List<float> dropsSeries,
            List<float> bitrateSeries)
        {
            try
            {
                var sessions = Load();
                var entry = sessions.FirstOrDefault(s => s.Id == sessionId);
                if (entry == null) return;

                entry.QualityStats      = stats;
                entry.Grade             = grade;
                entry.FpsTimeSeries     = fpsSeries.Count     > 0 ? fpsSeries     : null;
                entry.RttTimeSeries     = rttSeries.Count     > 0 ? rttSeries     : null;
                entry.DropsTimeSeries   = dropsSeries.Count   > 0 ? dropsSeries   : null;
                entry.BitrateTimeSeries = bitrateSeries.Count > 0 ? bitrateSeries : null;
                Save(sessions);
            }
            catch { }
        }

        public static void EndSession(string endReason = "User")
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
                    Save(sessions);
                }
            }
            catch { }
        }

        public static List<SessionEntry> Load()
        {
            try
            {
                if (!File.Exists(LogPath)) return new List<SessionEntry>();
                string json = File.ReadAllText(LogPath);
                return JsonSerializer.Deserialize<List<SessionEntry>>(json) ?? new List<SessionEntry>();
            }
            catch { return new List<SessionEntry>(); }
        }

        private static void Save(List<SessionEntry> sessions)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, JsonSerializer.Serialize(sessions,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
