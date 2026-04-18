using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Uses Steam's IStoreBrowseService/GetItems API to enrich discovered Steam games with:
    ///   • Correct English display names (forces country_code "US")
    ///   • App type filtering (keeps only actual games, drops DLC/tools/demos)
    ///   • Cover art URLs (library_capsule_2x asset)
    ///
    /// Replaces the old store.steampowered.com/api/appdetails approach which returned
    /// localised names (Cyrillic Sackboy) and had no cover art URL.
    ///
    /// Batch requests (up to 50 IDs per call) keep API usage minimal.
    /// Non-Steam games are passed through unchanged.
    /// </summary>
    public static class SteamMetadataFetcher
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private const int BatchSize = 50;

        /// <summary>
        /// Enriches the discovered game list with data from the Steam IStoreBrowseService API.
        /// Returns a filtered list where non-game Steam entries have been removed.
        /// Each returned Steam game has its CoverUrl populated (when available).
        /// </summary>
        public static async Task<List<DiscoveredGame>> EnrichAsync(
            List<DiscoveredGame> games,
            IReadOnlyList<GameLibraryEntry> existingEntries)
        {
            var stateByAppId = existingEntries
                .Where(e => e.SteamAppId != null)
                .ToDictionary(e => e.SteamAppId!, StringComparer.OrdinalIgnoreCase);

            // Separate Steam vs non-Steam
            var result = new List<DiscoveredGame>();
            var steamGames = new List<DiscoveredGame>();

            foreach (var game in games)
            {
                if (game.Store != "Steam" || game.SteamAppId == null)
                {
                    result.Add(game);
                    continue;
                }

                // UserRenamed → honour user's choice, no API call
                if (stateByAppId.TryGetValue(game.SteamAppId, out var existing) && existing.UserRenamed)
                {
                    result.Add(game with { Name = existing.Name });
                    continue;
                }

                steamGames.Add(game);
            }

            if (steamGames.Count == 0)
                return result;

            // Batch fetch from IStoreBrowseService
            var enriched = new Dictionary<string, (string name, string? coverUrl, bool keep)>(
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < steamGames.Count; i += BatchSize)
            {
                var batch = steamGames.Skip(i).Take(BatchSize).ToList();
                try
                {
                    var batchResults = await FetchBatchAsync(batch);
                    foreach (var (appId, info) in batchResults)
                        enriched[appId] = info;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"SteamMetadataFetcher: batch {i / BatchSize} failed — {ex.Message}");
                    // On batch failure, keep original names with no cover URL
                    foreach (var g in batch)
                        enriched[g.SteamAppId!] = (g.Name, null, true);
                }
            }

            // Apply enrichment results
            foreach (var game in steamGames)
            {
                if (enriched.TryGetValue(game.SteamAppId!, out var info))
                {
                    if (info.keep)
                        result.Add(game with { Name = info.name, CoverUrl = info.coverUrl });
                    // else: filtered out (not a game — DLC, tool, demo, etc.)
                }
                else
                {
                    // API didn't return this game → keep as-is
                    result.Add(game);
                }
            }

            return result;
        }

        /// <summary>
        /// Calls IStoreBrowseService/GetItems/v1 with a batch of Steam app IDs.
        /// Returns appId → (name, coverUrl, keep) for each item in the response.
        /// </summary>
        private static async Task<Dictionary<string, (string name, string? coverUrl, bool keep)>> FetchBatchAsync(
            List<DiscoveredGame> batch)
        {
            var result = new Dictionary<string, (string, string?, bool)>(StringComparer.OrdinalIgnoreCase);

            // Build the ids array: [{"appid":123}, {"appid":456}, ...]
            var ids = new List<object>();
            foreach (var g in batch)
            {
                if (int.TryParse(g.SteamAppId, out int appIdInt))
                    ids.Add(new { appid = appIdInt });
            }

            if (ids.Count == 0) return result;

            var requestBody = new
            {
                ids,
                context = new { country_code = "US" },
                data_request = new
                {
                    include_basic_info = true,
                    include_assets = true,
                }
            };

            string inputJson = JsonSerializer.Serialize(requestBody);
            string url = $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/" +
                         $"?input_json={Uri.EscapeDataString(inputJson)}";

            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var response))
                return result;
            if (!response.TryGetProperty("store_items", out var items))
                return result;

            foreach (var item in items.EnumerateArray())
            {
                string appIdStr = item.TryGetProperty("appid", out var aid)
                    ? aid.GetInt32().ToString() : "";
                if (string.IsNullOrEmpty(appIdStr)) continue;

                int success = item.TryGetProperty("success", out var s) ? s.GetInt32() : 0;
                if (success != 1)
                {
                    // Not found in store — keep original name
                    var orig = batch.FirstOrDefault(g => g.SteamAppId == appIdStr);
                    if (orig != null) result[appIdStr] = (orig.Name, null, true);
                    continue;
                }

                // App type filtering:
                // IStoreBrowseService returns "type" (store item type) and optionally "app_type".
                // We want to keep only actual games. Known app_type values:
                //   0=Invalid, 1=Game, 2=Application, 3=Tool, 4=Demo, 5=Depots, 6=DLC, 7=Guide, 10=Mod
                // If app_type is available, filter by it. Otherwise keep the item.
                if (item.TryGetProperty("app_type", out var appType))
                {
                    int at = appType.GetInt32();
                    if (at != 0 && at != 1) // 0=Invalid (keep, might be untyped), 1=Game
                    {
                        result[appIdStr] = ("", null, false);
                        continue;
                    }
                }

                // Extract name
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name))
                {
                    var orig = batch.FirstOrDefault(g => g.SteamAppId == appIdStr);
                    name = orig?.Name ?? "";
                }

                // Extract cover art URL from assets
                string? coverUrl = null;
                if (item.TryGetProperty("assets", out var assets))
                {
                    string? urlFormat = assets.TryGetProperty("asset_url_format", out var f)
                        ? f.GetString() : null;
                    string? capsule = assets.TryGetProperty("library_capsule_2x", out var c)
                        ? c.GetString() : null;

                    if (!string.IsNullOrEmpty(urlFormat) && !string.IsNullOrEmpty(capsule))
                    {
                        string relativePath = urlFormat.Replace("${FILENAME}", capsule);
                        coverUrl = $"https://shared.fastly.steamstatic.com/store_item_assets/{relativePath}";
                    }
                    else if (!string.IsNullOrEmpty(capsule))
                    {
                        // Fallback: try known CDN prefixes
                        coverUrl = $"https://shared.fastly.steamstatic.com/store_item_assets/{capsule}";
                    }
                }

                result[appIdStr] = (name, coverUrl, true);
            }

            return result;
        }
    }
}
