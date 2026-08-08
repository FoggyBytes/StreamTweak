using System;
using System.Diagnostics;

namespace StreamTweak
{
    /// <summary>
    /// Lightweight, read-only probe for "is this host sitting at a lock or logon screen?".
    /// Used by the bridge's LOCKSTATE command so an approved StreamLight client can decide
    /// whether it needs to show its PIN pad at all — and, after sending a PIN, whether the
    /// attempt worked.
    ///
    /// <para><b>Why LogonUI and not an API.</b> The obvious candidates both fall short here.
    /// <c>WTSConnectState</c> does NOT change when a session is locked — measured 04/08/2026,
    /// it stays <c>Active</c> either way — so it cannot answer this question at all. Session
    /// change events (<c>SystemEvents.SessionSwitch</c>) would work, but only for transitions
    /// that happen while we are running: with Automatic Restart Sign-On the host signs in and
    /// locks itself before StreamTweakUI has even started, so the one state that matters most
    /// (freshly woken by WOL, sitting at the lock screen) would never raise an event. A direct
    /// probe has no such blind spot, needs no subscription to keep alive, and pulls in no new
    /// dependency.</para>
    ///
    /// <para><b>What it actually measures.</b> <c>LogonUI.exe</c> exists exactly while the
    /// secure logon UI is on screen — lock screen, logon screen, user switch — and exits
    /// within about a second of unlocking. Verified against a real cold boot: present from
    /// first sample through the whole wait, gone one second after the PIN went in.</para>
    /// </summary>
    public static class LockState
    {
        /// <summary>
        /// True when a lock or logon screen is up in this process's own session.
        ///
        /// <para>The session check matters on a host with more than one signed-in user: another
        /// session sitting at its lock screen says nothing about ours, and reporting it as
        /// locked would send the client's PIN into a session it cannot see.</para>
        ///
        /// <para>Never throws: an unreadable process list answers "not locked", which degrades
        /// to "no PIN pad" rather than to a keypad typing into nothing.</para>
        /// </summary>
        public static bool IsLocked()
        {
            Process[]? found = null;
            try
            {
                int ownSession = Process.GetCurrentProcess().SessionId;
                found = Process.GetProcessesByName("LogonUI");

                foreach (var p in found)
                {
                    try
                    {
                        if (p.SessionId == ownSession) return true;
                    }
                    catch
                    {
                        // A process that vanished between enumeration and read; ignore it.
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (found != null)
                {
                    foreach (var p in found)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }

        /// <summary>Compact JSON for the LOCKSTATE bridge response.</summary>
        public static string ToJson() =>
            IsLocked() ? "{\"v\":1,\"locked\":true}" : "{\"v\":1,\"locked\":false}";
    }
}
