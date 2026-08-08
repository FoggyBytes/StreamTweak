using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Listens on TCP port 47998 for commands from the StreamTweak Moonlight fork.
    /// 
    /// Protocol (plain text, one command per connection):
    ///   NETINFO  — client queries the managed wired adapter: current speed, the speeds it
    ///              supports (in Mbps, with the driver's display string as an opaque key),
    ///              whether client control is permitted, and whether a session is running.
    ///   SETSPEED <mbps> — client asks the host to run at that speed before it connects.
    ///              Replies OK / ERR_NOT_ALLOWED / ERR_UNSUPPORTED / ERR_BUSY / ERR_NOT_LAN.
    ///              Requires a verified AUTH1 signature. The change runs in the background;
    ///              the client polls NETINFO until `state` is idle at the requested speed.
    ///   RESTORE  — client has ended the stream; ends the session, after which the host
    ///              restores the captured speed on its own (after a grace period).
    ///   STATUS   — client queries the current NIC link speed.
    ///              Server replies with the speed in Mbps (e.g. "1000") or "UNKNOWN".
    ///   SHUTDOWN — client asks the host to power off. Requires a verified AUTH1
    ///              signature (destructive); replies "OK", or "ERR" if unauthenticated.
    ///   SHUTDOWN_UPDATE — like SHUTDOWN, but installs any pending Windows updates
    ///              before powering off ("Update and shut down"). Same verified-signature
    ///              requirement as SHUTDOWN.
    ///   UPDATESTATE — client asks whether the host has updates waiting for a reboot.
    ///              Server replies with {"pending":true|false}.
    ///   UPDATECHECK — client asks the host to start an async Windows-update scan.
    ///              Requires a verified AUTH1 signature (it triggers host activity).
    ///              Replies "OK"/"ERR". Progress/result polled via UPDATEPROGRESS.
    ///   UPDATE_NOW <SEC|ALL> — install the scanned updates and reboot if required.
    ///              Destructive; requires a verified AUTH1 signature. Replies "OK"/"ERR".
    ///   UPDATEPROGRESS — poll the current update job state. Server replies with JSON
    ///              {"phase":…,"percent":…,"message":…,"updates":[…],"counts":{…}}.
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

        // ── DoS hardening ──────────────────────────────────────────────────────
        // The bridge listens on the LAN without authentication, so a single bad or
        // hostile peer must not be able to exhaust resources. These caps bound the
        // blast radius: idle connections time out, oversized lines are rejected
        // before they can be buffered, and the number of concurrent handlers is
        // limited so a flood of half-open sockets cannot pile up unbounded.
        private const int MaxConcurrentConnections = 32;
        private const int ReadTimeoutMs            = 10_000;
        private const int MaxCommandLineLength     = 256;
        private const int MaxPayloadLineLength     = 64 * 1024;

        private readonly SemaphoreSlim _connectionLimiter =
            new(MaxConcurrentConnections, MaxConcurrentConnections);

        // ── Bridge authentication (StreamTweak 7.1.0) ───────────────────────────
        // When RequireAuth is true, only commands carrying a valid AUTH1 signature
        // from an approved client are executed; legacy unauthenticated commands are
        // rejected with ERR_UNAUTHORIZED. CAPS and ENROLL are always accepted so a
        // client can negotiate capabilities and enroll its certificate.
        // The AUTH1 first line (verb + uniqueId + timestamp + base64 RSA-2048
        // signature ≈ 400 chars) needs a larger cap than a bare command.
        private const int MaxFirstLineLength = 1024;
        public BridgeAuthService? AuthService { get; set; }
        public bool RequireAuth { get; set; } = true;

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
        /// Optional delegate that returns the launch state of the app the streaming server
        /// started, as JSON — see <see cref="LaunchWatcher.ToJson"/>. Called when a GAMESTATE
        /// command arrives. Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? GameStateProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns the host's most recent finished session as JSON —
        /// see <see cref="LastSessionReport.BuildJson"/>. Called when a LASTSESSION command
        /// arrives. Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? LastSessionProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns a JSON object {"Game Name": "Store", ...}
        /// for all games currently synced to Sunshine by the Game Library feature.
        /// Called when an APPSTORES command arrives. Returns "{}" if no games are synced.
        /// Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? AppStoresProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns the Tailscale IPv4 address of the host
        /// (e.g. "100.64.1.2") if Tailscale is installed and active, or "NOT_DETECTED".
        /// Called when a TAILSCALE command arrives. Set this in App.xaml.cs after
        /// creating the bridge.
        /// </summary>
        public Func<string>? TailscaleProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns the host's Windows-update state as JSON
        /// ({"pending":true|false}). Called when an UPDATESTATE command arrives.
        /// Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? UpdateStateProvider { get; set; }

        /// <summary>
        /// Optional delegate that returns whether the host is sitting at a lock or logon
        /// screen, as JSON ({"v":1,"locked":true|false}). Called when a LOCKSTATE command
        /// arrives. Set this in App.xaml.cs after creating the bridge.
        /// </summary>
        public Func<string>? LockStateProvider { get; set; }

        /// <summary>
        /// Raised when UNLOCKBEGIN (true) or UNLOCKEND (false) arrives from an authenticated
        /// client, around a remote PIN-unlock attempt.
        ///
        /// <para>The client is about to open a streaming session purely to type a PIN into the
        /// host's logon screen. That session is plumbing, not something the user did: it must
        /// not reach the session history, spatial audio, managed apps or game capture. The host
        /// cannot tell such a session apart from a real one by looking at it — only the client
        /// knows why it opened — so the client says so, and the host obeys. Same split as the
        /// 8.1.0 link-speed inversion: the client owns intent, the host owns policy.</para>
        /// </summary>
        public event Action<bool>? UnlockSessionMarked;

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

        public event Action? RestoreRequested;

        /// <summary>
        /// The host's link-speed state machine: serves NETINFO and executes SETSPEED.
        /// Null when no manageable wired adapter exists, in which case NETINFO reports
        /// an empty adapter and clients skip the whole flow.
        /// </summary>
        public LinkSpeedManager? LinkSpeed { get; set; }

        /// <summary>
        /// Raised when a SHUTDOWN / SHUTDOWN_UPDATE command is received from an
        /// authenticated StreamLight client. Powers off the host PC; the bool argument is
        /// true when pending Windows updates should be installed first ("Update and shut
        /// down"). Subscribe in App.xaml.cs.
        /// Both commands are destructive, so they are only ever raised for a command that
        /// carried a verified AUTH1 signature — never from the legacy/open code path.
        /// </summary>
        public event Action<bool>? ShutdownRequested;

        /// <summary>
        /// Raised when an UPDATECHECK command is received: start an async Windows-update
        /// scan on the host. Not destructive; gated by RequireAuth like the read commands.
        /// </summary>
        public event Action? UpdateCheckRequested;

        /// <summary>
        /// Raised when an UPDATE_NOW &lt;scope&gt; command is received from an authenticated
        /// client: install the scanned updates ("SEC" or "ALL") and reboot if required.
        /// Destructive (reboots the host) — only ever raised for a verified AUTH1 signature.
        /// </summary>
        public event Action<string>? UpdateInstallRequested;

        /// <summary>
        /// Optional delegate returning the current Windows-update job state as JSON.
        /// Called when an UPDATEPROGRESS command arrives. Set in App.xaml.cs.
        /// </summary>
        public Func<string>? UpdateProgressProvider { get; set; }

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

                    // Reject immediately (no queueing) once the concurrency cap is hit,
                    // so a flood of connections cannot accumulate accepted sockets.
                    if (!_connectionLimiter.Wait(0))
                    {
                        DebugLog("StreamTweakBridge: connection limit reached — rejecting peer");
                        try { client.Dispose(); } catch { }
                        continue;
                    }

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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ReadTimeoutMs);
            CancellationToken token = timeoutCts.Token;

            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream))
                using (var writer = new StreamWriter(stream) { AutoFlush = true })
                {
                    string? first = await ReadLineLimitedAsync(reader, MaxFirstLineLength, token);
                    if (string.IsNullOrWhiteSpace(first))
                    {
                        await writer.WriteLineAsync("ERR");
                        return;
                    }
                    first = first.Trim();
                    var remote = client.Client.RemoteEndPoint as IPEndPoint;
                    // The local endpoint tells which of our NICs the client reached us on, so a
                    // SETSPEED arriving over Tailscale or Wi-Fi can be refused rather than
                    // renegotiating a LAN link the requester isn't even using.
                    var local  = client.Client.LocalEndPoint as IPEndPoint;

                    // ── CAPS: capability negotiation (always unauthenticated) ──────
                    if (first.Equals("CAPS", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync($"CAPS1 auth={(RequireAuth ? "required" : "optional")}");
                        return;
                    }

                    // ── ENROLL <uniqueId> <pin> <base64 name> + <base64 PEM cert> (unauthenticated) ──
                    if (first.StartsWith("ENROLL ", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] tok = first.Substring(7).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        string uid = tok.Length > 0 ? tok[0] : "";
                        string pin = tok.Length > 1 ? tok[1] : "";
                        string name = DecodeClientName(tok.Length > 2 ? tok[2] : "", remote);
                        string? certB64 = await ReadLineLimitedAsync(reader, MaxPayloadLineLength, token);
                        EnrollResult res = AuthService?.Enroll(uid, certB64 ?? "", name, pin) ?? EnrollResult.Denied;
                        await writer.WriteLineAsync(res switch
                        {
                            EnrollResult.Approved => "ENROLLED",
                            EnrollResult.Denied   => "DENIED",
                            _                     => "PENDING",
                        });
                        return;
                    }

                    // ── AUTH1 <uid> <ts> <sig> + <command> (authenticated) ─────────
                    if (first.StartsWith("AUTH1 ", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        string? cmdLine = await ReadLineLimitedAsync(reader, MaxCommandLineLength, token);
                        if (parts.Length != 4 || string.IsNullOrWhiteSpace(cmdLine))
                        {
                            await writer.WriteLineAsync("ERR_UNAUTHORIZED");
                            return;
                        }
                        string commandRaw = cmdLine.Trim();
                        bool ok = long.TryParse(parts[2], out long ts)
                               && AuthService != null
                               && AuthService.TryVerify(parts[1], ts, parts[3], commandRaw);
                        // When RequireAuth is off the bridge is open (legacy mode): an
                        // unverified signature falls through and the command still runs.
                        // When on, only an approved client's valid signature is accepted.
                        if (!ok && RequireAuth)
                        {
                            DebugLog($"StreamTweakBridge: rejected unauthorized command from {remote}");
                            await writer.WriteLineAsync("ERR_UNAUTHORIZED");
                            return;
                        }
                        // `ok` is the genuine signature-verification result. In open mode
                        // (RequireAuth off) a forged AUTH1 line falls through here with
                        // ok=false; destructive commands (SHUTDOWN) gate on this flag, so
                        // they never fire on an unverified signature regardless of RequireAuth.
                        await ProcessCommandAsync(commandRaw.ToUpperInvariant(), reader, writer, remote, local, parts[1], ok, token);
                        return;
                    }

                    // ── Legacy bare command ────────────────────────────────────────
                    if (RequireAuth)
                    {
                        DebugLog($"StreamTweakBridge: rejected unauthenticated '{first.ToUpperInvariant()}' from {remote} (RequireAuth on)");
                        await writer.WriteLineAsync("ERR_UNAUTHORIZED");
                        return;
                    }
                    await ProcessCommandAsync(first.ToUpperInvariant(), reader, writer, remote, local, clientId: null, authenticated: false, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Idle timeout or service shutdown — drop the connection silently.
            }
            catch (InvalidDataException)
            {
                DebugLog("StreamTweakBridge: input exceeded size limit — connection dropped");
            }
            catch (Exception ex)
            {
                DebugLog($"StreamTweakBridge client handler error: {ex.Message}");
            }
            finally
            {
                try { _connectionLimiter.Release(); } catch { }
            }
        }

        // Executes a verified (or, when RequireAuth is off, a legacy) command.
        // SESSIONDATA consumes its JSON payload from a following line.
        // `authenticated` is true only when the command arrived with a verified AUTH1
        // signature; destructive commands (SHUTDOWN) require it regardless of RequireAuth.
        private async Task ProcessCommandAsync(
            string command, StreamReader reader, StreamWriter writer,
            IPEndPoint? remote, IPEndPoint? local, string? clientId, bool authenticated, CancellationToken token)
        {
            // Routine traffic: STATUS/STATS poll at ~1 Hz whenever a client has the host list
            // open, and SESSIONDATA arrives every second for the whole session. Logging every
            // expected packet said nothing and drowned the anomalies. Rejections, unknown
            // verbs and parse failures below stay unconditional.
            DebugLogger.Verbose($"StreamTweakBridge received: {command} from {remote}");

            // Split off an optional argument (only UPDATE_NOW carries one). For every other
            // command verb == command, so the existing exact-match cases are unaffected.
            int sp = command.IndexOf(' ');
            string verb = sp < 0 ? command : command.Substring(0, sp);
            string arg  = sp < 0 ? "" : command.Substring(sp + 1).Trim();

            switch (verb)
            {
                // PREPARE was removed in 8.1.0. It meant "apply the target you have configured",
                // and the host no longer configures one — the client picks the speed and names it
                // in SETSPEED. A pre-4.6.0 client therefore gets no automatic switch, which is the
                // documented compatibility cost of dropping the log-triggered path.

                case "NETINFO":
                    await writer.WriteLineAsync(LinkSpeed?.ToNetInfoJson() ?? "{\"v\":1,\"adapter\":\"\",\"current_mbps\":0,\"state\":\"idle\",\"allow_client_control\":false,\"session_active\":false,\"switched\":false,\"supported\":[]}");
                    break;

                case "SETSPEED":
                {
                    // Changes host system state and briefly drops its connectivity: gate on the
                    // verified-signature flag, like UPDATECHECK, not merely on RequireAuth.
                    if (!authenticated)
                    {
                        DebugLog($"StreamTweakBridge: rejected unauthenticated SETSPEED from {remote}");
                        await writer.WriteLineAsync("ERR_UNAUTHORIZED");
                        break;
                    }
                    if (LinkSpeed == null)
                    {
                        await writer.WriteLineAsync("ERR");
                        break;
                    }
                    if (!long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out long wanted) || wanted <= 0)
                    {
                        DebugLog($"StreamTweakBridge: malformed SETSPEED argument '{arg}' from {remote}");
                        await writer.WriteLineAsync("ERR");
                        break;
                    }

                    string who = ResolveClientLabel(clientId, remote);
                    var result = LinkSpeed.RequestSpeed(wanted, who, local?.Address);
                    await writer.WriteLineAsync(result switch
                    {
                        SpeedRequestResult.Accepted    => "OK",
                        SpeedRequestResult.NotAllowed  => "ERR_NOT_ALLOWED",
                        SpeedRequestResult.Unsupported => "ERR_UNSUPPORTED",
                        SpeedRequestResult.Busy        => "ERR_BUSY",
                        SpeedRequestResult.NotLan      => "ERR_NOT_LAN",
                        _                              => "ERR_NO_ADAPTER"
                    });
                    break;
                }

                case "RESTORE":
                    RestoreRequested?.Invoke();
                    await writer.WriteLineAsync("OK");
                    break;

                case "SHUTDOWN":
                case "SHUTDOWN_UPDATE":
                    // Destructive: powers off the host. Require a verified signature even
                    // in open mode — a forged/unauthenticated SHUTDOWN must never fire.
                    // SHUTDOWN_UPDATE additionally installs pending updates before power-off.
                    if (!authenticated)
                    {
                        DebugLog($"StreamTweakBridge: rejected unauthenticated {command} from {remote}");
                        await writer.WriteLineAsync("ERR");
                        break;
                    }
                    ShutdownRequested?.Invoke(command == "SHUTDOWN_UPDATE");
                    await writer.WriteLineAsync("OK");
                    break;

                case "UPDATESTATE":
                    string updateState = UpdateStateProvider?.Invoke() ?? "{\"pending\":false}";
                    await writer.WriteLineAsync(updateState);
                    break;

                case "LOCKSTATE":
                    // Whether a lock/logon screen is up, so the client knows whether to offer
                    // its PIN pad and whether a PIN it sent worked. Read-only, so gated like
                    // STATS rather than like SETSPEED. A host that predates the verb answers
                    // ERR, which the client reads as "no unlock flow available".
                    string lockState = LockStateProvider?.Invoke()
                                       ?? "{\"v\":1,\"locked\":false}";
                    await writer.WriteLineAsync(lockState);
                    break;

                case "UNLOCKBEGIN":
                case "UNLOCKEND":
                    // Changes how the host treats the next session, so it takes a verified
                    // signature: an unauthenticated peer must not be able to make sessions
                    // vanish from the history.
                    if (!authenticated)
                    {
                        DebugLog($"StreamTweakBridge: rejected unauthenticated {command} from {remote}");
                        await writer.WriteLineAsync("ERR");
                        break;
                    }
                    UnlockSessionMarked?.Invoke(command == "UNLOCKBEGIN");
                    await writer.WriteLineAsync("OK");
                    break;

                case "UPDATECHECK":
                    // Triggers a real Windows-update scan on the host (network + CPU) and
                    // reveals pending updates, so gate it on a verified signature too — not
                    // just RequireAuth — as defense-in-depth if the bridge is ever opened.
                    if (!authenticated)
                    {
                        DebugLog($"StreamTweakBridge: rejected unauthenticated UPDATECHECK from {remote}");
                        await writer.WriteLineAsync("ERR");
                        break;
                    }
                    UpdateCheckRequested?.Invoke();
                    await writer.WriteLineAsync("OK");
                    break;

                case "UPDATE_NOW":
                    // Destructive: installs updates and reboots. Require a verified signature
                    // even in open mode, exactly like SHUTDOWN.
                    if (!authenticated)
                    {
                        DebugLog($"StreamTweakBridge: rejected unauthenticated UPDATE_NOW from {remote}");
                        await writer.WriteLineAsync("ERR");
                        break;
                    }
                    UpdateInstallRequested?.Invoke(string.IsNullOrWhiteSpace(arg) ? "SEC" : arg.ToUpperInvariant());
                    await writer.WriteLineAsync("OK");
                    break;

                case "UPDATEPROGRESS":
                    string updateProgress = UpdateProgressProvider?.Invoke() ?? "{\"phase\":\"IDLE\"}";
                    await writer.WriteLineAsync(updateProgress);
                    break;

                case "STATUS":
                    string status = StatusProvider?.Invoke() ?? "UNKNOWN";
                    await writer.WriteLineAsync(status);
                    break;

                case "STATS":
                    string stats = StatsProvider?.Invoke() ?? "STATS_UNAVAILABLE";
                    await writer.WriteLineAsync(stats);
                    break;

                case "GAMESTATE":
                    // How far along the launch is, so the client can keep its curtain up until the
                    // game is actually on screen. Read-only and non-destructive, so it is gated
                    // like STATS rather than like SETSPEED. A host that doesn't know answers
                    // "idle" and the client simply shows no curtain.
                    string gameState = GameStateProvider?.Invoke()
                                       ?? "{\"v\":1,\"phase\":\"idle\"}";
                    await writer.WriteLineAsync(gameState);
                    break;

                case "LASTSESSION":
                    // The host's last finished session, so the client can show it on its own
                    // home screen. Read-only, so it is gated like STATS: an approved client
                    // may read it, and a host that predates the verb answers ERR — which the
                    // client reads as "nothing to show", never as an error worth reporting.
                    string lastSession = LastSessionProvider?.Invoke()
                                         ?? "{\"v\":1,\"has\":false}";
                    await writer.WriteLineAsync(lastSession);
                    break;

                case "APPSTORES":
                    string appStores = AppStoresProvider?.Invoke() ?? "{}";
                    await writer.WriteLineAsync(appStores);
                    break;

                case "TAILSCALE":
                    string tailscale = TailscaleProvider?.Invoke() ?? "NOT_DETECTED";
                    await writer.WriteLineAsync(tailscale);
                    break;

                case "SESSIONID":
                    string sessionId = SessionLogger.ActiveSessionId ?? "NONE";
                    await writer.WriteLineAsync(sessionId);
                    break;

                case "SESSIONDATA":
                    string? payload = await ReadLineLimitedAsync(reader, MaxPayloadLineLength, token);
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

        // Decodes the optional base64 client hostname sent with ENROLL into the device
        // label (the bare device name, e.g. "Foggy-Ally"). Falls back to the remote IP for
        // older clients that don't send a name. Newlines stripped and length capped so a
        // hostile peer cannot inject control characters or an oversized label.
        // Label shown in the host UI ("switched by …"). Prefers the approved client's friendly
        // name over its IP, which is what the user recognises.
        private string ResolveClientLabel(string? clientId, IPEndPoint? remote)
        {
            string? name = clientId != null ? AuthService?.GetClientName(clientId) : null;
            return !string.IsNullOrWhiteSpace(name)
                ? name!
                : remote?.Address.ToString() ?? "a client";
        }

        private static string DecodeClientName(string base64Name, IPEndPoint? remote)
        {
            if (!string.IsNullOrEmpty(base64Name))
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64Name))
                        .Replace("\r", "").Replace("\n", "").Trim();
                    if (decoded.Length > 64) decoded = decoded.Substring(0, 64);
                    if (decoded.Length > 0) return decoded;
                }
                catch { }
            }
            return remote != null ? remote.Address.ToString() : "StreamLight";
        }

        // Reads a single '\n'-terminated line, capping the length at maxChars so an
        // unbounded line cannot be buffered into memory (OOM guard). '\r' is ignored
        // and the supplied token enforces the idle timeout. Returns null on EOF with
        // no data; throws InvalidDataException when the cap is exceeded.
        private static async Task<string?> ReadLineLimitedAsync(
            StreamReader reader, int maxChars, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new char[1];
            while (true)
            {
                int n = await reader.ReadAsync(buf.AsMemory(0, 1), ct);
                if (n == 0)
                    return sb.Length == 0 ? null : sb.ToString();

                char c = buf[0];
                if (c == '\n')
                    return sb.ToString();
                if (c == '\r')
                    continue;
                if (sb.Length >= maxChars)
                    throw new InvalidDataException("line exceeds maximum length");
                sb.Append(c);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _cts?.Dispose();
            _connectionLimiter.Dispose();
            _disposed = true;
        }

        private static void DebugLog(string message) => DebugLogger.Log(message);
    }
}
