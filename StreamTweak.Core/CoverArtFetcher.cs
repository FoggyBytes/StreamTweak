using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StreamTweak
{
    /// <summary>
    /// Cache layer for game cover art images (filename convention + path resolution).
    /// Shared by <see cref="GameLibraryService"/>, <see cref="StoreCoverFetcher"/> and
    /// <see cref="SunshineSync"/>. Also provides a Steam CDN fallback fetch for Steam games.
    /// Cover art is cached in %LOCALAPPDATA%\StreamTweak\covers\.
    /// </summary>
    public static class CoverArtFetcher
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads missing cover art for all games in parallel (up to 5 concurrent).
        /// Already-cached images are skipped. Failures are silently ignored.
        /// </summary>
        public static async Task FetchAllAsync(IEnumerable<DiscoveredGame> games, string cacheDir)
        {
            Directory.CreateDirectory(cacheDir);

            var toFetch = games.Where(g => GetDownloadUrl(g) != null && GetCachedPath(g, cacheDir) == null).ToList();
            if (toFetch.Count == 0) return;

            using var semaphore = new SemaphoreSlim(5);
            var tasks = toFetch.Select(g => FetchOneAsync(g, cacheDir, semaphore));
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Returns the expected cache file path for a game regardless of whether it exists yet.
        /// Returns null for games with no deterministic filename (e.g., empty name).
        /// </summary>
        public static string? GetCacheFilePath(DiscoveredGame game, string cacheDir)
        {
            string? fileName = GetCacheFileName(game);
            return fileName == null ? null : Path.Combine(cacheDir, fileName);
        }

        /// <summary>
        /// Returns the full path to the cached cover image for a game, or null if not yet cached.
        /// </summary>
        public static string? GetCachedPath(DiscoveredGame game, string cacheDir)
        {
            string? path = GetCacheFilePath(game, cacheDir);
            return (path != null && File.Exists(path)) ? path : null;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static async Task FetchOneAsync(DiscoveredGame game, string cacheDir, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            string tmp = string.Empty;
            try
            {
                string? url = GetDownloadUrl(game);
                if (url == null) return;

                string? fileName = GetCacheFileName(game);
                if (fileName == null) return;

                string cachePath = Path.Combine(cacheDir, fileName);
                if (File.Exists(cachePath)) return; // already cached

                tmp = cachePath + ".tmp";

                byte[] bytes = await _http.GetByteArrayAsync(url);

                // Sunshine/Vibeshine requires PNG for image-path.
                // Steam CDN delivers JPEG → decode and re-encode as PNG via Windows.Graphics.Imaging.
                // Write to .tmp first; move atomically to avoid a corrupt file on crash.
                using var inRas = new InMemoryRandomAccessStream();
                await inRas.WriteAsync(bytes.AsBuffer());
                inRas.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(inRas);
                var softBitmap = await decoder.GetSoftwareBitmapAsync();

                using (var pngStream = File.Create(tmp))
                using (var outRas = pngStream.AsRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outRas);
                    encoder.SetSoftwareBitmap(softBitmap);
                    await encoder.FlushAsync();
                }
                File.Move(tmp, cachePath, overwrite: true);
            }
            catch
            {
                if (tmp.Length > 0) try { File.Delete(tmp); } catch { }
            }
            finally
            {
                semaphore.Release();
            }
        }

        private static string? GetDownloadUrl(DiscoveredGame game)
        {
            // Prefer API-provided URL from IStoreBrowseService (exact, always correct)
            if (!string.IsNullOrEmpty(game.CoverUrl))
                return game.CoverUrl;

            // Legacy CDN fallback for Steam games without an API-provided URL.
            //
            // ⚠️ _2x, not the plainly-named file: library_600x900.jpg is actually 300x450,
            // and the "_2x" variant is the real 600x900. Measured on appid 1091500
            // (12/08/2026). See the matching note in StoreCoverFetcher.
            return game.Store switch
            {
                "Steam" when game.SteamAppId != null =>
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.SteamAppId}/library_600x900_2x.jpg",
                _ => null
            };
        }

        private static string? GetCacheFileName(DiscoveredGame game)
        {
            if (game.SteamAppId != null)
                return $"steam_{game.SteamAppId}.png";

            string store = game.Store.Replace(" ", "").ToLowerInvariant();

            // Non-Steam: prefer StoreId (stable, deterministic) over sanitized name
            if (game.StoreId != null)
            {
                string safeId = new string(game.StoreId
                    .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    .ToArray());
                if (!string.IsNullOrEmpty(safeId))
                    return $"{store}_{safeId}.png";
            }

            // Fallback: sanitized name
            string safe = new string(game.Name
                .Where(c => char.IsLetterOrDigit(c) || c == '-')
                .ToArray());
            return string.IsNullOrEmpty(safe) ? null : $"{store}_{safe}.png";
        }
    }
}
