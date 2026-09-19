using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace StreamTweak
{
    /// <summary>
    /// What THIS machine can do when a client asks it to power down (8.6.0, issue #10).
    ///
    /// Everything is read from the running system, never assumed: the same StreamTweak runs on
    /// desktops with S3, on Modern Standby machines that have no S3 at all, on machines where
    /// hibernation is switched off, and under policies that take the shutdown right away from
    /// the user. The client shows only what this reports, so a mode that is listed here must
    /// be one <c>App.Power.cs</c> can actually carry out.
    ///
    ///   sleep     — S1/S2/S3 (SetSuspendState), or Modern Standby (AoAc: the display is turned
    ///               off, which is how Windows enters it — there is no suspend call for S0ix)
    ///   hibernate — S4 supported AND a hibernation file present (`powercfg /h off` removes it)
    ///   restart / shutdown — whenever the user holds SeShutdownPrivilege
    ///
    /// ⚠️ With no shutdown privilege the list is EMPTY, not "shutdown only": every mode needs it.
    ///
    /// ⚠️ Hibernate is reported and carried out here, but StreamLight 6.2.0 does NOT offer it
    /// (decision of 19/09/2026): a host in hibernation woke ~30 s later although the client that
    /// put it there sent nothing — the wake comes from elsewhere on the network (§77). The host
    /// side is kept so a client can take it up again once that is understood.
    /// </summary>
    public static class HostPowerCapabilities
    {
        public const string Sleep     = "sleep";
        public const string Hibernate = "hibernate";
        public const string Restart   = "restart";
        public const string Shutdown  = "shutdown";

        /// <summary>The power states the firmware and the OS report, read once per call.</summary>
        public readonly record struct PowerStates(bool S1, bool S2, bool S3, bool S4,
                                                  bool HiberFilePresent, bool AoAc)
        {
            /// <summary>Classic suspend (S1-S3) is available: SetSuspendState(FALSE) sleeps.</summary>
            public bool HasSuspend => S1 || S2 || S3;
        }

        public static PowerStates ReadStates()
        {
            // SYSTEM_POWER_CAPABILITIES is a run of BOOLEANs (one byte each) followed by battery
            // and wake fields; the offsets below are fixed by the Win32 ABI. The buffer is sized
            // well past the real struct (76 bytes on x64) so the call can never write beyond it.
            var buf = new byte[256];
            try
            {
                if (!GetPwrCapabilities(buf))
                    return default;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Power] GetPwrCapabilities threw: {ex.Message}");
                return default;
            }
            return new PowerStates(
                S1: buf[3] != 0, S2: buf[4] != 0, S3: buf[5] != 0, S4: buf[6] != 0,
                HiberFilePresent: buf[8] != 0, AoAc: buf[20] != 0);
        }

        /// <summary>The modes a client may ask for, in display order.</summary>
        public static IReadOnlyList<string> GetModes()
        {
            if (!HasShutdownPrivilege())
                return Array.Empty<string>();

            var s = ReadStates();
            var modes = new List<string>(4);
            if (s.HasSuspend || s.AoAc)          modes.Add(Sleep);
            if (s.S4 && s.HiberFilePresent)      modes.Add(Hibernate);
            modes.Add(Restart);
            modes.Add(Shutdown);
            return modes;
        }

        public static bool IsSupported(string mode) =>
            GetModes().Contains(mode, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// POWERCAPS reply: <c>{"v":1,"modes":[…],"wake_lan":bool}</c>.
        /// <paramref name="adapterName"/> is the managed wired adapter (the NIC a magic packet
        /// would reach); <c>wake_lan</c> is false when there is none.
        /// </summary>
        public static string ToJson(string? adapterName) =>
            JsonSerializer.Serialize(new
            {
                v = 1,
                modes = GetModes(),
                wake_lan = IsWakeArmed(adapterName),
            });

        // ── Wake-on-LAN ─────────────────────────────────────────────────────────

        private static readonly object _wakeLock = new();
        private static string? _wakeAdapter;
        private static bool _wakeArmed;
        private static DateTime _wakeReadAtUtc = DateTime.MinValue;
        private const int WakeCacheSeconds = 60;

        /// <summary>
        /// True when the adapter is armed to wake the machine — the "Allow this device to wake
        /// the computer" box, as <c>powercfg /devicequery wake_armed</c> lists it. That list is
        /// the one source readable without elevation; the WMI classes behind it are not.
        /// ⚠️ It speaks for sleep and hibernation. Whether a magic packet also reaches a machine
        /// that is fully OFF depends on the firmware (ErP, "Power on by PCI-E"), which no API
        /// exposes, so the client never promises that.
        /// </summary>
        public static bool IsWakeArmed(string? adapterName)
        {
            if (string.IsNullOrWhiteSpace(adapterName)) return false;

            lock (_wakeLock)
            {
                if (_wakeAdapter == adapterName &&
                    (DateTime.UtcNow - _wakeReadAtUtc).TotalSeconds < WakeCacheSeconds)
                    return _wakeArmed;
            }

            bool armed = false;
            try
            {
                // powercfg names devices by their description ("Realtek PCIe 2.5GbE Family
                // Controller"), while the managed adapter is stored by its connection name
                // ("Ethernet"). NetworkInterface maps one to the other.
                string? description = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase))
                    ?.Description;
                if (!string.IsNullOrEmpty(description))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                        Arguments = "/devicequery wake_armed",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                        armed = output.Split('\n')
                            .Select(l => l.Trim())
                            .Any(l => l.Equals(description, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Power] wake_armed query failed: {ex.Message}");
            }

            lock (_wakeLock)
            {
                _wakeAdapter = adapterName;
                _wakeArmed = armed;
                _wakeReadAtUtc = DateTime.UtcNow;
            }
            return armed;
        }

        // ── Shutdown privilege ──────────────────────────────────────────────────

        /// <summary>
        /// Whether the user's token holds SeShutdownPrivilege at all (enabled or not). Group
        /// Policy can remove it ("Shut down the system"), and then no mode would work.
        /// </summary>
        public static bool HasShutdownPrivilege()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
                    return true;   // cannot tell: do not hide modes over an unreadable token
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out LUID luid))
                    return true;

                GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out int needed);
                if (needed <= 0) return true;
                IntPtr info = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!GetTokenInformation(token, TokenPrivileges, info, needed, out _))
                        return true;
                    int count = Marshal.ReadInt32(info);
                    int size = Marshal.SizeOf<LUID_AND_ATTRIBUTES>();
                    for (int i = 0; i < count; i++)
                    {
                        var la = Marshal.PtrToStructure<LUID_AND_ATTRIBUTES>(info + 4 + i * size);
                        if (la.Luid.LowPart == luid.LowPart && la.Luid.HighPart == luid.HighPart)
                            return true;
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(info); }
            }
            catch { return true; }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenPrivileges = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool GetPwrCapabilities([Out] byte[] lpspc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
            IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);
    }
}
