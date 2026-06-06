using System;
using Microsoft.Win32;

namespace StreamTweak
{
    /// <summary>
    /// Lightweight, read-only probe for "are there Windows updates waiting that a
    /// shutdown would install/finalize?". Used by the bridge's UPDATESTATE command so
    /// an approved StreamLight client can show the user whether "install updates before
    /// power-off" has anything to do.
    ///
    /// Detection is the *solid* registry signal only — the two keys Windows sets when an
    /// update has been installed and is pending a reboot. It deliberately does NOT try to
    /// detect "downloaded but not yet installed" updates (no clean API for that), so a
    /// false here does not mean "nothing to install": SHUTDOWN_INSTALL_UPDATES may still
    /// install staged updates at shutdown. The client therefore keeps the option always
    /// available and uses this only as an informational hint.
    /// </summary>
    public static class WindowsUpdateState
    {
        // Set by Component Based Servicing once an update is installed and needs a reboot.
        private const string CbsRebootPending =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

        // Set by Windows Update when an installed update requires a restart.
        private const string WuRebootRequired =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

        /// <summary>
        /// True when at least one installed update is waiting for a reboot to finalize.
        /// Reads HKLM (64-bit view); never throws.
        /// </summary>
        public static bool IsRebootPending()
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                return KeyExists(hklm, CbsRebootPending) || KeyExists(hklm, WuRebootRequired);
            }
            catch
            {
                return false;
            }
        }

        private static bool KeyExists(RegistryKey baseKey, string subPath)
        {
            using var k = baseKey.OpenSubKey(subPath);
            return k != null;
        }

        /// <summary>Compact JSON for the UPDATESTATE bridge response: {"pending":true|false}.</summary>
        public static string ToJson() => IsRebootPending() ? "{\"pending\":true}" : "{\"pending\":false}";
    }
}
