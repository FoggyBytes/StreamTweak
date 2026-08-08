using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StreamTweak
{
    public class LogParser
    {
        public enum StreamingEvent
        {
            None,
            StreamStarted,
            StreamStopped
        }

        // Known app names to look for in the registry and Program Files
        private static readonly string[] KnownAppNames =
        {
            "Vibepollo", "Vibeshine", "Apollo", "Sunshine"
        };

        public static StreamingEvent ParseLogLine(string logLine)
        {
            if (string.IsNullOrWhiteSpace(logLine))
                return StreamingEvent.None;

            string lowerLine = logLine.ToLower();

            // Check StreamStopped FIRST (more specific patterns)
            //
            // "session ended" is load-bearing and was missing until 8.1.0: when the streamed app
            // exits, the server tears the session down and logs *only* that line — there is no
            // CLIENT DISCONNECTED, because the client never disconnected. Without it StreamTweak
            // simply never learned the session was over, so the link stayed switched, the session
            // kept running in the history, and a stream started shortly afterwards was merged into
            // it. Verified present in src/stream.cpp of Sunshine, Apollo, Vibeshine and Vibepollo.
            if (lowerLine.Contains("client disconnected") ||
                lowerLine.Contains("session ended") ||
                lowerLine.Contains("stream ended") ||
                lowerLine.Contains("stream stopped") ||
                lowerLine.Contains("stopping stream"))
            {
                DebugLog($"StreamStopped detected: {logLine}");
                return StreamingEvent.StreamStopped;
            }

            // Then check StreamStarted
            if (lowerLine.Contains("client connected") ||
                lowerLine.Contains("starting stream") ||
                lowerLine.Contains("stream started") ||
                lowerLine.Contains("client ip") ||
                lowerLine.Contains("moonlight"))
            {
                DebugLog($"StreamStarted detected: {logLine}");
                return StreamingEvent.StreamStarted;
            }

            return StreamingEvent.None;
        }

        public static string? FindStreamingServiceLogFile()
        {
            // Step 1: try registry — fast and precise
            string? log = FindLogViaRegistry();
            if (log != null) return log;

            // Step 2: fallback — scan Program Files for known config structures
            log = FindLogViaProgramFilesScan();
            if (log != null) return log;

            VerboseLog("No streaming service log file found");
            return null;
        }

        #region Registry discovery

        private static string? FindLogViaRegistry()
        {
            foreach (string appName in KnownAppNames)
            {
                string? installDir = GetInstallDirFromRegistry(appName);
                if (string.IsNullOrEmpty(installDir)) continue;

                string? log = FindLogInInstallDir(installDir, appName);
                if (log != null) return log;
            }
            return null;
        }

        private static string? GetInstallDirFromRegistry(string appName)
        {
            // Try direct software key first
            string? dir = ReadRegistryInstallDir($@"SOFTWARE\{appName}")
                       ?? ReadRegistryInstallDir($@"SOFTWARE\WOW6432Node\{appName}");

            if (!string.IsNullOrEmpty(dir)) return dir;

            // Try Uninstall entries
            return FindInUninstallKeys(appName);
        }

        private static string? ReadRegistryInstallDir(string subKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key == null) return null;

                // Common value names used by installers
                foreach (string valueName in new[] { "InstallLocation", "InstallDir", "Path" })
                {
                    string? val = key.GetValue(valueName) as string;
                    if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                    {
                        VerboseLog($"Registry: found {subKey} → {val}");
                        return val;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? FindInUninstallKeys(string appName)
        {
            string[] uninstallPaths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (string uninstallPath in uninstallPaths)
            {
                try
                {
                    using var uninstallKey = Registry.LocalMachine.OpenSubKey(uninstallPath);
                    if (uninstallKey == null) continue;

                    foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = uninstallKey.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            string? displayName = subKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(displayName)) continue;

                            if (!displayName.Contains(appName, StringComparison.OrdinalIgnoreCase)) continue;

                            string? installDir = subKey.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                            {
                                VerboseLog($"Uninstall registry: found {appName} → {installDir}");
                                return installDir;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return null;
        }

        #endregion

        #region Program Files scan fallback

        private static string? FindLogViaProgramFilesScan()
        {
            VerboseLog("Registry lookup failed — scanning Program Files...");

            // Collect all candidate Program Files directories
            var searchRoots = new List<string>();

            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            string pfx86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";

            if (Directory.Exists(pf)) searchRoots.Add(pf);
            if (Directory.Exists(pfx86) && pfx86 != pf) searchRoots.Add(pfx86);

            // Search in known-name folders first (faster), then any folder
            foreach (string root in searchRoots)
            {
                // Priority scan: known app names first
                foreach (string appName in KnownAppNames)
                {
                    string candidate = Path.Combine(root, appName);
                    if (!Directory.Exists(candidate)) continue;

                    string? log = FindLogInInstallDir(candidate, appName);
                    if (log != null) return log;
                }

                // Broad scan: any subfolder with a sunshine config structure
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        // Skip already-checked known names
                        string dirName = Path.GetFileName(dir);
                        if (KnownAppNames.Any(n => n.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        string? log = FindLogInInstallDir(dir, dirName);
                        if (log != null) return log;
                    }
                }
                catch { }
            }

            return null;
        }

        #endregion

        #region Log file resolution

        private static string? FindLogInInstallDir(string installDir, string appName)
        {
            try
            {
                string configDir = Path.Combine(installDir, "config");
                if (!Directory.Exists(configDir)) return null;

                // Dynamic logs subfolder (Vibeshine/Vibepollo style)
                string logsDir = Path.Combine(configDir, "logs");
                if (Directory.Exists(logsDir))
                {
                    string? dynamic = FindMostRecentLogFile(logsDir);
                    if (dynamic != null) return dynamic;
                }

                // Static log file (Sunshine/Apollo style)
                string staticLog = Path.Combine(configDir, "sunshine.log");
                if (File.Exists(staticLog))
                {
                    VerboseLog($"Found static log for {appName}: {staticLog}");
                    return staticLog;
                }
            }
            catch { }
            return null;
        }

        private static string? FindMostRecentLogFile(string logDirectory, string searchPattern = "sunshine-*.log")
        {
            if (!Directory.Exists(logDirectory)) return null;
            try
            {
                var latest = Directory.GetFiles(logDirectory, searchPattern)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(latest))
                {
                    VerboseLog($"Found dynamic log file: {Path.GetFileName(latest)} in {logDirectory}");
                    return latest;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error scanning directory {logDirectory}: {ex.Message}");
            }
            return null;
        }

        #endregion

        // ─── Active session detection via TCP ─────────────────────────────────

        // Primary: find the streaming server process and check via GetExtendedTcpTable
        // whether it has an ESTABLISHED TCP connection on port 48010 (RTSP) to a non-loopback IP.
        // Filtering to port 48010 prevents false positives from Sunshine's HTTPS web UI
        // (47989/47990), which can be accessed from another machine without an active stream.
        // Fallback: same check via IPGlobalProperties — less precise (any process, same port).
        public static bool HasActiveMoonlightSession()
        {
            // Primary: process-scoped TCP check
            try
            {
                var serverInfo = FindStreamingAppInfo();
                if (serverInfo?.ExePath != null)
                {
                    string exeName = Path.GetFileNameWithoutExtension(serverInfo.ExePath);
                    Process[]? procs = null;
                    try
                    {
                        procs = Process.GetProcessesByName(exeName);
                        if (procs.Length > 0)
                        {
                            int pid = procs[0].Id;
                            bool active = TcpHelper.HasEstablishedExternalConnection(pid);
                            DebugLog(active
                                ? $"TCP check: {exeName} (PID {pid}) has external established connections — session active"
                                : $"TCP check: {exeName} (PID {pid}) has no external established connections");
                            return active;
                        }
                        DebugLog($"TCP check: {exeName} process not found");
                    }
                    finally
                    {
                        if (procs != null)
                            foreach (var p in procs) p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"TCP check (process) error: {ex.Message}");
            }

            // Fallback: port 48010 (RTSP default) via IPGlobalProperties
            try
            {
                var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                bool active = connections.Any(c =>
                    c.LocalEndPoint.Port == 48010 &&
                    c.State == TcpState.Established &&
                    !IPAddress.IsLoopback(c.RemoteEndPoint.Address));
                DebugLog(active
                    ? "TCP check (fallback): session detected on port 48010"
                    : "TCP check (fallback): no session on port 48010");
                return active;
            }
            catch (Exception ex)
            {
                DebugLog($"TCP check (fallback) error: {ex.Message}");
                return false;
            }
        }

        // ─── P/Invoke helper: GetExtendedTcpTable ─────────────────────────────

        private static class TcpHelper
        {
            private enum TcpTableClass
            {
                TcpTableOwnerPidConnections = 4
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MIB_TCPROW_OWNER_PID
            {
                public uint dwState;
                public uint dwLocalAddr;
                public uint dwLocalPort;
                public uint dwRemoteAddr;
                public uint dwRemotePort;
                public uint dwOwningPid;
            }

            [DllImport("iphlpapi.dll", SetLastError = true)]
            private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
                bool sort, int ipVersion, TcpTableClass tblClass, uint reserved);

            private const uint MIB_TCP_STATE_ESTAB = 5;
            private const int AF_INET = 2;

            public static bool HasEstablishedExternalConnection(int pid)
            {
                int bufLen = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref bufLen, false, AF_INET,
                    TcpTableClass.TcpTableOwnerPidConnections, 0);

                IntPtr buf = Marshal.AllocHGlobal(bufLen);
                try
                {
                    uint ret = GetExtendedTcpTable(buf, ref bufLen, false, AF_INET,
                        TcpTableClass.TcpTableOwnerPidConnections, 0);
                    if (ret != 0) return false;

                    int rowCount = Marshal.ReadInt32(buf);
                    int rowSize  = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                    for (int i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(
                            buf + 4 + i * rowSize);

                        if (row.dwOwningPid != (uint)pid) continue;
                        if (row.dwState    != MIB_TCP_STATE_ESTAB) continue;

                        // Only count connections on the RTSP streaming control port (48010).
                        // dwLocalPort is stored in network byte order (big-endian); convert
                        // to host order by swapping the low two bytes.
                        // Filtering to 48010 prevents false positives from Sunshine's HTTPS
                        // web UI (ports 47989/47990), which can be accessed from any machine
                        // on the LAN without an active streaming session.
                        // Note: the fallback path below also checks port 48010, so this is
                        // consistent with both detection paths.
                        int localPort = (int)(((row.dwLocalPort & 0xFF) << 8)
                                            | ((row.dwLocalPort >> 8) & 0xFF));
                        if (localPort != 48010) continue;

                        var remoteIp = new IPAddress(row.dwRemoteAddr);
                        if (!IPAddress.IsLoopback(remoteIp))
                            return true;
                    }
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        private static void DebugLog(string message) => DebugLogger.Log(message);

        // Discovery internals. FindStreamingServiceLogFile() is re-run every 10 s by
        // StreamingLogMonitor to catch a rotated/late-appearing server log, so anything
        // logged along the happy path repeats forever while saying nothing new. The caller
        // logs the outcome — initial file, and every switch — which is the part worth keeping.
        private static void VerboseLog(string message) => DebugLogger.Verbose(message);

        // ─── Streaming App Detection ─────────────────────────────────────────

        public static StreamingAppInfo? FindStreamingAppInfo()
        {
            foreach (string appName in KnownAppNames)
            {
                string? installDir = GetInstallDirFromRegistry(appName);
                if (!string.IsNullOrEmpty(installDir))
                {
                    var info = BuildStreamingAppInfo(appName, installDir);
                    if (info != null) return info;
                }
            }

            var searchRoots = new List<string>();
            string pf    = Environment.GetEnvironmentVariable("ProgramFiles")       ?? @"C:\Program Files";
            string pfx86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            if (Directory.Exists(pf))              searchRoots.Add(pf);
            if (Directory.Exists(pfx86) && pfx86 != pf) searchRoots.Add(pfx86);

            foreach (string root in searchRoots)
                foreach (string appName in KnownAppNames)
                {
                    string candidate = Path.Combine(root, appName);
                    if (Directory.Exists(candidate))
                    {
                        var info = BuildStreamingAppInfo(appName, candidate);
                        if (info != null) return info;
                    }
                }

            return null;
        }

        private static StreamingAppInfo? BuildStreamingAppInfo(string appName, string installDir)
        {
            var info = new StreamingAppInfo { AppName = appName };

            try
            {
                var exes = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly);
                info.ExePath = exes.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Contains(appName, StringComparison.OrdinalIgnoreCase))
                    ?? exes.FirstOrDefault();
            }
            catch { }

            if (string.IsNullOrEmpty(info.ExePath))
                return null;

            string configDir = Path.Combine(installDir, "config");
            string logsDir   = Path.Combine(configDir, "logs");
            if      (Directory.Exists(logsDir))   info.LogFolderPath = logsDir;
            else if (Directory.Exists(configDir)) info.LogFolderPath = configDir;
            else                                  info.LogFolderPath = installDir;

            return info;
        }
    }

    public class StreamingAppInfo
    {
        public string  AppName       { get; set; } = string.Empty;
        public string? ExePath        { get; set; }
        public string? LogFolderPath  { get; set; }
    }
}