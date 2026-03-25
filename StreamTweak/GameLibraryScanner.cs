using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace StreamTweak
{
    /// <summary>
    /// A game discovered in a local store library.
    /// SteamAppId is set only for Steam games (used for cover art and deduplication).
    /// StoreId is the store-specific identifier used for cover art lookup:
    ///   Epic  → CatalogItemId  |  GOG → ProductId  |  Ubisoft → InstallId
    ///   Xbox  → FamilyName!AppId  |  Battle.net → product_id  |  EA → ContentId
    /// ExePath is the install dir (Steam/Epic/Ubisoft/Xbox) or exe (GOG/EA).
    /// </summary>
    public record DiscoveredGame(
        string Name,
        string Store,
        string? SteamAppId,
        string ExePath,
        string? StoreId = null,
        string? CoverUrl = null);

    /// <summary>
    /// Scans installed game libraries on the local machine.
    /// Supported stores: Steam, Epic Games, GOG, Ubisoft Connect, Xbox/Game Pass,
    ///                   Battle.net (opens client on launch), EA App.
    /// </summary>
    public static class GameLibraryScanner
    {
        // Steam entries that are tools/runtimes, not actual games.
        private static readonly HashSet<string> _steamExclusions = new(StringComparer.OrdinalIgnoreCase)
        {
            "Steamworks Common Redistributables",
            "SteamVR",
            "Steam Linux Runtime",
            "Proton Experimental",
            "Steam Client",
        };


        public static List<DiscoveredGame> ScanAll()
        {
            var games = new List<DiscoveredGame>();
            try { games.AddRange(ScanSteam()); }     catch { }
            try { games.AddRange(ScanEpic()); }      catch { }
            try { games.AddRange(ScanGog()); }       catch { }
            try { games.AddRange(ScanUbisoft()); }   catch { }
            try { games.AddRange(ScanXbox()); }        catch { }
            try { games.AddRange(ScanBattleNet()); }  catch { }
            try { games.AddRange(ScanEaApp()); }      catch { }

            // Post-scan: resolve authoritative display names from Windows "Installed Apps"
            // (the Uninstall registry). This fixes internal codenames (e.g. "ACMirage_plus"
            // → "Assassin's Creed Mirage") for any store where the per-store scanner
            // couldn't determine the proper name.
            try
            {
                var windowsNames = BuildWindowsDisplayNameLookup();
                if (windowsNames.Count > 0)
                {
                    for (int i = 0; i < games.Count; i++)
                    {
                        var g = games[i];
                        // Steam: names already resolved by IStoreBrowseService API.
                        // Battle.net: names already correct from aggregate.json; ExePath points
                        //             to the client exe, not the game dir, so path matching fails.
                        if (g.Store == "Steam" || g.Store == "Battle.net") continue;

                        // Match by install path: the game's ExePath should be inside the
                        // InstallLocation from the registry entry.
                        string gamePath = NormalizePath(g.ExePath);
                        foreach (var (installLoc, displayName) in windowsNames)
                        {
                            if (gamePath.StartsWith(installLoc, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.Equals(g.Name, displayName, StringComparison.OrdinalIgnoreCase))
                                    games[i] = g with { Name = displayName };
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            return games;
        }

        /// <summary>
        /// Builds a lookup of normalized InstallLocation → DisplayName from the Windows
        /// Uninstall registry (both 64-bit and WOW6432Node hives). This provides the
        /// authoritative display name for any installed application, as shown in Windows
        /// Settings → Apps → Installed Apps.
        /// </summary>
        private static List<(string installLoc, string displayName)> BuildWindowsDisplayNameLookup()
        {
            var result = new List<(string, string)>();
            string[] hives =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (string hivePath in hives)
            {
                try
                {
                    using var hiveKey = Registry.LocalMachine.OpenSubKey(hivePath);
                    if (hiveKey == null) continue;

                    foreach (string subKeyName in hiveKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var appKey = hiveKey.OpenSubKey(subKeyName);
                            if (appKey == null) continue;

                            string? displayName  = appKey.GetValue("DisplayName")     as string;
                            string? installLocation = appKey.GetValue("InstallLocation") as string;
                            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
                                continue;

                            // Skip system components, runtimes, VS tools, etc.
                            int? systemComponent = appKey.GetValue("SystemComponent") as int?;
                            if (systemComponent == 1) continue;

                            string normalized = NormalizePath(installLocation);
                            if (normalized.Length > 3) // skip bare drive roots
                                result.Add((normalized, displayName));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Sort by longest path first so more specific matches win
            result.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
            return result;
        }

        private static string NormalizePath(string path) =>
            path.Replace('/', '\\').TrimEnd('\\') + '\\';

        // ── Steam ─────────────────────────────────────────────────────────────

        private static IEnumerable<DiscoveredGame> ScanSteam()
        {
            string? steamPath = Registry.GetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
            if (steamPath == null) yield break;

            var libraryPaths = new List<string> { Path.Combine(steamPath, "steamapps") };

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                string content = File.ReadAllText(vdfPath);
                foreach (Match m in Regex.Matches(content, @"""path""\s+""([^""]+)"""))
                {
                    string libPath = m.Groups[1].Value.Replace(@"\\", @"\");
                    string steamappsPath = Path.Combine(libPath, "steamapps");
                    if (Directory.Exists(steamappsPath) &&
                        !libraryPaths.Any(p => p.Equals(steamappsPath, StringComparison.OrdinalIgnoreCase)))
                        libraryPaths.Add(steamappsPath);
                }
            }

            foreach (string libPath in libraryPaths)
            {
                if (!Directory.Exists(libPath)) continue;
                foreach (string acf in Directory.GetFiles(libPath, "appmanifest_*.acf"))
                {
                    string content;
                    try { content = File.ReadAllText(acf); } catch { continue; }

                    string? name      = ExtractVdfValue(content, "name");
                    string? appId     = ExtractVdfValue(content, "appid");
                    string? installDir = ExtractVdfValue(content, "installdir");

                    if (string.IsNullOrWhiteSpace(name) || appId == null || installDir == null)
                        continue;

                    string? stateFlagsStr = ExtractVdfValue(content, "StateFlags");
                    if (int.TryParse(stateFlagsStr, out int stateFlags) && (stateFlags & 4) == 0)
                        continue;

                    if (_steamExclusions.Any(e => name.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string exeDir = Path.Combine(libPath, "common", installDir);
                    yield return new DiscoveredGame(name, "Steam", appId, exeDir);
                }
            }
        }

        private static string? ExtractVdfValue(string content, string key)
        {
            var m = Regex.Match(content, $@"""{Regex.Escape(key)}""\s+""([^""]+)""",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ── Epic Games ────────────────────────────────────────────────────────

        private static IEnumerable<DiscoveredGame> ScanEpic()
        {
            string manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestDir)) yield break;

            foreach (string item in Directory.GetFiles(manifestDir, "*.item"))
            {
                DiscoveredGame? game = null;
                try
                {
                    string json = File.ReadAllText(item);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("bIsApplication", out var isApp) && !isApp.GetBoolean())
                        continue;

                    if (!root.TryGetProperty("DisplayName", out var nameEl)) continue;
                    if (!root.TryGetProperty("LaunchExecutable", out var exeEl)) continue;
                    if (!root.TryGetProperty("InstallLocation", out var locEl)) continue;

                    string name = nameEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // CatalogItemId is used as StoreId for cover art lookup in catcache.bin
                    string? catalogItemId = root.TryGetProperty("CatalogItemId", out var cidEl)
                        ? cidEl.GetString() : null;

                    string exePath = Path.Combine(locEl.GetString() ?? "", exeEl.GetString() ?? "");
                    game = new DiscoveredGame(name, "Epic Games", null, exePath, catalogItemId);
                }
                catch { }

                if (game != null) yield return game;
            }
        }

        // ── GOG ───────────────────────────────────────────────────────────────

        private static IEnumerable<DiscoveredGame> ScanGog()
        {
            using var gamesKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (gamesKey == null) yield break;

            foreach (string gameId in gamesKey.GetSubKeyNames())
            {
                DiscoveredGame? game = null;
                try
                {
                    using var gameKey = gamesKey.OpenSubKey(gameId);
                    if (gameKey == null) continue;

                    string? name = gameKey.GetValue("gameName") as string;
                    string? exe  = gameKey.GetValue("exe")      as string;

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                        game = new DiscoveredGame(name, "GOG", null, exe, gameId); // gameId = ProductId
                }
                catch { }

                if (game != null) yield return game;
            }
        }

        // ── Ubisoft Connect ───────────────────────────────────────────────────

        private static IEnumerable<DiscoveredGame> ScanUbisoft()
        {
            // Load proper game names from Ubisoft config file (best-effort)
            var ubisoftInfo = LoadUbisoftGameInfo();

            using var installsKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
            if (installsKey == null) yield break;

            foreach (string installId in installsKey.GetSubKeyNames())
            {
                DiscoveredGame? game = null;
                try
                {
                    using var installKey = installsKey.OpenSubKey(installId);
                    string? installDir = installKey?.GetValue("InstallDir") as string;
                    if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) continue;

                    var exes = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(e => !IsUbisoftSystemExe(Path.GetFileName(e)))
                        .OrderByDescending(e => new FileInfo(e).Length)
                        .ToArray();
                    if (exes.Length == 0) continue;

                    // Prefer name from config file; fall back to FileVersionInfo.ProductName; last resort: exe filename
                    string name;
                    if (ubisoftInfo.TryGetValue(installId, out string? cfgName) && !string.IsNullOrWhiteSpace(cfgName))
                    {
                        name = cfgName;
                    }
                    else
                    {
                        try
                        {
                            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exes[0]);
                            name = !string.IsNullOrWhiteSpace(versionInfo.ProductName)
                                ? versionInfo.ProductName
                                : Path.GetFileNameWithoutExtension(exes[0]);
                        }
                        catch
                        {
                            name = Path.GetFileNameWithoutExtension(exes[0]);
                        }
                    }

                    game = new DiscoveredGame(name, "Ubisoft Connect", null, exes[0], installId);
                }
                catch { }

                if (game != null) yield return game;
            }
        }

        /// <summary>
        /// Parses the Ubisoft configuration cache to extract game names and thumb images.
        /// Returns dictionary: installId → game display name.
        /// </summary>
        internal static Dictionary<string, string> LoadUbisoftGameInfo()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ubisoft Game Launcher", "cache", "configuration", "configurations");
                if (!File.Exists(configPath)) return result;

                string content = File.ReadAllText(configPath);

                // Split into per-game sections at each "configuration_id:" occurrence
                var sections = Regex.Split(content, @"(?=\bconfiguration_id\b)");
                foreach (var section in sections)
                {
                    var idMatch = Regex.Match(section, @"configuration_id:\s*(\d+)");
                    if (!idMatch.Success) continue;
                    string id = idMatch.Groups[1].Value;

                    // Extract display name from localizations block
                    var nameMatch = Regex.Match(section,
                        @"localizations:.*?default:.*?(?:^|\s)name:\s*(.+?)(?:\r?\n)",
                        RegexOptions.Singleline | RegexOptions.Multiline);
                    if (nameMatch.Success)
                        result[id] = nameMatch.Groups[1].Value.Trim();
                }
            }
            catch { }
            return result;
        }

        private static bool IsUbisoftSystemExe(string fileName)
        {
            string fl = fileName.ToLowerInvariant();
            return fl.StartsWith("ubisoft") || fl.StartsWith("uplay") ||
                   fl.StartsWith("crashreport") || fl.StartsWith("uninst") ||
                   fl == "upc.exe" || fl == "easyanticheat.exe" || fl == "vcredist_x64.exe";
        }

        // ── Xbox / Microsoft Store / Game Pass ────────────────────────────────

        private static IEnumerable<DiscoveredGame> ScanXbox()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                DiscoveredGame[]? found = null;
                try
                {
                    string gamingRoot = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                    if (!File.Exists(gamingRoot)) continue;

                    // .GamingRoot format: RGBX magic (4 bytes) + 1 unknown byte + UTF-16 LE path
                    // The path is relative to the drive root (e.g. "XboxGames\").
                    byte[] data = File.ReadAllBytes(gamingRoot);
                    if (data.Length < 6) continue;
                    if (data[0] != 'R' || data[1] != 'G' || data[2] != 'B' || data[3] != 'X') continue;

                    // Decode the UTF-16 LE path starting at byte 5, stripping the null terminator.
                    int pathBytes = data.Length - 5;
                    // Round down to an even number so GetString doesn't choke on a stray byte.
                    if (pathBytes % 2 != 0) pathBytes--;
                    string relativePath = System.Text.Encoding.Unicode
                        .GetString(data, 5, pathBytes)
                        .TrimEnd('\0');
                    string gamesDir = Path.Combine(drive.RootDirectory.FullName, relativePath).TrimEnd('\\');
                    if (!Directory.Exists(gamesDir)) continue;

                    found = Directory.GetDirectories(gamesDir)
                        .Select(TryReadXboxGame)
                        .Where(g => g != null)
                        .Select(g => g!)
                        .ToArray();
                }
                catch { }

                if (found != null)
                    foreach (var g in found) yield return g;
            }
        }

        // Lazily-built lookup: PackageFamilyName → DisplayName from PackageManager
        private static Dictionary<string, string>? _xboxPackageNames;

        private static Dictionary<string, string> GetXboxPackageNames()
        {
            if (_xboxPackageNames != null) return _xboxPackageNames;
            _xboxPackageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                foreach (var pkg in pm.FindPackagesForUser(""))
                {
                    try
                    {
                        string? name = pkg.DisplayName;
                        string? familyName = pkg.Id?.FamilyName;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(familyName))
                            _xboxPackageNames.TryAdd(familyName, name);
                    }
                    catch { }
                }
            }
            catch { }
            return _xboxPackageNames;
        }

        private static DiscoveredGame? TryReadXboxGame(string gameDir)
        {
            try
            {
                // MicrosoftGame.config is in the Content subfolder or the root
                string configPath = Path.Combine(gameDir, "Content", "MicrosoftGame.config");
                if (!File.Exists(configPath))
                    configPath = Path.Combine(gameDir, "MicrosoftGame.config");
                if (!File.Exists(configPath)) return null;

                string configDir = Path.GetDirectoryName(configPath)!;
                XDocument doc = XDocument.Load(configPath);
                XNamespace ns = "http://schemas.microsoft.com/Gaming/2020/08/Game";

                var identity = doc.Descendants(ns + "Identity").FirstOrDefault()
                             ?? doc.Descendants("Identity").FirstOrDefault();

                string? packageName = identity?.Attribute("Name")?.Value;
                if (string.IsNullOrWhiteSpace(packageName)) return null;

                // Read ApplicationId from appxmanifest.xml for the full App User Model ID
                string? appId = null;
                foreach (string manifestName in new[] { "appxmanifest.xml", "AppxManifest.xml" })
                {
                    string manifestPath = Path.Combine(configDir, manifestName);
                    if (!File.Exists(manifestPath)) continue;
                    try
                    {
                        var manifest = XDocument.Load(manifestPath);
                        appId = manifest.Descendants()
                            .Where(e => e.Name.LocalName == "Application")
                            .Select(e => e.Attribute("Id")?.Value)
                            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    }
                    catch { }
                    break;
                }

                // Resolve display name via PackageManager (handles ms-resource: references)
                // Build PackageFamilyName from packageName: need publisher hash suffix.
                // We look up by matching package names that START with the Identity.Name.
                string? displayName = null;
                var pkgNames = GetXboxPackageNames();
                foreach (var kvp in pkgNames)
                {
                    // PackageFamilyName format: "PackageName_publisherHash"
                    if (kvp.Key.StartsWith(packageName, StringComparison.OrdinalIgnoreCase) &&
                        (kvp.Key.Length == packageName.Length || kvp.Key[packageName.Length] == '_'))
                    {
                        displayName = kvp.Value;

                        // Also use the full FamilyName for StoreId (needed for shell:appsFolder launch)
                        string storeId = appId != null ? $"{kvp.Key}!{appId}" : kvp.Key;
                        return new DiscoveredGame(displayName, "Xbox", null, configDir, storeId);
                    }
                }

                // Fallback: ShellVisuals or directory name
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    var shellVisuals = doc.Descendants(ns + "ShellVisuals").FirstOrDefault()
                                    ?? doc.Descendants("ShellVisuals").FirstOrDefault();
                    displayName = shellVisuals?.Attribute("DefaultDisplayName")?.Value;

                    // Skip ms-resource: references — they can't be resolved without PackageManager
                    if (displayName != null && displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        displayName = null;

                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = Path.GetFileName(gameDir);
                }

                // StoreId = "PackageName!AppId" → used to build shell:appsFolder launch URL
                string fallbackStoreId = appId != null ? $"{packageName}!{appId}" : packageName;

                return new DiscoveredGame(displayName!, "Xbox", null, configDir, fallbackStoreId);
            }
            catch { return null; }
        }

        // ── Battle.net ────────────────────────────────────────────────────────
        // Reads %ProgramData%\Battle.net\Agent\aggregate.json which the Battle.net Agent
        // keeps up to date with every installed product. ExePath is always the Battle.net
        // client executable — launching from StreamLight opens Battle.net so the user can
        // click Play. This avoids the hardcoded LauncherId dictionary that DLSS Swap requires.

        private static readonly HashSet<string> _bnetSkip =
            new(StringComparer.OrdinalIgnoreCase) { "bna", "agent", "bnetlauncher", "bnet" };

        private static string? FindBattleNetExe()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net");
                string? loc = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(loc))
                {
                    string exe = Path.Combine(loc, "Battle.net.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Battle.net", "Battle.net.exe");
            return File.Exists(fallback) ? fallback : null;
        }

        private static IEnumerable<DiscoveredGame> ScanBattleNet()
        {
            string? clientExe = FindBattleNetExe();
            if (clientExe == null) yield break;

            string aggPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Battle.net", "Agent", "aggregate.json");
            if (!File.Exists(aggPath)) yield break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(aggPath)); }
            catch { yield break; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("installed", out var installedEl) ||
                    installedEl.ValueKind != JsonValueKind.Array)
                    yield break;

                foreach (var item in installedEl.EnumerateArray())
                {
                    string? uid = item.TryGetProperty("product_id", out var pidEl)
                        ? pidEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(uid) || _bnetSkip.Contains(uid)) continue;

                    string? name = item.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) name = uid;

                    yield return new DiscoveredGame(name!, "Battle.net", null, clientExe, uid);
                }
            }
        }

        // ── EA App ────────────────────────────────────────────────────────────

        private static IEnumerable<DiscoveredGame> ScanEaApp()
        {
            // EA App registers games in HKLM and/or HKCU, in both 64-bit and 32-bit views
            // (same approach as DLSS Swap: scan all 4 combinations)
            var registryRoots = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser,  RegistryView.Registry64),
                (RegistryHive.CurrentUser,  RegistryView.Registry32),
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (hive, view) in registryRoots)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey == null) continue;

                foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                {
                    DiscoveredGame? game = null;
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        string? uninstallStr = appKey.GetValue("UninstallString") as string;
                        if (uninstallStr == null) continue;
                        // EA App games have "EAInstaller" and "Cleanup.exe" in the uninstall command
                        if (!uninstallStr.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) ||
                            !uninstallStr.Contains("Cleanup.exe", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? displayName = appKey.GetValue("DisplayName") as string;
                        string? installPath = appKey.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installPath)) continue;
                        if (!Directory.Exists(installPath)) continue;

                        // Deduplicate across hives
                        if (!seen.Add(installPath)) continue;

                        string? contentId = ReadEaContentId(installPath);

                        var exes = Directory.GetFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
                            .Where(e => !IsEaSystemExe(Path.GetFileName(e)))
                            .OrderByDescending(e => new FileInfo(e).Length)
                            .ToArray();
                        string exePath = exes.Length > 0 ? exes[0] : installPath;

                        game = new DiscoveredGame(displayName, "EA App", null, exePath, contentId);
                    }
                    catch { }

                    if (game != null) yield return game;
                }
            }
        }

        private static string? ReadEaContentId(string installPath)
        {
            try
            {
                string xmlPath = Path.Combine(installPath, "__Installer", "installerdata.xml");
                if (!File.Exists(xmlPath)) return null;
                var doc = XDocument.Load(xmlPath);
                return doc.Descendants("contentID").FirstOrDefault()?.Value
                    ?? doc.Descendants("ContentID").FirstOrDefault()?.Value
                    ?? doc.Descendants("productid").FirstOrDefault()?.Value;
            }
            catch { return null; }
        }

        private static bool IsEaSystemExe(string fileName)
        {
            string fl = fileName.ToLowerInvariant();
            return fl.Contains("cleanup") || fl.Contains("uninstall") || fl.Contains("eadesktop") ||
                   fl.Contains("eainstaller") || fl.StartsWith("vcredist") || fl.StartsWith("directx");
        }
    }
}
