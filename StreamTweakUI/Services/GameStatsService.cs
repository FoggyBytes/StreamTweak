namespace StreamTweak.Services
{
    /// <summary>What the session history says about one game (9.0 Library and Dashboard).</summary>
    public sealed class GameStats
    {
        public string Name { get; init; } = "";
        public DateTime? LastPlayed { get; set; }
        public int Sessions { get; set; }
        public double Minutes { get; set; }
        public QualityGrade? TypicalGrade { get; set; }

        /// <summary>Sessions that played this game, newest first.</summary>
        public List<SessionEntry> RecentSessions { get; } = new();
    }

    /// <summary>
    /// Per-game roll-ups computed from sessions.json: how long each game was streamed, how
    /// often, when last, and the grade its sessions usually got. Nothing new is stored — this
    /// is a read of the history the app already keeps, run off the UI thread by its callers.
    /// </summary>
    public static class GameStatsService
    {
        public static Dictionary<string, GameStats> Compute(IEnumerable<SessionEntry> sessions)
        {
            var map = new Dictionary<string, GameStats>(StringComparer.OrdinalIgnoreCase);
            var gradeSums = new Dictionary<string, (int Sum, int Count)>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in sessions.Where(s => s.EndTime != null && s.GamesDetected is { Count: > 0 })
                                      .OrderByDescending(s => s.StartTime))
            {
                var games = s.GamesDetected!;
                double sessionMinutes = (s.EndTime!.Value - s.StartTime).TotalMinutes;

                foreach (var name in games.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!map.TryGetValue(name, out var st))
                        map[name] = st = new GameStats { Name = name };

                    st.Sessions++;
                    st.RecentSessions.Add(s);
                    if (st.LastPlayed == null || s.StartTime > st.LastPlayed) st.LastPlayed = s.StartTime;

                    // Time played: the recorded range when there is one (9.0 sessions),
                    // otherwise an even share of the session between the games it detected.
                    var span = s.GameSpans?.FirstOrDefault(g =>
                        string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                    double minutes = span != null && span.End > span.Start
                        ? (span.End - span.Start).TotalMinutes
                        : sessionMinutes / games.Count;
                    st.Minutes += Math.Max(0, minutes);

                    if (s.Grade is QualityGrade.High or QualityGrade.Medium or QualityGrade.Low)
                    {
                        gradeSums.TryGetValue(name, out var g);
                        gradeSums[name] = (g.Sum + (int)s.Grade.Value, g.Count + 1);
                    }
                }
            }

            foreach (var (name, g) in gradeSums)
                if (g.Count > 0 && map.TryGetValue(name, out var st))
                    st.TypicalGrade = (QualityGrade)Math.Clamp((int)Math.Round((double)g.Sum / g.Count), 1, 3);

            return map;
        }

        /// <summary>"2 h 05" / "45 min" — the compact form used under covers.</summary>
        public static string FormatMinutes(double minutes)
        {
            int m = (int)Math.Round(minutes);
            if (m < 60) return $"{m} min";
            return $"{m / 60} h {m % 60:00}";
        }

        /// <summary>"Today" / "Yesterday" / weekday within a week / "12 Sep".</summary>
        public static string RelativeDay(DateTime when)
        {
            int days = (DateTime.Today - when.Date).Days;
            return days switch
            {
                <= 0 => "Today",
                1    => "Yesterday",
                < 7  => when.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture),
                _    => when.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        public static (string Label, string Fg, string Bg, string Border) GradeColors(QualityGrade? g) => g switch
        {
            QualityGrade.High   => ("Excellent", "#4ade80", "#214ade80", "#594ade80"),
            QualityGrade.Medium => ("Good",      "#fbbf24", "#21fbbf24", "#59fbbf24"),
            QualityGrade.Low    => ("Poor",      "#fca5a5", "#21f87171", "#59f87171"),
            _                   => ("—",         "#C8CFCB", "#0FFFFFFF", "#24FFFFFF"),
        };

        /// <summary>Solid grade colour for stripes and bars.</summary>
        public static string GradeStripe(QualityGrade? g) => g switch
        {
            QualityGrade.High   => "#4ade80",
            QualityGrade.Medium => "#fbbf24",
            QualityGrade.Low    => "#f87171",
            _                   => "#646B67",
        };
    }
}
