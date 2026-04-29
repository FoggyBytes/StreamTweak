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
    /// Fetches and caches game metadata (developer, release date) from external APIs.
    ///
    /// Resolution order:
    ///   Steam games   → Steam Store API  (appdetails endpoint, no key required)
    ///   Store games   → RAWG.io API      (requires a free API key — set RawgApiKey)
    ///   Manual games  → PCGamingWiki     (last resort; may be blocked by Cloudflare)
    ///
    /// To get a free RAWG API key: https://rawg.io/apidocs
    /// Set the key in config.json: { "RawgApiKey": "your-key-here" }
    /// or programmatically: GameMetadataService.RawgApiKey = "your-key-here";
    ///
    /// Cache is loaded from disk at startup and refreshed in background on every launch.
    /// Failed fetches never wipe previously good cache entries (cache is seeded, not rebuilt).
    /// </summary>
    public static class GameMetadataService
    {
        public record GameMetadata(string? Developer, string? ReleaseDate);

        // Set at startup by App.xaml.cs from ConfigService ("RawgApiKey" config key).
        // A free key is required — without it RAWG returns 401 and no data is fetched.
        public static string RawgApiKey { get; set; } = string.Empty;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        static GameMetadataService()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string ver = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "6.2.2";
            _http.DefaultRequestHeaders.Add("User-Agent", $"StreamTweak/{ver} (FoggyBytes)");
        }

        private static Dictionary<string, GameMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new();

        /// <summary>Fired on a background thread when the refresh cycle completes.</summary>
        public static event Action? CacheRefreshed;

        private static string CacheFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "gamemetadata.json");

        private static string GetKey(GameLibraryEntry g) =>
            g.SteamAppId != null ? $"steam:{g.SteamAppId}" : $"{g.Store}:{g.Name}";

        // True when value is real data (not null, not empty, not the N/A sentinel).
        private static bool IsRealValue(string? v) =>
            !string.IsNullOrWhiteSpace(v) && v != "N/A";

        /// <summary>Returns the cached metadata for a game, or null if not yet fetched.</summary>
        public static GameMetadata? GetCached(GameLibraryEntry game)
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
                var loaded = JsonSerializer.Deserialize<Dictionary<string, GameMetadata>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null)
                    lock (_cacheLock)
                        _cache = new Dictionary<string, GameMetadata>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        /// <summary>
        /// Refreshes metadata for every game in the list and fires <see cref="CacheRefreshed"/>.
        /// Safe to call on a background thread. Failed fetches are logged and skipped; the
        /// cache is seeded from the previous run so no previously good entry is ever lost.
        /// </summary>
        public static async Task RefreshAsync(IReadOnlyList<GameLibraryEntry> games)
        {
            if (games.Count == 0)
            {
                DebugLogger.Log("[Meta] Refresh skipped — game list empty (cache preserved)");
                return;
            }

            if (string.IsNullOrEmpty(RawgApiKey))
                DebugLogger.Log("[Meta] WARNING: RawgApiKey not set — RAWG fetch will fail. " +
                                "Get a free key at https://rawg.io/apidocs and set 'RawgApiKey' in config.json");

            DebugLogger.Log($"[Meta] Refresh started — {games.Count} games");

            // Seed from current cache so fetch failures preserve previously good data.
            Dictionary<string, GameMetadata> newCache;
            lock (_cacheLock)
                newCache = new Dictionary<string, GameMetadata>(_cache, StringComparer.OrdinalIgnoreCase);

            foreach (var game in games)
            {
                string key = GetKey(game);

                if (game.SteamAppId != null)
                {
                    // Steam: always refresh via Steam Store API (no key required)
                    try
                    {
                        GameMetadata? data = await FetchBySteamApiAsync(game.SteamAppId);
                        if (data != null) newCache[key] = data;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[Steam API] ERROR {game.Name}: {ex.Message}");
                    }
                    await Task.Delay(400);
                }
                else if (!game.IsManual)
                {
                    // All store games (Epic, GOG, Ubisoft, Xbox, Battle.net, EA App): RAWG
                    // Skip if both fields are already real values in the cache.
                    if (newCache.TryGetValue(key, out var cached)
                        && IsRealValue(cached.Developer)
                        && IsRealValue(cached.ReleaseDate))
                        continue;

                    string? dev = null, date = null;
                    try
                    {
                        GameMetadata? rawg = await FetchByRawgAsync(game.Name);
                        dev  = rawg?.Developer;
                        date = rawg?.ReleaseDate;
                        DebugLogger.Log($"[RAWG] '{game.Name}' → dev={dev} date={date}");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[RAWG] ERROR {game.Name}: {ex.Message}");
                    }
                    await Task.Delay(500);

                    // Always write for store games; N/A for fields RAWG could not provide.
                    newCache[key] = new GameMetadata(dev ?? "N/A", date ?? "N/A");
                }
                else
                {
                    // Manual games: PCGamingWiki as last resort (only if not already cached)
                    if (newCache.ContainsKey(key)) continue;
                    try
                    {
                        GameMetadata? data = await FetchByWikiSearchAsync(game.Name);
                        if (data != null) newCache[key] = data;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[Wiki] ERROR {game.Name}: {ex.Message}");
                    }
                    await Task.Delay(700);
                }
            }

            lock (_cacheLock) { _cache = newCache; }
            SaveCache(newCache);
            DebugLogger.Log($"[Meta] Refresh complete — {newCache.Count}/{games.Count} games matched");
            CacheRefreshed?.Invoke();
        }

        // ── Steam Store API ───────────────────────────────────────────────────

        private static async Task<GameMetadata?> FetchBySteamApiAsync(string steamAppId)
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={steamAppId}&l=english";
            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty(steamAppId, out var entry)) return null;
            if (!entry.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            if (!entry.TryGetProperty("data", out var data)) return null;

            string? developer = null;
            if (data.TryGetProperty("developers", out var devs) && devs.GetArrayLength() > 0)
                developer = devs[0].GetString();

            string? releaseDate = null;
            if (data.TryGetProperty("release_date", out var rd)
                && rd.TryGetProperty("date", out var dateEl))
            {
                string? raw = dateEl.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (DateTime.TryParse(raw, CultureInfo.GetCultureInfo("en-US"),
                            DateTimeStyles.None, out var parsed))
                        releaseDate = parsed.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                    else
                        releaseDate = raw;
                }
            }

            if (developer == null && releaseDate == null) return null;
            var result = new GameMetadata(developer, releaseDate);
            DebugLogger.Log($"[Steam API] '{steamAppId}' → dev={result.Developer} date={result.ReleaseDate}");
            return result;
        }

        // ── RAWG.io ───────────────────────────────────────────────────────────
        // Two-step fetch: search → get id + released; detail → get developers[].
        // Requires a free API key (rawg.io/apidocs). Without a key RAWG returns 401.

        private static string RawgSearchUrl(string name) =>
            $"https://api.rawg.io/api/games?search={Uri.EscapeDataString(name)}" +
            $"&page_size=5&search_precise=true" +
            (string.IsNullOrEmpty(RawgApiKey) ? "" : $"&key={Uri.EscapeDataString(RawgApiKey)}");

        private static string RawgDetailUrl(int id) =>
            $"https://api.rawg.io/api/games/{id}" +
            (string.IsNullOrEmpty(RawgApiKey) ? "" : $"?key={Uri.EscapeDataString(RawgApiKey)}");

        private static async Task<GameMetadata?> FetchByRawgAsync(string gameName)
        {
            string searchJson = await _http.GetStringAsync(RawgSearchUrl(gameName));
            using var searchDoc = JsonDocument.Parse(searchJson);

            if (!searchDoc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
                return null;

            int    matchId    = 0;
            string? releaseDate = null;
            string? developer   = null;
            string  nameNorm    = NormalizeForMatch(gameName);

            foreach (var item in results.EnumerateArray())
            {
                string? resultName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(resultName)) continue;
                if (!IsGoodNameMatch(nameNorm, NormalizeForMatch(resultName))) continue;

                matchId = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;

                if (item.TryGetProperty("released", out var relEl)
                    && relEl.ValueKind != JsonValueKind.Null)
                {
                    string? raw = relEl.GetString();
                    if (!string.IsNullOrEmpty(raw)
                        && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var dt))
                        releaseDate = dt.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                }

                // developers[] may be present in search results on some API tiers
                if (item.TryGetProperty("developers", out var devs)
                    && devs.ValueKind == JsonValueKind.Array
                    && devs.GetArrayLength() > 0
                    && devs[0].TryGetProperty("name", out var dn))
                    developer = dn.GetString();

                break; // first good match wins
            }

            if (matchId == 0) return releaseDate != null || developer != null
                ? new GameMetadata(developer, releaseDate) : null;

            // Step 2: detail call to get developers[] when search didn't include them
            if (string.IsNullOrEmpty(developer))
            {
                try
                {
                    string detailJson = await _http.GetStringAsync(RawgDetailUrl(matchId));
                    using var detailDoc = JsonDocument.Parse(detailJson);

                    if (detailDoc.RootElement.TryGetProperty("developers", out var devArr)
                        && devArr.ValueKind == JsonValueKind.Array
                        && devArr.GetArrayLength() > 0
                        && devArr[0].TryGetProperty("name", out var devName))
                        developer = devName.GetString();
                }
                catch { /* detail call failed — use what we have from search */ }
            }

            return new GameMetadata(developer, releaseDate);
        }

        private static string NormalizeForMatch(string s) =>
            Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

        private static bool IsGoodNameMatch(string a, string b) =>
            a == b || a.Contains(b) || b.Contains(a);

        // ── PCGamingWiki (last resort for manual games) ───────────────────────

        private static async Task<GameMetadata?> FetchByWikiSearchAsync(string gameName)
        {
            string? pageTitle = await FetchWikiPageTitleAsync(gameName);
            if (pageTitle == null) return null;

            string? markup = await FetchWikiMarkupAsync(pageTitle);
            if (markup == null) return null;

            string? developer   = ExtractFirstTemplateArg(markup, "Infobox game/row/developer");
            string? releaseDate = ExtractWindowsOrFirstReleaseDate(markup);

            if (developer == null && releaseDate == null) return null;

            var result = new GameMetadata(developer, releaseDate);
            DebugLogger.Log($"[Wiki] '{pageTitle}' → dev={result.Developer} date={result.ReleaseDate}");
            return result;
        }

        private static async Task<string?> FetchWikiPageTitleAsync(string name)
        {
            string url = "https://www.pcgamingwiki.com/w/api.php" +
                $"?action=opensearch&search={Uri.EscapeDataString(name)}&limit=1&format=json";
            string json  = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
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
                if (page.Name == "-1") return null;
                if (!page.Value.TryGetProperty("revisions", out var revs)) continue;
                if (revs.GetArrayLength() == 0) continue;
                var rev = revs[0];
                if (rev.TryGetProperty("slots", out var slots)
                    && slots.TryGetProperty("main", out var main)
                    && main.TryGetProperty("*", out var sc))
                    return sc.GetString();
                if (rev.TryGetProperty("*", out var dc))
                    return dc.GetString();
            }
            return null;
        }

        private static string? ExtractFirstTemplateArg(string markup, string templateName)
        {
            var m = Regex.Match(markup,
                @"\{\{" + Regex.Escape(templateName) + @"\|([^|}\n]+)",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

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

        // ── Cache persistence ─────────────────────────────────────────────────

        private static void SaveCache(Dictionary<string, GameMetadata> cache)
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
