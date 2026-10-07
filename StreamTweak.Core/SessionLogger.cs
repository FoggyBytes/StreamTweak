using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamTweak
{
    /// <summary>
    /// Lightweight record for a single game cover in the Logs table row.
    /// <see cref="CoverPath"/> is pre-resolved using snapshot-first fallback logic,
    /// so it stays valid even after the game is uninstalled.
    /// </summary>
    public record GameCoverItem(string Name, string? CoverPath);

    /// <summary>
    /// One stretch of time during which a stream was actually live inside this session.
    /// A session survives a client disconnect/reconnect (grace period), so a single record
    /// can span several streams with idle gaps between them — the 11/09/2026 record held 11.
    /// Without these the charts cannot be placed on a clock: samples only accrue while a
    /// stream is live, so mapping them linearly from StartTime to EndTime stretches them
    /// across the gaps and mislabels everything after the first one.
    /// </summary>
    public class StreamSpan
    {
        public DateTime Start { get; set; }

        /// <summary>Null when the stream was still live (session ended abruptly).</summary>
        public DateTime? End { get; set; }

        /// <summary>
        /// The server's encoder for this stream, as its log names it (<c>av1_nvenc</c>,
        /// <c>pyrowave</c>, …) — 9.1.0, §82. Null in records written before 9.1.0 and for a stream
        /// that never created one (a stream a few seconds long can end first); shown as unknown,
        /// never guessed. <see cref="EncoderNames"/> turns it into a label.
        /// <para>One name, the last one seen: the codec is negotiated once, when the client opens
        /// the stream, and every re-creation inside the stream (a resolution or display change)
        /// is made from that same configuration — the log of 30/09/2026 re-creates the encoder
        /// twice in one stream, with the same name both times. A different codec needs a new
        /// stream, which gets its own span.</para>
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Encoder { get; set; }
    }

    /// <summary>
    /// When one game was running during a session (9.0): from the first process scan that saw
    /// it to the last. Drives the "Games &amp; streams" lane of the session timeline.
    /// </summary>
    public class GameSpan
    {
        public string   Name  { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End   { get; set; }
    }

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
        public List<float>? RttTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? DropsTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? BitrateTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? DecodeTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostLatencyTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostGpuTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostEncTimeSeries { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostCpuTimeSeries { get; set; }

        /// <summary>NVIDIA board power, W (9.2.0). Null on older records and on hosts without
        /// an NVIDIA GPU or without StreamTweakService answering.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostPowerTimeSeries { get; set; }

        /// <summary>The enforced power limit at each point of <see cref="HostPowerTimeSeries"/>,
        /// W — same length. A step where the limit was changed during the session.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostPowerLimitTimeSeries { get; set; }

        /// <summary>GPU temperature over the session, °C (9.2.0; before it only the average and
        /// maximum were kept, in QualityStats).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostGpuTempTimeSeries { get; set; }

        /// <summary>The share of each slice the driver held the GPU at its power limit, 0..1
        /// (NVML "SW Power Cap", 9.2.0). Null when the service could not tell.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? HostPowerCappedTimeSeries { get; set; }

        /// <summary>
        /// Live-stream intervals within this session, in order. Null → the session pre-dates
        /// this feature; charts then fall back to "0 → duration" with no clock on the axis.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<StreamSpan>? StreamSpans { get; set; }

        /// <summary>
        /// When each detected game was running (9.0). Null for sessions recorded before 9.0 and
        /// for sessions where no game was seen; the timeline then shows the covers without a
        /// position on the clock.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<GameSpan>? GameSpans { get; set; }

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

        /// <summary>
        /// Ordered cover items for the Logs table row, one per <see cref="GamesDetected"/> entry.
        /// Uses <see cref="GamesDetectedCoverPaths"/> snapshot first (survives game uninstall;
        /// cover files are never deleted from cache), then falls back to the live
        /// <see cref="GameLibraryState"/> for sessions that predate the snapshot feature.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<GameCoverItem> GameCoversForDisplay
        {
            get
            {
                if (GamesDetected == null || GamesDetected.Count == 0)
                    return Array.Empty<GameCoverItem>();

                var gameMap = GameLibraryState.Current.Games
                    .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);

                return GamesDetected.Select(name =>
                {
                    if (GamesDetectedCoverPaths?.TryGetValue(name, out var snap) == true
                        && File.Exists(snap))
                        return new GameCoverItem(name, snap);

                    gameMap.TryGetValue(name, out var entry);
                    return new GameCoverItem(name, entry?.CoverImagePath);
                }).ToList();
            }
        }

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
        public string GradeColorHex => IsDebugSession ? "#9E9E9E"
            : Grade switch
            {
                QualityGrade.High   => "#4ade80",
                QualityGrade.Medium => "#fbbf24",
                QualityGrade.Low    => "#f87171",
                _                   => "#9a9691"
            };

        [JsonIgnore]
        public string GradeBgHex => IsDebugSession ? "#1A9E9E9E"
            : Grade switch
            {
                QualityGrade.High   => "#214ade80",
                QualityGrade.Medium => "#1Af59e0b",
                QualityGrade.Low    => "#1Aef4444",
                _                   => "#1A808080"
            };

        [JsonIgnore]
        public string GradeBorderHex => IsDebugSession ? "#409E9E9E"
            : Grade switch
            {
                QualityGrade.High   => "#594ade80",
                QualityGrade.Medium => "#40f59e0b",
                QualityGrade.Low    => "#40ef4444",
                _                   => "#40808080"
            };
    }

    public static class SessionLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "sessions.json");

        /// <summary>
        /// Path of the telemetry checkpoint written every 30 s during an active session.
        /// Accessed from App.xaml.cs for periodic writes and cleanup.
        /// </summary>
        public static readonly string CheckpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "telemetry_checkpoint.json");

        private static readonly object _fileLock = new();
        private static string? _activeSessionId = null;
        private static DateTime _activeSessionStartTime;

        /// <summary>
        /// Sessions shorter than this are discarded instead of being written to history.
        /// A stream that lasted seconds is a connection test, a mis-click or a failed
        /// launch — it carries no usable telemetry (the grade needs samples to mean
        /// anything) and only clutters the list. Applied when a session is closed, both
        /// on the normal end path and when an interrupted one is recovered at startup.
        /// Debug sessions are unaffected: they backdate their start by 30 minutes.
        /// </summary>
        private const double MinSessionSeconds = 60;

        /// <summary>
        /// When true, a session that never launched a game is discarded instead of being
        /// written to history — the user's opt-in "only record sessions with a game"
        /// (config key <c>RecordOnlyGameSessions</c>, Settings → Behavior). Set once at
        /// startup from config and again whenever the toggle changes; the Core has no
        /// config access of its own, same arrangement as <see cref="DebugLogger.VerboseEnabled"/>.
        ///
        /// Applied on both close paths (normal end + interrupted-session recovery) and
        /// never to debug sessions, which have no game by construction.
        /// </summary>
        public static bool RecordOnlyGameSessions { get; set; }

        public static string?  ActiveSessionId        => _activeSessionId;
        public static DateTime ActiveSessionStartTime => _activeSessionStartTime;

        /// <summary>
        /// Source of the game time ranges for the active session — set by the app to the
        /// running <see cref="SessionProcessMonitor"/>, read once at <see cref="EndSession"/>
        /// and cleared there. A delegate rather than a reference so the Core does not need to
        /// know which monitor instance (or the debug simulation) supplies them.
        /// </summary>
        public static Func<List<GameSpan>>? ActiveGameSpansProvider { get; set; }

        /// <summary>
        /// The game the active session is playing right now: the most recently seen one, if it
        /// was seen within the last <paramref name="freshness"/>. Null when nothing is running.
        /// </summary>
        public static string? CurrentGameName(TimeSpan freshness)
        {
            try
            {
                var spans = ActiveGameSpansProvider?.Invoke();
                if (spans == null || spans.Count == 0) return null;
                var latest = spans.OrderByDescending(s => s.End).First();
                return DateTime.Now - latest.End <= freshness ? latest.Name : null;
            }
            catch { return null; }
        }

        // Live-stream intervals for the active session. Held in memory and written at
        // session end (and into the checkpoint) rather than on every event, so a
        // reconnect storm does not rewrite the whole history file each time.
        // ⚠️ Its own lock, deliberately not _fileLock: SnapshotStreamSpans() is called
        // from inside code that already holds _fileLock.
        private static readonly object _spanLock = new();
        private static readonly List<StreamSpan> _activeStreamSpans = new();

        /// <summary>Opens a live-stream interval. Ignored when one is already open.</summary>
        public static void RecordStreamStart(DateTime when)
        {
            lock (_spanLock)
            {
                if (_activeStreamSpans.Count > 0 && _activeStreamSpans[^1].End == null) return;
                _activeStreamSpans.Add(new StreamSpan { Start = when, Encoder = TakePendingEncoder(when) });
            }
        }

        // An encoder line that arrived with no stream open to take it. The log writes it about
        // 0.3 s after CLIENT CONNECTED, and the app opens the stream on the start line before it,
        // on the same dispatcher, so this should never be needed — it is the net for the order
        // being reversed some day, kept for a few seconds only so it cannot land on a later stream.
        private static string?  _pendingEncoder;
        private static DateTime _pendingEncoderAt;
        private const double PENDING_ENCODER_MAX_AGE_SEC = 10;

        private static string? TakePendingEncoder(DateTime streamStart)
        {
            string? e = _pendingEncoder;
            _pendingEncoder = null;
            return e != null && Math.Abs((streamStart - _pendingEncoderAt).TotalSeconds) <= PENDING_ENCODER_MAX_AGE_SEC ? e : null;
        }

        /// <summary>
        /// Attaches the encoder the server just created to the stream in progress (9.1.0, §82).
        /// Called for every <c>Creating encoder</c> line, re-creations included: the last one wins.
        /// </summary>
        public static void RecordStreamEncoder(string encoder)
        {
            lock (_spanLock)
            {
                if (_activeSessionId != null && _activeStreamSpans.Count > 0 && _activeStreamSpans[^1].End == null)
                {
                    var open = _activeStreamSpans[^1];
                    if (open.Encoder != null && !open.Encoder.Equals(encoder, StringComparison.OrdinalIgnoreCase))
                        DebugLogger.Log($"[Session] encoder changed inside one stream: {open.Encoder} → {encoder}");
                    open.Encoder = encoder;
                    return;
                }
                _pendingEncoder   = encoder;
                _pendingEncoderAt = DateTime.Now;
            }
        }

        /// <summary>Closes the open live-stream interval, if any.</summary>
        public static void RecordStreamStop(DateTime when)
        {
            lock (_spanLock)
            {
                if (_activeStreamSpans.Count == 0) return;
                if (_activeStreamSpans[^1].End == null) _activeStreamSpans[^1].End = when;
            }
        }

        /// <summary>
        /// A first stream shorter than this, followed by another inside the grace period, is not
        /// part of the session (9.1.0, §82): the session restarts with the stream that follows.
        /// The case is the PIN pad after a Wake-on-LAN, or any brief Desktop session opened just
        /// to reach the host, when the client did not declare it with UNLOCKBEGIN — it opened the
        /// record and the real session, starting seconds later, was merged into it (30/09/2026:
        /// a 10 s Desktop stream at 19:18:42 in front of the real one at 19:19:01).
        /// </summary>
        public const double ShortFirstStreamSeconds = 20;

        /// <summary>
        /// Called when a stream starts inside the grace period. If the session's only stream so
        /// far was shorter than <see cref="ShortFirstStreamSeconds"/>, forgets it and restarts the
        /// session's record at <paramref name="now"/>, with this stream as its first; returns true
        /// and the caller resets whatever it accumulated. Otherwise changes nothing and returns
        /// false — the caller records a reconnect as usual.
        /// </summary>
        public static bool RestartIfOnlyShortStream(DateTime now)
        {
            string? sid = _activeSessionId;
            if (sid == null) return false;
            lock (_spanLock)
            {
                if (_activeStreamSpans.Count != 1 || _activeStreamSpans[0].End is not { } end ||
                    (end - _activeStreamSpans[0].Start).TotalSeconds >= ShortFirstStreamSeconds)
                    return false;
            }
            try
            {
                lock (_fileLock)
                {
                    var sessions = Load();
                    var entry = sessions.FirstOrDefault(s => s.Id == sid);
                    if (entry == null || entry.EndTime != null) return false;
                    entry.StartTime         = now;
                    _activeSessionStartTime = now;
                    lock (_spanLock)
                    {
                        _activeStreamSpans.Clear();
                        _activeStreamSpans.Add(new StreamSpan { Start = now, Encoder = TakePendingEncoder(now) });
                    }
                    Save(sessions);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Copy of the intervals so far, for persisting. Null when there are none.</summary>
        public static List<StreamSpan>? SnapshotStreamSpans()
        {
            lock (_spanLock)
            {
                if (_activeStreamSpans.Count == 0) return null;
                return _activeStreamSpans
                    .Select(s => new StreamSpan { Start = s.Start, End = s.End, Encoder = s.Encoder })
                    .ToList();
            }
        }

        public static void StartSession(string triggerMode, string originalSpeed)
        {
            try
            {
                lock (_fileLock)
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
                    ActiveGameSpansProvider = null;   // the app attaches the new monitor after this

                    // A session opens with a live stream by definition; later reconnects
                    // append their own spans via RecordStreamStart.
                    lock (_spanLock)
                    {
                        _activeStreamSpans.Clear();
                        _activeStreamSpans.Add(new StreamSpan { Start = entry.StartTime, Encoder = TakePendingEncoder(entry.StartTime) });
                    }
                    sessions.Insert(0, entry);

                    // No max-session cap: full history retained until the user manually
                    // clears it via the "Clear history" button in the Logs view.

                    Save(sessions);
                }
            }
            catch { }
        }

        public static void MarkActiveSessionAsDebug()
        {
            string? sid = _activeSessionId;
            if (sid == null) return;
            try
            {
                lock (_fileLock)
                {
                    var sessions = Load();
                    var entry = sessions.FirstOrDefault(s => s.Id == sid);
                    if (entry != null) { entry.IsDebugSession = true; Save(sessions); }
                }
            }
            catch { }
        }

        public static void ClearAll()
        {
            try
            {
                lock (_fileLock)
                {
                    var sessions = Load();
                    // Preserve the currently active session so EndSession() can still close it properly
                    var toKeep = sessions.Where(s => s.Id == _activeSessionId).ToList();
                    Save(toKeep);
                }
            }
            catch { }
        }

        /// <summary>
        /// Deletes sessions recorded at or after <paramref name="cutoff"/> (browser-style
        /// "clear the last hour/day/…" semantics — the most recent sessions go first).
        /// Pass <see cref="DateTime.MinValue"/> to clear everything. The currently active
        /// session is always preserved so EndSession() can still close it.
        /// </summary>
        public static void ClearSince(DateTime cutoff)
        {
            try
            {
                lock (_fileLock)
                {
                    var sessions = Load();
                    var toKeep = sessions
                        .Where(s => s.Id == _activeSessionId || s.StartTime < cutoff)
                        .ToList();
                    Save(toKeep);
                }
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
                // Ids closed by THIS pass — the short-session rule below must apply only to
                // these. Filtering by EndReason=="Interrupted" instead would also match
                // sessions closed by earlier runs and silently delete existing history.
                var closedNow = new HashSet<string>(StringComparer.Ordinal);

                foreach (var s in sessions.Where(s => s.EndTime == null && s.EndReason == null))
                {
                    s.EndReason = "Interrupted";
                    closedNow.Add(s.Id);

                    // Attempt to recover telemetry from the periodic checkpoint.
                    // The checkpoint holds aggregated stats up to the last flush
                    // (every 30 s), written atomically during an active session.
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
                        s.HostLatencyTimeSeries = cp.HostLatencySeries.Count > 0 ? cp.HostLatencySeries : null;
                        s.HostGpuTimeSeries = cp.HostGpuSeries.Count > 0 ? cp.HostGpuSeries : null;
                        s.HostEncTimeSeries = cp.HostEncSeries.Count > 0 ? cp.HostEncSeries : null;
                        s.HostCpuTimeSeries = cp.HostCpuSeries.Count > 0 ? cp.HostCpuSeries : null;
                        s.HostPowerTimeSeries      = cp.HostPowerSeries is { Count: > 0 }      ? cp.HostPowerSeries      : null;
                        s.HostPowerLimitTimeSeries = cp.HostPowerLimitSeries is { Count: > 0 } ? cp.HostPowerLimitSeries : null;
                        s.HostGpuTempTimeSeries    = cp.HostGpuTempSeries is { Count: > 0 }    ? cp.HostGpuTempSeries    : null;
                        s.HostPowerCappedTimeSeries = cp.HostPowerCappedSeries is { Count: > 0 } ? cp.HostPowerCappedSeries : null;
                        s.StreamSpans       = cp.StreamSpans is { Count: > 0 } ? cp.StreamSpans : null;
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

                // Same rule as EndSession, applied ONLY to the sessions this pass just closed
                // (tracked by id): an interrupted stream that lasted seconds is noise. History
                // written by earlier runs is never touched — silently deleting the user's
                // existing records at startup is not something a launch should do.
                if (closedNow.Count > 0)
                {
                    int dropped = sessions.RemoveAll(s =>
                        closedNow.Contains(s.Id) && s.EndTime != null &&
                        (s.EndTime.Value - s.StartTime).TotalSeconds < MinSessionSeconds);
                    if (dropped > 0)
                        DebugLogger.Log($"SessionLogger: discarded {dropped} interrupted session(s) shorter than {MinSessionSeconds}s");

                    // Same opt-in rule as EndSession, on the same closed-by-this-pass set.
                    // The recovered GamesDetected comes from the 30 s checkpoint, so a game
                    // launched in the last half-minute before the interruption is lost and
                    // the session is dropped — the price of not having an end-of-session list.
                    if (RecordOnlyGameSessions)
                    {
                        int noGame = sessions.RemoveAll(s =>
                            closedNow.Contains(s.Id) && !s.IsDebugSession &&
                            (s.GamesDetected == null || s.GamesDetected.Count == 0));
                        if (noGame > 0)
                            DebugLogger.Log($"SessionLogger: discarded {noGame} interrupted session(s) with no game launched (RecordOnlyGameSessions)");
                    }
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
            List<float> decodeSeries,
            List<float> hostLatencySeries,
            List<float> hostGpuSeries,
            List<float> hostEncSeries,
            List<float> hostCpuSeries,
            List<float>? hostPowerSeries      = null,
            List<float>? hostPowerLimitSeries = null,
            List<float>? hostGpuTempSeries    = null,
            List<float>? hostPowerCappedSeries = null)
        {
            try
            {
                lock (_fileLock)
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
                    entry.HostLatencyTimeSeries = hostLatencySeries.Count > 0 ? hostLatencySeries : null;
                    entry.HostGpuTimeSeries = hostGpuSeries.Count > 0 ? hostGpuSeries : null;
                    entry.HostEncTimeSeries = hostEncSeries.Count > 0 ? hostEncSeries : null;
                    entry.HostCpuTimeSeries = hostCpuSeries.Count > 0 ? hostCpuSeries : null;
                    entry.HostPowerTimeSeries      = hostPowerSeries is { Count: > 0 }      ? hostPowerSeries      : null;
                    entry.HostPowerLimitTimeSeries = hostPowerLimitSeries is { Count: > 0 } ? hostPowerLimitSeries : null;
                    entry.HostGpuTempTimeSeries    = hostGpuTempSeries is { Count: > 0 }    ? hostGpuTempSeries    : null;
                    entry.HostPowerCappedTimeSeries = hostPowerCappedSeries is { Count: > 0 } ? hostPowerCappedSeries : null;
                    entry.StreamSpans       = SnapshotStreamSpans();
                    Save(sessions);
                }
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
                lock (_fileLock)
                {
                    var sessions = Load();
                    var entry = sessions.FirstOrDefault(s => s.Id == sessionId);
                    if (entry?.EndTime == null)
                    {
                        entry!.EndTime = DateTime.Now;
                        entry.EndReason = endReason;
                        RecordStreamStop(entry.EndTime.Value);
                        entry.StreamSpans = SnapshotStreamSpans();
                        try
                        {
                            var gameSpans = ActiveGameSpansProvider?.Invoke();
                            if (gameSpans is { Count: > 0 }) entry.GameSpans = gameSpans;
                        }
                        catch { /* the ranges are a nicety; never lose the session over them */ }
                        ActiveGameSpansProvider = null;

                        // Too short to be a real session — drop it from history entirely
                        // rather than finalising it (see MinSessionSeconds).
                        if ((entry.EndTime.Value - entry.StartTime).TotalSeconds < MinSessionSeconds)
                        {
                            sessions.Remove(entry);
                            Save(sessions);
                            DeleteCheckpoint();
                            return;
                        }

                        // Opt-in: keep only sessions that actually launched a game.
                        // A null list means the process monitor never ran, which is
                        // indistinguishable from "no game" here — both are dropped.
                        // Debug sessions are exempt: they never have a game.
                        if (RecordOnlyGameSessions && !entry.IsDebugSession &&
                            (gamesDetected == null || gamesDetected.Count == 0))
                        {
                            sessions.Remove(entry);
                            Save(sessions);
                            DeleteCheckpoint();
                            DebugLogger.Log("SessionLogger: discarded session with no game launched (RecordOnlyGameSessions)");
                            return;
                        }

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
                // Atomic write: .tmp → File.Move overwrite — same pattern as GameLibraryState
                // and CoverArtFetcher. Prevents a truncated sessions.json if the process is
                // killed mid-write (host shutdown, crash), which would silently wipe all history.
                string tmp = LogPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(sessions,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, LogPath, overwrite: true);
            }
        }
    }
}
