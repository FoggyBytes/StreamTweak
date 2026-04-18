using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Fetches and caches game metadata from multiple sources.
    ///
    /// Data sources by store:
    ///   Steam     → store.steampowered.com/api/appdetails (official; developer + date + Metacritic, 1 call)
    ///   Non-Steam → PCGamingWiki opensearch + wiki markup  (developer + date + Metacritic, 2 calls)
    ///
    /// Cache is loaded from disk at startup for immediate display and refreshed
    /// in the background on every launch (Metacritic scores can change over time).
    /// </summary>
    public static class PcgwMetadataService
    {
        public record PcgwGameData(
            string? Developer,
            string? ReleaseDate);

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        static PcgwMetadataService()
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "StreamTweak/5.4.0 (FoggyBytes)");
        }

        private static Dictionary<string, PcgwGameData> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new();

        /// <summary>Fired on a background thread when the refresh cycle completes.</summary>
        public static event Action? CacheRefreshed;

        private static string CacheFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "gamemetadata.json");

        private static string GetKey(GameLibraryEntry g) =>
            g.SteamAppId != null ? $"steam:{g.SteamAppId}" : $"{g.Store}:{g.Name}";

        /// <summary>Returns the cached data for a game, or null if not yet fetched.</summary>
        public static PcgwGameData? GetCached(GameLibraryEntry game)
        {
            lock (_cacheLock)
                return _cache.TryGetValue(GetKey(game), out var d) ? d : null;
        }

        /// <summary>
        /// Loads the previous run's cache from disk. Synchronous and fast — call once at startup
        /// so data is available for immediate display before the background refresh completes.
        /// </summary>
        public static void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(CacheFilePath)) return;
                string json = File.ReadAllText(CacheFilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, PcgwGameData>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null)
                    lock (_cacheLock)
                        _cache = new Dictionary<string, PcgwGameData>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        /// <summary>
        /// Fetches fresh metadata for every game in the list.
        /// Replaces the entire cache when done and fires <see cref="CacheRefreshed"/>.
        /// Intended to run on a background thread at each app startup.
        /// </summary>
        public static async Task RefreshAsync(IReadOnlyList<GameLibraryEntry> games)
        {
            DebugLogger.Log($"[PCGW] Refresh started — {games.Count} games");
            var newCache = new Dictionary<string, PcgwGameData>(StringComparer.OrdinalIgnoreCase);

            foreach (var game in games)
            {
                try
                {
                    PcgwGameData? data = await FetchGameDataAsync(game);
                    if (data != null)
                        newCache[GetKey(game)] = data;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[PCGW] ERROR {game.Name}: {ex.Message}");
                }

                // Steam: 1 call → 400 ms.  Non-Steam: 2 calls → 700 ms.
                await Task.Delay(game.SteamAppId != null ? 400 : 700);
            }

            lock (_cacheLock) { _cache = newCache; }
            SaveCache(newCache);
            DebugLogger.Log($"[PCGW] Refresh complete — {newCache.Count}/{games.Count} games matched");
            CacheRefreshed?.Invoke();
        }

        // ── Top-level dispatcher ──────────────────────────────────────────────

        private static Task<PcgwGameData?> FetchGameDataAsync(GameLibraryEntry game) =>
            game.SteamAppId != null
                ? FetchBySteamApiAsync(game.SteamAppId)
                : FetchByPcgwAsync(game.Name);

        // ── Path A: Steam Store API ───────────────────────────────────────────

        /// <summary>
        /// Calls the official Steam Store API to get developer, release date, and
        /// Metacritic score in one round-trip. No PCGamingWiki dependency for Steam games.
        /// </summary>
        private static async Task<PcgwGameData?> FetchBySteamApiAsync(string steamAppId)
        {
            // l=english ensures English month names in the date string regardless of system locale.
            string url = $"https://store.steampowered.com/api/appdetails?appids={steamAppId}&l=english";

            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            // Root: { "730": { "success": true, "data": { ... } } }
            if (!doc.RootElement.TryGetProperty(steamAppId, out var entry)) return null;
            if (!entry.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            if (!entry.TryGetProperty("data", out var data)) return null;

            // Developer — first entry of the "developers" array
            string? developer = null;
            if (data.TryGetProperty("developers", out var devs) && devs.GetArrayLength() > 0)
                developer = devs[0].GetString();

            // Release date — "date" field inside "release_date" object; skip "coming soon" strings
            string? releaseDate = null;
            if (data.TryGetProperty("release_date", out var rd)
                && rd.TryGetProperty("date", out var dateEl))
            {
                string? raw = dateEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    // Steam format: "27 Sep, 2023" or "21 Aug, 2012"
                    // Parse with en-US and reformat consistently as "MMMM d, yyyy".
                    if (DateTime.TryParse(raw, CultureInfo.GetCultureInfo("en-US"),
                            DateTimeStyles.None, out var parsed))
                        releaseDate = parsed.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                    else
                        releaseDate = raw; // keep as-is if parse fails (e.g. "Coming soon")
                }
            }

            if (developer == null && releaseDate == null) return null;

            var result = new PcgwGameData(developer, releaseDate);
            DebugLogger.Log($"[Steam API] '{steamAppId}' → dev={result.Developer} date={result.ReleaseDate}");
            return result;
        }

        // ── Path B: PCGamingWiki (non-Steam) ──────────────────────────────────

        /// <summary>
        /// Two-step PCGamingWiki lookup for non-Steam games:
        /// 1. opensearch → canonical page title
        /// 2. wiki markup → developer, release date (regex)
        /// </summary>
        private static async Task<PcgwGameData?> FetchByPcgwAsync(string gameName)
        {
            // Step 1: page title
            string? pageTitle = await FetchPageTitleByNameAsync(gameName);
            if (pageTitle == null) return null;

            // Step 2: all fields from wiki markup
            string? markup = await FetchWikiMarkupAsync(pageTitle);
            if (markup == null) return null;

            string? developer   = ExtractFirstTemplateArg(markup, "Infobox game/row/developer");
            string? releaseDate = ExtractWindowsOrFirstReleaseDate(markup);

            if (developer == null && releaseDate == null) return null;

            var result = new PcgwGameData(developer, releaseDate);
            DebugLogger.Log($"[PCGW] '{pageTitle}' → dev={result.Developer} date={result.ReleaseDate}");
            return result;
        }

        // ── PCGamingWiki helpers ──────────────────────────────────────────────

        private static async Task<string?> FetchPageTitleByNameAsync(string name)
        {
            string url = "https://www.pcgamingwiki.com/w/api.php" +
                $"?action=opensearch&search={Uri.EscapeDataString(name)}&limit=1&format=json";

            string json  = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            // Response: [queryString, [titles], [descriptions], [urls]]
            if (doc.RootElement.GetArrayLength() < 2) return null;
            var titles = doc.RootElement[1];
            if (titles.GetArrayLength() == 0) return null;

            return titles[0].GetString();
        }

        private static async Task<string?> FetchWikiMarkupAsync(string pageTitle)
        {
            string url = "https://www.pcgamingwiki.com/w/api.php" +
                "?action=query&prop=revisions&rvprop=content&rvslots=main" +
                $"&titles={Uri.EscapeDataString(pageTitle)}&format=json";

            string json  = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("query", out var q)) return null;
            if (!q.TryGetProperty("pages", out var pages)) return null;

            foreach (var page in pages.EnumerateObject())
            {
                if (page.Name == "-1") return null;   // page not found
                if (!page.Value.TryGetProperty("revisions", out var revs)) continue;
                if (revs.GetArrayLength() == 0) continue;

                var rev = revs[0];

                // MediaWiki 1.32+ slot format
                if (rev.TryGetProperty("slots", out var slots)
                    && slots.TryGetProperty("main", out var main)
                    && main.TryGetProperty("*", out var sc))
                    return sc.GetString();

                // Legacy format
                if (rev.TryGetProperty("*", out var dc))
                    return dc.GetString();
            }

            return null;
        }

        // ── Wiki markup parsers ───────────────────────────────────────────────

        /// <summary>Returns the first argument of {{templateName|ARG…}}.</summary>
        private static string? ExtractFirstTemplateArg(string markup, string templateName)
        {
            var m = Regex.Match(markup,
                @"\{\{" + Regex.Escape(templateName) + @"\|([^|}\n]+)",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>{{Infobox game/row/date|PLATFORM|DATE}} — Windows preferred.</summary>
        private static string? ExtractWindowsOrFirstReleaseDate(string markup)
        {
            var m = Regex.Match(markup,
                @"\{\{Infobox game/row/date\|Windows\|([^|}\n]+)\}\}",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(markup,
                    @"\{\{Infobox game/row/date\|[^|]+\|([^|}\n]+)\}\}",
                    RegexOptions.IgnoreCase);

            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        // ── Cache persistence ────────────────────────────────────────────────

        private static void SaveCache(Dictionary<string, PcgwGameData> cache)
        {
            try
            {
                string tmp = CacheFilePath + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, CacheFilePath, overwrite: true);
            }
            catch { }
        }
    }
}
