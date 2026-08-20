using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>Where the manager persists the little state that must survive a restart.</summary>
    public interface ILinkSpeedStore
    {
        string Get(string key, string fallback);
        bool   GetBool(string key, bool fallback);
        void   Set(string key, string value);
        void   Set(string key, bool value);
    }

    public enum LinkSpeedState { Idle, Changing, Error }

    public enum SpeedRequestResult { Accepted, NotAllowed, Unsupported, Busy, NoAdapter, NotLan }

    /// <summary>
    /// Owns everything about the host's wired link speed: what it is, who changed it, and when it
    /// goes back. Before 8.1.0 this logic was spread between App.xaml.cs and App.Session.cs and
    /// keyed off the streaming server's log — which fires only *after* the session exists, so the
    /// renegotiation killed the stream it was meant to help. Now the client asks before it
    /// connects and this class is the only thing that touches the adapter.
    ///
    /// Two values are captured at the moment of a switch:
    ///   • the adapter's *SpeedDuplex <b>setting</b>, restored verbatim — an adapter left on
    ///     "Auto Negotiation" must stay on it, or it silently stops adapting to cable and switch;
    ///   • the <b>measured</b> speed at that moment, which is the only one shown to the user,
    ///     because "Auto Negotiation" says nothing about what they get back.
    /// </summary>
    public sealed class LinkSpeedManager : IDisposable
    {
        // Persisted keys. OriginalSpeed / StreamingMode keep their pre-8.1.0 names so an upgrade
        // still finds a link left switched by the previous version and can put it back.
        private const string CfgAdapter          = "NetworkAdapterName";
        private const string CfgAllowClients     = "AllowClientLinkControl";
        private const string CfgOriginalSetting  = "OriginalSpeed";
        private const string CfgOriginalMbps     = "OriginalSpeedMbps";
        private const string CfgSwitched         = "StreamingMode";

        /// <summary>Restore is deferred this long after a session ends, and cancelled if a client
        /// comes back. Every change is a multi-second network blackout, so a reconnect must not
        /// pay for two of them — that is exactly the loop 8.0.0 could fall into.</summary>
        public const int RestoreGraceSeconds = 60;

        // ⚠️ The host does not decide when to put the link back. It parks at the streaming speed
        // when a session ends and stays there until a client asks — in StreamLight the user
        // answers a prompt on returning to the home screen. Three timers used to make that
        // decision here and all three are gone:
        //
        //   • "the streamed app exited": a deduction from process liveness, and a weak one —
        //     the probe answered *unknown* for the Desktop entry and for every launcher title,
        //     which is most of the cases that matter.
        //   • a ten-minute cap on staying parked.
        //   • a three-minute undo of a change no session ever used, which would now be actively
        //     wrong: a client can ask for the speed ahead of a launch on purpose.
        //
        // Deliberate, and Marcello's decision with the trade-off in front of him: with no cap, a
        // client that never comes back leaves the adapter on the streaming speed until something
        // asks for it. Startup recovery below is the only self-repair left, and it needs
        // StreamTweak to restart.

        /// <summary>How long to wait for the adapter to report the new rate at all.</summary>
        private const int LinkUpTimeoutSeconds = 15;

        /// <summary>
        /// Held <b>after</b> the link is back at the target rate, still reporting "changing".
        ///
        /// The rate coming back is the earliest moment, not the first usable one: the streaming
        /// server keeps its UDP sockets for the life of the process, and a renegotiation leaves
        /// their interface binding stale, so every <c>WSASendMsg</c> fails with WSAEINVAL (10022)
        /// until Windows settles. Measured on 27/07: 12.1 s (17:38:17.9 → 17:38:30) and 8.3 s
        /// (18:06:12.9 → 18:06:21), 20.689 failed sends in total. A client that launches inside
        /// that window completes the RTSP handshake and then receives nothing at all.
        ///
        /// 15 s covers the worst measurement with margin. It is safe to overshoot: the client
        /// never gives up while it reads "changing" (linkmatcher.cpp), and switch + settle stays
        /// under its 40 s ceiling even if the link itself takes the full LinkUpTimeoutSeconds.
        /// </summary>
        private const int LinkSettleSeconds = 15;

        private readonly ILinkSpeedStore _store;
        private readonly ILinkSpeedEnvironment _env;
        private readonly object _lock = new();
        private IDisposable? _restoreHandle;
        private bool _disposed;

        public LinkSpeedManager(ILinkSpeedStore store, ILinkSpeedEnvironment? env = null)
        {
            _store = store;
            _env = env ?? new RealLinkSpeedEnvironment();
            AdapterName = ResolveAdapter();
        }

        /// <summary>
        /// Second opinion on whether a stream is running, independent of <see cref="SessionActive"/>.
        /// That flag comes from the streaming server's log, and a session the server never closed
        /// leaves it false while the server is still encoding — on 27/07 at 18:03 a switch was
        /// accepted with Sunshine at full tilt. Wired by App to LogParser.HasActiveMoonlightSession.
        /// </summary>
        public Func<bool>? LiveSessionProbe { get; set; }

        // ── Observable state ──────────────────────────────────────────────────────

        public string AdapterName { get; private set; }
        public LinkSpeedState State { get; private set; } = LinkSpeedState.Idle;

        /// <summary>True while a client-requested speed is in effect.</summary>
        public bool IsSwitched { get; private set; }

        /// <summary>Measured speed at the moment of the switch — the figure shown to the user.</summary>
        public long OriginalMbps { get; private set; }

        /// <summary>Name of the client that asked, for the status line.</summary>
        public string? SwitchedBy { get; private set; }

        /// <summary>When the deferred restore fires, or null when none is scheduled.</summary>
        public DateTime? RestoreAtUtc { get; private set; }

        /// <summary>
        /// True while a stream is live, from the server logging the client's connect to it logging
        /// the disconnect. ⚠️ NOT the same span as App's session, which stays open for a grace
        /// period afterwards so a reconnect rejoins the same history row — see
        /// <see cref="OnStreamDisconnected"/>. This is what NETINFO reports as session_active, and
        /// the client uses it to know whether asking for a speed is worth announcing.
        /// </summary>
        public bool SessionActive { get; private set; }

        public bool AllowClientControl
        {
            get => _store.GetBool(CfgAllowClients, true);
            set
            {
                _store.Set(CfgAllowClients, value);
                DebugLogger.Log($"[Link] client control {(value ? "enabled" : "disabled")}");
                Changed?.Invoke();
            }
        }

        public long CurrentMbps => _env.GetCurrentMbps(AdapterName);

        public bool HasManageableAdapter => !string.IsNullOrEmpty(AdapterName)
                                            && _env.GetSupportedSpeedOptions(AdapterName).Count > 0;

        /// <summary>True when a stream is running by either measure — the log-derived flag or the
        /// independent probe. Anything that renegotiates the link must consult this, never
        /// <see cref="SessionActive"/> alone.</summary>
        private bool IsStreamLive()
        {
            if (SessionActive) return true;
            try { return LiveSessionProbe?.Invoke() == true; }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Link] live-session probe threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>Raised on any state change, for the UI. Fired on a background thread.</summary>
        public event Action? Changed;

        /// <summary>Raised for user-visible notifications (title, body).</summary>
        public event Action<string, string>? Notify;

        // ── Adapter selection ─────────────────────────────────────────────────────

        private string ResolveAdapter()
        {
            string configured = _store.Get(CfgAdapter, "");
            var manageable = _env.GetManageableAdapterNames();

            if (!string.IsNullOrEmpty(configured) &&
                manageable.Any(n => n.Equals(configured, StringComparison.OrdinalIgnoreCase)))
                return configured;

            string picked = manageable.FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrEmpty(picked) && !picked.Equals(configured, StringComparison.OrdinalIgnoreCase))
            {
                _store.Set(CfgAdapter, picked);
                DebugLogger.Log($"[Link] managed adapter resolved to '{picked}'"
                              + (string.IsNullOrEmpty(configured) ? "" : $" (was '{configured}', no longer manageable)"));
            }
            return picked;
        }

        public void SetAdapter(string name)
        {
            lock (_lock)
            {
                if (AdapterName.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
                DebugLogger.Log($"[Link] managed adapter changed '{AdapterName}' → '{name}'");
                AdapterName = name;
                _store.Set(CfgAdapter, name);
            }
            Changed?.Invoke();
        }

        // ── Protocol surface ──────────────────────────────────────────────────────

        /// <summary>The NETINFO payload. Speeds travel as numbers; the driver's display string
        /// rides along as an opaque key so clients never have to parse vendor text.</summary>
        public string ToNetInfoJson()
        {
            var options = _env.GetSupportedSpeedOptions(AdapterName);
            var sb = new StringBuilder();

            sb.Append("{\"v\":1");
            sb.Append(",\"adapter\":").Append(JsonSerializer.Serialize(AdapterName));
            sb.Append(",\"current_mbps\":").Append(CurrentMbps.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"state\":\"").Append(State switch
            {
                LinkSpeedState.Changing => "changing",
                LinkSpeedState.Error    => "error",
                _                       => "idle"
            }).Append('"');
            sb.Append(",\"allow_client_control\":").Append(AllowClientControl ? "true" : "false");
            sb.Append(",\"session_active\":").Append(SessionActive ? "true" : "false");
            sb.Append(",\"switched\":").Append(IsSwitched ? "true" : "false");

            sb.Append(",\"supported\":[");
            bool first = true;
            foreach (var o in options.Where(o => o.Mbps > 0).OrderByDescending(o => o.Mbps))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"mbps\":").Append(o.Mbps.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"key\":").Append(JsonSerializer.Serialize(o.DisplayKey)).Append('}');
            }
            sb.Append("]}");

            return sb.ToString();
        }

        /// <summary>
        /// A client asks the host to run at <paramref name="mbps"/>. Returns immediately; the
        /// change proceeds in the background and the client watches <see cref="State"/> via NETINFO.
        /// </summary>
        public SpeedRequestResult RequestSpeed(long mbps, string clientName, IPAddress? localEndpoint)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(AdapterName))
                {
                    DebugLogger.Log("[Link] SETSPEED refused: no manageable wired adapter");
                    return SpeedRequestResult.NoAdapter;
                }
                if (!AllowClientControl)
                {
                    DebugLogger.Log($"[Link] SETSPEED refused: client control disabled (asked {mbps} by {clientName})");
                    return SpeedRequestResult.NotAllowed;
                }
                if (localEndpoint != null && !IsOnManagedAdapter(localEndpoint))
                {
                    // Arrived over Tailscale, Wi-Fi, or another NIC: a remote client must never
                    // be able to renegotiate the LAN link it isn't even using.
                    DebugLogger.Log($"[Link] SETSPEED refused: request reached {localEndpoint}, not on '{AdapterName}'");
                    return SpeedRequestResult.NotLan;
                }
                if (SessionActive)
                {
                    // Half of what makes the 8.0.0 reconnect loop impossible; the other half, and
                    // the one that does not depend on the server having logged anything, is the
                    // LiveSessionProbe check further down.
                    //
                    // ⚠️ True only while a stream is actually live. It used to stay set for the
                    // whole of App's thirty-second grace period after the disconnect, which turned
                    // this into a blanket refusal of the link match for half a minute after every
                    // session — see OnStreamDisconnected().
                    DebugLogger.Log($"[Link] SETSPEED refused: a stream is live (asked {mbps} by {clientName})");
                    return SpeedRequestResult.Busy;
                }
                if (State == LinkSpeedState.Changing)
                {
                    DebugLogger.Log($"[Link] SETSPEED refused: a change is already running");
                    return SpeedRequestResult.Busy;
                }

                var options = _env.GetSupportedSpeedOptions(AdapterName);
                var target = NetworkManager.FindOptionForMbps(options, mbps);
                if (target == null)
                {
                    DebugLogger.Log($"[Link] SETSPEED refused: '{AdapterName}' does not support {mbps} Mbps");
                    return SpeedRequestResult.Unsupported;
                }

                long current = CurrentMbps;
                if (current == mbps)
                {
                    // The common case once things settle. Cancel any pending restore so the link
                    // isn't yanked back out from under the session about to start. Deliberately
                    // ahead of the live-stream probe below: asking for the speed we are already at
                    // renegotiates nothing, and a reconnecting client must be able to cancel the
                    // restore while its own previous session is still winding down.
                    CancelScheduledRestore("client connected at the current speed");
                    DebugLogger.Log($"[Link] SETSPEED {mbps} from {clientName}: already there, nothing to do");
                    return SpeedRequestResult.Accepted;
                }

                if (LiveSessionProbe?.Invoke() == true)
                {
                    // SessionActive said no, but the server is demonstrably streaming. Renegotiating
                    // here breaks its UDP sockets under a live session — the 3000/s WSAEINVAL flood
                    // seen at 17:50 on 27/07.
                    DebugLogger.Log($"[Link] SETSPEED refused: the streaming server has a live connection "
                                  + $"(asked {mbps} by {clientName})");
                    return SpeedRequestResult.Busy;
                }

                if (!IsSwitched)
                {
                    var setting = _env.GetCurrentSpeedSetting(AdapterName);
                    _store.Set(CfgOriginalSetting, setting?.DisplayKey ?? string.Empty);
                    _store.Set(CfgOriginalMbps, current.ToString(CultureInfo.InvariantCulture));
                    OriginalMbps = current;
                    DebugLogger.Log($"[Link] captured original: setting='{setting?.DisplayKey ?? "(unknown)"}' measured={current} Mbps");
                }

                CancelScheduledRestore("a new change was requested");
                SwitchedBy = clientName;
                State = LinkSpeedState.Changing;
                _store.Set(CfgSwitched, true);
                IsSwitched = true;

                DebugLogger.Log($"[Link] SETSPEED {NetworkManager.FormatMbps(current)} → {NetworkManager.FormatMbps(mbps)} "
                              + $"on '{AdapterName}' for {clientName}");

                _ = Task.Run(() => ApplyAndSettleAsync(target, mbps));
                return SpeedRequestResult.Accepted;
            }
        }

        // ── Session lifecycle (driven by App) ─────────────────────────────────────

        public void OnSessionStarted()
        {
            lock (_lock)
            {
                SessionActive = true;
                CancelScheduledRestore("a session started");
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// The server logged the client's disconnect. The stream is over as far as the adapter is
        /// concerned, even though App keeps the session open for another half a minute so that a
        /// reconnect rejoins the same history row.
        /// </summary>
        /// <remarks>
        /// ⚠️ This exists because <see cref="SessionActive"/> used to stay true for that whole
        /// grace period, and <see cref="RequestSpeed"/> turns a client away while it is set. So a
        /// client that finished a game and started another within thirty seconds was refused the
        /// link match — silently, and in exactly the window where relaunching is most likely.
        /// The flag now means "a stream is live", which is the only thing that has ever made it
        /// worth refusing, and it is what NETINFO reports so the client can predict the answer.
        ///
        /// ⚠️ Deliberately NOT the same as <see cref="OnSessionEnded"/>: a restore the client
        /// asked for during a live stream is still held until the grace period is up, so that a
        /// reconnect inside it resumes on the streaming speed rather than on a link put back
        /// underneath it. Splitting the two is the whole point.
        ///
        /// Safety is unchanged either way. <see cref="LiveSessionProbe"/> is the hard guard
        /// against renegotiating under a running stream, and it answers from established TCP
        /// connections rather than from the log — so a server that never logged its disconnect
        /// still blocks, which is the case that flag could never see.
        /// </remarks>
        public void OnStreamDisconnected()
        {
            lock (_lock)
            {
                if (!SessionActive) return;
                SessionActive = false;
                DebugLogger.Log("[Link] client disconnected: no stream is live "
                              + "(the session stays open for its grace period)");
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// The session ended. The link stays exactly where it is: the streaming server keeps the
        /// app alive so a /resume can rejoin it, and even when the user really has finished it is
        /// their client that says so. Nothing is scheduled here — see the note on the constants.
        /// </summary>
        public void OnSessionEnded()
        {
            bool restorePending;
            lock (_lock)
            {
                SessionActive = false;
                if (!IsSwitched) return;

                // ⚠️ A pending timer can only be a restore the client already asked for that was
                // deferred because the stream was still live — nothing else schedules one now.
                // The reason it was held is precisely what has just gone away, so it must run
                // rather than be cancelled. Cancelling it here dropped the user's request in
                // silence, which is what the simulator's S14 caught.
                restorePending = RestoreAtUtc != null;
                if (!restorePending)
                    DebugLogger.Log("[Link] session ended: staying at the streaming speed "
                                  + "until a client asks for it back");
            }

            if (restorePending)
                RestoreNow("the stream ended, running the restore that was deferred");

            Changed?.Invoke();
        }

        // ── Restore ───────────────────────────────────────────────────────────────

        /// <summary>Puts the adapter back now, skipping any grace period.</summary>
        public void RestoreNow(string reason = "requested")
        {
            LinkSpeedOption? target;
            lock (_lock)
            {
                if (!IsSwitched && State != LinkSpeedState.Error) return;

                if (IsStreamLive())
                {
                    // Restoring under a live stream breaks the server's UDP sockets exactly as a
                    // switch does — on 27/07 the startup-recovery restore at 17:50 killed the sends
                    // of a running session for six seconds. Never restore mid-stream: wait and let
                    // the end of the session schedule it.
                    ScheduleRestore(RestoreGraceSeconds, $"deferred, a stream is live ({reason})");
                    return;
                }

                CancelScheduledRestore(null);
                target = ResolveOriginalOption();
                if (target == null)
                {
                    DebugLogger.Log("[Link] restore skipped: original setting unknown");
                    ClearSwitchedState();
                    return;
                }
                State = LinkSpeedState.Changing;
                DebugLogger.Log($"[Link] restoring to '{target.DisplayKey}' ({reason})");
            }

            _ = Task.Run(() => ApplyAndSettleAsync(target, OriginalMbps, isRestore: true));
        }

        /// <summary>
        /// Called once at startup. If a previous run left the link switched — a crash, an update,
        /// a forced quit — put it back, because nothing else ever will.
        /// </summary>
        public void RecoverAtStartup()
        {
            bool wasSwitched = _store.GetBool(CfgSwitched, false);
            string setting = _store.Get(CfgOriginalSetting, "");
            if (!wasSwitched || string.IsNullOrEmpty(setting)) return;

            OriginalMbps = long.TryParse(_store.Get(CfgOriginalMbps, "0"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long m) ? m : 0;
            IsSwitched = true;
            SwitchedBy = null;

            DebugLogger.Log($"[Link] startup recovery: link was left switched (original '{setting}', "
                          + $"{NetworkManager.FormatMbps(OriginalMbps)})");

            // RestoreNow defers by itself if a stream is already running — which is the normal case
            // when StreamTweak is restarted mid-session, and the one that used to break the stream.
            RestoreNow("startup recovery");
        }

        private LinkSpeedOption? ResolveOriginalOption()
        {
            string setting = _store.Get(CfgOriginalSetting, "");
            if (string.IsNullOrEmpty(setting)) return null;

            var options = _env.GetSupportedSpeedOptions(AdapterName);
            return options.FirstOrDefault(o => o.DisplayKey.Equals(setting, StringComparison.OrdinalIgnoreCase))
                // The adapter changed underneath us (driver update, different NIC): fall back to
                // the measured speed we recorded, which is at least the right rate.
                ?? NetworkManager.FindOptionForMbps(options, OriginalMbps);
        }

        // Caller holds _lock.
        private void ScheduleRestore(int seconds, string reason)
        {
            _restoreHandle?.Dispose();
            RestoreAtUtc = _env.UtcNow.AddSeconds(seconds);
            DebugLogger.Log($"[Link] restore scheduled in {seconds}s ({reason})");
            _restoreHandle = _env.ScheduleOnce(TimeSpan.FromSeconds(seconds), () =>
            {
                lock (_lock)
                {
                    if (_disposed || !IsSwitched) return;
                    RestoreAtUtc = null;
                }
                // RestoreNow re-checks for a live stream and defers again if one appeared while
                // this timer was pending.
                RestoreNow(reason);
            });
        }

        // Caller holds _lock.
        private void CancelScheduledRestore(string? reason)
        {
            if (_restoreHandle == null) return;
            _restoreHandle.Dispose();
            _restoreHandle = null;
            RestoreAtUtc = null;
            if (reason != null) DebugLogger.Log($"[Link] scheduled restore cancelled: {reason}");
        }

        // Caller holds _lock.
        private void ClearSwitchedState()
        {
            IsSwitched = false;
            SwitchedBy = null;
            OriginalMbps = 0;
            State = LinkSpeedState.Idle;
            _store.Set(CfgSwitched, false);
            _store.Set(CfgOriginalSetting, string.Empty);
            _store.Set(CfgOriginalMbps, "0");
        }

        // ── Applying ──────────────────────────────────────────────────────────────

        private async Task ApplyAndSettleAsync(LinkSpeedOption target, long expectedMbps,
                                               bool isRestore = false)
        {
            bool applied;
            try
            {
                applied = _env.Apply(AdapterName, target.RegistryValue);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Link] apply threw: {ex}");
                applied = false;
            }

            if (!applied)
            {
                lock (_lock) { State = LinkSpeedState.Error; }
                DebugLogger.Log($"[Link] FAILED to apply '{target.DisplayKey}' on '{AdapterName}'");
                Notify?.Invoke("Link speed unchanged", $"Could not set {AdapterName} to {target.DisplayKey}.");
                Changed?.Invoke();
                return;
            }

            long settled = await WaitForLinkAsync(expectedMbps);

            // The link is up, but nothing that matters can use it yet — see LinkSettleSeconds.
            // State stays Changing throughout, which is what holds the client back.
            DebugLogger.Log($"[Link] link up at {NetworkManager.FormatMbps(settled)}; "
                          + $"settling for {LinkSettleSeconds}s before reporting idle");
            await _env.Delay(LinkSettleSeconds * 1000).ConfigureAwait(false);

            lock (_lock)
            {
                if (isRestore)
                {
                    ClearSwitchedState();
                    DebugLogger.Log($"[Link] restored: '{target.DisplayKey}', link now {NetworkManager.FormatMbps(settled)}");
                }
                else
                {
                    State = LinkSpeedState.Idle;
                    DebugLogger.Log($"[Link] switch complete: link now {NetworkManager.FormatMbps(settled)}"
                                  + (settled == expectedMbps ? "" : $" (asked {NetworkManager.FormatMbps(expectedMbps)})"));
                }
            }
            Changed?.Invoke();
        }

        /// <summary>Waits for the adapter to come back up after a renegotiation. Changing the
        /// speed drops the link for several seconds, so the client is told the change is done
        /// only once the interface reports the new rate.</summary>
        private async Task<long> WaitForLinkAsync(long expectedMbps)
        {
            var deadline = _env.UtcNow.AddSeconds(LinkUpTimeoutSeconds);
            long last = 0;
            while (_env.UtcNow < deadline)
            {
                await _env.Delay(500).ConfigureAwait(false);
                last = CurrentMbps;
                if (last == expectedMbps) return last;
            }
            return last;
        }

        // ── LAN check ─────────────────────────────────────────────────────────────

        /// <summary>True when <paramref name="local"/> is an address of the managed adapter — i.e.
        /// the client reached us over the very link it is asking us to change.</summary>
        public bool IsOnManagedAdapter(IPAddress local) => _env.IsOnManagedAdapter(AdapterName, local);

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _restoreHandle?.Dispose();
                _restoreHandle = null;
            }
        }
    }
}
