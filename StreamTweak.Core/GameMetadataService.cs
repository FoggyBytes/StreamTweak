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
    /// Fetches and caches game metadata (developer, release date) from Steam Store API.
    ///
    /// Resolution order for ALL games (Steam, store, manual):
    ///   1. If SteamAppId is known → fetch directly via appdetails endpoint
    ///   2. Otherwise             → search Steam by name to resolve AppId, then fetch details
    ///
    /// No API key required — Steam Store API is free and public.
    ///
    /// Cache is loaded from disk at startup and refreshed in background on every launch.
    /// Failed fetches never wipe previously good cache entries (cache is seeded, not rebuilt).
    /// </summary>
    public static class GameMetadataService
    {
        public record GameMetadata(string? Developer, string? ReleaseDate);

        // Bump this when the fetch logic changes in a way that makes previously cached
        // values incorrect (e.g. a different cc/locale shifts release dates by a day).
        // On a mismatch, LoadFromDisk discards the file and the background refresh
        // rebuilds the cache from scratch on next launch.
        private const int CacheSchemaVersion = 3;

        private sealed class CacheFile
        {
            public int SchemaVersion { get; set; }
            public Dictionary<string, GameMetadata> Entries { get; set; } = new();
        }

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // Country code derived from Windows regional settings. Steam returns the
        // release_date.date string already localized for the (cc, l) pair, so passing
        // the user's actual region keeps the date in the user's timezone — without
        // cc, Steam defaults to US Pacific and a global midnight-UTC launch can show
        // as the previous day.
        private static readonly string _cc = ResolveCountryCode();

        private static string ResolveCountryCode()
        {
            try
            {
                string c = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return string.IsNullOrWhiteSpace(c) ? "US" : c;
            }
            catch { return "US"; }
        }

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
                var loaded = JsonSerializer.Deserialize<CacheFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (loaded == null || loaded.SchemaVersion != CacheSchemaVersion)
                {
                    DebugLogger.Log($"[Meta] Cache schema mismatch (file={loaded?.SchemaVersion ?? 0}, expected={CacheSchemaVersion}) — discarding cache, will rebuild");
                    return;
                }

                lock (_cacheLock)
                    _cache = new Dictionary<string, GameMetadata>(loaded.Entries, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        /// <summary>
        /// Refreshes metadata for every game in the list via Steam Store API and fires
        /// <see cref="CacheRefreshed"/>. Safe to call on a background thread.
        /// For games without a known SteamAppId, a name search is performed first to resolve it.
        /// Failed fetches are logged and skipped; previously good cache entries are never lost.
        /// </summary>
        public static async Task RefreshAsync(IReadOnlyList<GameLibraryEntry> games)
        {
            if (games.Count == 0)
            {
                DebugLogger.Log("[Meta] Refresh skipped — game list empty (cache preserved)");
                return;
            }

            DebugLogger.Log($"[Meta] Refresh started — {games.Count} games");

            // Seed from current cache so fetch failures preserve previously good data.
            Dictionary<string, GameMetadata> newCache;
            lock (_cacheLock)
                newCache = new Dictionary<string, GameMetadata>(_cache, StringComparer.OrdinalIgnoreCase);

            foreach (var game in games)
            {
                string key = GetKey(game);

                // Skip if both fields are already real values in the cache.
                if (newCache.TryGetValue(key, out var cached)
                    && IsRealValue(cached.Developer)
                    && IsRealValue(cached.ReleaseDate))
                    continue;

                string? appId = game.SteamAppId;

                // For non-Steam games, try to resolve the AppId by searching Steam.
                if (appId == null)
                {
                    try
                    {
                        appId = await SearchSteamAppIdAsync(game.Name);
                        if (appId != null)
                            DebugLogger.Log($"[Steam Search] '{game.Name}' → AppId={appId}");
                        else
                            DebugLogger.Log($"[Steam Search] '{game.Name}' → not found on Steam");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[Steam Search] ERROR {game.Name}: {ex.Message}");
                    }
                    await Task.Delay(400);
                }

                if (appId != null)
                {
                    try
                    {
                        GameMetadata? data = await FetchBySteamApiAsync(appId);
                        if (data != null)
                        {
                            newCache[key] = data;
                            DebugLogger.Log($"[Steam API] '{game.Name}' (AppId={appId}) → dev={data.Developer} date={data.ReleaseDate}");
                        }
                        else
                        {
                            DebugLogger.Log($"[Steam API] '{game.Name}' (AppId={appId}) → no data returned");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[Steam API] ERROR {game.Name}: {ex.Message}");
                    }
                    await Task.Delay(400);
                }
                else
                {
                    // Not on Steam (e.g. Epic-exclusive). Fall back to Wikidata, which — unlike
                    // PCGamingWiki (Cloudflare-walled) or the Epic local catalog (publisher-as-dev
                    // + catalog-add date) — returns the real studio and release date key-free.
                    GameMetadata? wd = null;
                    try { wd = await WikidataMetadataService.FetchByNameAsync(game.Name); }
                    catch (Exception ex) { DebugLogger.Log($"[Wikidata] ERROR {game.Name}: {ex.Message}"); }

                    if (wd != null && (IsRealValue(wd.Developer) || IsRealValue(wd.ReleaseDate)))
                    {
                        newCache[key] = new GameMetadata(
                            IsRealValue(wd.Developer) ? wd.Developer : "N/A",
                            IsRealValue(wd.ReleaseDate) ? wd.ReleaseDate : "N/A");
                        DebugLogger.Log($"[Wikidata] '{game.Name}' → dev={wd.Developer} date={wd.ReleaseDate}");
                    }
                    else
                    {
                        // Not found anywhere — write N/A.
                        newCache[key] = new GameMetadata("N/A", "N/A");
                        DebugLogger.Log($"[Wikidata] '{game.Name}' → not found");
                    }
                    await Task.Delay(400);
                }
            }

            lock (_cacheLock) { _cache = newCache; }
            SaveCache(newCache);
            DebugLogger.Log($"[Meta] Refresh complete — {newCache.Count}/{games.Count} games matched");
            CacheRefreshed?.Invoke();
        }

        // ── Steam Store Search ────────────────────────────────────────────────
        // Resolves a game name to a Steam AppId using the Steam search suggest endpoint.
        // Returns the first result whose name matches well enough, or null if not found.

        private static async Task<string?> SearchSteamAppIdAsync(string gameName)
        {
            string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(gameName)}&l=english&cc={_cc}";
            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() == 0)
                return null;

            string nameNorm = NormalizeForMatch(gameName);

            foreach (var item in items.EnumerateArray())
            {
                string? resultName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(resultName)) continue;
                if (!IsGoodNameMatch(nameNorm, NormalizeForMatch(resultName))) continue;

                int id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                return id > 0 ? id.ToString() : null;
            }

            return null;
        }

        // ── Steam Store API ───────────────────────────────────────────────────

        private static async Task<GameMetadata?> FetchBySteamApiAsync(string steamAppId)
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={steamAppId}&l=english&cc={_cc}";
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
                    releaseDate = ParseSteamDate(raw);
            }

            if (developer == null && releaseDate == null) return null;
            return new GameMetadata(developer, releaseDate);
        }

        // Steam returns release_date.date as a free-text string whose format depends
        // on (cc, l). With l=english it can come back as either "May 19, 2026" (US
        // month-first) or "19 May, 2026" (EU day-first). Try cultures in order, fall
        // back to the raw string if none parse.
        private static string? ParseSteamDate(string raw)
        {
            CultureInfo[] cultures =
            {
                CultureInfo.GetCultureInfo("en-GB"), // day-first English
                CultureInfo.GetCultureInfo("en-US"), // month-first English
                CultureInfo.InvariantCulture,
            };

            foreach (var culture in cultures)
            {
                if (DateTime.TryParse(raw, culture, DateTimeStyles.None, out var parsed))
                    return parsed.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
            }

            return raw;
        }

        // ── Name matching helpers ─────────────────────────────────────────────

        private static string NormalizeForMatch(string s) =>
            Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

        private static bool IsGoodNameMatch(string a, string b) =>
            a == b || a.Contains(b) || b.Contains(a);

        // ── Cache persistence ─────────────────────────────────────────────────

        private static void SaveCache(Dictionary<string, GameMetadata> cache)
        {
            try
            {
                var file = new CacheFile
                {
                    SchemaVersion = CacheSchemaVersion,
                    Entries = cache,
                };
                string tmp = CacheFilePath + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(file,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, CacheFilePath, overwrite: true);
            }
            catch { }
        }
    }
}