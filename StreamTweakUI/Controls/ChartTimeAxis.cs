namespace StreamTweak.Controls
{
    /// <summary>
    /// Maps a position along a telemetry series to wall-clock time.
    /// Samples only accrue while a stream is live, so the axis walks the live spans and
    /// skips the idle gaps between them: the chart is compressed to active time and each
    /// gap is marked with a vertical line instead of being drawn as empty width.
    /// WARNING: pixel distance is therefore NOT proportional to wall-clock time across a
    /// gap. That is deliberate: the markers say where the jumps are, and the hover readout
    /// always reports the true timestamp of the sample under the pointer.
    /// </summary>
    public sealed class ChartTimeAxis
    {
        private readonly List<(DateTime Start, DateTime End, double Seconds, string? Encoder)> _spans = new();

        public ChartTimeAxis(IEnumerable<StreamSpan>? spans, DateTime? fallbackEnd)
        {
            if (spans != null)
            {
                foreach (var s in spans)
                {
                    DateTime end = s.End ?? fallbackEnd ?? s.Start;
                    double secs = (end - s.Start).TotalSeconds;
                    if (secs > 0) _spans.Add((s.Start, end, secs, s.Encoder));
                }
            }

            // No spans recorded: the session pre-dates the feature, and there is no way
            // to tell a single uninterrupted stream from a merged one after the fact.
            // Synthesising one span across StartTime..EndTime would be right for the
            // former and wrong for the latter, silently — the 11/09/2026 record held 11
            // streams with idle gaps, and every label after the first would be off by up
            // to ~110 s. So the axis stays unusable and the chart keeps "0 -> duration".

            ActiveSeconds = _spans.Sum(s => s.Seconds);
        }

        public double   ActiveSeconds  { get; }
        public TimeSpan ActiveDuration => TimeSpan.FromSeconds(ActiveSeconds);
        public bool     IsUsable       => _spans.Count > 0 && ActiveSeconds > 0;

        /// <summary>Wall-clock time at a 0..1 position across the series.</summary>
        public DateTime TimeAt(double t)
        {
            if (!IsUsable) return DateTime.MinValue;
            double target = Math.Clamp(t, 0, 1) * ActiveSeconds;
            double acc = 0;
            foreach (var s in _spans)
            {
                if (target <= acc + s.Seconds) return s.Start.AddSeconds(target - acc);
                acc += s.Seconds;
            }
            return _spans[^1].End;
        }

        /// <summary>
        /// Position in 0..1 of a wall-clock time — the inverse of <see cref="TimeAt"/>. A time
        /// that falls in an idle gap maps to the boundary where the next stream starts; times
        /// outside the session clamp to its edges. Used to place the game ranges (9.0).
        /// </summary>
        public double FractionAt(DateTime t)
        {
            if (!IsUsable) return 0;
            double acc = 0;
            foreach (var s in _spans)
            {
                if (t <= s.Start) return acc / ActiveSeconds;
                if (t <= s.End) return (acc + (t - s.Start).TotalSeconds) / ActiveSeconds;
                acc += s.Seconds;
            }
            return 1;
        }

        /// <summary>Number of live streams the session was made of.</summary>
        public int StreamCount => _spans.Count;

        /// <summary>
        /// Each stream as its 0..1 range on the axis, with the encoder its record names (null when
        /// unknown) — 9.1.0, §82. Same order and same streams as <see cref="GapFractions"/>.
        /// </summary>
        public IEnumerable<(double F0, double F1, string? Encoder)> Streams()
        {
            if (!IsUsable) yield break;
            double acc = 0;
            foreach (var s in _spans)
            {
                yield return (acc / ActiveSeconds, (acc + s.Seconds) / ActiveSeconds, s.Encoder);
                acc += s.Seconds;
            }
        }

        /// <summary>Positions in 0..1 where one stream ended and the next began.</summary>
        public IEnumerable<double> GapFractions()
        {
            if (!IsUsable) yield break;
            double acc = 0;
            for (int i = 0; i < _spans.Count - 1; i++)
            {
                acc += _spans[i].Seconds;
                yield return acc / ActiveSeconds;
            }
        }
    }
}
