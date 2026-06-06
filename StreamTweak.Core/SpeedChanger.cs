using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace StreamTweak
{
    public static class SpeedChanger
    {
        private const string ServiceName = "StreamTweakService";
        private const string PipeName = "StreamTweakService";
        private const int PipeTimeoutMs = 5000;

        /// <summary>
        /// Sends the speed change command to the StreamTweakService via Named Pipe.
        /// The service runs as LocalSystem and executes the change without UAC.
        /// </summary>
        public static bool Apply(string adapterName, string registryValue)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(PipeTimeoutMs);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                string json = JsonSerializer.Serialize(new
                {
                    Command = "SetSpeed",
                    AdapterName = adapterName,
                    RegistryValue = registryValue
                });

                writer.WriteLine(json);

                string? response = reader.ReadLine();
                return response == "OK";
            }
            catch { return false; }
        }

        /// <summary>
        /// Asks the StreamTweakService (LocalSystem) to write content to an apps.json path
        /// that may be inside a protected directory (e.g. C:\Program Files\Sunshine\config\).
        /// Returns true on success; false if the service is unavailable or the write fails.
        /// </summary>
        public static bool WriteAppsJson(string path, string jsonContent)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(PipeTimeoutMs);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                string json = JsonSerializer.Serialize(new
                {
                    Command = "WriteFile",
                    Path    = path,
                    Content = jsonContent
                });

                writer.WriteLine(json);

                string? response = reader.ReadLine();
                return response == "OK";
            }
            catch { return false; }
        }

        /// <summary>
        /// Asks the StreamTweakService (LocalSystem) to back up the host's default
        /// desktop.png / steam.png in <paramref name="assetsDir"/> and copy custom
        /// replacements over them.
        /// </summary>
        public static bool SwapHostAssets(string assetsDir, string desktopSource, string steamSource)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(PipeTimeoutMs);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                string json = JsonSerializer.Serialize(new
                {
                    Command       = "SwapAssets",
                    AssetsDir     = assetsDir,
                    DesktopSource = desktopSource,
                    SteamSource   = steamSource
                });
                writer.WriteLine(json);

                string? response = reader.ReadLine();
                return response == "OK";
            }
            catch { return false; }
        }

        /// <summary>
        /// Asks the StreamTweakService (LocalSystem) to delete the custom tile PNGs
        /// in <paramref name="assetsDir"/> and rename desktop_backup.png / steam_backup.png
        /// back to their original names.
        /// </summary>
        public static bool RestoreHostAssets(string assetsDir)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(PipeTimeoutMs);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                string json = JsonSerializer.Serialize(new
                {
                    Command   = "RestoreAssets",
                    AssetsDir = assetsDir
                });
                writer.WriteLine(json);

                string? response = reader.ReadLine();
                return response == "OK";
            }
            catch { return false; }
        }

        // ── Windows Update relay (remote "Update host" feature) ──────────────
        // The privileged WUA work runs in the LocalSystem service; these just relay
        // start/poll commands over the pipe. Fire-and-forget for start, JSON snapshot
        // for poll.

        /// <summary>Asks the service to start an async Windows-update scan.</summary>
        public static bool StartUpdateCheck() => SendSimple("UpdateCheck", null);

        /// <summary>Asks the service to start downloading+installing the scanned updates
        /// filtered by scope ("SEC" or "ALL"), rebooting if required.</summary>
        public static bool StartUpdateInstall(string scope) => SendSimple("UpdateInstall", scope);

        /// <summary>Polls the service for the current update job state (JSON).
        /// Returns an IDLE snapshot if the service is unreachable.</summary>
        public static string GetUpdateProgress()
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(PipeTimeoutMs);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                writer.WriteLine(JsonSerializer.Serialize(new { Command = "UpdateProgress" }));
                string? response = reader.ReadLine();
                return string.IsNullOrWhiteSpace(response) || response.StartsWith("ERROR")
                    ? "{\"phase\":\"IDLE\"}"
                    : response;
            }
            catch { return "{\"phase\":\"IDLE\"}"; }
        }

        private static bool SendSimple(string command, string? scope)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(PipeTimeoutMs);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                writer.WriteLine(JsonSerializer.Serialize(new { Command = command, Scope = scope }));
                return reader.ReadLine() == "OK";
            }
            catch { return false; }
        }

        /// <summary>
        /// Fallback for environments without the service installed (e.g. development).
        /// Launches PowerShell with Verb = "runas" — triggers a UAC prompt.
        /// </summary>
        public static bool ApplyWithUac(string adapterName, string registryValue)
        {
            // Escape single quotes for PowerShell string literals (double them up).
            string safeAdapterName    = adapterName.Replace("'", "''");
            string safeRegistryValue  = registryValue.Replace("'", "''");

            // Use an unguessable filename in a dedicated subfolder, created exclusively,
            // so another local user cannot pre-create or symlink the script path before
            // it is executed elevated (TOCTOU hardening).
            string tempDir = Path.Combine(Path.GetTempPath(), "StreamTweak");
            Directory.CreateDirectory(tempDir);
            string tempScript = Path.Combine(tempDir, Path.GetRandomFileName() + ".ps1");
            string psScript = $@"
$adapterName = '{safeAdapterName}'
$registryValue = '{safeRegistryValue}'
Set-NetAdapterAdvancedProperty -Name $adapterName -RegistryKeyword '*SpeedDuplex' -RegistryValue $registryValue -NoRestart
Restart-NetAdapter -Name $adapterName -Confirm:$false
";
            try
            {
                using (var fs = new FileStream(tempScript, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs))
                    sw.Write(psScript);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit();
                return true;
            }
            catch { return false; }
            finally
            {
                if (File.Exists(tempScript))
                    try { File.Delete(tempScript); } catch { }
            }
        }
    }
}