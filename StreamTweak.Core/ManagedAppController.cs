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

        // 10.0.0: the bridge and the Apps page both write the file now; one lock for both.
        private static readonly object _fileLock = new();

        /// <summary>Raised (any thread) after the file changed through <see cref="SetAutoManage"/>,
        /// so a page holding its own copy of the list reloads instead of overwriting the change.</summary>
        public static event Action? Changed;

        private static List<ManagedApp> Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return new();
                return JsonSerializer.Deserialize<List<ManagedApp>>(File.ReadAllText(_filePath)) ?? new();
            }
            catch { return new(); }
        }

        /// <summary>The managed-app list as it is on disk.</summary>
        public static List<ManagedApp> List()
        {
            lock (_fileLock) return Load();
        }

        /// <summary>
        /// A stable, opaque id for an app (10.0.0): 16 hex digits of SHA-256 over the lowercased
        /// path. It is what StreamLight names an app by on the bridge — hex because the bridge
        /// upper-cases the command line, and so that no path ever travels back to the host.
        /// </summary>
        public static string IdOf(ManagedApp app) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(app.Path.Trim().ToLowerInvariant())))[..16];

        /// <summary>Turns an app's management on or off, found by <see cref="IdOf"/>. False when
        /// no app has that id. Only the flag changes: the list itself is edited on the host.</summary>
        public static bool SetAutoManage(string id, bool enabled)
        {
            lock (_fileLock)
            {
                var list = Load();
                var app = list.FirstOrDefault(a => string.Equals(IdOf(a), id, StringComparison.OrdinalIgnoreCase));
                if (app == null) return false;
                if (app.AutoManage != enabled)
                {
                    app.AutoManage = enabled;
                    Save(list);
                }
            }
            Changed?.Invoke();
            return true;
        }

        /// <summary>Writes the whole list (the Apps page). Same lock as the bridge's writes.</summary>
        public static void SaveAll(List<ManagedApp> list)
        {
            lock (_fileLock) Save(list);
        }

        private static void Save(List<ManagedApp> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { }
        }

        /// <summary>
        /// Kills every managed app that is currently running.
        /// Returns the paths of apps that were actually running, for later relaunch.
        /// Silent — never throws or shows UI.
        /// </summary>
        public static List<string> KillRunning()
        {
            var killed = new List<string>();
            foreach (var app in List().Where(a => a.AutoManage))
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
