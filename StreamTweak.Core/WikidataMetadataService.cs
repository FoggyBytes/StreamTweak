using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Fallback metadata source for games that are NOT on Steam (e.g. Epic-exclusive titles
    /// like Alan Wake 2). Queries Wikidata's free, key-less MediaWiki API
    /// (www.wikidata.org/w/api.php) for developer (P178) and publication date (P577).
    ///
    /// Why Wikidata and not PCGamingWiki: PCGW sits behind Cloudflare bot protection and
    /// returns challenge/403 pages to non-browser clients. Wikidata is Wikimedia-hosted,
    /// designed for automated access, and carries structured data — so the developer field
    /// is the actual studio (P178), not the publisher (the Epic local catalog only exposes
    /// the publisher + a meaningless catalog-add date).
    ///
    /// Used only as a last resort by <see cref="GameMetadataService"/> before writing N/A.
    /// </summary>
    public static class WikidataMetadataService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // Wikidata items that are "video game" (Q7889) — used to reject soundtracks, films,
        // game series and other same-name entities the name search may return.
        private const string VideoGameQid = "Q7889";

        static WikidataMetadataService()
        {
            // Wikimedia's User-Agent policy requires a descriptive UA for automated access.
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string ver = v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "7.3";
            _http.DefaultRequestHeaders.Add("User-Agent",
                $"StreamTweak/{ver} (FoggyBytes; https://github.com/FoggyBytes/StreamTweak)");
        }

        /// <summary>
        /// Resolves developer + release date for a game by name. Returns null if no suitable
        /// video-game entity is found. Either field may come back null when Wikidata lacks it.
        /// </summary>
        public static async Task<GameMetadataService.GameMetadata?> FetchByNameAsync(string gameName)
        {
            try
            {
                // 1. Fuzzy name search → candidate QIDs (already relevance-ranked by Wikidata).
                string searchUrl = "https://www.wikidata.org/w/api.php?action=wbsearchentities" +
                    $"&search={Uri.EscapeDataString(gameName)}&language=en&uselang=en" +
                    "&format=json&type=item&limit=7";
                string sjson = await _http.GetStringAsync(searchUrl);

                var candidates = new List<string>();
                using (var sdoc = JsonDocument.Parse(sjson))
                {
                    if (!sdoc.RootElement.TryGetProperty("search", out var hits)
                        || hits.ValueKind != JsonValueKind.Array)
                        return null;

                    foreach (var h in hits.EnumerateArray())
                    {
                        if (h.TryGetProperty("id", out var idEl) && idEl.GetString() is string id
                            && !string.IsNullOrEmpty(id))
                            candidates.Add(id);
                    }
                }
                if (candidates.Count == 0) return null;

                // 2. Batch-fetch claims for all candidates in a single call.
                string ids = string.Join("|", candidates);
                string claimsUrl = "https://www.wikidata.org/w/api.php?action=wbgetentities" +
                    $"&ids={ids}&props=claims&format=json";
                string cjson = await _http.GetStringAsync(claimsUrl);

                using var cdoc = JsonDocument.Parse(cjson);
                if (!cdoc.RootElement.TryGetProperty("entities", out var entities))
                    return null;

                // 3. First candidate (in search order) that is a video game with usable data.
                foreach (var qid in candidates)
                {
                    if (!entities.TryGetProperty(qid, out var ent)) continue;
                    if (!ent.TryGetProperty("claims", out var claims)) continue;
                    if (!IsVideoGame(claims)) continue;

                    string? devId = FirstEntityClaimId(claims, "P178");
                    string? dateStr = EarliestDate(claims, "P577");
                    if (devId == null && dateStr == null) continue;

                    string? devLabel = devId != null ? await ResolveLabelAsync(devId) : null;
                    if (devLabel == null && dateStr == null) return null;

                    return new GameMetadataService.GameMetadata(devLabel, dateStr);
                }

                return null;
            }
            catch { return null; }
        }

        // ── claim helpers ─────────────────────────────────────────────────────

        private static bool IsVideoGame(JsonElement claims)
        {
            if (!claims.TryGetProperty("P31", out var p31)) return false;
            foreach (var c in p31.EnumerateArray())
            {
                if (TryGetClaimEntityId(c, out string? id) && id == VideoGameQid)
                    return true;
            }
            return false;
        }

        private static string? FirstEntityClaimId(JsonElement claims, string prop)
        {
            if (!claims.TryGetProperty(prop, out var arr)) return null;
            foreach (var c in arr.EnumerateArray())
                if (TryGetClaimEntityId(c, out string? id)) return id;
            return null;
        }

        private static bool TryGetClaimEntityId(JsonElement claim, out string? id)
        {
            id = null;
            if (!claim.TryGetProperty("mainsnak", out var snak)) return false;
            if (!snak.TryGetProperty("datavalue", out var dv)) return false;
            if (!dv.TryGetProperty("value", out var val)) return false;
            if (val.ValueKind != JsonValueKind.Object) return false;
            if (!val.TryGetProperty("id", out var idEl)) return false;
            id = idEl.GetString();
            return id != null;
        }

        // Picks the earliest P577 value (a game can list several per-platform release dates)
        // and formats it to match the Steam path's "MMMM d, yyyy" style, honouring the
        // Wikidata time precision (9 = year, 10 = month, 11 = day).
        private static string? EarliestDate(JsonElement claims, string prop)
        {
            if (!claims.TryGetProperty(prop, out var arr)) return null;

            DateTime best = DateTime.MaxValue;
            int bestPrecision = 11;

            foreach (var c in arr.EnumerateArray())
            {
                if (!c.TryGetProperty("mainsnak", out var snak)) continue;
                if (!snak.TryGetProperty("datavalue", out var dv)) continue;
                if (!dv.TryGetProperty("value", out var val)) continue;
                if (val.ValueKind != JsonValueKind.Object) continue;
                if (!val.TryGetProperty("time", out var timeEl)) continue;

                string? time = timeEl.GetString();
                if (string.IsNullOrEmpty(time)) continue;

                int precision = val.TryGetProperty("precision", out var pEl) ? pEl.GetInt32() : 11;
                if (!TryParseWikidataTime(time, out DateTime dt)) continue;

                if (dt < best)
                {
                    best = dt;
                    bestPrecision = precision;
                }
            }

            if (best == DateTime.MaxValue) return null;

            return bestPrecision switch
            {
                <= 9 => best.ToString("yyyy", CultureInfo.InvariantCulture),
                10 => best.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                _ => best.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
            };
        }

        // Wikidata times look like "+2023-10-27T00:00:00Z". Month/day can be 00 for reduced
        // precision, which DateTime rejects, so clamp them to 01.
        private static bool TryParseWikidataTime(string time, out DateTime dt)
        {
            dt = default;
            try
            {
                string t = time.TrimStart('+');
                if (t.Length < 10) return false;
                int year = int.Parse(t.Substring(0, 4), CultureInfo.InvariantCulture);
                int month = int.Parse(t.Substring(5, 2), CultureInfo.InvariantCulture);
                int day = int.Parse(t.Substring(8, 2), CultureInfo.InvariantCulture);
                if (month < 1) month = 1;
                if (day < 1) day = 1;
                if (year < 1 || month > 12 || day > 31) return false;
                dt = new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
                return true;
            }
            catch { return false; }
        }

        // ── label resolution ──────────────────────────────────────────────────

        private static async Task<string?> ResolveLabelAsync(string qid)
        {
            try
            {
                // languagefallback=1 is required: Wikidata stores language-agnostic proper names
                // (e.g. studio names like "Remedy Entertainment") under the special `mul` label,
                // not `en` — without the fallback, languages=en returns an empty labels object.
                string url = "https://www.wikidata.org/w/api.php?action=wbgetentities" +
                    $"&ids={qid}&props=labels&languages=en&languagefallback=1&format=json";
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("entities", out var ents)) return null;
                if (!ents.TryGetProperty(qid, out var ent)) return null;
                if (!ent.TryGetProperty("labels", out var labels)) return null;
                if (!labels.TryGetProperty("en", out var en)) return null;
                if (!en.TryGetProperty("value", out var val)) return null;
                return val.GetString();
            }
            catch { return null; }
        }
    }
}
