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
    /// them against the Game Library. Uses two strategies:
    ///   1. Exe-name match  — for all stores (Steam, Epic, GOG, Xbox, EA, Ubisoft, Manual).
    ///   2. Install-dir match — for Battle.net games, whose launcher exe is short-lived;
    ///      any process whose full path starts with a game's install directory is counted,
    ///      excluding known Battle.net support processes.
    /// Detected games are accumulated in a deduplicated list (insertion order ≈ launch order).
    /// </summary>
    public sealed class SessionProcessMonitor : IDisposable
    {
        // exeName (lowercase, no extension) → display name
        private readonly Dictionary<string, string> _exeToGame;

        // installDir (trailing separator, OrdinalIgnoreCase) → display name  [Battle.net only]
        private readonly Dictionary<string, string> _installDirToGame;

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
            _exeToGame       = BuildExeLookup(games);
            _installDirToGame = BuildDirLookup(games);
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

        // ── Private ──────────────────────────────────────────────────────────────

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

        private void Tick(object? _)
        {
            if (_disposed || (_exeToGame.Count == 0 && _installDirToGame.Count == 0)) return;
            try
            {
                Process[] procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    try
                    {
                        // Try to read the full exe path (more reliable, avoids name collisions)
                        string? fullPath = null;
                        try { fullPath = p.MainModule?.FileName; }
                        catch { /* access denied on system/elevated processes — handled below */ }

                        string exeName = string.IsNullOrEmpty(fullPath)
                            ? p.ProcessName.ToLowerInvariant()
                            : Path.GetFileNameWithoutExtension(fullPath).ToLowerInvariant();

                        // ── Strategy 1: exe-name match (all stores) ──────────────────
                        if (_exeToGame.TryGetValue(exeName, out string? gameName))
                        {
                            lock (_lock)
                            {
                                if (_detectedNamesSet.Add(gameName))
                                    _detectedNames.Add(gameName);
                            }
                            continue; // already matched — skip directory check
                        }

                        // ── Strategy 2: install-dir match (Battle.net only) ──────────
                        if (_installDirToGame.Count > 0 && !string.IsNullOrEmpty(fullPath))
                        {
                            string exeBaseName = Path.GetFileNameWithoutExtension(fullPath);
                            if (!_bnetSupportExes.Contains(exeBaseName))
                            {
                                foreach (var kvp in _installDirToGame)
                                {
                                    if (fullPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        lock (_lock)
                                        {
                                            if (_detectedNamesSet.Add(kvp.Value))
                                                _detectedNames.Add(kvp.Value);
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* process may have exited between GetProcesses() and here */ }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { /* GetProcesses() failure — non-fatal */ }
        }
    }
}
