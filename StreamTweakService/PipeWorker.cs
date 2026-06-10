using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Management.Infrastructure;

namespace StreamTweakService;

public class PipeWorker : BackgroundService
{
    public const string PipeName = "StreamTweakService";
    private readonly ILogger<PipeWorker> _logger;

    public PipeWorker(ILogger<PipeWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StreamTweakService pipe worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Allow any authenticated local user to connect
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                // Create server without using — ownership is transferred to HandleClientAsync
                var server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    4096, 4096,
                    pipeSecurity);

                await server.WaitForConnectionAsync(stoppingToken);

                // Fire and forget — HandleClientAsync disposes the server stream
                _ = HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe server error — restarting in 1s");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("StreamTweakService pipe worker stopped.");
    }

    private static readonly string[] _allowedAppNames = { "Sunshine", "Apollo", "Vibeshine", "Vibepollo" };

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

                // Security gate: only StreamTweakUI.exe installed alongside this service
                // (i.e. in an admin-only directory) may issue commands. This blocks any
                // other local user's process from abusing the LocalSystem pipe — e.g. the
                // WriteFile command, which could otherwise plant a malicious apps.json.
                if (!IsAuthorizedClient(server))
                {
                    _logger.LogWarning("Rejected pipe client: caller is not the trusted StreamTweak UI.");
                    await writer.WriteLineAsync("ERROR:unauthorized");
                    return;
                }

                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line))
                {
                    await writer.WriteLineAsync("ERROR:empty command");
                    return;
                }

                PipeCommand? cmd;
                try
                {
                    cmd = JsonSerializer.Deserialize<PipeCommand>(line);
                }
                catch
                {
                    await writer.WriteLineAsync("ERROR:invalid json");
                    return;
                }

                if (cmd == null)
                {
                    await writer.WriteLineAsync("ERROR:missing fields");
                    return;
                }

                string commandType = cmd.Command?.ToUpperInvariant() ?? "SETSPEED";

                switch (commandType)
                {
                    case "SETSPEED":
                        if (string.IsNullOrWhiteSpace(cmd.AdapterName) || string.IsNullOrWhiteSpace(cmd.RegistryValue))
                        {
                            await writer.WriteLineAsync("ERROR:missing fields");
                            return;
                        }
                        _logger.LogInformation("Applying speed: adapter={Adapter} value={Value}", cmd.AdapterName, cmd.RegistryValue);
                        bool speedOk = ApplySpeedViaCim(cmd.AdapterName, cmd.RegistryValue)
                                    || ApplySpeedViaPowerShell(cmd.AdapterName, cmd.RegistryValue);
                        await writer.WriteLineAsync(speedOk ? "OK" : "ERROR:apply failed");
                        break;

                    case "WRITEFILE":
                        if (string.IsNullOrWhiteSpace(cmd.Path) || cmd.Content == null)
                        {
                            await writer.WriteLineAsync("ERROR:missing fields");
                            return;
                        }
                        if (!IsAllowedAppsJsonPath(cmd.Path))
                        {
                            await writer.WriteLineAsync("ERROR:path not allowed");
                            return;
                        }
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(cmd.Path)!);
                            File.WriteAllText(cmd.Path, cmd.Content, System.Text.Encoding.UTF8);
                            _logger.LogInformation("WriteFile OK: {Path}", cmd.Path);
                            await writer.WriteLineAsync("OK");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "WriteFile failed: {Path}", cmd.Path);
                            await writer.WriteLineAsync($"ERROR:{ex.Message}");
                        }
                        break;

                    case "SWAPASSETS":
                        if (string.IsNullOrWhiteSpace(cmd.AssetsDir) ||
                            string.IsNullOrWhiteSpace(cmd.DesktopSource) ||
                            string.IsNullOrWhiteSpace(cmd.SteamSource))
                        {
                            await writer.WriteLineAsync("ERROR:missing fields");
                            return;
                        }
                        if (!IsAllowedHostAssetsDir(cmd.AssetsDir))
                        {
                            await writer.WriteLineAsync("ERROR:path not allowed");
                            return;
                        }
                        try
                        {
                            SwapAssets(cmd.AssetsDir, cmd.DesktopSource, cmd.SteamSource);
                            _logger.LogInformation("SwapAssets OK: {Dir}", cmd.AssetsDir);
                            await writer.WriteLineAsync("OK");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "SwapAssets failed: {Dir}", cmd.AssetsDir);
                            await writer.WriteLineAsync($"ERROR:{ex.Message}");
                        }
                        break;

                    case "RESTOREASSETS":
                        if (string.IsNullOrWhiteSpace(cmd.AssetsDir))
                        {
                            await writer.WriteLineAsync("ERROR:missing fields");
                            return;
                        }
                        if (!IsAllowedHostAssetsDir(cmd.AssetsDir))
                        {
                            await writer.WriteLineAsync("ERROR:path not allowed");
                            return;
                        }
                        try
                        {
                            RestoreAssets(cmd.AssetsDir);
                            _logger.LogInformation("RestoreAssets OK: {Dir}", cmd.AssetsDir);
                            await writer.WriteLineAsync("OK");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "RestoreAssets failed: {Dir}", cmd.AssetsDir);
                            await writer.WriteLineAsync($"ERROR:{ex.Message}");
                        }
                        break;

                    case "UPDATECHECK":
                        WindowsUpdateManager.Instance.StartCheck();
                        await writer.WriteLineAsync("OK");
                        break;

                    case "UPDATEINSTALL":
                        if (string.IsNullOrWhiteSpace(cmd.Scope))
                        {
                            await writer.WriteLineAsync("ERROR:missing fields");
                            return;
                        }
                        WindowsUpdateManager.Instance.StartInstall(cmd.Scope);
                        await writer.WriteLineAsync("OK");
                        break;

                    case "UPDATEPROGRESS":
                        await writer.WriteLineAsync(WindowsUpdateManager.Instance.GetStateJson());
                        break;

                    default:
                        await writer.WriteLineAsync("ERROR:unknown command");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client handler error");
        }
    }

    /// <summary>
    /// Security check: only allow writing to apps.json files inside known streaming server directories.
    ///
    /// Only the machine-wide bases are listed. The per-user AppData folders are intentionally
    /// NOT here: this service runs as LocalSystem, so SpecialFolder.ApplicationData /
    /// LocalApplicationData resolve to the SYSTEM profile (…\config\systemprofile\AppData\…),
    /// never to the logged-in user's profile — so they could only ever match a SYSTEM-profile
    /// path, not the real user apps.json. Servers that keep apps.json under the user's AppData
    /// (e.g. Apollo / Vibeshine) are handled entirely in the UI process, which writes there
    /// directly (it owns that folder) when the service declines the path — see
    /// SunshineSync.Sync's WriteAppsJson → direct File.WriteAllText fallback.
    /// </summary>
    private static readonly string[] _allowedBasePaths =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    };

    private static bool IsAllowedAppsJsonPath(string path)
    {
        if (!path.EndsWith("apps.json", StringComparison.OrdinalIgnoreCase))
            return false;

        string normalized = Path.GetFullPath(path);

        bool hasAppName = _allowedAppNames.Any(app =>
            normalized.Contains(Path.DirectorySeparatorChar + app + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

        return hasAppName && IsUnderAllowedBase(normalized);
    }

    /// <summary>
    /// True when <paramref name="normalized"/> sits under one of the allowed base
    /// directories. The base must be followed by a directory separator (or be an exact
    /// match) so that a sibling like "C:\Program FilesEvil" cannot pass a naive
    /// StartsWith("C:\Program Files") prefix check.
    /// </summary>
    private static bool IsUnderAllowedBase(string normalized)
    {
        return _allowedBasePaths.Any(b =>
        {
            if (string.IsNullOrEmpty(b)) return false;
            string baseDir = b.TrimEnd(Path.DirectorySeparatorChar);
            return normalized.Equals(baseDir, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(baseDir + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Security check for host-tile asset operations:
    ///  - the directory must end with "\assets"
    ///  - must contain one of the known streaming-server app names as a directory component
    ///  - must sit under a known base path (Program Files / ProgramData / AppData)
    /// </summary>
    private static bool IsAllowedHostAssetsDir(string dir)
    {
        string normalized;
        try { normalized = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        if (!normalized.EndsWith(Path.DirectorySeparatorChar + "assets", StringComparison.OrdinalIgnoreCase))
            return false;

        bool hasAppName = _allowedAppNames.Any(app =>
            normalized.Contains(Path.DirectorySeparatorChar + app + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

        return hasAppName && IsUnderAllowedBase(normalized);
    }

    private static readonly string[] _tileFiles = { "desktop.png", "steam.png" };

    private static void SwapAssets(string assetsDir, string desktopSource, string steamSource)
    {
        if (!Directory.Exists(assetsDir))
            throw new DirectoryNotFoundException($"Assets directory not found: {assetsDir}");
        if (!File.Exists(desktopSource))
            throw new FileNotFoundException($"Source desktop tile not found: {desktopSource}");
        if (!File.Exists(steamSource))
            throw new FileNotFoundException($"Source steam tile not found: {steamSource}");

        var ops = new (string SourcePath, string TileName)[]
        {
            (desktopSource, "desktop.png"),
            (steamSource,   "steam.png"),
        };

        foreach (var (sourcePath, tileName) in ops)
        {
            string original = Path.Combine(assetsDir, tileName);
            string backup   = Path.Combine(assetsDir, Path.GetFileNameWithoutExtension(tileName) + "_backup.png");

            // Only create a backup if one isn't already present AND an original file exists.
            // Re-running Swap on an already-swapped directory must not overwrite the backup.
            if (File.Exists(original) && !File.Exists(backup))
                File.Move(original, backup);
            else if (File.Exists(original) && File.Exists(backup))
                File.Delete(original); // already-applied state: drop the current override before copying the new one

            File.Copy(sourcePath, original, overwrite: true);
        }
    }

    private static void RestoreAssets(string assetsDir)
    {
        if (!Directory.Exists(assetsDir))
            throw new DirectoryNotFoundException($"Assets directory not found: {assetsDir}");

        foreach (var tileName in _tileFiles)
        {
            string original = Path.Combine(assetsDir, tileName);
            string backup   = Path.Combine(assetsDir, Path.GetFileNameWithoutExtension(tileName) + "_backup.png");

            if (File.Exists(original))
                File.Delete(original);
            if (File.Exists(backup))
                File.Move(backup, original);
        }
    }

    // Primary method: use CIM (WMI) directly — no child process needed
    private bool ApplySpeedViaCim(string adapterName, string registryValue)
    {
        try
        {
            using var session = CimSession.Create(null);

            // Escape for WQL string literals: the escape character is the backslash,
            // so a backslash must be doubled and a single quote prefixed with a backslash.
            // (Doubling the quote is the SQL rule and is NOT correct for WQL.)
            string safeAdapterName = adapterName.Replace("\\", "\\\\").Replace("'", "\\'");

            string query = $"SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData " +
                           $"WHERE Name = '{safeAdapterName}' AND RegistryKeyword = '*SpeedDuplex'";

            var instances = session.QueryInstances(@"root\StandardCimv2", "WQL", query).ToList();
            if (instances.Count == 0) return false;

            var instance = instances[0];
            instance.CimInstanceProperties["RegistryValue"].Value = registryValue;
            session.ModifyInstance(instance);

            // Restart the adapter to apply the new speed
            string adapterQuery = $"SELECT * FROM MSFT_NetAdapter WHERE Name = '{safeAdapterName}'";
            var adapters = session.QueryInstances(@"root\StandardCimv2", "WQL", adapterQuery).ToList();
            if (adapters.Count > 0)
                session.InvokeMethod(adapters[0], "Restart", null);

            _logger.LogInformation("CIM speed change applied successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CIM method failed, will try PowerShell fallback");
            return false;
        }
    }

    // Fallback: PowerShell — still no UAC because the service already runs as LocalSystem
    private bool ApplySpeedViaPowerShell(string adapterName, string registryValue)
    {
        try
        {
            // Escape single quotes for PowerShell string literals.
            string safeAdapterName   = adapterName.Replace("'", "''");
            string safeRegistryValue = registryValue.Replace("'", "''");

            string script =
                $"Set-NetAdapterAdvancedProperty -Name '{safeAdapterName}' " +
                $"-RegistryKeyword '*SpeedDuplex' -RegistryValue '{safeRegistryValue}' -NoRestart; " +
                $"Restart-NetAdapter -Name '{safeAdapterName}' -Confirm:$false";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();

            bool succeeded = process?.ExitCode == 0;
            if (succeeded)
                _logger.LogInformation("PowerShell speed change applied successfully.");
            else
                _logger.LogWarning("PowerShell speed change exited with code {Code}", process?.ExitCode);

            return succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell fallback also failed");
            return false;
        }
    }

    // ── Client authorization ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that the connected pipe client is the trusted StreamTweak UI:
    /// the same StreamTweakUI.exe that sits in this service's own (admin-only)
    /// install directory. Any failure to prove that is treated as unauthorized.
    /// </summary>
    private bool IsAuthorizedClient(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out uint pid))
                return false;

            string? clientPath = GetProcessImagePath(pid);
            if (string.IsNullOrEmpty(clientPath))
                return false;

            string? serviceDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            if (string.IsNullOrEmpty(serviceDir))
                return false;

            string expected = Path.Combine(serviceDir, "StreamTweakUI.exe");
            return string.Equals(
                Path.GetFullPath(clientPath),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe client authorization check failed");
            return false;
        }
    }

    private static string? GetProcessImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            uint size = 1024;
            var sb = new System.Text.StringBuilder((int)size);
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>
    /// Unified pipe command.
    /// Omit Command (or set to "SetSpeed") for NIC speed changes.
    /// Set Command = "WriteFile" to write a file as LocalSystem.
    /// Set Command = "SwapAssets" / "RestoreAssets" for host-tile swap/restore.
    /// </summary>
    private record PipeCommand(
        string?  Command,
        string?  AdapterName,
        string?  RegistryValue,
        string?  Path,
        string?  Content,
        string?  AssetsDir,
        string?  DesktopSource,
        string?  SteamSource,
        string?  Scope
    );
}
