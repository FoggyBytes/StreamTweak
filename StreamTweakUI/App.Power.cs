using System.Runtime.InteropServices;

namespace StreamTweak
{
    // Host power actions from an approved StreamLight client: SHUTDOWN / SHUTDOWN_UPDATE, and
    // since 8.6.0 POWER <sleep|hibernate|restart|shutdown> [UPDATE].
    // Split out of App.xaml.cs: pure Win32 plumbing, no shared streaming state.
    public partial class App
    {
        // ── Host power-off (Win32) ───────────────────────────────────────────────
        // Enables SeShutdownPrivilege on the current process token, then requests a
        // full power-off through InitiateShutdown — with SHUTDOWN_INSTALL_UPDATES ("Update
        // and shut down") when installUpdates is true, so pending Windows updates are installed
        // first, and always with SHUTDOWN_ARSO (10.1.0). Then ExitWindowsEx, then the
        // `shutdown` CLI, each with its own ARSO flag.
        private static void ShutdownHost(bool installUpdates = false)
        {
            try
            {
                if (TryEnableShutdownPrivilege())
                {
                    // SHUTDOWN_FORCE_SELF: guarantee our own session is logged off (no
                    // interactive prompt on a headless host). Planned reason avoids the
                    // unplanned-shutdown state-file delay (see InitiateShutdown docs).
                    // 10.1.0: InitiateShutdown for the plain power-off too, for SHUTDOWN_ARSO.
                    uint rc = InitiateShutdownW(null, null, 0,
                        SHUTDOWN_POWEROFF | SHUTDOWN_FORCE_SELF | SHUTDOWN_ARSO
                        | (installUpdates ? SHUTDOWN_INSTALL_UPDATES : 0),
                        SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_FLAG_PLANNED);
                    if (rc == ERROR_SUCCESS)
                        return;

                    // Fall through to a plain power-off so the host still shuts down even
                    // if the update path is refused (updates just won't be installed).
                    DebugLogger.Log($"[Shutdown] InitiateShutdown({(installUpdates ? "install updates" : "power off")}) failed (rc {rc}); falling back to ExitWindowsEx");
                }

                if (TryEnableShutdownPrivilege() &&
                    ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF | EWX_ARSO, SHTDN_REASON_MAJOR_OTHER))
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
                    // /sg: shut down and, on the next boot, sign in and lock (ARSO) — 10.1.0.
                    Arguments = "/sg /t 0",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex) { DebugLogger.Log($"[Shutdown] shutdown.exe fallback failed: {ex}"); }
        }

