using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace StreamTweak
{
    /// <summary>How StreamTweak is launched when the user signs in.</summary>
    public enum StartupMode
    {
        /// <summary>Not launched automatically at all.</summary>
        Off = 0,
        /// <summary>Launched from the HKCU Run key, like any other startup app.</summary>
        Normal = 1,
        /// <summary>Launched by a scheduled task on the logon event, ahead of the Run queue.</summary>
        Priority = 2,
    }

    /// <summary>
    /// Single source of truth for "how does this app start". Owns BOTH autostart mechanisms so
    /// they cannot drift out of step: exactly one of them is ever installed.
    ///
    /// <para><b>Why a scheduled task buys anything.</b> The Run key is not launched by Windows at
    /// logon — it is launched by Explorer, after the shell has initialised, delayed and staggered
    /// across the startup apps. Measured on the developer host (05/09/2026, cold boot): logon at
    /// +26 s, explorer at +26,8 s, and StreamTweakUI at <b>+56 s</b> — fifth of six in the queue,
    /// with the bridge answering on 47998 at +57,9 s. Every no-delay logon <i>task</i> on the same
    /// boot ran at +26,5 s, a fraction of a second <i>before</i> explorer. So the task reclaims
    /// about 29 s, which is the whole of the gap: the streaming server (a service) was already up
    /// at +25 s, so a client that had woken the host by WOL sat waiting on StreamTweak alone.
    ///
    /// <para><b>What it cannot buy.</b> This is a user-session process; the bridge cannot answer
    /// before a session exists. Priority mode reclaims Explorer's queue, never the boot and logon
    /// ahead of it. On a host where Automatic Restart Sign-On is off, a cold boot parks at the
    /// logon screen with no session at all and nothing here helps — see LockState.cs.</para>
    ///
    /// <para><b>Why COM and not a NuGet package.</b> Same reason as WindowsUpdateManager: late
    /// binding through the Schedule.Service ProgID keeps the Windows-targeted TFM compiling on the
    /// Linux CI runners, which a TaskScheduler interop reference would not (CLAUDE.md §7).</para>
    ///
    /// <para>Registration needs no elevation: Authenticated Users hold Write on
    /// <c>%WINDIR%\System32\Tasks</c>, and the task runs as the current user with an interactive
    /// token, so no password and no admin rights are involved — matching the tray app's standing
    /// rule that it never asks for UAC.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class StartupModeManager
    {
        private const string RunKey       = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "StreamTweak";

        /// <summary>Task name, at the root folder. The uninstaller deletes this exact name.</summary>
        public const string TaskName = "StreamTweak Startup";

        /// <summary>The argument both mechanisms pass — no window, tray only.</summary>
        private const string LaunchArgs = "--minimized";

        // ── Task Scheduler 2.0 constants (late-bound, so they are not imported) ──────
        private const int TASK_TRIGGER_LOGON            = 9;
        private const int TASK_ACTION_EXEC              = 0;
        private const int TASK_CREATE_OR_UPDATE         = 6;
        private const int TASK_LOGON_INTERACTIVE_TOKEN  = 3;
        private const int TASK_RUNLEVEL_LUA             = 0;
        private const int TASK_INSTANCES_IGNORE_NEW     = 2;

        // ── Reading ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The mode currently installed on this machine, read from the mechanisms themselves
        /// rather than from config — the user can remove either one behind our back (Task
        /// Scheduler, Task Manager's Startup tab, regedit) and the UI must show the truth.
        /// </summary>
        public static StartupMode Current
        {
            get
            {
                if (TaskExists()) return StartupMode.Priority;
                return RunValueExists() ? StartupMode.Normal : StartupMode.Off;
            }
        }

        private static bool RunValueExists()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        private static bool TaskExists() => ReadTaskExePath() != null;

        /// <summary>
        /// The exe path the registered task launches, or null when there is no enabled task.
        /// A task that exists but has been disabled counts as absent: it would not run, and
        /// reporting Priority for it would leave the user with no autostart and a UI saying
        /// otherwise.
        /// </summary>
        private static string? ReadTaskExePath()
        {
            try
            {
                dynamic? svc = CreateService();
                if (svc == null) return null;

                dynamic folder = svc.GetFolder("\\");
                dynamic task   = folder.GetTask(TaskName);   // throws when missing
                if (!(bool)task.Enabled) return null;

                // Actions is a 1-based COM collection. Item(1) rather than [1]: the explicit
                // accessor is the one verified against the live API, and late binding gives no
                // compile-time warning if the indexer form fails to resolve.
                dynamic actions = task.Definition.Actions;
                if ((int)actions.Count < 1) return null;
                return (string)actions.Item(1).Path;
            }
            catch
            {
                // Missing task, Task Scheduler service stopped, COM denied — all mean
                // "no priority task in force", which is the safe reading.
                return null;
            }
        }

        private static dynamic? CreateService()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;
            dynamic? svc = Activator.CreateInstance(type);
            svc?.Connect();
            return svc;
        }

        // ── Writing ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Installs <paramref name="mode"/> and removes the other mechanism, so the two can never
        /// both be live. Returns false with <paramref name="error"/> set when the requested mode
        /// could not be installed; in that case nothing is torn down, so the previous mode keeps
        /// working rather than leaving the app with no autostart at all.
        /// </summary>
        public static bool TryApply(StartupMode mode, out string error)
        {
            error = string.Empty;

            string exe = Environment.ProcessPath ?? string.Empty;
            if (mode != StartupMode.Off && string.IsNullOrEmpty(exe))
            {
                error = "Could not determine the StreamTweak executable path.";
                return false;
            }

            try
            {
                // Install first, tear down second. The reverse order would leave a window in
                // which a failure means no autostart at all.
                switch (mode)
                {
                    case StartupMode.Priority:
                        RegisterTask(exe);
                        RemoveRunValue();
                        break;

                    case StartupMode.Normal:
                        WriteRunValue(exe);
                        DeleteTask();
                        break;

                    case StartupMode.Off:
                        RemoveRunValue();
                        DeleteTask();
                        break;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                DebugLogger.Log($"[Startup] apply {mode} failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Re-points the task at the running executable when they disagree. The task stores an
        /// absolute path, so reinstalling to a different folder would otherwise leave it launching
        /// an exe that no longer exists — failing silently at every logon. Cheap and a no-op
        /// outside Priority mode; call it once at startup.
        /// </summary>
        public static void SyncTaskExePath()
        {
            try
            {
                string? registered = ReadTaskExePath();
                if (registered == null) return;             // not in Priority mode

                string exe = Environment.ProcessPath ?? string.Empty;
                if (string.IsNullOrEmpty(exe)) return;
                if (string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)) return;

                DebugLogger.Log($"[Startup] task points at '{registered}', re-registering for '{exe}'");
                RegisterTask(exe);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Startup] path sync failed: {ex.Message}");
            }
        }

        // ── Run key ─────────────────────────────────────────────────────────────────

        private static void WriteRunValue(string exe)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            key?.SetValue(RunValueName, $"\"{exe}\" {LaunchArgs}");
        }

        private static void RemoveRunValue()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Startup] could not remove Run value: {ex.Message}");
            }
        }

        // ── Scheduled task ──────────────────────────────────────────────────────────

        /// <summary>
        /// Creates or replaces the logon task. Every setting below that is assigned explicitly is
        /// there because the Task Scheduler default would break this feature in a way that only
        /// shows up days later or on someone else's machine — read the comments before trimming.
        /// </summary>
        private static void RegisterTask(string exe)
        {
            dynamic? svc = CreateService()
                           ?? throw new InvalidOperationException("The Task Scheduler service is not available.");

            string user = WindowsIdentity.GetCurrent().Name;

            dynamic td = svc.NewTask(0);

            td.RegistrationInfo.Author      = "FoggyBytes";
            td.RegistrationInfo.Description =
                "Starts StreamTweak at sign-in, ahead of the Windows startup queue, so a client "
                + "waking this host can reach it sooner. Managed from StreamTweak's Settings page.";

            td.Principal.UserId    = user;
            td.Principal.LogonType = TASK_LOGON_INTERACTIVE_TOKEN;
            // Never "highest privileges": the tray app is deliberately non-elevated, and an
            // elevated copy launched without a UAC prompt would change that for no benefit.
            td.Principal.RunLevel  = TASK_RUNLEVEL_LUA;

            dynamic s = td.Settings;
            s.Enabled              = true;
            s.Hidden               = false;
            s.MultipleInstances    = TASK_INSTANCES_IGNORE_NEW;

            // Default is 3 days, after which Task Scheduler TERMINATES the app. A host left on
            // for a long weekend would lose StreamTweak with no explanation. PT0S = no limit.
            s.ExecutionTimeLimit   = "PT0S";

            // Both default to true, which would stop the task from ever running on a laptop host
            // and kill it the moment one was unplugged.
            s.DisallowStartIfOnBatteries = false;
            s.StopIfGoingOnBatteries     = false;

            // Nothing here may hold the launch back: idle state, network readiness and a
            // missed-logon catch-up run are all irrelevant to a tray app that must be early.
            s.RunOnlyIfIdle             = false;
            s.RunOnlyIfNetworkAvailable = false;
            s.StartWhenAvailable        = false;
            s.WakeToRun                 = false;
            s.IdleSettings.StopOnIdleEnd = false;

            // The one that would quietly defeat the whole feature: task priority defaults to 7,
            // which launches the process at BELOW_NORMAL — worse than the Run key it replaces,
            // and during the boot storm that is exactly when it hurts. 5 is NORMAL.
            s.Priority = 5;

            dynamic trigger = td.Triggers.Create(TASK_TRIGGER_LOGON);
            trigger.Id      = "AtLogon";
            trigger.Enabled = true;
            trigger.UserId  = user;   // this user's logon only, not everyone's
            trigger.Delay   = "PT0S";

            dynamic action = td.Actions.Create(TASK_ACTION_EXEC);
            action.Path             = exe;
            action.Arguments        = LaunchArgs;
            action.WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;

            dynamic folder = svc.GetFolder("\\");
            folder.RegisterTaskDefinition(
                TaskName,
                td,
                TASK_CREATE_OR_UPDATE,
                null,                            // user  — taken from the principal
                null,                            // password — none, interactive token
                TASK_LOGON_INTERACTIVE_TOKEN,
                null);                           // sddl — inherit

            DebugLogger.Log($"[Startup] registered logon task '{TaskName}' for '{exe}'");
        }

        /// <summary>Removes the task. Silent when it was never there.</summary>
        public static void DeleteTask()
        {
            try
            {
                dynamic? svc = CreateService();
                if (svc == null) return;
                dynamic folder = svc.GetFolder("\\");
                folder.DeleteTask(TaskName, 0);
                DebugLogger.Log($"[Startup] deleted logon task '{TaskName}'");
            }
            catch
            {
                // Missing task is the common case and is not an error.
            }
        }
    }
}
