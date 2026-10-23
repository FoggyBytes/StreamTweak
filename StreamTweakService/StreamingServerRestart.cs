using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Management.Infrastructure;
using StreamTweak;

namespace StreamTweakService;

/// <summary>
/// Restarts the streaming server's own Windows service so it rereads apps.json (10.0.0, the
/// Library tab's Sync and restart — SECURITY.md describes every check below; keep the two in step).
///
/// The caller names one of two fixed services and nothing else. This class then decides by itself
/// whether that service is a streaming server it may touch: its binary, read from the Service
/// Control Manager, must be tools\sunshinesvc.exe with sunshine.exe beside the tools folder and
/// config\apps.json in the same folder, under Program Files or ProgramData; it must run as
/// LocalSystem; and no client may be streaming. Stop and start go through the SCM only — the
/// server's own service stops it gracefully, and killing sunshine.exe would also trip the
/// crash-loop guard of Vibeshine and Vibepollo (§93.19).
/// </summary>
public static class StreamingServerRestart
{
    // Sunshine and Vibeshine install as SunshineService, Apollo and Vibepollo as ApolloService:
    // the name does not tell the fork, which is why the binary is checked (plan 8.0 / 10.0).
    private static readonly string[] AllowedServices = { "SunshineService", "ApolloService" };

    // sunshinesvc waits up to 20 s for the server to close before forcing it.
    private static readonly TimeSpan StopWait  = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(30);

    /// <summary>"OK", or "ERROR:&lt;reason&gt;" — the reason is a word the tray app maps to a message.</summary>
    public static string Restart(string? serviceName, Action<string> log)
    {
        string? name = AllowedServices.FirstOrDefault(s => string.Equals(s, serviceName, StringComparison.OrdinalIgnoreCase));
        if (name == null) return "ERROR:not allowed";

        string? installDir = CheckedInstallDir(name, log);
        if (installDir == null) return "ERROR:no server";

        if (IsStreaming(installDir))
        {
            log($"RestartServer: {name} refused, a client is streaming");
            return "ERROR:streaming";
        }

        using var sc = new ServiceController(name);
        bool stopped = true;
        try
        {
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, StopWait);
            }
        }
        catch (Exception ex)
        {
            // A stop that did not finish is no reason to leave the server down: start is tried anyway.
            stopped = false;
            log($"RestartServer: stopping {name} did not finish — {ex.Message}");
        }

        try
        {
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, StartWait);
        }
        catch (Exception ex)
        {
            log($"RestartServer: starting {name} failed — {ex.Message}");
            return "ERROR:start failed";
        }

        log($"RestartServer: {name} restarted ({installDir})" + (stopped ? "" : ", after a stop that timed out"));
        return "OK";
    }

    /// <summary>
    /// The server folder behind the service, when every check passes; null otherwise (logged).
    /// </summary>
    private static string? CheckedInstallDir(string name, Action<string> log)
    {
        string? pathName = null, startName = null;
        try
        {
            using var session = CimSession.Create(null);
            var svc = session.QueryInstances(@"root\cimv2", "WQL",
                $"SELECT PathName, StartName FROM Win32_Service WHERE Name = '{name}'").FirstOrDefault();
            pathName  = svc?.CimInstanceProperties["PathName"]?.Value as string;
            startName = svc?.CimInstanceProperties["StartName"]?.Value as string;
        }
        catch (Exception ex)
        {
            log($"RestartServer: {name} could not be read — {ex.Message}");
            return null;
        }
        if (string.IsNullOrWhiteSpace(pathName)) { log($"RestartServer: {name} is not installed"); return null; }

        if (!string.Equals(startName, "LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            log($"RestartServer: {name} does not run as LocalSystem ({startName})");
            return null;
        }

        string? exe = BinaryOf(pathName);
        string? installDir = null;
        try
        {
            if (exe != null)
            {
                exe = Path.GetFullPath(exe);
                string? tools = Path.GetDirectoryName(exe);
                if (string.Equals(Path.GetFileName(exe), "sunshinesvc.exe", StringComparison.OrdinalIgnoreCase)
                    && tools != null && string.Equals(Path.GetFileName(tools), "tools", StringComparison.OrdinalIgnoreCase))
                    installDir = Path.GetDirectoryName(tools);
            }
        }
        catch { installDir = null; }

        if (installDir == null || !IsUnderTrustedRoot(installDir)
            || !File.Exists(Path.Combine(installDir, "sunshine.exe"))
            || !File.Exists(Path.Combine(installDir, "config", "apps.json")))
        {
            log($"RestartServer: {name} is not a streaming server this service may restart ({pathName})");
            return null;
        }
        return installDir;
    }

    // The service's command line: a quoted path, or a bare one up to ".exe".
    private static string? BinaryOf(string pathName)
    {
        string p = pathName.Trim();
        if (p.StartsWith('"'))
        {
            int end = p.IndexOf('"', 1);
            return end > 1 ? p.Substring(1, end - 1) : null;
        }
        int exe = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? p.Substring(0, exe + 4) : null;
    }

    // Program Files (both) and ProgramData, where the servers install. Not a trust boundary by
    // itself — ordinary users may create folders in ProgramData — but the service's binary path
    // comes from its configuration, which only an administrator can change.
    private static bool IsUnderTrustedRoot(string dir)
    {
        string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                     Environment.SpecialFolder.CommonApplicationData })
        {
            string r = Environment.GetFolderPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (r.Length > 0 && full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // The server holds UDP sockets only while a client streams (§65): the same probe the tray app
    // uses, asked again here because this is the last moment before the stream would be cut.
    private static bool IsStreaming(string installDir)
    {
        string server = Path.Combine(installDir, "sunshine.exe");
        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName("sunshine"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, server, StringComparison.OrdinalIgnoreCase))
                    pids.Add(p.Id);
            }
            catch { /* exited meanwhile */ }
            finally { p.Dispose(); }
        }
        return pids.Count > 0 && UdpSockets.PortsOwnedBy(pids.ToArray()).Length > 0;
    }
}
