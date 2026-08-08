using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace StreamTweak
{
    /// <summary>
    /// Polls running processes every 5 seconds during a streaming session and matches
    /// them against the Game Library. Uses three strategies, evaluated in order:
    ///
    ///   1. Exe-name match (Strategy 1) — fastest, used as a quick hit when ExePath is a
    ///      real exe file (Epic LaunchExecutable, GOG registry exe, Manual user pick, …).
    ///
    ///   2. Install-dir match (Strategy 2) — robust against renamed binaries, launcher→game
    ///      handoff, multi-process games, and games whose ExePath is a directory (Steam, Xbox).
    ///      Any process whose full path lies under a game's install directory is counted,
    ///      EXCEPT processes whose filename appears in the store-specific support-exe denylist
    ///      (Steam overlay, Epic launcher helpers, Battle.net client, …). This avoids false
    ///      positives when the user has a store client open without playing a game.
    ///
    ///   3. Process-name match (Strategy 3) — only avenue for Xbox Game Pass / UWP games
    ///      whose executables live in the protected WindowsApps folder. MainModule.FileName
    ///      always throws AccessDenied, so we fall back to matching p.ProcessName against the
    ///      ProcessName field populated by the Xbox scanner from MicrosoftGame.Config.
    ///
    /// Detected games are accumulated in a deduplicated list (insertion order ≈ launch order).
    /// </summary>
    public sealed class SessionProcessMonitor : IDisposable
    {
        // exeName (lowercase, no extension) → display name
        private readonly Dictionary<string, string> _exeToGame;

        // installDir (trailing separator, OrdinalIgnoreCase) → (display name, store) — every store
        private readonly Dictionary<string, (string Name, string Store)> _installDirToGame;

        // processName (OrdinalIgnoreCase) → display name — Xbox / UWP only
        private readonly Dictionary<string, string> _processNameToGame;

        // Process names already reported as having no readable exe path. The diagnostic value
        // of that line is discovering the ProcessName once; without this set the same handful
        // of protected processes was re-logged on every 5 s tick for the whole session.
        private readonly HashSet<string> _loggedNoExePath = new(StringComparer.OrdinalIgnoreCase);

        // Per-store denylist of support-exe filenames (no extension, case-insensitive).
        // A process whose filename matches here is NOT credited to the store's game via
        // Strategy 2 even if it sits under the game's install directory.
        // Keys are the GameLibraryEntry.Store values exactly as written by the scanners.
        private static readonly Dictionary<string, HashSet<string>> _supportExesByStore =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Steam"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "steam", "steamwebhelper", "gameoverlayui", "streaming_client",
                    "crashhandler64", "crashhandler", "steamservice", "steamerrorreporter",
                    "steamerrorreporter64",
                },
                ["Epic Games"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "EpicGamesLauncher", "EpicWebHelper", "UnrealCEFSubProcess",
                    "CrashReportClient", "EpicOnlineServicesHost",
                    "EpicOnlineServicesUserHelper", "EpicOnlineServicesUIHelper",
                },
                ["GOG"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "GalaxyClient", "GalaxyClient Helper", "GalaxyClientService",
                    "GalaxyCommunication", "GalaxyOverlay",
                    "GOG Galaxy Notifications Renderer",
                },
                ["Ubisoft Connect"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "upc", "UbisoftConnect", "UbisoftGameLauncher", "UplayWebCore",
                    "CrashReporter", "CrashReport", "UbisoftConnect.Renderer",
                    "UbisoftGameLauncher64",
                },
                ["EA App"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "EADesktop", "EALauncher", "EAConnect", "EAUpdater",
                    "OriginWebHelperService", "OriginThinSetupInternal", "EACrashReporter",
                    "EABackgroundService", "Origin",
                },
                ["Battle.net"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "BlizzardError", "Battle.net", "Battle.net Helper", "Agent",
                    "SceneCefBrowser", "ClientSdkFirewallHelper", "ClientSdkMDNSHost",
                },
            };

        /// <summary>
        /// Returns the support-exe denylist for the given store, or an empty set if none defined.
        /// <para>Shared with <see cref="LaunchWatcher"/>, which needs the same list read the other
        /// way round: here these names mean "not the game, don't credit it as played", there they
        /// mean "the store's own client, so a window of its is the launch asking for something".
        /// One curated list, two questions — better than a second copy that drifts.</para>
        /// </summary>
        internal static HashSet<string> SupportExesFor(string store) =>
            _supportExesByStore.TryGetValue(store, out var set)
                ? set
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ordered list of detected games (insertion order = detection/launch order)
        private readonly List<string> _detectedNames = new();
        // Fast dedup: same set of names, used only for Contains() check
        private readonly HashSet<string> _detectedNamesSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private Timer? _timer;
        private bool _disposed;

        public SessionProcessMonitor(IReadOnlyList<GameLibraryEntry> games)
        {
            _exeToGame = BuildExeLookup(games);
            _installDirToGame = BuildDirLookup(games);
            _processNameToGame = BuildProcessNameLookup(games);
        }

        public void Start()
        {
            if (_disposed) return;
            // First tick immediately, then every 5 seconds
            _timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        public void Stop() => Dispose();

        /// <summary>Returns detected game display names in detection order (approximates launch order).</summary>
        public List<string> GetDetectedGames()
        {
            lock (_lock)
                return new List<string>(_detectedNames);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        // ── Lookup builders ───────────────────────────────────────────────────────

        private static Dictionary<string, string> BuildExeLookup(IReadOnlyList<GameLibraryEntry> games)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                if (string.IsNullOrEmpty(g.ExePath)) continue;
                try
                {
                    string key = Path.GetFileNameWithoutExtension(g.ExePath).ToLowerInvariant();
                    if (!string.IsNullOrEmpty(key) && !dict.ContainsKey(key))
                        dict[key] = g.Name;
                }
                catch { /* malformed path — skip */ }
            }
            return dict;
        }

        /// <summary>
        /// Builds the install-dir lookup used by Strategy 2 (prefix match against process full path).
        /// Includes every store with a known InstallDir EXCEPT Xbox/UWP — those processes always
        /// have a null full path (Access denied on MainModule.FileName), so prefix matching can't
        /// see them. They are handled by Strategy 3 instead.
        /// The lookup value carries both the display name and the originating store so Tick() can
        /// apply the correct support-exe denylist.
        ///
        /// IMPORTANT: keys are canonicalized via Path.GetFullPath, which yields the Windows
        /// canonical form (uppercase drive letter, all backslashes). This matters because
        /// MainModule.FileName always returns canonical paths, and the Steam scanner historically
        /// produced InstallDir values with mixed separators (`c:/program files (x86)/steam\steamapps\common\<game>`)
        /// because Steam's HKCU\SOFTWARE\Valve\Steam\SteamPath uses forward slashes and
        /// Path.Combine doesn't normalize the prefix. Without this normalization, the OrdinalIgnoreCase
        /// StartsWith check in Tick() would silently fail for every Steam game.
        /// </summary>
        private static Dictionary<string, (string Name, string Store)> BuildDirLookup(
            IReadOnlyList<GameLibraryEntry> games)
        {
            var dict = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                if (string.IsNullOrEmpty(g.InstallDir)) continue;
                // Xbox uses Strategy 3 (UWP processes have no readable full path → prefix match
                // can't fire). Including Xbox here would be harmless but is removed for clarity.
                if (string.Equals(g.Store, "Xbox", StringComparison.OrdinalIgnoreCase)) continue;

                // Canonicalize separators + drive letter so the StartsWith match in Tick() works
                // regardless of how the scanner originally wrote the path. Falls back to the raw
                // string if GetFullPath throws (malformed/absurdly-long paths).
                string normalized;
                try { normalized = Path.GetFullPath(g.InstallDir); }
                catch { normalized = g.InstallDir; }

                // Normalize: trailing separator ensures prefix match doesn't bleed into sibling dirs
                // (e.g. "...\Cyberpunk" must not match a process under "...\Cyberpunk 2077 DLC").
                string key = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
                if (!dict.ContainsKey(key))
                    dict[key] = (g.Name, g.Store ?? "");
            }
            return dict;
        }

        /// <summary>
        /// Builds the process-name lookup for Xbox Game Pass / UWP games.
        /// These games live in the protected WindowsApps folder — MainModule.FileName always
        /// throws AccessDeniedException, so exe-path matching is impossible. Instead we match
        /// p.ProcessName against:
        ///   a) GameLibraryEntry.ProcessName  — explicit override set by the Xbox scanner or the user.
        ///   b) GameLibraryEntry.Name         — fallback: Xbox often uses the game title as process name.
        /// Only entries whose Store is "Xbox" OR whose ExePath is null are included, to avoid
        /// false positives with stores that already have reliable exe-path matching.
        /// </summary>
        private static Dictionary<string, string> BuildProcessNameLookup(IReadOnlyList<GameLibraryEntry> games)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                bool isXboxOrUnknown = string.Equals(g.Store, "Xbox", StringComparison.OrdinalIgnoreCase)
                                    || string.IsNullOrEmpty(g.ExePath);
                if (!isXboxOrUnknown) continue;

                // Prefer explicit ProcessName override if available
                if (!string.IsNullOrEmpty(g.ProcessName))
                {
                    if (!dict.ContainsKey(g.ProcessName))
                        dict[g.ProcessName] = g.Name;
                    continue;
                }

                // Fallback: game display name (Xbox Game Pass frequently uses title as process name)
                if (!string.IsNullOrEmpty(g.Name) && !dict.ContainsKey(g.Name))
                    dict[g.Name] = g.Name;
            }
            return dict;
        }

        // ── Polling tick ──────────────────────────────────────────────────────────

        /// <summary>
        /// True for processes hosted in session 0 (services). Cheap: ProcessIdToSessionId
        /// needs only PROCESS_QUERY_LIMITED_INFORMATION, unlike MainModule which enumerates
        /// the module list and throws on anything protected. Returns false when the session
        /// can't be read, so an unknown process is still probed normally.
        /// <para>Shared with <see cref="LaunchWatcher"/>, which scans processes for a different
        /// question but pays the same price for getting this wrong — see the note below about
        /// the ~70 thrown exceptions per tick this avoids.</para>
        /// </summary>
        internal static bool IsServiceSessionProcess(Process p)
        {
            try { return p.SessionId == 0; }
            catch { return false; }
        }

        private void Tick(object? _)
        {
            if (_disposed || (_exeToGame.Count == 0 && _installDirToGame.Count == 0 && _processNameToGame.Count == 0))
                return;

            try
            {
                Process[] procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    try
                    {
                        // Attempt to read the full exe path.
                        // This will throw for UWP / WindowsApps processes — caught silently below.
                        string? fullPath = null;

                        // Session 0 is the service session: a game never runs there, and every
                        // one of those processes (≈45 svchost instances alone) fails MainModule.
                        // Skipping the probe avoids ~70 thrown Win32Exceptions per tick during
                        // gameplay. Downstream behaviour is identical — MainModule would have
                        // thrown anyway, leaving fullPath null and falling through to the
                        // ProcessName-based strategies below.
                        if (!IsServiceSessionProcess(p))
                        {
                            try { fullPath = p.MainModule?.FileName; }
                            catch
                            {
                                // Access denied (UWP/WindowsApps) — log for diagnostics so callers
                                // can discover the correct ProcessName to populate GameLibraryEntry.
                                // Once per process name: repeating it every tick buried the log.
                                bool firstSighting;
                                lock (_lock) { firstSighting = _loggedNoExePath.Add(p.ProcessName); }
                                if (firstSighting)
                                    DebugLog($"No exe path for ProcessName='{p.ProcessName}' (protected process)");
                            }
                        }

                        string exeName = string.IsNullOrEmpty(fullPath)
                            ? p.ProcessName.ToLowerInvariant()
                            : Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();

                        // ── Strategy 1: exe-name match (all stores with known ExePath) ─────────
                        if (_exeToGame.TryGetValue(exeName, out string? gameName))
                        {
                            AddDetected(gameName);
                            continue;
                        }

                        // ── Strategy 2: install-dir match (all stores with a known InstallDir) ─
                        // Robust against renamed binaries, launcher→game handoffs, and stores
                        // whose ExePath is a directory (Steam). The store-specific support-exe
                        // denylist prevents Steam overlay, EA Desktop, Battle.net client, etc.
                        // from being credited as the game they happen to sit next to.
                        if (_installDirToGame.Count > 0 && !string.IsNullOrEmpty(fullPath))
                        {
                            string exeBaseName = Path.GetFileNameWithoutExtension(fullPath);
                            foreach (var kvp in _installDirToGame)
                            {
                                if (!fullPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                if (SupportExesFor(kvp.Value.Store).Contains(exeBaseName))
                                    continue;
                                AddDetected(kvp.Value.Name);
                                break;
                            }
                        }

                        // ── Strategy 3: process-name match (Xbox Game Pass / UWP) ─────────────
                        // Runs regardless of whether fullPath was resolved, because UWP processes
                        // always fail MainModule — p.ProcessName is the only reliable identifier.
                        if (_processNameToGame.Count > 0 &&
                            _processNameToGame.TryGetValue(p.ProcessName, out string? xboxGameName))
                        {
                            AddDetected(xboxGameName);
                        }
                    }
                    catch { /* process exited between GetProcesses() and here — non-fatal */ }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { /* GetProcesses() failure — non-fatal */ }
        }

        /// <summary>
        /// Adds a game detected out-of-band (e.g. resolved from the server log's launch line),
        /// merged into the same deduplicated list as the process-scan hits.
        /// </summary>
        public void AddDetectedByName(string gameName)
        {
            if (!string.IsNullOrWhiteSpace(gameName)) AddDetected(gameName);
        }

        /// <summary>Thread-safe insert into the detected-games list. No-op if already present.</summary>
        private void AddDetected(string gameName)
        {
            lock (_lock)
            {
                if (_detectedNamesSet.Add(gameName))
                    _detectedNames.Add(gameName);
            }
        }

        private static void DebugLog(string message) => DebugLogger.Log(message);
    }
}