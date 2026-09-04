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

        /// <summary>
        /// The server's own log level prefixes, stripped before matching. Sunshine and all three
        /// forks write <c>[timestamp]: Level: message</c>.
        /// </summary>
        private static readonly string[] LevelPrefixes =
        {
            "Verbose: ", "Debug: ", "Info: ", "Warning: ", "Error: ", "Fatal: "
        };

        /// <summary>
        /// Markers that open a session, matched against the <i>start</i> of the message.
        /// <c>CLIENT CONNECTED</c> is the one that fires in practice — the other two are kept as
        /// wording insurance across forks, and cost nothing now that they are anchored.
        /// </summary>
        private static readonly string[] StartMarkers =
        {
            "CLIENT CONNECTED", "Starting stream", "Stream started",
            // Vibeshine and Vibepollo only (verified absent from Sunshine and Apollo, whose src/
            // has no session_history at all). Written by the server's own history subsystem when
            // the RTSP session is negotiated — about a second *before* CLIENT CONNECTED, already
            // carrying resolution, fps, codec and HDR. Two lines meant for machines, paired by a
            // uuid, instead of prose: see StreamingLogMonitor for what the pairing is worth.
            "session_history: begin_session"
        };

        /// <summary>
        /// Markers that close a session. <c>Session ended</c> is load-bearing and was missing until
        /// 8.1.0: when the streamed app exits, the server tears the session down and logs *only*
        /// that line — there is no CLIENT DISCONNECTED, because the client never disconnected.
        /// Without it StreamTweak never learned the session was over, so the link stayed switched,
        /// the session kept running in the history, and a stream started shortly afterwards was
        /// merged into it. Verified present in src/stream.cpp of Sunshine, Apollo, Vibeshine and
        /// Vibepollo.
        /// </summary>
        private static readonly string[] StopMarkers =
        {
            "CLIENT DISCONNECTED", "Session ended", "Stream ended", "Stream stopped", "Stopping stream",
            // The other half of the pair above. Written in the same breath as "Session ended" —
            // literally the next statement in the server's teardown (stream.cpp) — so it is no more
            // reliable than that line is. What it adds is the uuid, not robustness.
            "session_history: end_session"
        };

        /// <summary>
        /// Strips <c>[timestamp]: </c>, the log level, and any leading <c>[tag] </c>, leaving the
        /// server's message. A line with none of them is returned as-is (wrapped continuation
        /// lines, foreign formats).
        /// </summary>
        private static string ExtractMessage(string logLine)
        {
            string s = logLine;

            int ts = s.IndexOf("]: ", StringComparison.Ordinal);
            if (ts >= 0) s = s.Substring(ts + 3);

            foreach (string prefix in LevelPrefixes)
            {
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(prefix.Length);
                    break;
                }
            }

            s = s.TrimStart();

            // The belt on the anchoring above: a fork that one day prefixes its message with a
            // tag ("Info: [rtsp] Session ended") would otherwise have its session lines silently
            // ignored, and a missed *stop* is the expensive direction — 8.1.0 shipped with
            // "Session ended" absent from the vocabulary, and the link stayed switched while the
            // session ran on in the history. None of the four servers writes a tag today (the two
            // forks' logs measured for issue #9 write the message bare), so this costs nothing and
            // covers the case that would cost a release.
            //
            // Deliberately narrow: the bracket must close, and a space must follow it, so
            // "[Pixel 9 Moonlight V+]: …" (a client name, ']' followed by ':') is not a tag. Two
            // at most, because a message that opens with three brackets is not a log format we
            // are prepared to guess at.
            for (int i = 0; i < 2 && s.Length > 0 && s[0] == '['; i++)
            {
                int close = s.IndexOf(']');
                if (close < 0 || close + 1 >= s.Length || s[close + 1] != ' ') break;
                s = s.Substring(close + 1).TrimStart();
            }

            return s;
        }

        /// <summary>
        /// True when the message <i>begins</i> with one of the markers.
        /// <para><b>Anchored on purpose — this is issue #9.</b> These markers used to be matched as
        /// substrings anywhere in the line, which made three ordinary lines look like session
        /// events: Apollo's Playnite bridge (<c>Playnite IPC: client connected</c>), the display
        /// teardown (<c>Display restore: final stream ended; …</c>), and — via a <c>moonlight</c>
        /// marker that is now gone — every line naming a client whose own name contains
        /// "Moonlight". In the reporter's log 177 of 205 detected starts were of that kind, and one
        /// of them left a phantom session running for hours. Matching from the start of the message
        /// discriminates them all: the noise always carries a prefix, the real lines never do.</para>
        /// StartsWith rather than equality so a fork appending a detail
        /// (<c>CLIENT DISCONNECTED [1 remaining]</c>) still counts.
        /// </summary>
        private static bool StartsWithMarker(string message, string[] markers)
        {
            foreach (string marker in markers)
                if (message.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static StreamingEvent ParseLogLine(string logLine)
        {
            if (string.IsNullOrWhiteSpace(logLine))
                return StreamingEvent.None;

            string message = ExtractMessage(logLine);

            // Check StreamStopped FIRST (more specific patterns).
            if (StartsWithMarker(message, StopMarkers))
            {
                DebugLog($"StreamStopped detected: {logLine}");
                return StreamingEvent.StreamStopped;
            }

            if (StartsWithMarker(message, StartMarkers))
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
        //
        // ⚠️⚠️ MEASURED 04/09/2026: THIS DOES NOT DETECT A LIVE SESSION. Polled every 11 s across
        // a real session on Vibeshine (14:27:00 → 14:40:56, MGS4): false at all 76 polls inside
        // the session, with zero established sockets on 48010 at every one of them. It was true
        // exactly once, at 14:26:59 — the launch handshake, one second before CLIENT CONNECTED —
        // and gone again 11 s later. (tools/ProbeWatch is the harness; keep it for the next one.)
        //
        // The client source says why: RTSP is used for setup only, and moonlight-common-c opens a
        // TCP connection per RTSP message and closes it at the end of the transaction
        // (RtspConnection.c, transactRtspMessageTcp), or skips TCP entirely on servers where it
        // uses ENet. The stream itself is UDP. So this is true only for the milliseconds of an
        // RTSP exchange during launch, and the comment above — "maintained for the entire
        // session" — was never true.
        //
        // It is therefore NOT a liveness signal, and nothing may be gated on a false result from
        // it. Callers to re-examine: LinkSpeedManager.LiveSessionProbe (the guard meant to stop a
        // client renegotiating the adapter mid-stream) and StreamingLogMonitor Phase 2.
        // The obvious replacement was tried and does NOT fit either. GET
        // http://127.0.0.1:47989/serverinfo answers unauthenticated with <state> and <currentgame>,
        // and it does track a session: measured 04/09/2026, BUSY within a second of CLIENT
        // CONNECTED, FREE within one poll of the client going away. But a second measurement, with
        // the client disconnected and the game left running, showed it stays BUSY with nobody
        // attached (15:07:43 disconnect → still BUSY at 15:07:51 and 15:08:02 → FREE only at
        // 15:08:24, once the game was gone). So <state> means "a session or its app is up", not
        // "a client is streaming", and it must NOT be wired into the guards here: a client that
        // drops and relaunches would be refused its SETSPEED for as long as the game stays up —
        // the relaunch case LinkSim S16 exists to protect.
        //
        // Net: there is no known live-stream signal available to the host today except the
        // log-derived flag. Do not replace this with a probe that has not been measured across a
        // disconnect-with-game-running, which is where both candidates so far have failed.
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

        // ─── Active session detection via the stream's own UDP sockets ────────

        /// <summary>
        /// True while the streaming server holds any UDP socket — which means a client is attached
        /// and streaming.
        /// <para><b>Why this and not a port list.</b> The stream runs on UDP ports derived from the
        /// server's configurable base port, so naming 47998-48000 would break on a host that moved
        /// it. The measurement that makes the simpler question sound: with no session running the
        /// server process holds <i>zero</i> UDP sockets and four TCP listeners, so the presence of
        /// any UDP socket at all is the event. Ports are still logged, for diagnosis.</para>
        /// <para><b>Measured 04/09/2026</b>, two full cycles on Vibeshine, polled every 11 s:
        /// the sockets (47998/47999/48000) appeared within one poll of CLIENT CONNECTED and were
        /// gone within one poll of CLIENT DISCONNECTED, both times. Crucially they were <i>absent</i>
        /// through the 43 s when the client had gone but the game was still running — the case
        /// where <c>/serverinfo</c>'s <c>&lt;state&gt;</c> keeps saying BUSY. So this tracks the
        /// client, not the app, which is exactly the question StreamTweak asks.</para>
        /// <para>⚠️ It cannot see through a server that has hung in its teardown: the sockets stay
        /// open with the process, and so does the session. Nothing available to the host covers
        /// that case.</para>
        /// <para>⚠️ NOT wired into <c>LinkSpeedManager.LiveSessionProbe</c>. That guard acts on a
        /// single reading, so a momentary gap mid-session — a client reconnecting, a stream
        /// restarting on a settings change — would let it renegotiate the adapter under a live
        /// stream. Wiring it there needs a measurement across a long session first, showing the
        /// signal has no gaps between the first and last poll. The consumers here all tolerate a
        /// gap: the watchdog needs three in a row, the startup check reads twice.</para>
        /// </summary>
        public static bool HasActiveStreamSockets()
        {
            try
            {
                var serverInfo = FindStreamingAppInfo();
                if (serverInfo?.ExePath == null)
                {
                    VerboseLog("UDP check: no streaming server installed");
                    return false;
                }

                string exeName = Path.GetFileNameWithoutExtension(serverInfo.ExePath);
                int[] pids;
                var procs = Process.GetProcessesByName(exeName);
                try
                {
                    pids = procs.Select(p => p.Id).ToArray();
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }

                if (pids.Length == 0)
                {
                    VerboseLog($"UDP check: {exeName} is not running");
                    return false;
                }

                int[] ports = UdpHelper.PortsOwnedBy(pids);
                VerboseLog(ports.Length > 0
                    ? $"UDP check: {exeName} holds {string.Join(", ", ports)} — a client is streaming"
                    : $"UDP check: {exeName} holds no UDP socket — no client streaming");
                return ports.Length > 0;
            }
            catch (Exception ex)
            {
                // Deliberately false on error: every caller treats false as "no session", and the
                // costly mistake is the other one — ending a session that is running.
                DebugLog($"UDP check error: {ex.Message}");
                return false;
            }
        }

        // ─── P/Invoke helper: GetExtendedUdpTable ─────────────────────────────

        private static class UdpHelper
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct MIB_UDPROW_OWNER_PID
            {
                public uint dwLocalAddr;
                public uint dwLocalPort;
                public uint dwOwningPid;
            }

            // IPv6 rows carry a 16-byte address and a scope id before the port, and the fields are
            // laid out in a different order — hence a second struct rather than a shared one.
            [StructLayout(LayoutKind.Sequential)]
            private struct MIB_UDP6ROW_OWNER_PID
            {
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
                public byte[] ucLocalAddr;
                public uint dwLocalScopeId;
                public uint dwLocalPort;
                public uint dwOwningPid;
            }

            [DllImport("iphlpapi.dll", SetLastError = true)]
            private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
                bool sort, int ipVersion, int tblClass, uint reserved);

            private const int AF_INET = 2, AF_INET6 = 23, UDP_TABLE_OWNER_PID = 1;

            /// <summary>Local UDP ports currently bound by any of the given processes, both
            /// address families. Empty when there are none.</summary>
            public static int[] PortsOwnedBy(int[] pids)
            {
                var ports = new List<int>();
                Collect(AF_INET, pids, ports);
                Collect(AF_INET6, pids, ports);
                ports.Sort();
                return ports.ToArray();
            }

            private static void Collect(int family, int[] pids, List<int> ports)
            {
                int len = 0;
                GetExtendedUdpTable(IntPtr.Zero, ref len, false, family, UDP_TABLE_OWNER_PID, 0);
                if (len <= 0) return;

                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (GetExtendedUdpTable(buf, ref len, false, family, UDP_TABLE_OWNER_PID, 0) != 0)
                        return;

                    int rows = Marshal.ReadInt32(buf);
                    int size = family == AF_INET
                        ? Marshal.SizeOf<MIB_UDPROW_OWNER_PID>()
                        : Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();

                    for (int i = 0; i < rows; i++)
                    {
                        IntPtr row = buf + 4 + i * size;
                        uint pid, port;
                        if (family == AF_INET)
                        {
                            var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(row);
                            pid = r.dwOwningPid; port = r.dwLocalPort;
                        }
                        else
                        {
                            var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(row);
                            pid = r.dwOwningPid; port = r.dwLocalPort;
                        }

                        if (Array.IndexOf(pids, (int)pid) < 0) continue;
                        // Network byte order, same as the TCP table.
                        ports.Add((int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF)));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
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