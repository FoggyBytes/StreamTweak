using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Brings back the cover of a game the session history still names but whose picture is gone:
    /// the session's snapshot points to a file no longer in the cache, and the game left the
    /// library, so no sync will ever fetch it again (10.0.4, §95.9). Seen on 28/10/2026 for
    /// 007 First Light (snapshot <c>steam_3768760.png</c>) and Resonance: A Plague Tale Legacy
    /// (an Xbox one) — StreamLight's Sessions tab showed both without a cover.
    ///
    /// Only the sources that need nothing but the name work for a game that is not installed:
    /// the Steam app id the snapshot's file name carries, then the Steam Store search by name.
    /// The launchers' own caches (Epic, GOG, Ubisoft, Battle.net, Xbox) are gone with the game.
    ///
    /// ⚠️ The picture is written to a file of its own, <c>recovered_&lt;hash&gt;.png</c>, never
    /// over a name of the library's scheme: a later sync of the same game must find its cache
    /// exactly as it left it.
    /// </summary>
    public static class CoverRecovery
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "covers");

        // How long a failed name is left alone: the bridge asks for a cover every time a row is
        // drawn, and a name Steam does not know would otherwise cost a search each time.
        private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

        // Lazy, so the download starts once: GetOrAdd may run its factory twice when two callers
        // race, and two downloads of one name would share SaveBytesAsPngAsync's fixed ".tmp".
        private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _running =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> _failedAt =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex SteamSnapshot = new(@"^steam_(\d+)\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The file a recovered cover of this game lives in.</summary>
        public static string RecoveredPath(string name)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()));
            return Path.Combine(CacheDir, "recovered_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".png");
        }

        /// <summary>The recovered cover of this game, or null when there is none yet.</summary>
        public static string? Existing(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string path = RecoveredPath(name);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Fetches the cover once, however many callers ask at the same time. Null when nothing
        /// was found, or when the same name failed less than <see cref="RetryAfter"/> ago.
        /// </summary>
        public static Task<string?> RecoverAsync(string name, string? snapshotPath)
        {
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult<string?>(null);
            if (Existing(name) is string done) return Task.FromResult<string?>(done);
            if (_failedAt.TryGetValue(name, out var at) && DateTime.UtcNow - at < RetryAfter)
                return Task.FromResult<string?>(null);

            return _running.GetOrAdd(name, n => new Lazy<Task<string?>>(() => Task.Run(async () =>
            {
                try
                {
                    string? path = await FetchAsync(n, snapshotPath);
                    if (path == null) _failedAt[n] = DateTime.UtcNow;
                    else _failedAt.TryRemove(n, out _);
                    return path;
                }
                finally { _running.TryRemove(n, out _); }
            }))).Value;
        }

        private static async Task<string?> FetchAsync(string name, string? snapshotPath)
        {
            Directory.CreateDirectory(CacheDir);
            string target = RecoveredPath(name);
            string how = "";

            // The snapshot's own name says which Steam game it was: no search, no guessing.
            var m = SteamSnapshot.Match(Path.GetFileName(snapshotPath ?? ""));
            if (m.Success && await StoreCoverFetcher.TryFetchSteamCoverByAppIdAsync(m.Groups[1].Value, target))
                how = "Steam app " + m.Groups[1].Value;
            else if (await StoreCoverFetcher.TryFetchViaSteamSearchAsync(
                         new DiscoveredGame(name, "Steam", null, ""), target))
                how = "Steam search";

            bool ok = how.Length > 0 && File.Exists(target);
            DebugLogger.Log(ok
                ? $"[Covers] recovered '{name}' from {how}"
                : $"[Covers] no cover found for '{name}' (snapshot {(snapshotPath is null ? "-" : Path.GetFileName(snapshotPath))})");
            return ok ? target : null;
        }

        /// <summary>
        /// Once per run, a while after startup: every game of the history whose cover resolves
        /// nowhere, one at a time, so StreamTweak's Sessions page and StreamLight's find them
        /// ready. Costs nothing when every cover is there.
        /// </summary>
        public static async Task SweepAsync(CancellationToken token = default)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                var live = GameLibraryState.Current.Games
                    .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in SessionLogger.Load().OrderByDescending(s => s.StartTime))
                    foreach (var name in s.GamesDetected ?? new List<string>())
                    {
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                        if (LastSessionReport.CoverPathFor(s, name, live) != null) continue;
                        string? snap = null;
                        s.GamesDetectedCoverPaths?.TryGetValue(name, out snap);
                        missing[name] = snap;
                    }
                foreach (var kv in missing)
                {
                    token.ThrowIfCancellationRequested();
                    await RecoverAsync(kv.Key, kv.Value);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DebugLogger.Log($"[Covers] recovery sweep failed: {ex.Message}"); }
        }
    }
}
