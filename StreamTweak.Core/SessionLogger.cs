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

        /// <summary>
        /// Absolute cover PNG paths snapshotted at session-end time, keyed by game display name.
        /// Captured while GameLibraryState is still intact, so covers remain visible in the
        /// session card even after a game is later uninstalled or removed from the library.
        /// Cover files are never deleted from the cache directory.
        /// Null → not populated (session pre-dates this feature, or no games were detected).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? GamesDetectedCoverPaths { get; set; }

        /// <summary>
        /// True for sessions created by Debug Mode (Settings → Maintenance).
        /// No real stream occurred; all telemetry data is synthetic.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsDebugSession { get; set; } = false;

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
        public bool HasGrade => IsDebugSession || Grade != null;

        [JsonIgnore]
        public string GradeShortLabel => IsDebugSession ? "DEBUG"
            : Grade switch
            {
                QualityGrade.High   => "Excellent",
                QualityGrade.Medium => "Good",
                QualityGrade.Low    => "Poor",
                _                   => "—"
            };

        [JsonIgnore]
        public string GradeColorHex => IsDebugSession ? "#FF9E9E9E"
            : Grade switch
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

        /// <summary>
        /// Percorso del checkpoint telemetria scritto ogni 30 s durante una sessione attiva.
        /// Accessibile da App.xaml.cs per la scrittura periodica e la pulizia.
        /// </summary>
        public static readonly string CheckpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "telemetry_checkpoint.json");

        private static readonly object _fileLock = new();
        private static string? _activeSessionId = null;
        private static DateTime _activeSessionStartTime;

        public static string?  ActiveSessionId        => _activeSessionId;
        public static DateTime ActiveSessionStartTime => _activeSessionStartTime;

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

                _activeSessionId        = entry.Id;
                _activeSessionStartTime = entry.StartTime;
                sessions.Insert(0, entry);

                if (sessions.Count > MaxSessions)
                    sessions = sessions.Take(MaxSessions).ToList();

                Save(sessions);
            }
            catch { }
        }

        public static void MarkActiveSessionAsDebug()
        {
            string? sid = _activeSessionId;
            if (sid == null) return;
            try
            {
                var sessions = Load();
                var entry = sessions.FirstOrDefault(s => s.Id == sid);
                if (entry != null) { entry.IsDebugSession = true; Save(sessions); }
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
                bool usedCheckpoint = false;

                foreach (var s in sessions.Where(s => s.EndTime == null && s.EndReason == null))
                {
                    s.EndReason = "Interrupted";

                    // Tenta di recuperare la telemetria dal checkpoint periodico.
                    // Il checkpoint contiene gli stats aggregati fino all'ultimo flush
                    // (ogni 30 s), scritti atomicamente durante la sessione attiva.
                    var cp = LoadCheckpoint(s.Id);
                    s.EndTime = cp?.Timestamp ?? DateTime.Now;

                    if (cp != null && cp.Stats.SampleCount >= 2)
                    {
                        s.QualityStats      = cp.Stats;
                        s.Grade             = (QualityGrade)cp.Grade;
                        s.RttTimeSeries     = cp.RttSeries.Count     > 0 ? cp.RttSeries     : null;
                        s.DropsTimeSeries   = cp.DropsSeries.Count   > 0 ? cp.DropsSeries   : null;
                        s.BitrateTimeSeries = cp.BitrateSeries.Count > 0 ? cp.BitrateSeries : null;
                        s.DecodeTimeSeries  = cp.DecodeSeries.Count  > 0 ? cp.DecodeSeries  : null;
                        usedCheckpoint = true;
                    }

                    // Recover games detected list from checkpoint (written every 30 s).
                    // This is the only source of game data when the session was interrupted
                    // before SessionLogger.EndSession() had a chance to run.
                    if (cp?.GamesDetected is { Count: > 0 } games)
                    {
                        s.GamesDetected = games;

                        // Snapshot cover paths from the current library state.
                        // Files are never deleted from the covers cache, so paths
                        // resolved now are still valid even after a game is removed.
                        var gameMap = GameLibraryState.Current.Games
                            .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
                        var coverPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var name in games)
                        {
                            if (gameMap.TryGetValue(name, out var g) && g.CoverImagePath != null)
                                coverPaths[name] = g.CoverImagePath;
                        }
                        if (coverPaths.Count > 0)
                            s.GamesDetectedCoverPaths = coverPaths;
                    }

                    changed = true;
                }

                if (changed)
                {
                    Save(sessions);
                    // Rimuove il checkpoint sia che sia stato usato sia che non
                    // corrisponda alla sessione interrotta (ID diverso → file stale).
                    if (usedCheckpoint || File.Exists(CheckpointPath))
                        DeleteCheckpoint();
                }
            }
            catch { }
        }

        private static TelemetryCheckpoint? LoadCheckpoint(string sessionId)
        {
            try
            {
                if (!File.Exists(CheckpointPath)) return null;
                string json = File.ReadAllText(CheckpointPath);
                var cp = JsonSerializer.Deserialize<TelemetryCheckpoint>(json);
                // Verifica corrispondenza ID: checkpoint di sessioni precedenti ignorati.
                return cp?.SessionId == sessionId ? cp : null;
            }
            catch { return null; }
        }

        private static void DeleteCheckpoint()
        {
            try { File.Delete(CheckpointPath); } catch { }
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
                    {
                        entry.GamesDetected = gamesDetected;

                        // Snapshot cover paths while GameLibraryState is still intact.
                        // This ensures covers remain visible in the session card even after
                        // a game is later uninstalled or removed from the library.
                        if (gamesDetected.Count > 0)
                        {
                            var gameMap = GameLibraryState.Current.Games
                                .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
                            var coverPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var name in gamesDetected)
                            {
                                if (gameMap.TryGetValue(name, out var g) && g.CoverImagePath != null)
                                    coverPaths[name] = g.CoverImagePath;
                            }
                            if (coverPaths.Count > 0)
                                entry.GamesDetectedCoverPaths = coverPaths;
                        }
                    }
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
