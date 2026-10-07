using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak.Nvidia
{
    /// <summary>
    /// The UI side of the GPU power readings (9.2.0): they come from StreamTweakService over
    /// its named pipe. This process never calls NVML for them — an NVML call can fault during
    /// a driver reset (TDR), and the service is the process that can afford it (it restarts
    /// itself). See StreamTweakService/Nvml.cs. (The UI does still initialise NVML for the
    /// 7.4.0 temperature fallback in HostMetricsCollector, which it calls only on a system
    /// where D3DKMT never produced a temperature.)
    ///
    /// The call is bounded: a service that is stopped or busy answers "unavailable" within the
    /// timeout instead of holding the host metrics' timer thread.
    /// </summary>
    public static class GpuPowerClient
    {
        private const string PipeName = "StreamTweakService";

        /// <summary>Polled once a second by the host metrics during a stream: kept short.</summary>
        private const int ReadTimeoutMs = 800;

        /// <summary>The service's view, or null when the service did not answer (stopped,
        /// not installed, or this is not the installed StreamTweakUI.exe — the pipe accepts
        /// only that one).</summary>
        public static async Task<GpuPowerState?> GetStateAsync()
        {
            string? reply = await SendAsync(new { Command = "GpuPowerState" }, ReadTimeoutMs).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply) || reply.StartsWith("ERROR", StringComparison.Ordinal)) return null;
            try { return JsonSerializer.Deserialize<GpuPowerState>(reply); }
            catch { return null; }
        }


        private static async Task<string?> SendAsync(object command, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);

                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);

                await writer.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
                return await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }
}
