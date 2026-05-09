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
    /// them against the Game Library. Uses three strategies:
    ///   1. Exe-name match      — for all stores with a known ExePath (Steam, Epic, GOG, EA, Ubisoft, Manual).
    ///   2. Install-dir match   — for Battle.net games, whose launcher exe is short-lived;
    ///                            any process whose full path starts with a game's install directory
    ///                            is counted, excluding known Battle.net support processes.
    ///   3. Process-name match  — for Xbox Game Pass / UWP games, whose executables live in the
    ///                            protected WindowsApps folder and cannot be resolved via MainModule.
    ///                            Matches p.ProcessName against an explicit ProcessName field or,
    ///                            as a fallback, the game's display name.
    /// Detected games are accumulated in a deduplicated list (insertion order ≈ launch order).
    /// </summary>
    public sealed class SessionProcessMonitor : IDisposable
    {
        // exeName (lowercase, no extension) → display name
        private readonly Dictionary<string, string> _exeToGame;

        // installDir (trailing separator, OrdinalIgnoreCase) → display name  [Battle.net only]
        private readonly Dictionary<string, string> _installDirToGame;

        // processName (OrdinalIgnoreCase) → display name  [Xbox Game Pass / UWP only]
        private readonly Dictionary<string, string> _processNameToGame;

        // Battle.net support processes excluded from directory-based matching
        private static readonly HashSet<string> _bnetSupportExes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "BlizzardError", "Battle.net", "Battle.net Helper",
                "Agent", "SceneCefBrowser", "ClientSdkFirewallHelper", "ClientSdkMDNSHost"
            };

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

        private static Dictionary<string, string> BuildDirLookup(IReadOnlyList<GameLibraryEntry> games)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                if (string.IsNullOrEmpty(g.InstallDir)) continue;
                // Normalize: trailing separator ensures prefix match doesn't bleed into sibling dirs
                string key = g.InstallDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
                if (!dict.ContainsKey(key))
                    dict[key] = g.Name;
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
                        try { fullPath = p.MainModule?.FileName; }
                        catch
                        {
                            // Access denied (UWP/WindowsApps) — log for diagnostics so callers
                            // can discover the correct ProcessName to populate GameLibraryEntry.
                            DebugLog($"UWP process (no fullPath): ProcessName='{p.ProcessName}' PID={p.Id}");
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

                        // ── Strategy 2: install-dir match (Battle.net only) ───────────────────
                        if (_installDirToGame.Count > 0 && !string.IsNullOrEmpty(fullPath))
                        {
                            string exeBaseName = Path.GetFileNameWithoutExtension(fullPath);
                            if (!_bnetSupportExes.Contains(exeBaseName))
                            {
                                foreach (var kvp in _installDirToGame)
                                {
                                    if (fullPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        AddDetected(kvp.Value);
                                        break;
                                    }
                                }
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