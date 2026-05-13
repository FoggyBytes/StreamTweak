using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StreamTweak
{
    /// <summary>
    /// Shared logic for killing and relaunching managed apps.
    /// Used both by AppsViewModel (manual kill/relaunch buttons) and App.xaml.cs (automation).
    /// </summary>
    public static class ManagedAppController
    {
        private static readonly string _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "managedapps.json");

        private static List<ManagedApp> Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return new();
                return JsonSerializer.Deserialize<List<ManagedApp>>(File.ReadAllText(_filePath)) ?? new();
            }
            catch { return new(); }
        }

        /// <summary>
        /// Kills every managed app that is currently running.
        /// Returns the paths of apps that were actually running, for later relaunch.
        /// Silent — never throws or shows UI.
        /// </summary>
        public static List<string> KillRunning()
        {
            var killed = new List<string>();
            foreach (var app in Load().Where(a => a.AutoManage))
            {
                try
                {
                    if (KillAllByPath(app.Path))
                        killed.Add(app.Path);
                }
                catch { }
            }
            return killed;
        }

        /// <summary>
        /// Starts each app in the given path list.
        /// Silent — best-effort, never throws.
        /// </summary>
        public static void StartApps(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        });
                }
                catch { }
            }
        }

        /// <summary>
        /// Kills every process matching the given executable path.
        /// Falls back to a full-process scan when the reported process name differs
        /// from the filename (e.g. Electron wrappers, renamed hosts).
        /// Returns true if at least one process was found.
        /// Throws Win32Exception on access-denied so callers can handle it appropriately.
        /// </summary>
        public static bool KillAllByPath(string exePath)
        {
            string exeName   = Path.GetFileName(exePath);
            string nameNoExt = Path.GetFileNameWithoutExtension(exePath);

            var procs = System.Diagnostics.Process.GetProcessesByName(nameNoExt);

            if (procs.Length == 0)
            {
                procs = System.Diagnostics.Process.GetProcesses()
                    .Where(p =>
                    {
                        try
                        {
                            return string.Equals(
                                Path.GetFileName(p.MainModule?.FileName ?? string.Empty),
                                exeName,
                                StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    })
                    .ToArray();
            }

            if (procs.Length == 0) return false;

            foreach (var proc in procs)
            {
                try { proc.Kill(); } catch { }
                proc.Dispose();
            }

            return true;
        }
    }
}
