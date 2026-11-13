using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Orchestrates a full game library sync cycle:
    ///   1. Auto-detect Sunshine apps.json path
    ///   2. Scan installed games (Steam / Epic / GOG / Ubisoft / Xbox / EA)
    ///   3. Enrich with Steam Store API (correct names, drop non-games)
    ///   4. Download missing cover art (Steam CDN + native store CDNs)
    ///   5. Write updated apps.json (enabled games only)
    ///   6. Persist the merged game list to GameLibraryState
    ///
    /// Called both from the "Sync Now" button and automatically at app startup
    /// when Game Library Sync is enabled.
    /// </summary>
    public static class GameLibraryService
    {
        private static readonly string CoverDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "covers");

        // Guards against concurrent sync runs (e.g. auto-sync at startup + user clicking "Sync Now").
        private static readonly System.Threading.SemaphoreSlim _syncLock = new(1, 1);

        // Detected streaming server name (Sunshine / Apollo / Vibeshine / Vibepollo).
        // Lazily resolved once and cached for the app lifetime.
        private static string? _hostAppName;
        private static string HostAppName =>
            _hostAppName ??= LogParser.FindStreamingAppInfo()?.AppName ?? "Sunshine";

        /// <summary>
        /// Performs a full sync and returns a human-readable status string.
        /// Pass isManual=true when triggered by the user's "Sync Now" button —
        /// this clears the excluded-games blacklist so removed games reappear.
        /// Never throws — errors are returned as a status message.
        /// </summary>
        /// <summary>
        /// Raised after a sync has saved the library, whoever asked for it — the Library page,
        /// the start-up sync, or StreamLight's Sync and restart (10.0.0). An open Library page
        /// reloads on it. Raised on the sync's thread: subscribers marshal to the UI.
        /// </summary>
        public static event Action? LibraryChanged;

        /// <summary>
        /// The sync: scan the stores, keep the user's choices, write the enabled games to apps.json.
        /// Excluded games (Enabled false) stay in the list and out of the server at every sync,
        /// whoever starts it; since 10.0 there is no "manual" sync that brings them back.
        /// </summary>
        public static async Task<string> PerformSyncAsync()
        {
            if (!await _syncLock.WaitAsync(0))
                return "Sync already in progress.";
            try
            {
                // 1. Find Sunshine
                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson == null)
                    return $"{HostAppName} not found. Make sure Sunshine, Apollo, Vibeshine or Vibepollo is installed.";

                var state = GameLibraryState.Current;

                // 2. Scan game libraries
                var games = await Task.Run(() => GameLibraryScanner.ScanAll());
                if (games.Count == 0)
                    return "No installed games found.";

                // 3. Enrich with Steam metadata (correct names, type filter)
                games = await SteamMetadataFetcher.EnrichAsync(games, state.Games);
                if (games.Count == 0)
                    return "No games found after filtering.";

                // Deduplicate: scanner can return the same game from multiple library paths.
                // Use SteamAppId, then StoreId (store-specific key), then Store:Name as fallback.
                games = games
                    .GroupBy(g => g.SteamAppId
                                  ?? (g.StoreId != null ? $"{g.Store}:{g.StoreId}" : null)
                                  ?? $"{g.Store}:{g.Name}",
                             StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                // Games removed before 10.0 by a key without a name: kept out until the Library's
                // "Let them back" clears the keys.
                if (state.ExcludedGames.Count > 0)
                {
                    var excluded = new System.Collections.Generic.HashSet<string>(
                        state.ExcludedGames, StringComparer.OrdinalIgnoreCase);
                    games = games.Where(g => !excluded.Contains(GetExclusionKey(g))).ToList();
                    if (games.Count == 0)
                        return "No games found after filtering.";
                }

                // 4. Download missing cover art
                // Step 4a: Steam games — Steam CDN (no API key required)
                await CoverArtFetcher.FetchAllAsync(games, CoverDir);
                // Step 4b: All other stores — RAWG.io (requires RawgApiKey in config.json)
                await StoreCoverFetcher.FetchAllAsync(games, CoverDir);

                // 5. Merge with persisted state so Enabled flags survive re-syncs.
                //    Match by: SteamAppId (Steam) → StoreId (non-Steam) → Name (fallback)
                var autoEntries = state.Games.Where(e => !e.IsManual).ToList();
                var oldByAppId = autoEntries
                    .Where(e => e.SteamAppId != null)
                    .ToDictionary(e => e.SteamAppId!, StringComparer.OrdinalIgnoreCase);
                var oldByStoreId = autoEntries
                    .Where(e => e.SteamAppId == null && e.StoreId != null)
                    .ToDictionary(e => $"{e.Store}:{e.StoreId}", StringComparer.OrdinalIgnoreCase);
                var oldByName = autoEntries
                    .Where(e => e.SteamAppId == null && e.StoreId == null)
                    .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

                var mergedGames = games.Select(g =>
                {
                    GameLibraryEntry? existing = null;
                    if (g.SteamAppId != null)
                        oldByAppId.TryGetValue(g.SteamAppId, out existing);
                    if (existing == null && g.StoreId != null)
                        oldByStoreId.TryGetValue($"{g.Store}:{g.StoreId}", out existing);
                    if (existing == null)
                        oldByName.TryGetValue(g.Name, out existing);

                    if (existing != null)
                    {
                        // Update name only when API resolved a better one and user hasn't renamed
                        if (!existing.UserRenamed && g.Name != existing.Name)
                            existing.Name = g.Name;
                        existing.Store      = g.Store;
                        existing.SteamAppId = g.SteamAppId;
                        existing.StoreId    = g.StoreId;
                        existing.ExePath    = g.ExePath;      // refresh path from latest scan
                        existing.InstallDir = g.InstallDir;   // refresh install dir (used by Strategy 2 process detection)
                        existing.LaunchId   = g.LaunchId;     // refresh the store's own launch id (Epic)
                        existing.GameExePath    = g.GameExePath;     // 10.4.0: the game behind a launcher (GOG)
                        existing.GameWorkingDir = g.GameWorkingDir;
                        existing.GameArguments  = g.GameArguments;
                        // Refresh ProcessName from scanner when available (Xbox/UWP) — don't
                        // clobber user-set values: only overwrite when the scanner provides one.
                        if (!string.IsNullOrEmpty(g.ProcessName))
                            existing.ProcessName = g.ProcessName;
                        return existing;
                    }

                    return new GameLibraryEntry
                    {
                        Name        = g.Name,
                        Store       = g.Store,
                        SteamAppId  = g.SteamAppId,
                        StoreId     = g.StoreId,
                        ExePath     = g.ExePath,               // persist path from scanner
                        InstallDir  = g.InstallDir,            // persist install dir (used by Strategy 2 process detection)
                        LaunchId    = g.LaunchId,              // the store's own launch id, when its launcher must start the game
                        ProcessName = g.ProcessName,           // persist runtime process name (Xbox/UWP)
                        GameExePath    = g.GameExePath,        // 10.4.0: the game behind a launcher (GOG)
                        GameWorkingDir = g.GameWorkingDir,
                        GameArguments  = g.GameArguments,
                        Enabled     = true,
                    };
                }).ToList();

                // Each scanned game with its entry, by position, before the list is extended and
                // sorted: the sync writes the scan's facts with the user's launch choice (10.4.0).
                var scannedWithEntry = games.Zip(mergedGames).ToList();

                // Preserve manually-added games across re-syncs
                var manualGames = state.Games.Where(e => e.IsManual).ToList();
                mergedGames.AddRange(manualGames);

                // Sort alphabetically
                mergedGames.Sort((a, b) =>
                    string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                // 6. Sync only enabled games to Sunshine, each with its entry's launch choice (10.4.0).
                // Paired by position instead of the old lookup by app id / store id / name, which
                // found the same entry: the merge above builds one entry per scanned game.
                var gamesToSync = scannedWithEntry
                    .Where(p => p.Second.Enabled)
                    .Select(p => p.First with
                    {
                        LaunchChoice     = p.Second.LaunchChoice,
                        CustomCommand    = p.Second.CustomCommand,
                        CustomWorkingDir = p.Second.CustomWorkingDir,
                    })
                    .ToList();

                // Add enabled manual games to the sync list
                foreach (var mg in manualGames.Where(m => m.Enabled && !string.IsNullOrEmpty(m.ExePath)))
                    gamesToSync.Add(new DiscoveredGame(mg.Name, mg.Store, null, mg.ExePath!,
                        LaunchChoice: mg.LaunchChoice, CustomCommand: mg.CustomCommand, CustomWorkingDir: mg.CustomWorkingDir,
                        CoverPath: mg.CustomCoverPath));

                var (added, removed, writeError) = await Task.Run(() =>
                    SunshineSync.Sync(appsJson, gamesToSync, CoverDir));

                if (writeError != null)
                    return $"Sync failed — could not write apps.json: {writeError}\n" +
                           "Make sure StreamTweakService is running and up to date.";

                // 7. Persist merged state
                state.Games       = mergedGames;
                state.LastSyncUtc = DateTime.UtcNow;
                state.Save();
                try { LibraryChanged?.Invoke(); } catch { }

                var local = state.LastSyncUtc.Value.ToLocalTime();
                return $"Done — {mergedGames.Count} games ({added} added, {removed} removed).\n" +
                       $"Last sync: {local:dd/MM/yyyy HH:mm}.";
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"GameLibraryService: sync error — {ex.Message}");
                return $"Sync failed: {ex.Message}";
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private static string GetExclusionKey(DiscoveredGame g) =>
            g.SteamAppId != null ? $"steam:{g.SteamAppId}" :
            g.StoreId    != null ? $"{g.Store}:{g.StoreId}" :
                                   $"{g.Store}:{g.Name}";

        /// <summary>
        /// Adds a single manually-selected game to the library and syncs it to Sunshine.
        /// </summary>
        public static async Task<string> AddManualGameAsync(string exePath)
        {
            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return "Sync in progress — please try again.";
            try
            {
                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson == null)
                    return $"{HostAppName} not found.";

                // Derive display name from the executable
                string name;
                try
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                    name = !string.IsNullOrWhiteSpace(vi.ProductName)
                        ? vi.ProductName : Path.GetFileNameWithoutExtension(exePath);
                }
                catch { name = Path.GetFileNameWithoutExtension(exePath); }

                string? manualInstallDir = null;
                try { manualInstallDir = Path.GetDirectoryName(exePath); }
                catch { }

                var entry = new GameLibraryEntry
                {
                    Name       = name,
                    Store      = "Manual",
                    ExePath    = exePath,
                    InstallDir = manualInstallDir,   // enables Strategy 2 detection in SessionProcessMonitor
                    IsManual   = true,
                    Enabled    = true,
                };

                var state = GameLibraryState.Current;
                state.Games.Add(entry);
                state.Save();

                // Add the single game to Sunshine without re-syncing everything
                var singleGame = new DiscoveredGame(name, "Manual", null, exePath);
                await Task.Run(() => SunshineSync.AddApp(appsJson, singleGame, CoverDir));

                return $"Added \"{name}\" to library.";
            }
            catch (Exception ex)
            {
                return $"Failed to add game: {ex.Message}";
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// 10.4.0, the Library's menu on the command: how this game starts — null (automatic),
        /// <see cref="GameLibraryEntry.ChoiceStore"/> or <see cref="GameLibraryEntry.ChoiceCustom"/>
        /// with its command and folder. Saved in the entry, so every later sync writes it, and
        /// written into this game's apps.json entry now. Returns null, or why it failed. The server
        /// reads apps.json when it starts: the change shows after Sync and restart.
        /// </summary>
        public static async Task<string?> SetLaunchChoiceAsync(GameLibraryEntry entry, string? choice,
                                                              string? customCommand = null, string? customWorkingDir = null)
        {
            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return "a sync is running — try again in a moment";
            try
            {
                var state = GameLibraryState.Current;
                entry.LaunchChoice = choice;
                if (choice == GameLibraryEntry.ChoiceCustom)
                {
                    entry.CustomCommand    = customCommand?.Trim();
                    entry.CustomWorkingDir = string.IsNullOrWhiteSpace(customWorkingDir) ? null : customWorkingDir.Trim();
                }
                // Another choice keeps the custom command: picking it again brings it back.
                state.Save();

                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson == null) return $"{HostAppName} not found";
                string? error = await Task.Run(() => SunshineSync.UpdateLaunch(appsJson, entry.ToLaunchGame()));
                DebugLogger.Log($"GameLibraryService: launch of \"{entry.Name}\" set to {choice ?? "automatic"}" +
                                (error != null ? $" — apps.json not written: {error}" : ""));
                return error;
            }
            catch (Exception ex) { return ex.Message; }
            finally { _syncLock.Release(); }
        }

        /// <summary>
        /// 10.4.2: a game added by hand renamed — in the list and in its apps.json entry. Refused when
        /// the name is empty or another game, StreamTweak's or the server's own, already has it.
        /// Returns null, or why not. The server shows it after Sync and restart.
        /// </summary>
        public static async Task<string?> RenameManualGameAsync(GameLibraryEntry entry, string newName)
        {
            newName = (newName ?? "").Trim();
            if (newName.Length == 0) return "the name is empty";
            if (!entry.IsManual) return "only games added by hand can be renamed here";
            if (newName == entry.Name) return null;

            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return "a sync is running — try again in a moment";
            try
            {
                var state = GameLibraryState.Current;
                if (state.Games.Any(g => !ReferenceEquals(g, entry)
                                         && string.Equals(g.Name, newName, StringComparison.OrdinalIgnoreCase)))
                    return $"another game is already called \"{newName}\"";

                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson != null && SunshineSync.NameTaken(appsJson, newName, except: entry.Name))
                    return $"{HostAppName} already has an entry called \"{newName}\"";

                string oldName = entry.Name;
                string? error = appsJson == null ? null
                    : await Task.Run(() => SunshineSync.UpdateAppLook(appsJson, oldName, newName, null));
                if (error != null) return $"could not write apps.json: {error}";

                entry.Name        = newName;
                entry.UserRenamed = true;
                state.Save();
                DebugLogger.Log($"GameLibraryService: \"{oldName}\" renamed \"{newName}\"");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { _syncLock.Release(); }
        }

        /// <summary>
        /// 10.4.2: a picture for a game added by hand. Converted to PNG (the server takes nothing
        /// else) into the covers folder under a name taken from its content, made the game's
        /// image-path, and the picture it replaces deleted. Returns null, or why not. The server
        /// shows it after Sync and restart, and StreamLight with it.
        /// </summary>
        public static async Task<string?> SetCustomCoverAsync(GameLibraryEntry entry, string sourcePath)
        {
            if (!entry.IsManual) return "only games added by hand take a picture here";
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(sourcePath); }
            catch (Exception ex) { return ex.Message; }

            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant();
            string fileName = $"custom_{hash}.png";
            string path = Path.Combine(CoverDir, fileName);

            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return "a sync is running — try again in a moment";
            try
            {
                Directory.CreateDirectory(CoverDir);
                if (!File.Exists(path))
                {
                    try
                    {
                        if (await StoreCoverFetcher.SaveBytesAsPngAsync(bytes, path) == 0 && !File.Exists(path))
                            return "the picture could not be read";
                    }
                    catch { return "this file is not a picture StreamTweak can read"; }
                }

                string? appsJson = SunshineSync.FindAppsJsonPath();
                string? error = appsJson == null ? null
                    : await Task.Run(() => SunshineSync.UpdateAppLook(appsJson, entry.Name, null, path));
                if (error != null) return $"could not write apps.json: {error}";

                var state = GameLibraryState.Current;
                string? previous = entry.CustomCover;
                entry.CustomCover = fileName;
                state.Save();
                if (!string.Equals(previous, fileName, StringComparison.OrdinalIgnoreCase))
                    DeleteCustomCover(previous, state);
                DebugLogger.Log($"GameLibraryService: picture of \"{entry.Name}\" set to {fileName}");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { _syncLock.Release(); }
        }

        /// <summary>A custom picture no game uses any more, deleted. Only ever a custom_*.png.</summary>
        private static void DeleteCustomCover(string? fileName, GameLibraryState state)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)) return;
            if (state.Games.Any(g => string.Equals(g.CustomCover, fileName, StringComparison.OrdinalIgnoreCase))) return;
            try { File.Delete(Path.Combine(CoverDir, Path.GetFileName(fileName))); } catch { }
        }

        /// <summary>
        /// Removes a single game from the library and from Sunshine's apps.json.
        /// </summary>
        public static async Task<string> RemoveGameAsync(GameLibraryEntry entry)
        {
            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return "Sync in progress — please try again.";
            try
            {
                var state = GameLibraryState.Current;
                state.Games.Remove(entry);

                // Blacklist non-manual games so auto-sync doesn't re-add them.
                if (!entry.IsManual)
                {
                    string key = GameLibraryState.GetExclusionKey(entry);
                    if (!state.ExcludedGames.Contains(key, StringComparer.OrdinalIgnoreCase))
                        state.ExcludedGames.Add(key);
                }

                state.Save();

                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson != null)
                {
                    string? error = await Task.Run(() =>
                        SunshineSync.RemoveApp(appsJson, entry.Name));
                    if (error != null)
                        return $"Removed from list, but could not update {HostAppName}: {error}";
                }

                DeleteCustomCover(entry.CustomCover, state);
                return $"Removed \"{entry.Name}\".";
            }
            catch (Exception ex)
            {
                return $"Failed to remove game: {ex.Message}";
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// 10.0, Clear library: every entry StreamTweak wrote leaves apps.json and the list starts
        /// empty — synced games, games added by hand, exclusions (in the list and the pre-10.0 keys),
        /// the last sync's time. The server's own entries stay. Returns null, or why it failed. The
        /// server shows it after a restart: App.Library runs this as the first step of its job.
        /// </summary>
        public static async Task<string?> ClearAsync()
        {
            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return "a sync is running";
            try
            {
                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson == null) return $"{HostAppName} not found";

                string? error = await Task.Run(() => SunshineSync.ClearAll(appsJson));
                if (error != null) return $"could not write apps.json: {error}";

                var state = GameLibraryState.Current;
                state.Games.Clear();
                state.ExcludedGames.Clear();
                state.LastSyncUtc = null;
                state.Save();
                return null;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"GameLibraryService: clear error — {ex.Message}");
                return ex.Message;
            }
            finally
            {
                _syncLock.Release();
            }
        }

    }
}