        // ── Host restart (8.6.0) ─────────────────────────────────────────────────
        // The twin of ShutdownHost: InitiateShutdown with SHUTDOWN_RESTART (plus
        // SHUTDOWN_INSTALL_UPDATES for "Update and restart"), then ExitWindowsEx(EWX_REBOOT),
        // then shutdown.exe /r.
        private static void RestartHost(bool installUpdates = false)
        {
            try
            {
                if (TryEnableShutdownPrivilege())
                {
                    uint flags = SHUTDOWN_RESTART | SHUTDOWN_FORCE_SELF | SHUTDOWN_ARSO
                               | (installUpdates ? SHUTDOWN_INSTALL_UPDATES : 0);
                    uint rc = InitiateShutdownW(null, null, 0, flags,
                        SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_FLAG_PLANNED);
                    if (rc == ERROR_SUCCESS)
                        return;
                    DebugLogger.Log($"[Power] InitiateShutdown(restart{(installUpdates ? ", install updates" : "")}) failed (rc {rc}); trying ExitWindowsEx");

                    if (ExitWindowsEx(EWX_REBOOT | EWX_ARSO, SHTDN_REASON_MAJOR_OTHER))
                        return;
                    DebugLogger.Log($"[Power] ExitWindowsEx(reboot) failed (err {Marshal.GetLastWin32Error()}); falling back to shutdown.exe");
                }
            }
            catch (Exception ex) { DebugLogger.Log($"[Power] restart Win32 path threw: {ex}"); }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    // /g: restart and sign in and lock (ARSO) — 10.1.0.
                    Arguments = "/g /t 0",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex) { DebugLogger.Log($"[Power] shutdown.exe /r fallback failed: {ex}"); }
        }

        // ── Host sleep / hibernation (8.6.0) ─────────────────────────────────────
        // Decided from what the machine reports NOW (HostPowerCapabilities), never from what a
        // particular host is known to have:
        //   • hibernate                → SetSuspendState(TRUE)
        //   • sleep with S1-S3         → SetSuspendState(FALSE)
        //   • sleep on Modern Standby  → display off. S0 low-power idle has no suspend call;
        //     Windows enters it when the screen goes off, which SC_MONITORPOWER does. The same
        //     is used if SetSuspendState refuses on a machine that also reports AoAc.
        // Wake events stay enabled (third argument FALSE): the whole point is to be woken.
        private static void SuspendHost(bool hibernate)
        {
            var states = HostPowerCapabilities.ReadStates();
            DebugLogger.Log($"[Power] {(hibernate ? "hibernate" : "sleep")} — S1 {states.S1}, S2 {states.S2}, S3 {states.S3}, "
                          + $"S4 {states.S4}, hiberfile {states.HiberFilePresent}, AoAc {states.AoAc}");
            try
            {
                if (hibernate || states.HasSuspend)
                {
                    if (TryEnableShutdownPrivilege() && SetSuspendState(hibernate, false, false))
                        return;   // returns after the machine has RESUMED
                    DebugLogger.Log($"[Power] SetSuspendState({hibernate}) failed (err {Marshal.GetLastWin32Error()})");
                    if (hibernate || !states.AoAc)
                        return;
                }

                if (states.AoAc)
                {
                    SendMessageTimeoutW(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_OFF,
                                        SMTO_ABORTIFHUNG, 2000, out _);
                    DebugLogger.Log("[Power] Modern Standby: display turned off");
                }
            }
            catch (Exception ex) { DebugLogger.Log($"[Power] suspend threw: {ex}"); }
        }

        // ── Suspend / resume notifications (8.6.0) ───────────────────────────────
        // A callback registration, so it needs no window and no message pump. The delegate is
        // held in a static field: the OS keeps only a raw pointer to it, and a collected
        // delegate would crash the process on the next sleep.
        private static PowerCallback? _powerCallback;
        private static Action<string>? _powerSink;
        private static IntPtr _powerNotifyHandle;

        private static void RegisterPowerTrace(Action<string> sink)
        {
            if (_powerNotifyHandle != IntPtr.Zero) return;
            _powerSink = sink;
            _powerCallback = (context, type, setting) =>
            {
                try
                {
                    switch (type)
                    {
                        case PBT_APMSUSPEND:         _powerSink?.Invoke("Suspend"); break;
                        case PBT_APMRESUMEAUTOMATIC: _powerSink?.Invoke("Resume"); break;
                    }
                }
                catch { }
                return 0;
            };
            var p = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_powerCallback),
                Context = IntPtr.Zero,
            };
            uint rc = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, ref p, out _powerNotifyHandle);
            if (rc != ERROR_SUCCESS)
                DebugLogger.Log($"[Power] suspend/resume notification not registered (rc {rc})");
        }

        private static void UnregisterPowerTrace()
        {
            if (_powerNotifyHandle == IntPtr.Zero) return;
            try { PowerUnregisterSuspendResumeNotification(_powerNotifyHandle); } catch { }
            _powerNotifyHandle = IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { public IntPtr Callback; public IntPtr Context; }

        private const uint DEVICE_NOTIFY_CALLBACK  = 2;
        private const uint PBT_APMSUSPEND          = 0x0004;
        private const uint PBT_APMRESUMEAUTOMATIC  = 0x0012;

        [DllImport("powrprof.dll")]
        private static extern uint PowerRegisterSuspendResumeNotification(uint flags,
            ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

        [DllImport("powrprof.dll")]
        private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

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
        private const uint EWX_REBOOT             = 0x00000002;
        private const uint SHUTDOWN_RESTART       = 0x00000004;

        private static readonly IntPtr HWND_BROADCAST = new(0xffff);
        private const uint WM_SYSCOMMAND   = 0x0112;
        private static readonly IntPtr SC_MONITORPOWER = new(0xF170);
        private static readonly IntPtr MONITOR_OFF     = new(2);
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.U1)] bool bHibernate,
            [MarshalAs(UnmanagedType.U1)] bool bForce,
            [MarshalAs(UnmanagedType.U1)] bool bWakeupEventsDisabled);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        // InitiateShutdown flags / reason for the "Update and shut down" path.
        private const uint SHUTDOWN_FORCE_SELF        = 0x00000002;
        private const uint SHUTDOWN_POWEROFF          = 0x00000008;
        private const uint SHUTDOWN_INSTALL_UPDATES   = 0x00000040;
        // 10.1.0: "sign in and lock the last interactive user" on the next boot (Automatic Restart
        // Sign-On), asked for explicitly — what the Start menu's Shut down does. Without it Windows
        // chose by itself, and on 02/11 and 03/11/2026 it chose not to: no session after the WOL,
        // so no StreamTweak, so no PIN pad (Marcello). Honoured only when ARSO is on for the user.
        private const uint SHUTDOWN_ARSO              = 0x00002000;
        private const uint EWX_ARSO                   = 0x04000000;
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
