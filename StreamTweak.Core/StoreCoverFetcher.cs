using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace StreamTweak
{
    /// <summary>
    /// Downloads game cover art using each store's native CDN or local files.
    /// No API keys required — all sources are public or local.
    ///
    /// Resolution order for non-Steam games:
    ///   1. Steam CDN (via AppId already resolved by GameMetadataService — no search needed)
    ///   2. Store-native source:
    ///        Epic Games  → catcache.bin (local CDN URL cache, no API call needed)
    ///        GOG         → local GOG Galaxy SQLite DB + webcache
    ///        Ubisoft     → local YAML config + ubistatic3-a.akamaihd.net CDN
    ///        Xbox        → Steam Store search by name (portrait cover) → local MicrosoftGame.config logos (fallback)
    ///        Battle.net  → aggregate.json logo_art_uri (local file, no API call needed)
    ///   3. GOG catalog/product API (last resort remote fallback, all stores)
    /// </summary>
    public static class StoreCoverFetcher
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// How tall a cover has to be before it is allowed to end the search.
        ///
        /// A shorter one is still written — some art beats none — but the chain keeps
        /// looking, because a launcher's own cache holds the thumbnail it draws in its
        /// grid, not the store's cover. GOG Galaxy keeps Cyberpunk 2077 at 342x482 while
        /// Steam publishes the same cover at 600x900, and the old "any non-empty bytes
        /// wins" rule stopped on the thumbnail every time.
        ///
        /// 600 is the height of the smallest asset the good sources return (Steam's
        /// library capsule is 600x900), so this accepts every one of them and rejects
        /// only what is genuinely a thumbnail.
        /// </summary>
        private const int GoodCoverHeight = 600;

        /// <summary>
        /// For every non-Steam game whose cover is not yet cached, attempts to download
        /// a cover. Resolution order: Steam CDN (AppId from metadata cache) →
        /// store-native source → GOG remote API as last resort.
        /// Failures are silently ignored.
        /// </summary>
        public static async Task FetchAllAsync(IEnumerable<DiscoveredGame> games, string cacheDir)
        {
            Directory.CreateDirectory(cacheDir);

            var toFetch = games
                .Where(g => g.Store != "Steam"
                         && CoverArtFetcher.GetCachedPath(g, cacheDir) == null
                         && CoverArtFetcher.GetCacheFilePath(g, cacheDir) != null)
                .ToList();

            if (toFetch.Count == 0) return;

            var epicUrls = await LoadEpicCatalogUrlsAsync();
            var ubisoftThumbs = LoadUbisoftThumbImages();
            var bnetUrls = LoadBattleNetCoverUrls();

            using var sem = new SemaphoreSlim(3);
            var tasks = toFetch.Select(g =>
                FetchOneAsync(g, cacheDir, sem, epicUrls, ubisoftThumbs, bnetUrls));
            await Task.WhenAll(tasks);
        }

        private static async Task FetchOneAsync(
            DiscoveredGame game,
            string cacheDir,
            SemaphoreSlim sem,
            Dictionary<string, string> epicUrls,
            Dictionary<string, string> ubisoftThumbs,
            Dictionary<string, string> bnetUrls)
        {
            await sem.WaitAsync();
            try
            {
                string cachePath = CoverArtFetcher.GetCacheFilePath(game, cacheDir)!;

                // ── Tier 1: Steam CDN (AppId already resolved by GameMetadataService) ──
                string? resolvedSteamAppId = TryGetResolvedSteamAppId(game);
                if (!string.IsNullOrEmpty(resolvedSteamAppId))
                {
                    bool fetched = await TryFetchSteamCoverByAppIdAsync(resolvedSteamAppId, cachePath);
                    if (fetched) return;
                }

                // ── Tier 2: store-native source ──────────────────────────────────────
                bool nativeFetched = await TryFetchNativeAsync(game, cachePath, epicUrls, ubisoftThumbs, bnetUrls);
                if (nativeFetched) return;

                // ── Tier 3: GOG catalog/product API (last resort) ───────────────────
                await FetchGogRemoteFallbackAsync(game, cachePath);
            }
            catch { }
            finally { sem.Release(); }
        }

        /// <summary>
        /// Looks up the Steam AppId that GameMetadataService already resolved for this game.
        /// Returns null if no AppId was resolved.
        /// </summary>
        private static string? TryGetResolvedSteamAppId(DiscoveredGame game)
        {
            // SteamAppId is set for Steam games; for non-Steam games it remains null
            // and the Steam CDN tier is skipped in favour of store-native sources.
            return game.SteamAppId;
        }

        /// <summary>
        /// Attempts to fetch a cover directly from Steam CDN given a known AppId.
        /// Tries IStoreBrowseService first (high-res library_capsule_2x), then the
        /// deterministic CDN pattern (library_600x900.jpg) as fallback.
        /// Returns true if a cover was successfully saved to cachePath.
        /// </summary>
        private static async Task<bool> TryFetchSteamCoverByAppIdAsync(string appId, string cachePath)
        {
            try
            {
                if (!int.TryParse(appId, out int appIdInt)) return false;

                string? coverUrl = await GetSteamCoverUrlAsync(appIdInt);
                if (!string.IsNullOrEmpty(coverUrl))
                {
                    if (await DownloadAsPngAsync(coverUrl, cachePath) > 0) return true;
                }

                // Deterministic CDN fallback — no API call needed.
                //
                // ⚠️ _2x, not the plainly-named file. Despite being called library_600x900.jpg,
                // that one is 300x450; the "_2x" variant is the actual 600x900. Measured on
                // appid 1091500 (12/08/2026), and it is why a game that fell through to this
                // fallback ended up with a quarter of the pixels of one that did not.
                string fallbackUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg";
                return await DownloadAsPngAsync(fallbackUrl, cachePath) > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Attempts the store-native cover fetch for the game's store.
        /// Returns true if a cover was successfully saved. Does NOT fall through to
        /// remote GOG tiers — those are handled separately in Tier 3.
        /// </summary>
        private static async Task<bool> TryFetchNativeAsync(
            DiscoveredGame game,
            string cachePath,
            Dictionary<string, string> epicUrls,
            Dictionary<string, string> ubisoftThumbs,
            Dictionary<string, string> bnetUrls)
        {
            try
            {
                switch (game.Store)
                {
                    case "Epic Games":
                        if (game.StoreId != null && epicUrls.TryGetValue(game.StoreId, out string? epicUrl))
                        {
                            await DownloadAsPngAsync(epicUrl, cachePath);
                            return true;
                        }
                        return false;

                    case "GOG":
                        // Tier 1: Galaxy's local data — free, offline, and the right shape, so
                        // it still goes first. But it only ends the search when it is big
                        // enough: what Galaxy caches is the thumbnail for its own grid.
                        if (await TryFetchGogLocalAsync(game, cachePath)) return true;
                        // Tier 2: Steam Store search by name. GOG was the one store with no
                        // Steam rescue at all — which is why its covers were the smallest in
                        // the library while Battle.net's, which has one, were 600x900.
                        return await TryFetchViaSteamSearchAsync(game, cachePath);

                    case "Ubisoft Connect":
                        // Tier 1: Steam Store search by name (portrait 600×900 — preferred over
                        // Ubisoft's landscape keyart thumb, which crops badly into a 2:3 cover)
                        if (await TryFetchViaSteamSearchAsync(game, cachePath)) return true;
                        // Tier 2: local Ubisoft thumb (landscape ~610×410 — last resort)
                        if (game.StoreId != null && ubisoftThumbs.TryGetValue(game.StoreId, out string? thumb)
                            && !string.IsNullOrEmpty(thumb))
                        {
                            string url = $"https://ubistatic3-a.akamaihd.net/orbit/uplay_launcher_3_0/assets/{thumb}";
                            await DownloadAsPngAsync(url, cachePath);
                            return true;
                        }
                        return false;

                    case "Xbox":
                        // Tier 1: Steam Store search by name (portrait cover — preferred over square local logos)
                        bool xboxSteamFetched = await TryFetchViaSteamSearchAsync(game, cachePath);
                        if (xboxSteamFetched) return true;
                        // Tier 2: local MicrosoftGame.config logo files (square — last resort)
                        await FetchXboxCoverAsync(game, cachePath);
                        return File.Exists(cachePath);

                    case "EA App":
                        // EA App has no local cover artwork in a standard format; nearly all EA
                        // titles ship on Steam too, so a Steam Store name search is the most
                        // reliable source for a portrait cover.
                        return await TryFetchViaSteamSearchAsync(game, cachePath);

                    case "Battle.net":
                        // Tier 1: Steam Store search by name (portrait 600×900 — preferred over
                        // Battle.net's square ~300×300 logo_art, which looks soft when enlarged)
                        if (await TryFetchViaSteamSearchAsync(game, cachePath)) return true;
                        // Tier 2: local aggregate.json logo_art_uri (square — last resort)
                        if (game.StoreId != null &&
                            bnetUrls.TryGetValue(game.StoreId, out string? bnetUrl) &&
                            !string.IsNullOrEmpty(bnetUrl))
                        {
                            await DownloadAsPngAsync(bnetUrl, cachePath);
                            return true;
                        }
                        return false;

                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        // ── Epic: catcache.bin ────────────────────────────────────────────────
        // catcache.bin is a base64-encoded JSON array of catalog items maintained by
        // the Epic Games Launcher. Each item has CatalogItemId and keyImages[].
        // This gives us direct CDN URLs without any API call.

        private static async Task<Dictionary<string, string>> LoadEpicCatalogUrlsAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string binPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
                if (!File.Exists(binPath)) return result;

                string b64 = await File.ReadAllTextAsync(binPath);
                byte[] decoded = Convert.FromBase64String(b64.Trim());
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(decoded));

                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl)) continue;
                    string? id = idEl.GetString();
                    if (string.IsNullOrEmpty(id)) continue;

                    if (!item.TryGetProperty("keyImages", out var keyImages)) continue;

                    string? bestUrl = null;
                    foreach (var img in keyImages.EnumerateArray())
                    {
                        string? type = img.TryGetProperty("type", out var t) ? t.GetString() : null;
                        string? url = img.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (string.IsNullOrEmpty(url)) continue;

                        if (type == "DieselGameBoxTall") { bestUrl = url; break; }
                        if (type == "DieselGameBox") bestUrl ??= url;
                        else bestUrl ??= url;
                    }

                    if (!string.IsNullOrEmpty(bestUrl))
                        result[id] = bestUrl;
                }
            }
            catch { }
            return result;
        }

        // ── GOG: local Galaxy SQLite DB ───────────────────────────────────────
        // Reads covers from local GOG Galaxy data only (no internet API).
        // Remote GOG API tiers are handled separately in FetchGogRemoteFallbackAsync,
        // called as Tier 3 after Steam CDN and all native sources have failed.
        //
        // DB: %ProgramData%\GOG.com\Galaxy\storage\galaxy-2.0.db
        // Cache: %ProgramData%\GOG.com\Galaxy\webcache\{userId}\gog\{platformId}\{filename}

        /// <summary>
        /// GOG local-only fetch: Tier 1 (webcache file) + Tier 2 (originalImages URL) +
        /// Tier 3 (LimitedDetails logo2x). Does NOT call any remote API.
        /// Returns true if a cover was saved.
        /// </summary>
        private static async Task<bool> TryFetchGogLocalAsync(DiscoveredGame game, string cachePath)
        {
            if (game.StoreId == null) return false;

            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string dbPath = Path.Combine(programData, "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
            if (!File.Exists(dbPath)) return false;

            try
            {
                string connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();

                string releaseKey = $"gog_{game.StoreId}";

                int verticalCoverTypeId = 3;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM WebCacheResourceTypes WHERE type = 'verticalCover' LIMIT 1";
                    if (cmd.ExecuteScalar() is long id) verticalCoverTypeId = (int)id;
                }

                int originalImagesTypeId = 378;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM GamePieceTypes WHERE type = 'originalImages' LIMIT 1";
                    if (cmd.ExecuteScalar() is long id) originalImagesTypeId = (int)id;
                }

                // Tier 1: local webcache file
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, userId FROM WebCache WHERE releaseKey = @rk";
                    cmd.Parameters.AddWithValue("@rk", releaseKey);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        long webCacheId = reader.GetInt64(0);
                        long userId = reader.GetInt64(1);

                        using var cmd2 = conn.CreateCommand();
                        cmd2.CommandText = "SELECT filename FROM WebCacheResources WHERE webCacheId = @wid AND webCacheResourceTypeId = @tid LIMIT 1";
                        cmd2.Parameters.AddWithValue("@wid", webCacheId);
                        cmd2.Parameters.AddWithValue("@tid", verticalCoverTypeId);
                        string? filename = cmd2.ExecuteScalar() as string;

                        if (!string.IsNullOrEmpty(filename))
                        {
                            string localPath = Path.Combine(programData, "GOG.com", "Galaxy", "webcache",
                                userId.ToString(CultureInfo.InvariantCulture),
                                "gog", game.StoreId, filename);
                            if (File.Exists(localPath))
                            {
                                byte[] bytes = await File.ReadAllBytesAsync(localPath);
                                if (bytes.Length > 0)
                                {
                                    // Written either way — it is the floor if nothing better
                                    // turns up — but only good enough to stop here at full size.
                                    if (await SaveBytesAsPngAsync(bytes, cachePath) >= GoodCoverHeight)
                                        return true;
                                }
                            }
                        }
                    }
                }

                // Tier 2: originalImages.verticalCover from GamePieces
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM GamePieces WHERE releaseKey = @rk AND gamePieceTypeId = @tid LIMIT 1";
                    cmd.Parameters.AddWithValue("@rk", releaseKey);
                    cmd.Parameters.AddWithValue("@tid", originalImagesTypeId);
                    if (cmd.ExecuteScalar() is string json)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("verticalCover", out var vc))
                            {
                                string? url = vc.GetString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    if (await DownloadAsPngAsync(url, cachePath) >= GoodCoverHeight)
                                        return true;
                                }
                            }
                        }
                        catch { }
                    }
                }

                // Tier 3: LimitedDetails.Images.logo2x
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT images FROM LimitedDetails WHERE productId = @pid LIMIT 1";
                    cmd.Parameters.AddWithValue("@pid", game.StoreId);
                    if (cmd.ExecuteScalar() is string imagesJson)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(imagesJson);
                            if (doc.RootElement.TryGetProperty("logo2x", out var logo2x))
                            {
                                string? url = logo2x.GetString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    if (await DownloadAsPngAsync(url, cachePath) >= GoodCoverHeight)
                                        return true;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { /* DB locked or missing tables — fall through */ }

            return false;
        }

        /// <summary>
        /// GOG remote fallback: Tier 4 (GOG Catalog API) + Tier 5 (GOG Product API).
        /// Called as last resort for all stores after Steam CDN and native sources fail.
        /// </summary>
        private static async Task FetchGogRemoteFallbackAsync(DiscoveredGame game, string cachePath)
        {
            if (game.StoreId == null) return;

            // Tier 4: GOG Catalog API search by name with ID verification
            try
            {
                string searchUrl = $"https://catalog.gog.com/v1/catalog?productType=in:game&limit=5&query=like:{Uri.EscapeDataString(game.Name)}";
                string json = await _http.GetStringAsync(searchUrl);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("products", out var products) &&
                    products.ValueKind == JsonValueKind.Array)
                {
                    foreach (var product in products.EnumerateArray())
                    {
                        string? productId = product.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.Equals(productId, game.StoreId, StringComparison.OrdinalIgnoreCase)) continue;
                        string? coverUrl = product.TryGetProperty("coverVertical", out var c) ? c.GetString() : null;
                        if (!string.IsNullOrEmpty(coverUrl))
                        {
                            await DownloadAsPngAsync(coverUrl, cachePath);
                            return;
                        }
                    }
                }
            }
            catch { }

            // Tier 5: GOG Product API — construct vertical cover from logo URL
            try
            {
                string apiUrl = $"https://api.gog.com/products/{Uri.EscapeDataString(game.StoreId)}";
                string json = await _http.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("images", out var images))
                {
                    string? logo = images.TryGetProperty("logo", out var l) ? l.GetString() : null;
                    if (!string.IsNullOrEmpty(logo))
                    {
                        string coverUrl = $"https:{logo.Replace("glx_logo", "glx_vertical_cover")}";
                        await DownloadAsPngAsync(coverUrl, cachePath);
                    }
                }
            }
            catch { }
        }

        // ── Ubisoft: local YAML config ────────────────────────────────────────
        // The configurations file is a binary-delimited sequence of YAML sections.
        // Each game references its install via "register: ...Installs\{id}\InstallDir".
        // The thumb_image appears BEFORE the register reference in the same logical section.
        // We search backwards from each register reference to find the nearest thumb_image.
        // Returns installId → thumb image filename (e.g. "6100" → "d810...jpg")

        private static Dictionary<string, string> LoadUbisoftThumbImages()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ubisoft Game Launcher", "cache", "configuration", "configurations");
                if (!File.Exists(configPath)) return result;

                byte[] bytes = File.ReadAllBytes(configPath);
                string content = Encoding.GetEncoding("iso-8859-1").GetString(bytes);

                var installRefs = Regex.Matches(content, @"Installs[\\/](\d+)[\\/]InstallDir");
                var thumbRe = new Regex(@"thumb_image:\s*([a-f0-9]{20,}\.(?:jpg|png|webp))", RegexOptions.IgnoreCase);

                foreach (Match m in installRefs)
                {
                    string installId = m.Groups[1].Value;
                    if (result.ContainsKey(installId)) continue;

                    int searchStart = Math.Max(0, m.Index - 5000);
                    string before = content.Substring(searchStart, m.Index - searchStart);
                    var thumbMatches = thumbRe.Matches(before);
                    if (thumbMatches.Count > 0)
                    {
                        string thumb = thumbMatches[thumbMatches.Count - 1].Groups[1].Value;
                        result[installId] = thumb;
                    }
                }

                // Also handle thumb_image that is a localization key (not a hash filename).
                var locKeyRefs = Regex.Matches(content, @"thumb_image:\s*([A-Z_]+)\b");
                foreach (Match lm in locKeyRefs)
                {
                    string key = lm.Groups[1].Value;
                    if (key == "THUMBIMAGE" || key.StartsWith("RELATED_")) continue;

                    int searchEnd = Math.Min(content.Length, lm.Index + 5000);
                    string after = content.Substring(lm.Index, searchEnd - lm.Index);
                    var locMatch = Regex.Match(after, $@"{Regex.Escape(key)}:\s*([a-f0-9]{{20,}}\.(?:jpg|png|webp))");
                    if (!locMatch.Success) continue;

                    string afterForInstall = content.Substring(lm.Index, Math.Min(10000, content.Length - lm.Index));
                    var installMatch = Regex.Match(afterForInstall, @"Installs[\\/](\d+)[\\/]InstallDir");
                    if (installMatch.Success)
                        result.TryAdd(installMatch.Groups[1].Value, locMatch.Groups[1].Value);
                }
            }
            catch { }
            return result;
        }

        // ── Steam Store search by name (Xbox + EA App fallback) ──────────────
        // For games without a known SteamAppId, search the Steam Store by name.
        // The search endpoint returns JSON with items[].id (appId) which we then use
        // to fetch the portrait cover via IStoreBrowseService + CDN fallback.
        // Returns true if a portrait cover was successfully saved.

        private static async Task<bool> TryFetchViaSteamSearchAsync(DiscoveredGame game, string cachePath)
        {
            try
            {
                string searchUrl = $"https://store.steampowered.com/api/storesearch/" +
                                   $"?term={Uri.EscapeDataString(game.Name)}&l=english&cc=US";
                string json = await _http.GetStringAsync(searchUrl);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                    return false;

                string nameNorm = NormalizeName(game.Name);
                int matchAppId = 0;

                // Pass 1: exact normalized match
                foreach (var item in items.EnumerateArray())
                {
                    string? resultName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(resultName)) continue;
                    if (NormalizeName(resultName) != nameNorm) continue;

                    matchAppId = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                    break;
                }

                // Pass 2: partial match — one name contains the other (handles subtitle variants
                // e.g. "Halo Infinite" ↔ "Halo Infinite (Campaign)")
                if (matchAppId == 0)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        string? resultName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(resultName)) continue;

                        string resultNorm = NormalizeName(resultName);
                        if (!nameNorm.Contains(resultNorm) && !resultNorm.Contains(nameNorm)) continue;

                        matchAppId = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                        break;
                    }
                }

                if (matchAppId == 0) return false;

                // Try high-res IStoreBrowseService cover first, then deterministic CDN fallback
                string? coverUrl = await GetSteamCoverUrlAsync(matchAppId);
                if (!string.IsNullOrEmpty(coverUrl))
                {
                    if (await DownloadAsPngAsync(coverUrl, cachePath) > 0) return true;
                }

                // _2x — see the note in TryFetchSteamCoverByAppIdAsync.
                string fallbackUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{matchAppId}/library_600x900_2x.jpg";
                return await DownloadAsPngAsync(fallbackUrl, cachePath) > 0;
            }
            catch { return false; }
        }

        // ── Xbox: local logo files (fallback) ────────────────────────────────
        // Used only when Steam Store search fails. Reads ShellVisuals logo paths
        // from MicrosoftGame.config — these are square icons, not portrait covers.

        private static async Task FetchXboxCoverAsync(DiscoveredGame game, string cachePath)
        {
            try
            {
                string configDir = game.ExePath;
                if (!Directory.Exists(configDir)) return;

                string configPath = Path.Combine(configDir, "MicrosoftGame.config");
                if (!File.Exists(configPath)) return;

                XDocument doc = XDocument.Load(configPath);
                XNamespace ns = "http://schemas.microsoft.com/Gaming/2020/08/Game";

                var shellVisuals = doc.Descendants(ns + "ShellVisuals").FirstOrDefault()
                                ?? doc.Descendants("ShellVisuals").FirstOrDefault();
                if (shellVisuals == null) return;

                // Priority: largest logo first
                string? logoRel =
                    shellVisuals.Attribute("Square480x480Logo")?.Value ??
                    shellVisuals.Attribute("Square150x150Logo")?.Value ??
                    shellVisuals.Attribute("StoreLogo")?.Value ??
                    shellVisuals.Attribute("Square44x44Logo")?.Value;

                if (string.IsNullOrEmpty(logoRel)) return;

                string logoPath = Path.Combine(configDir, logoRel);
                if (!File.Exists(logoPath)) return;

                if (string.Equals(Path.GetExtension(logoPath), ".png", StringComparison.OrdinalIgnoreCase))
                    File.Copy(logoPath, cachePath, overwrite: true);
                else
                    await SaveBytesAsPngAsync(File.ReadAllBytes(logoPath), cachePath);
            }
            catch { }
        }

        // ── Battle.net: aggregate.json ────────────────────────────────────────
        // aggregate.json is kept by the Battle.net Agent on the local disk.
        // Each installed product has logo_art_uri (portrait, preferred) and
        // box_art_uri (box art, fallback). URLs are webp; WIC converts them to PNG.

        private static Dictionary<string, string> LoadBattleNetCoverUrls()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string aggPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Battle.net", "Agent", "aggregate.json");
                if (!File.Exists(aggPath)) return result;

                using var doc = JsonDocument.Parse(File.ReadAllText(aggPath));
                if (!doc.RootElement.TryGetProperty("installed", out var installedEl) ||
                    installedEl.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var item in installedEl.EnumerateArray())
                {
                    string? uid = item.TryGetProperty("product_id", out var pidEl)
                        ? pidEl.GetString() : null;
                    if (string.IsNullOrEmpty(uid)) continue;

                    // Prefer logo_art_uri (portrait format — best for game card covers)
                    string? url = item.TryGetProperty("logo_art_uri", out var logoEl)
                        ? logoEl.GetString() : null;
                    if (string.IsNullOrEmpty(url))
                        url = item.TryGetProperty("box_art_uri", out var boxEl)
                            ? boxEl.GetString() : null;

                    if (!string.IsNullOrEmpty(url))
                        result[uid] = url!;
                }
            }
            catch { }
            return result;
        }

        // ── Steam: IStoreBrowseService ────────────────────────────────────────

        private static async Task<string?> GetSteamCoverUrlAsync(int appId)
        {
            try
            {
                var requestBody = new
                {
                    ids = new[] { new { appid = appId } },
                    context = new { country_code = "US" },
                    data_request = new { include_assets = true }
                };
                string inputJson = JsonSerializer.Serialize(requestBody);
                string url = $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/" +
                             $"?input_json={Uri.EscapeDataString(inputJson)}";
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var storeItems = doc.RootElement
                    .GetProperty("response")
                    .GetProperty("store_items");

                foreach (var item in storeItems.EnumerateArray())
                {
                    if (!item.TryGetProperty("assets", out var assets)) continue;
                    string? urlFormat = assets.TryGetProperty("asset_url_format", out var f) ? f.GetString() : null;
                    string? capsule = assets.TryGetProperty("library_capsule_2x", out var c) ? c.GetString() : null;
                    if (!string.IsNullOrEmpty(urlFormat) && !string.IsNullOrEmpty(capsule))
                        return $"https://shared.fastly.steamstatic.com/store_item_assets/{urlFormat.Replace("${FILENAME}", capsule)}";
                }
            }
            catch { }
            return null;
        }

        // ── Image helpers ─────────────────────────────────────────────────────

        private static string NormalizeName(string name) =>
            // Collapse any run of non-alphanumeric chars (™, ®, ':', spaces, …) into a single
            // space, so "Diablo® IV" → "diablo iv" matches Battle.net's "Diablo IV". Without the
            // run-collapse a stray symbol leaves a double space and breaks both exact and Contains.
            Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

        /// <summary>
        /// Downloads and stores a cover. Returns the pixel height written, or 0 if nothing
        /// was written — so a caller can tell "got a good one" from "got a thumbnail" from
        /// "got nothing", which the old void signature could not.
        /// </summary>
        private static async Task<int> DownloadAsPngAsync(string url, string cachePath)
        {
            byte[] bytes = await _http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return 0;
            return await SaveBytesAsPngAsync(bytes, cachePath);
        }

        /// <summary>
        /// Pixel height of the cover already in the cache, or 0 if there isn't one.
        /// Reads the PNG IHDR header instead of decoding the image: everything in this
        /// cache is written as PNG by the method below, so 24 bytes are enough and exact.
        /// </summary>
        private static int CachedCoverHeight(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                byte[] head = new byte[24];
                if (fs.Read(head, 0, head.Length) < head.Length) return 0;
                if (head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return 0;
                // 8-byte signature, chunk length, "IHDR", width, then height — big-endian.
                return (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            }
            catch { return 0; }
        }

        /// <summary>
        /// Decodes any image format supported by Windows.Graphics.Imaging (JPEG, PNG, BMP, WebP)
        /// and re-encodes it as PNG. Required because Sunshine only accepts PNG for image-path.
        /// Writes to a temp file first so a failed conversion never leaves a corrupt PNG in cache.
        ///
        /// Returns the pixel height written, or 0 if nothing was.
        ///
        /// ⚠️ It refuses to replace a taller cover that is already cached. The tiers below run
        /// in order of convenience — local files before network calls — not in order of quality,
        /// so without this a later, worse source silently overwrites a better one that ran
        /// first, and which of the two you end up with depends on the order they finish in.
        /// </summary>
        private static async Task<int> SaveBytesAsPngAsync(byte[] bytes, string path)
        {
            string tempPath = path + ".tmp";
            int height;
            try
            {
                using var inRas = new InMemoryRandomAccessStream();
                await inRas.WriteAsync(bytes.AsBuffer());
                inRas.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(inRas);
                height = (int)decoder.PixelHeight;
                if (height <= CachedCoverHeight(path)) return 0;

                var softBitmap = await decoder.GetSoftwareBitmapAsync();

                using var outFile = File.Create(tempPath);
                using var outRas = outFile.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outRas);
                encoder.SetSoftwareBitmap(softBitmap);
                await encoder.FlushAsync();
            }
            catch
            {
                try { File.Delete(tempPath); } catch { }
                throw;
            }
            File.Move(tempPath, path, overwrite: true);
            return height;
        }
    }
}