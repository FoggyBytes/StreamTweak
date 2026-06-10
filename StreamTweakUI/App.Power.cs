using System.Runtime.InteropServices;

namespace StreamTweak
{
    // Host power-off (remote SHUTDOWN / SHUTDOWN_UPDATE from an approved StreamLight client).
    // Split out of App.xaml.cs: pure Win32 plumbing, no shared streaming state.
    public partial class App
    {
        // ── Host power-off (Win32) ───────────────────────────────────────────────
        // Enables SeShutdownPrivilege on the current process token, then requests a
        // full power-off. When installUpdates is true, uses InitiateShutdown with
        // SHUTDOWN_INSTALL_UPDATES ("Update and shut down") so any pending Windows
        // updates are installed before the machine powers off; otherwise uses the plain
        // ExitWindowsEx path. Falls back to the `shutdown` CLI if the Win32 path fails.
        private static void ShutdownHost(bool installUpdates = false)
        {
            try
            {
                if (installUpdates && TryEnableShutdownPrivilege())
                {
                    // SHUTDOWN_FORCE_SELF: guarantee our own session is logged off (no
                    // interactive prompt on a headless host). Planned reason avoids the
                    // unplanned-shutdown state-file delay (see InitiateShutdown docs).
                    uint rc = InitiateShutdownW(null, null, 0,
                        SHUTDOWN_INSTALL_UPDATES | SHUTDOWN_POWEROFF | SHUTDOWN_FORCE_SELF,
                        SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_FLAG_PLANNED);
                    if (rc == ERROR_SUCCESS)
                        return;

                    // Fall through to a plain power-off so the host still shuts down even
                    // if the update path is refused (updates just won't be installed).
                    DebugLogger.Log($"[Shutdown] InitiateShutdown(install updates) failed (rc {rc}); falling back to plain power-off");
                }

                if (TryEnableShutdownPrivilege() &&
                    ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, SHTDN_REASON_MAJOR_OTHER))
                    return;

                DebugLogger.Log($"[Shutdown] ExitWindowsEx failed (err {Marshal.GetLastWin32Error()}); falling back to shutdown.exe");
            }
            catch (Exception ex) { DebugLogger.Log($"[Shutdown] Win32 path threw: {ex}"); }

            try
            {
                // Full System32 path (not bare "shutdown") so the elevated/Win32 fallback
                // can't be redirected by a hijacked PATH.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    Arguments = "/s /t 0",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex) { DebugLogger.Log($"[Shutdown] shutdown.exe fallback failed: {ex}"); }
        }

        private static bool TryEnableShutdownPrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out LUID luid))
                    return false;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;

                // AdjustTokenPrivileges returns true even if not all privileges were
                // assigned — ERROR_SUCCESS confirms SeShutdown was actually enabled.
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }

        private const uint EWX_SHUTDOWN           = 0x00000001;
        private const uint EWX_POWEROFF           = 0x00000008;
        private const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;

        // InitiateShutdown flags / reason for the "Update and shut down" path.
        private const uint SHUTDOWN_FORCE_SELF        = 0x00000002;
        private const uint SHUTDOWN_POWEROFF          = 0x00000008;
        private const uint SHUTDOWN_INSTALL_UPDATES   = 0x00000040;
        private const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;
        private const uint SHTDN_REASON_FLAG_PLANNED  = 0x80000000;
        private const uint ERROR_SUCCESS              = 0;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY             = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED    = 0x00000002;
        private const string SE_SHUTDOWN_NAME      = "SeShutdownPrivilege";

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint InitiateShutdownW(
            string? lpMachineName, string? lpMessage, uint dwGracePeriod, uint dwShutdownFlags, uint dwReason);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);
    }
}
