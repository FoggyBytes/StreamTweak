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
        public static async Task<string> PerformSyncAsync(bool isManual = false)
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

                // Manual sync clears the exclusion list so previously removed games reappear.
                if (isManual)
                    state.ExcludedGames.Clear();

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

                // Auto-sync: skip games the user explicitly removed.
                if (!isManual && state.ExcludedGames.Count > 0)
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
                        Enabled     = true,
                    };
                }).ToList();

                // Preserve manually-added games across re-syncs
                var manualGames = state.Games.Where(e => e.IsManual).ToList();
                mergedGames.AddRange(manualGames);

                // Sort alphabetically
                mergedGames.Sort((a, b) =>
                    string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                // 6. Sync only enabled games to Sunshine
                // Include manual games as DiscoveredGame objects
                var gamesToSync = games.Where(g =>
                    mergedGames.FirstOrDefault(e =>
                        !e.IsManual &&
                        ((e.SteamAppId != null && e.SteamAppId == g.SteamAppId) ||
                        (e.StoreId    != null && e.StoreId    == g.StoreId && e.Store == g.Store) ||
                        e.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)))
                        ?.Enabled ?? false
                ).ToList();

                // Add enabled manual games to the sync list
                foreach (var mg in manualGames.Where(m => m.Enabled && !string.IsNullOrEmpty(m.ExePath)))
                    gamesToSync.Add(new DiscoveredGame(mg.Name, mg.Store, null, mg.ExePath!));

                var (added, removed, writeError) = await Task.Run(() =>
                    SunshineSync.Sync(appsJson, gamesToSync, CoverDir));

                if (writeError != null)
                    return $"Sync failed — could not write apps.json: {writeError}\n" +
                           "Make sure StreamTweakService is running and up to date.";

                // 7. Persist merged state
                state.Games       = mergedGames;
                state.LastSyncUtc = DateTime.UtcNow;
                state.Save();

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
        /// Removes all StreamTweak-managed apps from Sunshine and clears the local game list.
        /// Never throws — errors are returned as a status message.
        /// </summary>
        public static async Task<string> ClearSyncAsync()
        {
            if (!await _syncLock.WaitAsync(0))
                return "Sync already in progress.";
            try
            {
                string? appsJson = SunshineSync.FindAppsJsonPath();
                if (appsJson == null)
                    return $"{HostAppName} not found.";

                string? error = await Task.Run(() => SunshineSync.ClearAll(appsJson));
                if (error != null)
                    return $"Clear failed — could not write apps.json: {error}\n" +
                           "Make sure StreamTweakService is running and up to date.";

                var state = GameLibraryState.Current;
                state.Games.Clear();
                state.ExcludedGames.Clear();
                state.LastSyncUtc = null;
                state.Save();

                return $"Sync cleared — managed apps removed from {HostAppName}. " +
                       $"Restart {HostAppName} to apply.";
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"GameLibraryService: clear error — {ex.Message}");
                return $"Clear failed: {ex.Message}";
            }
            finally
            {
                _syncLock.Release();
            }
        }
    }
}
