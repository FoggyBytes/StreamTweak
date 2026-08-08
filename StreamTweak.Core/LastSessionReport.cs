using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
// AsBuffer() / ToArray() on IBuffer live here, not in Windows.Storage.Streams. Without it
// the byte[] -> IBuffer hop does not compile and ToArray() binds to LINQ's instead.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StreamTweak
{
    /// <summary>
    /// Serves the host's most recent finished session over the bridge (LASTSESSION), so a
    /// StreamLight client can show on its own home screen what StreamTweak's Dashboard shows
    /// on the host: the grade, how long ago, how long it ran, and the three headline numbers.
    ///
    /// This is the host's last session, not the asking client's — StreamTweak logs whatever
    /// streamed, and it has no notion of which client a past session belonged to. The client
    /// labels it accordingly.
    ///
    /// The cover art is the one thing that cannot simply be named: it lives in
    /// %LOCALAPPDATA%\StreamTweak\covers\ and the client has no way to reach the host's disk.
    /// So it travels inline, downscaled to a thumbnail first — a Steam cover is 600x900 and
    /// would be a third of a megabyte of base64 for something drawn 100px wide. Thumbnails are
    /// cached by path + write time, so the encode happens once and not on every poll.
    /// </summary>
    public static class LastSessionReport
    {
        // The size the client draws it at. Sending anything larger is paying transport for
        // pixels that get thrown away on arrival.
        private const int ThumbWidth  = 100;
        private const int ThumbHeight = 150;

        // A session can credit several games (a launcher handing over, a second title started
        // mid-session). The card has room for a few; past that it is a list, not a picture.
        private const int MaxCovers = 3;

        private static readonly object _cacheLock = new();
        private static readonly Dictionary<string, (DateTime stamp, string b64)> _thumbCache = new();

        /// <summary>
        /// The JSON served for a LASTSESSION command. Never throws: the bridge must always
        /// have a line to write, and a client that gets {"has":false} simply draws nothing.
        /// </summary>
        public static string BuildJson()
        {
            try
            {
                // Sessions are stored newest-first (SessionLogger.StartSession inserts at 0),
                // so the first finished one is the last one that ran.
                var last = SessionLogger.Load().FirstOrDefault(s => s.EndTime != null);
                if (last == null)
                    return "{\"v\":1,\"has\":false}";

                var qs = last.QualityStats;

                var payload = new Dictionary<string, object?>
                {
                    ["v"]           = 1,
                    ["has"]         = true,
                    ["ago"]         = FormatAgo(DateTime.Now - last.EndTime!.Value),
                    ["started"]     = last.StartTimeDisplay,
                    ["duration"]    = last.DurationDisplay,
                    ["has_grade"]   = last.HasGrade,
                    ["grade"]       = last.HasGrade ? last.GradeShortLabel : "",
                    ["grade_color"] = last.GradeColorHex,
                    // -1 means "not measured", which the client shows as a dash. It is not the
                    // same as zero, and a client that renders 0 ms would be inventing a result.
                    ["rtt_ms"]          = qs != null ? MathF.Round(qs.RttAvgMs, 1) : -1f,
                    ["rtt_peak_ms"]     = qs != null ? MathF.Round(qs.RttMaxMs, 1) : -1f,
                    ["host_latency_ms"] = qs != null ? MathF.Round(qs.HostLatencyAvgMs, 1) : -1f,
                    ["drops_pct"]       = qs != null ? MathF.Round(qs.DropRatePct, 1) : -1f,
                    // How many the session actually credited, which is not games.Count: the
                    // list is capped at MaxCovers. The client needs the real figure to say
                    // "+2" — it has no other way to know anything was left out.
                    ["games_total"]     = last.GamesDetected?.Count ?? 0,
                    ["games"]           = BuildGames(last)
                };

                return JsonSerializer.Serialize(payload);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[LastSession] report failed: {ex}");
                return "{\"v\":1,\"has\":false}";
            }
        }

        private static List<Dictionary<string, string>> BuildGames(SessionEntry entry)
        {
            var games = new List<Dictionary<string, string>>();
            if (entry.GamesDetected == null || entry.GamesDetected.Count == 0)
                return games;

            // Same two-tier lookup the Dashboard uses: the path snapshotted when the session
            // ended wins, because it still resolves for a game since removed from the library;
            // the live library is the fallback for sessions that predate that snapshot.
            Dictionary<string, GameLibraryEntry> live;
            try
            {
                live = GameLibraryState.Current.Games
                    .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                live = new Dictionary<string, GameLibraryEntry>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (string name in entry.GamesDetected.Take(MaxCovers))
            {
                string? path = null;

                if (entry.GamesDetectedCoverPaths != null &&
                    entry.GamesDetectedCoverPaths.TryGetValue(name, out string? snap) &&
                    !string.IsNullOrEmpty(snap) && File.Exists(snap))
                {
                    path = snap;
                }
                else if (live.TryGetValue(name, out var g) &&
                         !string.IsNullOrEmpty(g.CoverImagePath) && File.Exists(g.CoverImagePath))
                {
                    path = g.CoverImagePath;
                }

                var item = new Dictionary<string, string> { ["name"] = name };
                string? thumb = path != null ? ThumbnailBase64(path) : null;
                if (thumb != null) item["cover"] = thumb;
                games.Add(item);
            }

            return games;
        }

        /// <summary>
        /// A base64 PNG thumbnail of the cover, or null when it cannot be produced — a missing
        /// picture costs the client a placeholder, a thrown exception would cost it the whole
        /// report.
        /// </summary>
        private static string? ThumbnailBase64(string path)
        {
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(path);

                lock (_cacheLock)
                {
                    if (_thumbCache.TryGetValue(path, out var hit) && hit.stamp == stamp)
                        return hit.b64;
                }

                // Task.Run so the WinRT async completions land on the thread pool with no
                // captured context — this runs on a bridge connection thread and must not
                // depend on anything the caller happens to be sitting on.
                string b64 = Task.Run(() => EncodeThumbnailAsync(path)).GetAwaiter().GetResult();

                lock (_cacheLock)
                {
                    _thumbCache[path] = (stamp, b64);
                }
                return b64;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[LastSession] thumbnail failed for '{path}': {ex.Message}");
                return null;
            }
        }

        private static async Task<string> EncodeThumbnailAsync(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            using var inRas = new InMemoryRandomAccessStream();
            await inRas.WriteAsync(bytes.AsBuffer());
            inRas.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inRas);

            using var outRas = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outRas);
            encoder.SetSoftwareBitmap(await decoder.GetSoftwareBitmapAsync());
            // Fant is the slowest of the WIC scalers and the only one that holds up shrinking
            // a 600x900 cover by six — and this runs once per cover, not per frame.
            encoder.BitmapTransform.ScaledWidth       = ThumbWidth;
            encoder.BitmapTransform.ScaledHeight      = ThumbHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            outRas.Seek(0);
            var buffer = new Windows.Storage.Streams.Buffer((uint)outRas.Size);
            await outRas.ReadAsync(buffer, (uint)outRas.Size, InputStreamOptions.None);
            return Convert.ToBase64String(buffer.ToArray());
        }

        // Matches the Dashboard's wording exactly, so the host and the client never disagree
        // about how long ago the same session was.
        private static string FormatAgo(TimeSpan t)
        {
            if (t.TotalMinutes < 1)  return "just now";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m ago";
            if (t.TotalHours   < 24) return $"{(int)t.TotalHours}h ago";
            return $"{(int)t.TotalDays}d ago";
        }
    }
}
