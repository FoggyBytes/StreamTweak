using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Listens on TCP port 47998 for commands from the StreamTweak Moonlight fork.
    /// 
    /// Protocol (plain text, one command per connection):
    ///   PREPARE  — client is about to launch a stream; set NIC to 1 Gbps now,
    ///              before the stream starts, so no disconnect occurs mid-session.
    ///   RESTORE  — client has ended the stream; restore original NIC speed.
    ///              Acts as an explicit fallback alongside the log monitor.
    ///   STATUS   — client queries the current NIC link speed.
    ///              Server replies with the speed in Mbps (e.g. "1000") or "UNKNOWN".
    /// 
    /// Each connection is short-lived: client sends one line, server replies "OK",
    /// the speed string, or "ERR".
    /// 
    /// Wire up StatusProvider, StatsProvider and AppStoresProvider in App.xaml.cs after _bridge.Start():
    ///   _bridge.StatusProvider     = () => GetCurrentLinkSpeedMbps().ToString();
    ///   _bridge.StatsProvider      = () => _metricsCollector.GetLatestSample().ToJson();
    ///   _bridge.AppStoresProvider  = () => GameLibraryState.Current.ToAppStoresJson();
    /// </summary>
    public sealed class StreamTweakBridge : IDisposable
    {
        public const int Port = 47998;

        /// <summary>
        /// Optional delegate that returns the current NIC link speed in Mbps
        /// as a string (e.g. "1000", "2500"). Called when a STATUS command arrives.
        /// Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? StatusProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns a JSON string of real-time host metrics.
        /// Called when a STATS command arrives. Should return the output of
        /// HostMetricsSample.ToJson(), or "STATS_UNAVAILABLE" on failure.
        /// Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? StatsProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns a JSON object {"Game Name": "Store", ...}
        /// for all games currently synced to Sunshine by the Game Library feature.
        /// Called when an APPSTORES command arrives. Returns "{}" if no games are synced.
        /// Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? AppStoresProvider { get; set; }

        /// <summary>
        /// Raised when a SESSIONDATA command is received from StreamLight.
        /// The argument is the deserialized ClientBatch for the current session.
        /// Subscribe in App.xaml.cs to feed the TelemetryAccumulator.
        /// </summary>
        public event Action<ClientBatch>? SessionDataReceived;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private bool _disposed;

        public event Action? PrepareRequested;
        public event Action? RestoreRequested;

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamTweakBridge));
            if (_listener != null) return;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _listenTask = ListenAsync(_cts.Token);
                DebugLog($"StreamTweakBridge listening on 0.0.0.0:{Port}");
            }
            catch (Exception ex)
            {
                DebugLog($"StreamTweakBridge failed to start: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop(); // closes the socket → port released immediately
                _listener = null;
                _listenTask = null; // ListenAsync exits on its own on the thread pool
            }
            catch { }
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(client, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        DebugLog($"StreamTweakBridge accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream))
                using (var writer = new StreamWriter(stream) { AutoFlush = true })
                {
                    string? command = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        await writer.WriteLineAsync("ERR");
                        return;
                    }

                    command = command.Trim().ToUpperInvariant();
                    DebugLog($"StreamTweakBridge received: {command} from {client.Client.RemoteEndPoint}");

                    switch (command)
                    {
                        case "PREPARE":
                            PrepareRequested?.Invoke();
                            await writer.WriteLineAsync("OK");
                            break;

                        case "RESTORE":
                            RestoreRequested?.Invoke();
                            await writer.WriteLineAsync("OK");
                            break;

                        case "STATUS":
                            string status = StatusProvider?.Invoke() ?? "UNKNOWN";
                            await writer.WriteLineAsync(status);
                            break;

                        case "STATS":
                            string stats = StatsProvider?.Invoke() ?? "STATS_UNAVAILABLE";
                            await writer.WriteLineAsync(stats);
                            break;

                        case "APPSTORES":
                            string appStores = AppStoresProvider?.Invoke() ?? "{}";
                            await writer.WriteLineAsync(appStores);
                            break;

                        case "SESSIONID":
                            string sessionId = SessionLogger.ActiveSessionId ?? "NONE";
                            await writer.WriteLineAsync(sessionId);
                            break;

                        case "SESSIONDATA":
                            string? payload = await reader.ReadLineAsync(ct);
                            if (string.IsNullOrWhiteSpace(payload))
                            {
                                await writer.WriteLineAsync("ERR");
                                break;
                            }
                            try
                            {
                                var batch = JsonSerializer.Deserialize<ClientBatch>(payload,
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (batch != null)
                                    SessionDataReceived?.Invoke(batch);
                                await writer.WriteLineAsync("OK");
                            }
                            catch
                            {
                                DebugLog("StreamTweakBridge: failed to parse SESSIONDATA payload");
                                await writer.WriteLineAsync("ERR");
                            }
                            break;

                        default:
                            DebugLog($"StreamTweakBridge unknown command: {command}");
                            await writer.WriteLineAsync("ERR");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"StreamTweakBridge client handler error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _cts?.Dispose();
            _disposed = true;
        }

        private static void DebugLog(string message) => DebugLogger.Log(message);
    }
}
