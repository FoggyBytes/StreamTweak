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
    /// Sources by store:
    ///   Epic Games   → catcache.bin (local cache of CDN image URLs, no API call needed)
    ///   GOG          → local GOG Galaxy SQLite DB + webcache → GOG catalog/product API → Steam search fallback
    ///   Ubisoft      → local YAML config + ubistatic3-a.akamaihd.net CDN
    ///   Xbox         → local image files from MicrosoftGame.config (Square480x480Logo etc.)
    ///   Battle.net   → aggregate.json logo_art_uri (local file, no API call needed)
    ///   EA App       → Steam Store Search fallback (Origin API is dead)
    /// </summary>
    public static class StoreCoverFetcher
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// For every non-Steam game whose cover is not yet cached, attempts to download
        /// a cover from the appropriate store source. Failures are silently ignored.
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

            // Load data sources that are shared across multiple games (one I/O per source)
            var epicUrls      = await LoadEpicCatalogUrlsAsync();
            var ubisoftThumbs = LoadUbisoftThumbImages();
            var bnetUrls      = LoadBattleNetCoverUrls();

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

                switch (game.Store)
                {
                    case "Epic Games":
                        if (game.StoreId != null && epicUrls.TryGetValue(game.StoreId, out string? epicUrl))
                            await DownloadAsPngAsync(epicUrl, cachePath);
                        break;

                    case "GOG":
                        await FetchGogCoverAsync(game, cachePath);
                        break;

                    case "Ubisoft Connect":
                        await FetchUbisoftCoverAsync(game, cachePath, ubisoftThumbs);
                        break;

                    case "Xbox":
                        await FetchXboxCoverAsync(game, cachePath);
                        break;

                    case "Battle.net":
                        if (game.StoreId != null &&
                            bnetUrls.TryGetValue(game.StoreId, out string? bnetUrl) &&
                            !string.IsNullOrEmpty(bnetUrl))
                            await DownloadAsPngAsync(bnetUrl, cachePath);
                        break;

                    case "EA App":
                        await FetchCoverViaSteamSearchAsync(game, cachePath);
                        break;
                }
            }
            catch { /* silently skip per-game failures */ }
            finally { sem.Release(); }
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
                        string? url  = img.TryGetProperty("url",  out var u) ? u.GetString() : null;
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

        // ── GOG: local Galaxy SQLite DB + webcache ────────────────────────────
        // Following DLSS Swap's approach: read covers from local GOG Galaxy data,
        // no internet API needed. Fallback tiers use remote APIs only as last resort.
        //
        // DB: %ProgramData%\GOG.com\Galaxy\storage\galaxy-2.0.db
        // Cache: %ProgramData%\GOG.com\Galaxy\webcache\{userId}\gog\{platformId}\{filename}

        /// <summary>
        /// GOG cover art retrieval following DLSS Swap's exact approach:
        ///   Tier 1: Local webcache file (verticalCover resource from Galaxy DB)
        ///   Tier 2: Remote URL from GamePieces.originalImages.verticalCover (Galaxy DB)
        ///   Tier 3: Remote URL from LimitedDetails.Images.logo2x (Galaxy DB)
        ///   Tier 4: GOG Catalog API search by name with ID verification
        ///   Tier 5: GOG Product API → logo → glx_vertical_cover
        /// </summary>
        private static async Task FetchGogCoverAsync(DiscoveredGame game, string cachePath)
        {
            if (game.StoreId == null) return;

            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string dbPath = Path.Combine(programData, "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
            string releaseKey = $"gog_{game.StoreId}";

            // Tier 1 & 2: Try local Galaxy DB
            if (File.Exists(dbPath))
            {
                try
                {
                    string connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly
                    }.ToString();

                    using var conn = new SqliteConnection(connStr);
                    conn.Open();

                    // Get resource type ID for "verticalCover" (default: 3)
                    int verticalCoverTypeId = 3;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id FROM WebCacheResourceTypes WHERE type = 'verticalCover' LIMIT 1";
                        var result = cmd.ExecuteScalar();
                        if (result is long id) verticalCoverTypeId = (int)id;
                    }

                    // Get game piece type ID for "originalImages" (default: 378)
                    int originalImagesTypeId = 378;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id FROM GamePieceTypes WHERE type = 'originalImages' LIMIT 1";
                        var result = cmd.ExecuteScalar();
                        if (result is long id) originalImagesTypeId = (int)id;
                    }

                    // Tier 1: Local webcache file
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
                                        await SaveBytesAsPngAsync(bytes, cachePath);
                                        return;
                                    }
                                }
                            }
                        }
                    }

                    // Tier 2: Remote URL from GamePieces.originalImages.verticalCover
                    string? fallbackUrl = null;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT value FROM GamePieces WHERE releaseKey = @rk AND gamePieceTypeId = @tid LIMIT 1";
                        cmd.Parameters.AddWithValue("@rk", releaseKey);
                        cmd.Parameters.AddWithValue("@tid", originalImagesTypeId);
                        string? json = cmd.ExecuteScalar() as string;
                        if (!string.IsNullOrEmpty(json))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("verticalCover", out var vc))
                                {
                                    string? url = vc.GetString();
                                    if (!string.IsNullOrEmpty(url))
                                        fallbackUrl = url;
                                }
                            }
                            catch { }
                        }
                    }

                    // Tier 3: Remote URL from LimitedDetails.Images.logo2x
                    if (string.IsNullOrEmpty(fallbackUrl))
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT images FROM LimitedDetails WHERE productId = @pid LIMIT 1";
                        cmd.Parameters.AddWithValue("@pid", game.StoreId);
                        string? imagesJson = cmd.ExecuteScalar() as string;
                        if (!string.IsNullOrEmpty(imagesJson))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(imagesJson);
                                if (doc.RootElement.TryGetProperty("logo2x", out var logo2x))
                                {
                                    string? url = logo2x.GetString();
                                    if (!string.IsNullOrEmpty(url))
                                        fallbackUrl = url;
                                }
                            }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrEmpty(fallbackUrl))
                    {
                        try { await DownloadAsPngAsync(fallbackUrl, cachePath); return; }
                        catch { }
                    }
                }
                catch { /* DB locked or missing tables — fall through to API */ }
            }

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
                        if (!string.Equals(productId, game.StoreId, StringComparison.OrdinalIgnoreCase))
                            continue;
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
                        return;
                    }
                }
            }
            catch { }

            // Tier 6: Steam Store Search — last resort for GOG games also on Steam
            await FetchCoverViaSteamSearchAsync(game, cachePath);
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
                string content = System.Text.Encoding.GetEncoding("iso-8859-1").GetString(bytes);

                // Find all registry references: Installs\{id}\InstallDir
                var installRefs = Regex.Matches(content, @"Installs[\\/](\d+)[\\/]InstallDir");
                var thumbRe = new Regex(@"thumb_image:\s*([a-f0-9]{20,}\.(?:jpg|png|webp))", RegexOptions.IgnoreCase);

                foreach (Match m in installRefs)
                {
                    string installId = m.Groups[1].Value;
                    if (result.ContainsKey(installId)) continue;

                    // Search backwards from this reference (within 5000 chars) for the nearest thumb_image
                    int searchStart = Math.Max(0, m.Index - 5000);
                    string before = content.Substring(searchStart, m.Index - searchStart);
                    var thumbMatches = thumbRe.Matches(before);
                    if (thumbMatches.Count > 0)
                    {
                        // Take the LAST match (nearest to the register reference)
                        string thumb = thumbMatches[thumbMatches.Count - 1].Groups[1].Value;
                        result[installId] = thumb;
                    }
                }

                // Also handle thumb_image that is a localization key (not a hash filename).
                // Search for patterns like "thumb_image: THUMBIMAGE" and resolve via localizations.default
                var locKeyRefs = Regex.Matches(content, @"thumb_image:\s*([A-Z_]+)\b");
                foreach (Match lm in locKeyRefs)
                {
                    string key = lm.Groups[1].Value;
                    if (key == "THUMBIMAGE" || key.StartsWith("RELATED_")) continue;

                    // Find the localizations.default section nearby and look up the key
                    int searchEnd = Math.Min(content.Length, lm.Index + 5000);
                    string after = content.Substring(lm.Index, searchEnd - lm.Index);
                    var locMatch = Regex.Match(after, $@"{Regex.Escape(key)}:\s*([a-f0-9]{{20,}}\.(?:jpg|png|webp))");
                    if (!locMatch.Success) continue;

                    // Find which installId this belongs to
                    string afterForInstall = content.Substring(lm.Index, Math.Min(10000, content.Length - lm.Index));
                    var installMatch = Regex.Match(afterForInstall, @"Installs[\\/](\d+)[\\/]InstallDir");
                    if (installMatch.Success)
                        result.TryAdd(installMatch.Groups[1].Value, locMatch.Groups[1].Value);
                }
            }
            catch { }
            return result;
        }

        private static async Task FetchUbisoftCoverAsync(
            DiscoveredGame game, string cachePath, Dictionary<string, string> ubisoftThumbs)
        {
            // Match by installId (StoreId for Ubisoft games)
            if (game.StoreId != null && ubisoftThumbs.TryGetValue(game.StoreId, out string? thumb)
                && !string.IsNullOrEmpty(thumb))
            {
                string url = $"https://ubistatic3-a.akamaihd.net/orbit/uplay_launcher_3_0/assets/{thumb}";
                try { await DownloadAsPngAsync(url, cachePath); return; }
                catch { }
            }

            // Fallback: search Steam Store (many Ubisoft games are also on Steam)
            await FetchCoverViaSteamSearchAsync(game, cachePath);
        }

        private static string NormalizeName(string name) =>
            Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9 ]", " ").Trim();

        // ── Xbox: local logo files ────────────────────────────────────────────
        // Reads ShellVisuals logo paths from MicrosoftGame.config and copies them to cache.

        private static async Task FetchXboxCoverAsync(DiscoveredGame game, string cachePath)
        {
            try
            {
                string configDir = game.ExePath; // ExePath = Content dir set by scanner
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

        // ── Fallback: Steam Store Search ───────────────────────────────────────
        // For stores without a working cover art API (e.g. EA App after Origin shutdown),
        // search the Steam Store by game name and use the Steam CDN cover if found.
        // Most major titles are cross-listed on Steam, so this works well in practice.

        private static async Task FetchCoverViaSteamSearchAsync(DiscoveredGame game, string cachePath)
        {
            try
            {
                string searchUrl = $"https://store.steampowered.com/api/storesearch/" +
                    $"?term={Uri.EscapeDataString(game.Name)}&l=english&cc=US";
                string json = await _http.GetStringAsync(searchUrl);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array) return;

                // Find the best match by name similarity
                string gameNameNorm = NormalizeName(game.Name);
                foreach (var item in items.EnumerateArray())
                {
                    string? itemName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    int appId = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0;
                    if (string.IsNullOrEmpty(itemName) || appId == 0) continue;

                    // Only use if type is "app" (not DLC/bundle)
                    string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type != "app") continue;

                    if (NormalizeName(itemName).Contains(gameNameNorm) ||
                        gameNameNorm.Contains(NormalizeName(itemName)))
                    {
                        // Use IStoreBrowseService for high-quality library_capsule_2x
                        string? coverUrl = await GetSteamCoverUrlAsync(appId);
                        if (!string.IsNullOrEmpty(coverUrl))
                        {
                            await DownloadAsPngAsync(coverUrl, cachePath);
                            return;
                        }

                        // Fallback: direct CDN pattern
                        string fallbackUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";
                        await DownloadAsPngAsync(fallbackUrl, cachePath);
                        return;
                    }
                }
            }
            catch { }
        }

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

        private static async Task DownloadAsPngAsync(string url, string cachePath)
        {
            byte[] bytes = await _http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return;
            await SaveBytesAsPngAsync(bytes, cachePath);
        }

        /// <summary>
        /// Decodes any image format supported by Windows.Graphics.Imaging (JPEG, PNG, BMP, WebP)
        /// and re-encodes it as PNG. Required because Sunshine only accepts PNG for image-path.
        /// Writes to a temp file first so a failed conversion never leaves a corrupt PNG in cache.
        /// </summary>
        private static async Task SaveBytesAsPngAsync(byte[] bytes, string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                using var inRas = new InMemoryRandomAccessStream();
                await inRas.WriteAsync(bytes.AsBuffer());
                inRas.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(inRas);
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
        }
    }
}
