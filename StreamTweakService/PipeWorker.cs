using System.IO.Pipes;
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
    /// </summary>
    private static readonly string[] _allowedBasePaths =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    };

    private static bool IsAllowedAppsJsonPath(string path)
    {
        if (!path.EndsWith("apps.json", StringComparison.OrdinalIgnoreCase))
            return false;

        string normalized = Path.GetFullPath(path);

        bool hasAppName = _allowedAppNames.Any(app =>
            normalized.Contains(Path.DirectorySeparatorChar + app + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

        bool hasValidBase = _allowedBasePaths.Any(b =>
            !string.IsNullOrEmpty(b) &&
            normalized.StartsWith(b, StringComparison.OrdinalIgnoreCase));

        return hasAppName && hasValidBase;
    }

    // Primary method: use CIM (WMI) directly — no child process needed
    private bool ApplySpeedViaCim(string adapterName, string registryValue)
    {
        try
        {
            using var session = CimSession.Create(null);

            // Escape single quotes for WQL string literals (same rule as SQL: double them up).
            string safeAdapterName = adapterName.Replace("'", "''");

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

    /// <summary>
    /// Unified pipe command.
    /// Omit Command (or set to "SetSpeed") for NIC speed changes.
    /// Set Command = "WriteFile" to write a file as LocalSystem.
    /// </summary>
    private record PipeCommand(
        string?  Command,
        string?  AdapterName,
        string?  RegistryValue,
        string?  Path,
        string?  Content
    );
}
