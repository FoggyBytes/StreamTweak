using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace StreamTweak
{
    /// <summary>
    /// Synchronises the local game library with Sunshine's apps.json.
    ///
    /// Strategy:
    ///   • Apps created by StreamTweak are marked with "output": "streamtweak-managed".
    ///   • Manual apps (any other output value) are never touched.
    ///   • On each sync: managed apps whose games are no longer installed are removed;
    ///     newly discovered games are added; existing managed apps are updated (cover art).
    ///
    /// Note: Sunshine reads apps.json on startup and when its own UI saves changes.
    /// If Sunshine is running, a Sunshine UI save may overwrite our changes.
    /// Restarting Sunshine after sync is recommended for changes to take effect immediately.
    /// </summary>
    public static class SunshineSync
    {
        // Custom field used to identify apps managed by StreamTweak.
        // Using a dedicated boolean field avoids collision with any Sunshine-native field.
        private const string MANAGED_FIELD = "_streamtweak_managed";


        // ── Path discovery ────────────────────────────────────────────────────

        private static readonly string[] _knownAppNames = { "Sunshine", "Apollo", "Vibeshine", "Vibepollo" };

        public static string? FindAppsJsonPath()
        {
            string appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localApp   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progData   = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string progFiles  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFiles86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            foreach (string app in _knownAppNames)
            {
                var candidates = new[]
                {
                    Path.Combine(appData,     app, "config", "apps.json"),
                    Path.Combine(appData,     app, "apps.json"),
                    Path.Combine(localApp,    app, "config", "apps.json"),
                    Path.Combine(localApp,    app, "apps.json"),
                    Path.Combine(progData,    app, "config", "apps.json"),
                    Path.Combine(progData,    app, "apps.json"),
                    Path.Combine(progFiles,   app, "config", "apps.json"),
                    Path.Combine(progFiles86, app, "config", "apps.json"),
                };

                foreach (var candidate in candidates)
                    if (File.Exists(candidate)) return candidate;
            }

            // Registry fallback
            foreach (string app in _knownAppNames)
            {
                try
                {
                    string? installDir =
                        Registry.LocalMachine.OpenSubKey($@"SOFTWARE\{app}")
                            ?.GetValue("InstallLocation") as string
                        ?? Registry.LocalMachine.OpenSubKey($@"SOFTWARE\WOW6432Node\{app}")
                            ?.GetValue("InstallLocation") as string;

                    if (!string.IsNullOrEmpty(installDir))
                    {
                        string candidate = Path.Combine(installDir, "config", "apps.json");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Resolves a launched executable path (from the server log's "Executing:" line) to the
        /// display name of the matching app in apps.json, matched by exe filename against each
        /// app's "cmd". This is the ground-truth game name (StreamTweak-synced OR manually added
        /// in the server UI), robust to launcher→game handoffs that process scanning misses.
        /// Returns null if apps.json is unavailable or nothing matches.
        /// </summary>
        public static string? ResolveAppNameForExecutable(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath)) return null;
                string exeFile = Path.GetFileName(exePath);
                if (string.IsNullOrEmpty(exeFile)) return null;

                string? appsJsonPath = FindAppsJsonPath();
                if (string.IsNullOrEmpty(appsJsonPath) || !File.Exists(appsJsonPath)) return null;

                if (JsonNode.Parse(File.ReadAllText(appsJsonPath)) is not JsonObject root) return null;
                if (root["apps"] is not JsonArray apps) return null;

                // Collect ALL matches instead of returning the first one. Several games can
                // legitimately share a launch binary: every Battle.net title launches the
                // Battle.net client, and Xbox/Game Pass titles all go through explorer.exe
                // (see §9). Returning the first match would credit the wrong game — starting
                // Overwatch would log "Diablo IV" purely because it comes first in apps.json.
                // When the executable doesn't identify a single app we return null and leave
                // the game to the process monitor: a missing game is recoverable, a wrong one
                // silently corrupts the session history.
                var matches = new List<string>();
                foreach (var node in apps)
                {
                    if (node is not JsonObject app) continue;
                    string name = app["name"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    string cmd = app["cmd"]?.ToString() ?? "";
                    if (cmd.IndexOf(exeFile, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !matches.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        matches.Add(name);
                    }
                }

                if (matches.Count == 1) return matches[0];
                if (matches.Count > 1)
                {
                    DebugLogger.Log($"SunshineSync: '{exeFile}' matches {matches.Count} apps " +
                                    $"({string.Join(", ", matches)}) — ambiguous, leaving the game to the process monitor");
                }
            }
            catch { /* apps.json unreadable/parse error — fall through to null */ }
            return null;
        }

        /// <summary>
        /// Resolves a launch command to the display name of the app that carries it, matching the
        /// whole command exactly. This is the counterpart of
        /// <see cref="ResolveAppNameForExecutable"/> for commands that are not executable paths —
        /// Steam titles reach the log as <c>steam://rungameid/…</c> and Xbox ones as
        /// <c>explorer.exe shell:appsFolder\…</c>, where there is no filename to match.
        /// <para><b>Both <c>cmd</c> and <c>detached</c> are searched.</b> Steam and Xbox apps are
        /// written with an empty <c>cmd</c> and the real launch in <c>detached</c> (see
        /// <c>BuildLaunchCommand</c>), so looking only at <c>cmd</c> would never match the two
        /// biggest stores.</para>
        /// <para>The exact match still can't be assumed unique: when the Battle.net scanner fails
        /// to resolve a game's own binary it falls back to the Battle.net client, and every game
        /// that fell back then carries the identical command. Same rule as the executable lookup —
        /// more than one match means we don't know, and a missing name is recoverable while a wrong
        /// one silently corrupts what the user is told.</para>
        /// </summary>
        public static string? ResolveAppNameForCommand(string command)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(command)) return null;
                string wanted = NormalizeCommand(command);
                if (wanted.Length == 0) return null;

                string? appsJsonPath = FindAppsJsonPath();
                if (string.IsNullOrEmpty(appsJsonPath) || !File.Exists(appsJsonPath)) return null;

                if (JsonNode.Parse(File.ReadAllText(appsJsonPath)) is not JsonObject root) return null;
                if (root["apps"] is not JsonArray apps) return null;

                var matches = new List<string>();
                foreach (var node in apps)
                {
                    if (node is not JsonObject app) continue;
                    string name = app["name"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (matches.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    bool hit = string.Equals(NormalizeCommand(app["cmd"]?.ToString() ?? ""), wanted,
                                             StringComparison.OrdinalIgnoreCase) && wanted.Length > 0;

                    if (!hit && app["detached"] is JsonArray detached)
                    {
                        foreach (var d in detached)
                        {
                            if (string.Equals(NormalizeCommand(d?.ToString() ?? ""), wanted,
                                              StringComparison.OrdinalIgnoreCase))
                            {
                                hit = true;
                                break;
                            }
                        }
                    }

                    if (hit) matches.Add(name);
                }

                if (matches.Count == 1) return matches[0];
                if (matches.Count > 1)
                {
                    DebugLogger.Log($"SunshineSync: command '{wanted}' matches {matches.Count} apps " +
                                    $"({string.Join(", ", matches)}) — ambiguous, no name resolved");
                }
            }
            catch { /* apps.json unreadable/parse error — fall through to null */ }
            return null;
        }

        /// <summary>
        /// apps.json stores executable commands quoted (<c>"C:\games\x.exe"</c>) while the server
        /// log prints them unquoted, so the two never compare equal without this.
        /// </summary>
        private static string NormalizeCommand(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
            return s;
        }

        // ── Sync ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads apps.json, updates the managed section, writes it back.
        /// </summary>
        /// <param name="appsJsonPath">Full path to Sunshine's apps.json.</param>
        /// <param name="games">Current list of discovered installed games.</param>
        /// <param name="coverDir">Directory containing cached cover art images.</param>
        /// <returns>(added, removed) counts.</returns>
        public static (int added, int removed, string? writeError) Sync(
            string appsJsonPath,
            IReadOnlyList<DiscoveredGame> games,
            string coverDir)
        {
            // Read existing file (or start fresh)
            string existingJson = "{}";
            if (File.Exists(appsJsonPath))
            {
                try { existingJson = File.ReadAllText(appsJsonPath); }
                catch { }
            }

            JsonObject doc;
            JsonArray appsArray;
            try
            {
                doc = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
                appsArray = doc["apps"] as JsonArray ?? new JsonArray();
            }
            catch
            {
                doc = new JsonObject();
                appsArray = new JsonArray();
            }

            // Separate manual vs managed entries
            var manualApps  = new List<JsonNode>();
            var managedApps = new List<JsonNode>();

            foreach (var entry in appsArray)
            {
                if (entry == null) continue;
                bool isManaged = entry[MANAGED_FIELD]?.GetValue<bool>() == true
                             || (entry["output"]?.GetValue<string>() == "streamtweak-managed"); // legacy
                if (isManaged)
                    managedApps.Add(entry);
                else
                    manualApps.Add(entry);
            }

            // Build lookups for existing managed entries — safe against duplicates in apps.json.
            // Primary key: _steam_appid (survives renames).
            // Secondary key: name (non-Steam games, or legacy entries without _steam_appid).
            var existingByAppId = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            var existingByName  = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in managedApps)
            {
                string? appId = entry["_steam_appid"]?.GetValue<string>();
                string  name  = entry["name"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(appId) && !existingByAppId.ContainsKey(appId))
                    existingByAppId[appId] = entry;
                if (!string.IsNullOrEmpty(name) && !existingByName.ContainsKey(name))
                    existingByName[name] = entry;
            }

            // Names already taken by a manual app
            var manualNames = new HashSet<string>(
                manualApps.Select(a => a["name"]?.GetValue<string>() ?? ""),
                StringComparer.OrdinalIgnoreCase);

            // Build new managed list
            var newManaged  = new List<JsonNode>();
            var usedEntries = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
            int added = 0;

            foreach (var game in games)
            {
                if (manualNames.Contains(game.Name)) continue;

                string coverPath  = CoverArtFetcher.GetCachedPath(game, coverDir) ?? "";
                string workingDir = string.IsNullOrEmpty(game.ExePath)
                    ? ""
                    : Path.GetDirectoryName(game.ExePath) ?? "";
                BuildLaunchCommand(game, out string cmd, out string detached);

                // Match by AppId first (stable across renames), then fall back to name
                JsonNode? existing = null;
                if (game.SteamAppId != null)
                    existingByAppId.TryGetValue(game.SteamAppId, out existing);
                if (existing == null)
                    existingByName.TryGetValue(game.Name, out existing);

                if (existing != null && !usedEntries.Contains(existing))
                {
                    // Update all mutable fields.
                    // Setting ["name"] propagates user renames from StreamTweak → Sunshine.
                    existing["name"]         = game.Name;
                    existing["cmd"]          = cmd;
                    existing["working-dir"]  = workingDir;
                    existing["_steam_appid"] = game.SteamAppId;
                    existing["detached"]     = string.IsNullOrEmpty(detached)
                        ? new JsonArray()
                        : new JsonArray(JsonValue.Create(detached));
                    if (!string.IsNullOrEmpty(coverPath))
                        existing["image-path"] = coverPath;
                    // Backfill id/uuid if missing; migrate legacy output marker
                    if (existing["id"] == null)
                        existing["id"] = StableAppId(game.Name);
                    if (existing["uuid"] == null)
                        existing["uuid"] = Guid.NewGuid().ToString("D").ToUpperInvariant();
                    if (existing["output"]?.GetValue<string>() == "streamtweak-managed")
                        existing.AsObject().Remove("output");
                    existing[MANAGED_FIELD] = true;
                    newManaged.Add(existing);
                    usedEntries.Add(existing);
                }
                else if (existing == null)
                {
                    // Genuinely new game
                    var newEntry = new JsonObject
                    {
                        [MANAGED_FIELD]  = true,
                        ["id"]           = StableAppId(game.Name),
                        ["uuid"]         = Guid.NewGuid().ToString("D").ToUpperInvariant(),
                        ["_steam_appid"] = game.SteamAppId,
                        ["name"]         = game.Name,
                        ["cmd"]          = cmd,
                        ["image-path"]   = coverPath,
                        ["working-dir"]  = workingDir,
                        ["prep-cmd"]     = new JsonArray(),
                        ["detached"]     = string.IsNullOrEmpty(detached)
                            ? new JsonArray()
                            : new JsonArray(JsonValue.Create(detached)),
                    };
                    newManaged.Add(newEntry);
                    usedEntries.Add(newEntry);
                    added++;
                }
                // else: duplicate game in gamesToSync (same entry already processed) — skip
            }

            int removed = managedApps.Count(a => !usedEntries.Contains(a));

            // Rebuild apps array: manual first, then managed
            var finalApps = new JsonArray();
            foreach (var app in manualApps)  finalApps.Add(app?.DeepClone());
            foreach (var app in newManaged)   finalApps.Add(app?.DeepClone());

            doc["apps"] = finalApps;

            // Write back — route through StreamTweakService (LocalSystem) so we can write
            // to protected paths like C:\Program Files\Sunshine\config\apps.json.
            string newJson = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            bool written = SpeedChanger.WriteAppsJson(appsJsonPath, newJson);
            if (!written)
            {
                // Service unavailable (dev environment) — try direct write as fallback
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(appsJsonPath)!);
                    File.WriteAllText(appsJsonPath, newJson, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { return (0, 0, ex.Message); }
            }

            return (added, removed, null);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Generates a stable positive numeric app ID from the game name using FNV-1a.
        /// Sunshine uses a numeric string as the app ID in the Moonlight protocol.
        /// The same name always produces the same ID, so IDs survive re-syncs.
        /// </summary>
        private static string StableAppId(string name)
        {
            uint hash = 2166136261u;
            foreach (char c in name.ToLowerInvariant())
            {
                hash ^= (uint)c;
                hash *= 16777619u;
            }
            // Clamp to positive 31-bit range to match Sunshine's signed-int app IDs
            return (hash & 0x7FFFFFFFu).ToString();
        }

        // ── Rename ────────────────────────────────────────────────────────────

        /// <summary>
        /// Renames a single StreamTweak-managed entry in apps.json in-place.
        /// Matches by <paramref name="steamAppId"/> first, then by <paramref name="oldName"/>.
        /// Returns null on success (or when the entry is not found), error string on write failure.
        /// </summary>
        public static string? RenameApp(
            string appsJsonPath,
            string? steamAppId,
            string oldName,
            string newName)
        {
            if (!File.Exists(appsJsonPath)) return null;

            string existingJson;
            try { existingJson = File.ReadAllText(appsJsonPath); }
            catch (Exception ex) { return ex.Message; }

            JsonObject doc;
            try { doc = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject(); }
            catch { return null; }

            var appsArray = doc["apps"] as JsonArray;
            if (appsArray == null) return null;

            bool found = false;
            foreach (var entry in appsArray)
            {
                if (entry == null) continue;
                bool isManaged = entry[MANAGED_FIELD]?.GetValue<bool>() == true
                              || entry["output"]?.GetValue<string>() == "streamtweak-managed";
                if (!isManaged) continue;

                string? entryAppId = entry["_steam_appid"]?.GetValue<string>();
                string? entryName  = entry["name"]?.GetValue<string>();

                bool byId   = steamAppId != null
                           && steamAppId.Equals(entryAppId, StringComparison.OrdinalIgnoreCase);
                bool byName = entryName != null
                           && entryName.Equals(oldName, StringComparison.OrdinalIgnoreCase);

                if (byId || byName)
                {
                    entry["name"] = newName;
                    // Keep existing id/uuid stable — do NOT regenerate them on rename
                    found = true;
                    break;
                }
            }

            if (!found) return null; // Not yet in Sunshine; next sync will add it

            string newJson = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            bool written = SpeedChanger.WriteAppsJson(appsJsonPath, newJson);
            if (!written)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(appsJsonPath)!);
                    File.WriteAllText(appsJsonPath, newJson, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { return ex.Message; }
            }

            return null;
        }

        // ── Clear ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes all StreamTweak-managed entries from apps.json, leaving manual apps untouched.
        /// Returns null on success, or an error string if the file could not be written.
        /// </summary>
        public static string? ClearAll(string appsJsonPath)
        {
            if (!File.Exists(appsJsonPath)) return null;

            string existingJson;
            try { existingJson = File.ReadAllText(appsJsonPath); }
            catch (Exception ex) { return ex.Message; }

            JsonObject doc;
            JsonArray appsArray;
            try
            {
                doc = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
                appsArray = doc["apps"] as JsonArray ?? new JsonArray();
            }
            catch { return null; }

            var manualApps = appsArray
                .Where(e => e != null
                         && e[MANAGED_FIELD]?.GetValue<bool>() != true
                         && e["output"]?.GetValue<string>() != "streamtweak-managed")
                .ToList();

            var finalApps = new JsonArray();
            foreach (var app in manualApps)
                finalApps.Add(app?.DeepClone());
            doc["apps"] = finalApps;

            string newJson = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            bool written = SpeedChanger.WriteAppsJson(appsJsonPath, newJson);
            if (!written)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(appsJsonPath)!);
                    File.WriteAllText(appsJsonPath, newJson, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { return ex.Message; }
            }

            return null;
        }

        // ── Add single app ───────────────────────────────────────────────

        /// <summary>
        /// Adds a single game to Sunshine's apps.json without touching any existing entries.
        /// Returns null on success, or an error string on write failure.
        /// </summary>
        public static string? AddApp(string appsJsonPath, DiscoveredGame game, string coverDir)
        {
            string existingJson = "{}";
            if (File.Exists(appsJsonPath))
            {
                try { existingJson = File.ReadAllText(appsJsonPath); }
                catch { }
            }

            JsonObject doc;
            JsonArray appsArray;
            try
            {
                doc = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
                appsArray = doc["apps"] as JsonArray ?? new JsonArray();
            }
            catch
            {
                doc = new JsonObject();
                appsArray = new JsonArray();
            }

            // Prevent duplicates: if any entry (managed or manual) with this name already exists, skip.
            foreach (var entry in appsArray)
            {
                if (entry == null) continue;
                if (string.Equals(entry["name"]?.GetValue<string>(), game.Name,
                        StringComparison.OrdinalIgnoreCase))
                    return null; // already present — nothing to add
            }

            string coverPath  = CoverArtFetcher.GetCachedPath(game, coverDir) ?? "";
            string workingDir = string.IsNullOrEmpty(game.ExePath)
                ? "" : Path.GetDirectoryName(game.ExePath) ?? "";
            BuildLaunchCommand(game, out string cmd, out string detached);

            var newEntry = new JsonObject
            {
                [MANAGED_FIELD]  = true,
                ["id"]           = StableAppId(game.Name),
                ["uuid"]         = Guid.NewGuid().ToString("D").ToUpperInvariant(),
                ["name"]         = game.Name,
                ["cmd"]          = cmd,
                ["image-path"]   = coverPath,
                ["working-dir"]  = workingDir,
                ["prep-cmd"]     = new JsonArray(),
                ["detached"]     = string.IsNullOrEmpty(detached)
                    ? new JsonArray()
                    : new JsonArray(JsonValue.Create(detached)),
            };

            appsArray.Add(newEntry);
            doc["apps"] = appsArray;

            string newJson = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            bool written = SpeedChanger.WriteAppsJson(appsJsonPath, newJson);
            if (!written)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(appsJsonPath)!);
                    File.WriteAllText(appsJsonPath, newJson, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { return ex.Message; }
            }

            return null;
        }

        // ── Remove single app ─────────────────────────────────────────────

        /// <summary>
        /// Removes a single StreamTweak-managed entry from apps.json by name.
        /// Returns null on success, or an error string on write failure.
        /// </summary>
        public static string? RemoveApp(string appsJsonPath, string gameName)
        {
            if (!File.Exists(appsJsonPath)) return null;

            string existingJson;
            try { existingJson = File.ReadAllText(appsJsonPath); }
            catch (Exception ex) { return ex.Message; }

            JsonObject doc;
            JsonArray appsArray;
            try
            {
                doc = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
                appsArray = doc["apps"] as JsonArray ?? new JsonArray();
            }
            catch { return null; }

            var kept = new JsonArray();
            foreach (var entry in appsArray)
            {
                if (entry == null) continue;
                bool isManaged = entry[MANAGED_FIELD]?.GetValue<bool>() == true
                              || entry["output"]?.GetValue<string>() == "streamtweak-managed";
                string? name = entry["name"]?.GetValue<string>();

                if (isManaged && name != null && name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                    continue; // skip this one — it's the entry to remove

                kept.Add(entry.DeepClone());
            }

            doc["apps"] = kept;
            string newJson = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            bool written = SpeedChanger.WriteAppsJson(appsJsonPath, newJson);
            if (!written)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(appsJsonPath)!);
                    File.WriteAllText(appsJsonPath, newJson, System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { return ex.Message; }
            }

            return null;
        }

        // ── Launch command helpers ────────────────────────────────────────────

        /// <summary>
        /// The URI a store's own launcher needs to start a game, or <c>null</c> when the game is
        /// started by running its executable.
        /// </summary>
        /// <remarks>
        /// Shared on purpose. There are two places that launch a game — the apps.json the
        /// streaming server runs, and the Play button in the Library page — and when only the
        /// first one knew about this, an Epic title streamed correctly and failed when started
        /// from StreamTweak's own window. A rule that lives in one of two launchers is a rule
        /// that will disagree with itself.
        /// </remarks>
        public static string? StoreLaunchUri(string? store, string? launchId)
        {
            if (string.Equals(store, "Epic Games", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(launchId))
            {
                // Verbatim, no Uri.EscapeDataString — see the note in BuildLaunchCommand.
                return $"com.epicgames.launcher://apps/{launchId}?action=launch&silent=true";
            }

            return null;
        }

        private static void BuildLaunchCommand(DiscoveredGame game, out string cmd, out string detached)
        {
            switch (game.Store)
            {
                case "Steam" when game.SteamAppId != null:
                    // Use Steam protocol so the Steam overlay works correctly
                    cmd      = "";
                    detached = $"steam://rungameid/{game.SteamAppId}";
                    break;

                case "Epic Games" when !string.IsNullOrEmpty(game.LaunchId):
                    // Through the launcher's own protocol, and it has to be. Most Epic titles
                    // cannot be started by running their exe at all — the entitlement and Epic
                    // Online Services tokens come in on the command line from the launcher, so a
                    // direct launch plays the intro and then quits. Having the launcher already
                    // running changes nothing; the game has to be started *by* it.
                    //
                    // This is what the comment here always claimed the code did. It didn't:
                    // it ran the exe, and Alan Wake 2 quit to the client every time.
                    //
                    // ⚠️ The id goes in verbatim — no Uri.EscapeDataString. It is built only
                    // from the manifest's own ids (namespace, catalog item, app name), which
                    // are hex, so the only thing escaping would touch is the two colons *we*
                    // put between them, turning them into %3A. Plain colons are the form Epic
                    // writes into its own desktop shortcuts, so that is the form with evidence
                    // behind it; %3A may well work too, and that is precisely the difference
                    // between an untested launch and a tested one.
                    cmd      = "";
                    detached = StoreLaunchUri(game.Store, game.LaunchId)!;
                    break;

                case "Epic Games":
                    // No launch id in the manifest: nothing better to try than the exe, which is
                    // what every version up to now did for all Epic titles.
                    cmd      = $"\"{game.ExePath}\"";
                    detached = "";
                    break;

                case "Xbox" when !string.IsNullOrEmpty(game.StoreId):
                    // Xbox/Game Pass UWP apps must be launched via the shell:appsFolder protocol
                    // StoreId = "PackageFamilyName!ApplicationId"
                    cmd      = "";
                    detached = $"explorer.exe shell:appsFolder\\{game.StoreId}";
                    break;

                default:
                    // Direct exe launch (GOG, Ubisoft Connect, EA App, etc.)
                    cmd      = $"\"{game.ExePath}\"";
                    detached = "";
                    break;
            }
        }
    }
}
