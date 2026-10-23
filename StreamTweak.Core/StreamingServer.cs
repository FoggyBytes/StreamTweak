using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Microsoft.Management.Infrastructure;

namespace StreamTweak
{
    /// <summary>The streaming server's Windows service, as the Library's Sync and restart sees it.</summary>
    public sealed class StreamingServerService
    {
        public string Name       { get; init; } = "";   // SunshineService · ApolloService
        public string ServerName { get; init; } = "";   // Sunshine · Apollo · Vibeshine · Vibepollo
        public string InstallDir { get; init; } = "";
    }

    /// <summary>
    /// What the tray app needs to know about the streaming server around a library sync (10.0.0):
    /// which service runs the server whose apps.json StreamTweak writes, whether that server is up
    /// and answering, and whether its own Steam sync is on. Read-only; the restart itself is the
    /// service's (SpeedChanger.RestartStreamingServer), which checks all of this again.
    /// </summary>
    public static class StreamingServer
    {
        private static readonly string[] ServiceNames = { "SunshineService", "ApolloService" };

        /// <summary>
        /// The service whose install folder holds this apps.json (as &lt;folder&gt;\config\apps.json), or
        /// null. With Sunshine and Apollo both installed this is how the right one is restarted.
        /// </summary>
        public static StreamingServerService? ServiceFor(string? appsJsonPath)
        {
            if (string.IsNullOrEmpty(appsJsonPath)) return null;
            string configDir;
            try { configDir = Path.GetDirectoryName(Path.GetFullPath(appsJsonPath)) ?? ""; }
            catch { return null; }

            foreach (string name in ServiceNames)
            {
                var (pathName, displayName, _) = Query(name);
                string? installDir = InstallDirOf(pathName);
                if (installDir == null) continue;
                if (!string.Equals(Path.Combine(installDir, "config"), configDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                return new StreamingServerService { Name = name, ServerName = ServerNameFor(installDir, displayName), InstallDir = installDir };
            }
            return null;
        }

        /// <summary>The service's state as the SCM reports it: Running, Stopped, Start Pending… or "".</summary>
        public static string State(string serviceName) => Query(serviceName).state;

        /// <summary>True when the server accepts a connection on its HTTP port (the one Moonlight polls).</summary>
        public static bool IsAnswering(StreamingServerService svc)
        {
            try
            {
                using var tcp = new TcpClient();
                var task = tcp.ConnectAsync("127.0.0.1", HttpPort(svc.InstallDir));
                return task.Wait(TimeSpan.FromMilliseconds(800)) && tcp.Connected;
            }
            catch { return false; }
        }

        /// <summary>
        /// Vibeshine's and Vibepollo's own Steam sync: on only with both steam_enabled and
        /// steam_auto_sync in sunshine.conf (both off by default — src/config_steam.cpp, 2.0.1).
        /// With it on as well, Steam games would be listed twice.
        /// </summary>
        public static bool SteamSyncOn(StreamingServerService svc) =>
            IsTrue(ConfValue(svc.InstallDir, "steam_enabled")) && IsTrue(ConfValue(svc.InstallDir, "steam_auto_sync"));

        // ── pieces ──────────────────────────────────────────────────────────

        private static (string? pathName, string? displayName, string state) Query(string name)
        {
            try
            {
                using var session = CimSession.Create(null);
                var svc = session.QueryInstances(@"root\cimv2", "WQL",
                    $"SELECT PathName, DisplayName, State FROM Win32_Service WHERE Name = '{name}'").FirstOrDefault();
                return (svc?.CimInstanceProperties["PathName"]?.Value as string,
                        svc?.CimInstanceProperties["DisplayName"]?.Value as string,
                        svc?.CimInstanceProperties["State"]?.Value as string ?? "");
            }
            catch { return (null, null, ""); }
        }

        // "<folder>\tools\sunshinesvc.exe" --service → <folder>
        private static string? InstallDirOf(string? pathName)
        {
            if (string.IsNullOrWhiteSpace(pathName)) return null;
            string p = pathName.Trim();
            string? exe = p.StartsWith('"')
                ? (p.IndexOf('"', 1) is int e and > 1 ? p.Substring(1, e - 1) : null)
                : (p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is int x and > 0 ? p.Substring(0, x + 4) : null);
            try
            {
                if (exe == null || !string.Equals(Path.GetFileName(exe), "sunshinesvc.exe", StringComparison.OrdinalIgnoreCase))
                    return null;
                return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(exe)));
            }
            catch { return null; }
        }

        // The fork's name: the log detection knows it (Vibeshine installs into a "Sunshine" folder);
        // otherwise the service's display name, which Vibepollo leaves as "Apollo Service".
        private static string ServerNameFor(string installDir, string? displayName)
        {
            try
            {
                var info = LogParser.FindStreamingAppInfo();
                if (info?.ExePath != null && !string.IsNullOrEmpty(info.AppName) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(info.ExePath)), installDir, StringComparison.OrdinalIgnoreCase))
                    return info.AppName;
            }
            catch { }
            string d = displayName ?? "";
            return d.EndsWith(" Service", StringComparison.OrdinalIgnoreCase) ? d[..^8] : (d.Length > 0 ? d : "Sunshine");
        }

        // The HTTP port: sunshine.conf's "port", 47989 by default (Moonlight's default too).
        private static int HttpPort(string installDir) =>
            int.TryParse(ConfValue(installDir, "port"), out int p) && p > 0 && p < 65536 ? p : 47989;

        private static string? ConfValue(string installDir, string key)
        {
            try
            {
                string conf = Path.Combine(installDir, "config", "sunshine.conf");
                if (!File.Exists(conf)) return null;
                foreach (string raw in File.ReadLines(conf))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith('#')) continue;
                    if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return line[(eq + 1)..].Trim();
                }
            }
            catch { }
            return null;
        }

        private static bool IsTrue(string? v) =>
            v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                          || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v == "1");
    }
}
