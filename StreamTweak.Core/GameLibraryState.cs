using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamTweak
{
    public class GameLibraryEntry
    {
        public string Name { get; set; } = "";
        public string Store { get; set; } = "";
        public string? SteamAppId { get; set; }

        /// <summary>Store-specific identifier used for cover art lookup and stable deduplication.</summary>
        public string? StoreId { get; set; }

        /// <summary>
        /// If false, this game is excluded from the Sunshine sync (but remains visible in the list).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Set to true when the user manually edits the game name, preventing auto-correction
        /// from the Steam Store API on subsequent syncs.
        /// </summary>
        public bool UserRenamed { get; set; } = false;

        /// <summary>
        /// True for games added manually by the user (not discovered by a store scanner).
        /// Manual games are preserved across automatic syncs and cleared by Clear Sync.
        /// </summary>
        public bool IsManual { get; set; } = false;

        /// <summary>
        /// Full path to the game executable. Only persisted for manual games;
        /// auto-discovered games re-resolve their ExePath on each scan.
        /// </summary>
        public string? ExePath { get; set; }

        /// <summary>
        /// Explicit process name for Xbox Game Pass / UWP games (e.g. "Forza Motorsport").
        /// Populated by the Xbox scanner or set manually by the user.
        /// Takes priority over the display Name in process-name matching.
        /// </summary>
        public string? ProcessName { get; set; }

        /// <summary>
        /// Root installation directory. Populated only for Battle.net games and used by
        /// SessionProcessMonitor for directory-based process matching (since all Battle.net
        /// games share the same client launcher and cannot be identified by exe name alone).
        /// </summary>
        public string? InstallDir { get; set; }

        /// <summary>
        /// Store-specific launch id, when the store's own launcher has to do the launching.
        /// Epic uses "&lt;namespace&gt;:&lt;catalogItemId&gt;:&lt;appName&gt;" — its games cannot be started
        /// by running their exe, whatever else is running, because the entitlement and Epic
        /// Online Services tokens arrive on the command line from the launcher.
        /// </summary>
        public string? LaunchId { get; set; }

        /// <summary>
        /// 10.4.0: the game's own program when the store declares ExePath a launcher in front of it
        /// (GOG playTasks). Refreshed at every scan, never set by hand; null when there is none.
        /// </summary>
        public string? GameExePath { get; set; }
        public string? GameWorkingDir { get; set; }
        public string? GameArguments { get; set; }

        /// <summary>
        /// 10.4.0, the Library's menu on the command: null = automatic (the game's own program when
        /// <see cref="GameExePath"/> is known, else the store's way), <see cref="ChoiceStore"/> = the
        /// store's way even when a game program is known (the launcher), <see cref="ChoiceCustom"/> =
        /// <see cref="CustomCommand"/>. Kept across syncs; the sync writes what it says.
        /// </summary>
        public string? LaunchChoice { get; set; }
        public string? CustomCommand { get; set; }
        public string? CustomWorkingDir { get; set; }

        /// <summary>
        /// 10.4.2: the picture the user chose for a game added by hand — a file name in the covers
        /// folder (custom_&lt;hash&gt;.png), or null. ⚠️ A NEW name for every new picture, never the old
        /// file overwritten: the streaming server derives the game's id from its picture and
        /// StreamLight keeps covers by that id, so a picture changed in place could stay the old one
        /// on the devices.
        /// </summary>
        public string? CustomCover { get; set; }

        public const string ChoiceStore  = "store";
        public const string ChoiceCustom = "custom";

        /// <summary>What the sync writes for this game: the scan's facts plus the user's choice.</summary>
        public DiscoveredGame ToLaunchGame() => new(
            Name, Store, SteamAppId, ExePath ?? "", StoreId,
            InstallDir: InstallDir, ProcessName: ProcessName, LaunchId: LaunchId,
            GameExePath: GameExePath, GameWorkingDir: GameWorkingDir, GameArguments: GameArguments,
            LaunchChoice: LaunchChoice, CustomCommand: CustomCommand, CustomWorkingDir: CustomWorkingDir,
            CoverPath: CustomCoverPath);

        // ── UI helpers (not persisted) ────────────────────────────────────────

        private static readonly string CoverCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "covers");

        /// <summary>10.4.2: the full path of <see cref="CustomCover"/> when the file is there.</summary>
        [JsonIgnore]
        public string? CustomCoverPath
        {
            get
            {
                if (string.IsNullOrEmpty(CustomCover)) return null;
                string path = Path.Combine(CoverCacheDir, CustomCover);
                return File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// Absolute path to the cached cover PNG for this game, or null if not yet downloaded —
        /// since 10.4.2 the user's own picture first, for a game added by hand.
        /// Uses the same naming convention as CoverArtFetcher / StoreCoverFetcher.
        /// </summary>
        [JsonIgnore]
        public string? CoverImagePath
        {
            get
            {
                if (CustomCoverPath is string custom) return custom;
                string? fileName = GetCoverFileName();
                if (fileName == null) return null;
                string path = Path.Combine(CoverCacheDir, fileName);
                return File.Exists(path) ? path : null;
            }
        }

        private string? GetCoverFileName()
        {
            if (SteamAppId != null)
                return $"steam_{SteamAppId}.png";

            string store = Store.Replace(" ", "").ToLowerInvariant();

            if (StoreId != null)
            {
                string safeId = new string(StoreId
                    .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    .ToArray());
                if (!string.IsNullOrEmpty(safeId))
                    return $"{store}_{safeId}.png";
            }

            string safe = new string(Name
                .Where(c => char.IsLetterOrDigit(c) || c == '-')
                .ToArray());
            return string.IsNullOrEmpty(safe) ? null : $"{store}_{safe}.png";
        }
    }

    /// <summary>
    /// Persists game library sync state to %LOCALAPPDATA%\StreamTweak\gamelibrarystate.json.
    /// Provides the APPSTORES JSON payload served by StreamTweakBridge.
    /// </summary>
    public class GameLibraryState
    {
        public bool SyncEnabled { get; set; } = false;
        public string? SunshinePath { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        public List<GameLibraryEntry> Games { get; set; } = new();

        /// <summary>
        /// Stable keys of games the user has explicitly removed via the Remove button.
        /// Auto-sync skips any discovered game whose key is in this set.
        /// Cleared when the user clicks "Sync Now" manually.
        /// </summary>
        public List<string> ExcludedGames { get; set; } = new();

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Returns the stable exclusion key for a game entry.</summary>
        public static string GetExclusionKey(GameLibraryEntry e) =>
            e.SteamAppId != null ? $"steam:{e.SteamAppId}" :
            e.StoreId    != null ? $"{e.Store}:{e.StoreId}" :
                                   $"{e.Store}:{e.Name}";


        // ── Static singleton ─────────────────────────────────────────────────

        private static GameLibraryState? _current;
        private static readonly object _currentLock = new();

        /// <summary>Singleton loaded from disk on first access. Thread-safe.</summary>
        public static GameLibraryState Current
        {
            get
            {
                lock (_currentLock)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        /// <summary>Forces a reload from disk on next access of Current.</summary>
        public static void Invalidate()
        {
            lock (_currentLock) { _current = null; }
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "gamelibrarystate.json");

        public static GameLibraryState Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new GameLibraryState();
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<GameLibraryState>(json) ?? new GameLibraryState();
            }
            catch
            {
                return new GameLibraryState();
            }
        }

        public void Save()
        {
            string tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                lock (_currentLock) { _current = this; } // keep singleton in sync
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // ── APPSTORES payload ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the JSON object {"Game Name": "Store", ...} for the APPSTORES command.
        /// </summary>
        public string ToAppStoresJson()
        {
            var enabled = Games.Where(g => g.Enabled).ToList();
            if (enabled.Count == 0) return "{}";
            var dict = enabled.ToDictionary(g => g.Name, g => g.Store);
            return JsonSerializer.Serialize(dict);
        }
    }
}
