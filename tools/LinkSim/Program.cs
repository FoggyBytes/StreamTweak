using System.Net;
using StreamTweak;

// ─────────────────────────────────────────────────────────────────────────────
// LinkSpeedManager simulator.
//
// Replays the real timelines of 27/07/2026 (StreamTweak debug.log + Sunshine log)
// plus the cases that were never reached by hand, against virtual time, using the
// production LinkSpeedManager unmodified.
//
// The load-bearing invariant, derived from 20.689 "WSASendMsg() failed: 10022"
// lines in sunshine-20260727-173444-278.log:
//
//     the streaming server cannot send for ~8-12 s after the link comes back up,
//     therefore the manager must not report state=idle before that window closes,
//     because idle is the client's signal to launch.
//
// ─────────────────────────────────────────────────────────────────────────────

// DebugLogger writes to %LOCALAPPDATA%\StreamTweak\debug.log and GetFolderPath ignores the
// LOCALAPPDATA environment variable (verified), so a run would otherwise append a few hundred
// invented "[Link] SETSPEED …" lines to the real diagnostic log — the one we read to find bugs
// like the one this harness exists for. Snapshot it here and put it back at the end.
string realLog = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "StreamTweak", "debug.log");
string logBackup = realLog + ".simbak";
bool logSaved = false;
if (File.Exists(realLog))
{
    File.Copy(realLog, logBackup, overwrite: true);
    logSaved = true;
}

int failures = 0;
var results = new List<string>();

void Check(string scenario, string assertion, bool ok, string detail = "")
{
    if (!ok) failures++;
    results.Add($"  {(ok ? "PASS" : "FAIL")}  {assertion}{(detail.Length > 0 ? "   [" + detail + "]" : "")}");
    if (!ok) results[^1] += "  <<<<";
}

void Scenario(string name)
{
    results.Add("");
    results.Add($"── {name}");
}

// ── Scenario 1 ───────────────────────────────────────────────────────────────
// 27/07 17:38 and 18:06: client asks 2500 -> 1000, then launches as soon as the
// host reports idle. Before the fix the host reported idle ~1 s after the link
// returned and the launch landed inside the dead window.
Scenario("S1  switch 2.5G -> 1G, client launches on idle  (real: 17:38, 18:06)");
{
    var (env, mgr) = Build(linkMbps: 2500);
    var r = mgr.RequestSpeed(1000, "Foggy-Ally", IPAddress.Parse("192.168.188.32"));
    Check("S1", "SETSPEED accepted", r == SpeedRequestResult.Accepted, r.ToString());
    env.Quiesce();
    Check("S1", "state is changing immediately", mgr.State == LinkSpeedState.Changing, mgr.State.ToString());

    // Advance one second at a time and record the first moment the host says idle.
    DateTime? idleAt = null;
    for (int i = 0; i < 60 && idleAt == null; i++)
    {
        env.Advance(TimeSpan.FromSeconds(1));
        if (mgr.State == LinkSpeedState.Idle) idleAt = env.UtcNow;
    }

    Check("S1", "host eventually reports idle", idleAt != null);
    Check("S1", "link is at the requested speed", mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");
    Check("S1", "THE INVARIANT: server sends work when idle is reported",
          idleAt != null && !env.SendsBrokenAt(idleAt.Value),
          idleAt == null ? "never idle"
                         : $"idle at +{(idleAt.Value - env.Start).TotalSeconds:0.0}s, "
                         + $"sends recover at +{(env.SendsUsableFromUtc - env.Start).TotalSeconds:0.0}s");
    Check("S1", "client's 40 s ceiling is not exceeded",
          idleAt != null && (idleAt.Value - env.Start).TotalSeconds < 40,
          idleAt == null ? "-" : $"+{(idleAt.Value - env.Start).TotalSeconds:0.0}s");

    // How much room is left over the worst window ever measured. If this ever goes near zero,
    // LinkSettleSeconds is too tight for the hardware.
    double margin = idleAt == null ? -1 : (idleAt.Value - env.SendsUsableFromUtc).TotalSeconds;
    Check("S1", "at least 2 s of margin over the worst measured recovery", margin >= 2,
          $"margin {margin:0.0}s");
}

// ── Scenario 2 ───────────────────────────────────────────────────────────────
// 27/07 18:03: the client's /launch hung; at +60 s the unused-change restore fired
// and dropped the link under the pending launch. The client's worst case is
// 40 s (match ceiling) + 120 s (LAUNCH_TIMEOUT_MS) = 160 s.
Scenario("S2  a slow launch must not be cut off by the unused-change restore  (real: 18:03/18:04)");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    int applies = env.ApplyCount;

    env.Advance(TimeSpan.FromSeconds(160));
    Check("S2", "no restore within the client's 160 s worst case",
          env.ApplyCount == applies && mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");

    // The launch finally succeeds at +165 s.
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(120));
    Check("S2", "session start cancels the pending restore",
          env.ApplyCount == applies && mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 3 ───────────────────────────────────────────────────────────────
// 27/07 17:50: StreamTweak restarted while a stream was running and its startup
// recovery restored the link immediately — 3000 failed sends/s on a live session.
Scenario("S3  startup recovery must not restore under a live stream  (real: 17:50)");
{
    var (env, mgr) = Build(linkMbps: 1000, switchedInStore: true, originalSetting: "2.5 Gbps Full Duplex",
                           originalMbps: 2500);
    env.StreamLive = true;
    mgr.RecoverAtStartup();
    env.Quiesce();
    Check("S3", "no adapter write while the stream is live", env.ApplyCount == 0, $"{env.ApplyCount} applies");

    env.Advance(TimeSpan.FromSeconds(300));
    Check("S3", "still no restore after 5 minutes of streaming",
          env.ApplyCount == 0 && mgr.CurrentMbps == 1000, $"{env.ApplyCount} applies, {mgr.CurrentMbps} Mbps");

    env.StreamLive = false;
    env.Advance(TimeSpan.FromSeconds(120));
    Check("S3", "restore happens once the stream ends", env.ApplyCount == 1, $"{env.ApplyCount} applies");
    Check("S3", "link is back to the original rate", mgr.CurrentMbps == 2500, $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 4 ───────────────────────────────────────────────────────────────
// 27/07 18:03: SessionActive was false (the server never logged a disconnect)
// while Sunshine was encoding at full tilt, so the switch was accepted.
Scenario("S4  a ghost session must still block a switch  (real: 18:03)");
{
    var (env, mgr) = Build(linkMbps: 2500);
    env.StreamLive = true;              // the probe sees it
    // mgr.SessionActive stays false — the log-derived flag is wrong, as it was that evening.
    var r = mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.Quiesce();
    Check("S4", "SETSPEED refused as busy", r == SpeedRequestResult.Busy, r.ToString());
    Check("S4", "the adapter was never touched", env.ApplyCount == 0, $"{env.ApplyCount} applies");
    Check("S4", "link untouched", mgr.CurrentMbps == 2500, $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 5 ───────────────────────────────────────────────────────────────
// The case that motivated the fire-and-forget SETSPEED on the client: relaunching
// inside the 60 s restore grace must cancel the restore, not pay for a second
// renegotiation.
Scenario("S5  relaunch at T+55 s inside the restore grace");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(600));
    mgr.OnSessionEnded();
    int applies = env.ApplyCount;

    env.Advance(TimeSpan.FromSeconds(55));
    var r = mgr.RequestSpeed(1000, "Foggy-Ally", null);   // already at 1000
    env.Quiesce();
    Check("S5", "same-speed request accepted", r == SpeedRequestResult.Accepted, r.ToString());
    Check("S5", "no second renegotiation", env.ApplyCount == applies, $"{env.ApplyCount - applies} extra applies");

    env.Advance(TimeSpan.FromSeconds(120));
    Check("S5", "the pending restore was cancelled, link stays at 1 Gbps",
          mgr.CurrentMbps == 1000 && env.ApplyCount == applies, $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 6 ───────────────────────────────────────────────────────────────
// The adapter accepts the write but never reaches the requested rate. The client
// must not be left waiting forever on state=changing.
Scenario("S6  adapter settles at the wrong rate — the client must be released");
{
    var (env, mgr) = Build(linkMbps: 2500);
    env.ForceSettleMbps = 2500;         // the write is accepted but the rate does not change
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.Advance(TimeSpan.FromSeconds(90));
    Check("S6", "state returns to idle (never stuck on changing)",
          mgr.State == LinkSpeedState.Idle, mgr.State.ToString());
    Check("S6", "reported speed is the truth, not the request",
          mgr.CurrentMbps == 2500, $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 7 ───────────────────────────────────────────────────────────────
Scenario("S7  a second client during the change must be refused, not queued");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.Quiesce();
    env.Advance(TimeSpan.FromSeconds(2));
    var r = mgr.RequestSpeed(100, "Other-Client", null);
    env.Quiesce();
    Check("S7", "second SETSPEED refused as busy", r == SpeedRequestResult.Busy, r.ToString());

    env.AdvanceUntilIdle(mgr);
    Check("S7", "the first request still lands correctly", mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");
    Check("S7", "exactly one adapter write", env.ApplyCount == 1, $"{env.ApplyCount} applies");
}

// ── Scenario 8 ───────────────────────────────────────────────────────────────
// A reconnecting client asks for the speed the host already runs at while its own
// previous session is still winding down. This must stay a no-op that cancels the
// restore — it must NOT be caught by the live-stream guard.
Scenario("S8  same-speed request during a live stream stays a restore-cancelling no-op");
{
    var (env, mgr) = Build(linkMbps: 1000, switchedInStore: true,
                           originalSetting: "2.5 Gbps Full Duplex", originalMbps: 2500);
    env.StreamLive = true;              // live *before* recovery runs, so the restore defers
    mgr.RecoverAtStartup();
    env.Quiesce();
    int applies = env.ApplyCount;

    var r = mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.Quiesce();
    Check("S8", "accepted, not refused as busy", r == SpeedRequestResult.Accepted, r.ToString());
    Check("S8", "no adapter write", env.ApplyCount == applies, $"{env.ApplyCount - applies} extra applies");
}

// ── Scenario 9 ───────────────────────────────────────────────────────────────
// A change nobody used stays. This is the inverse of what it used to assert, and the
// inversion is the point: the client can now ask for the speed *ahead* of a launch, so
// undoing it after three minutes would break the feature rather than protect anything.
Scenario("S9  a change no session ever used is left alone");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    Check("S9", "switched to 1 Gbps", mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");

    env.Advance(TimeSpan.FromMinutes(20));
    Check("S9", "still there twenty minutes later", mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");
    Check("S9", "and still flagged as switched", mgr.IsSwitched);
}

// ── Scenario 10 ──────────────────────────────────────────────────────────────
// A disconnect is not the end: the server keeps the app alive for a /resume, so the
// speed must stay put rather than pay for a renegotiation now and another on return.
Scenario("S10  a disconnect with the game still running parks the speed, it does not restore");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(300));
    int applies = env.ApplyCount;

    mgr.OnSessionEnded();
    env.Advance(TimeSpan.FromMinutes(5));
    Check("S10", "still at the streaming speed five minutes later",
          mgr.CurrentMbps == 1000 && env.ApplyCount == applies, $"{mgr.CurrentMbps} Mbps");

    // Coming back must cost nothing.
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(60));
    Check("S10", "resuming costs no renegotiation", env.ApplyCount == applies,
          $"{env.ApplyCount - applies} extra applies");
}

// ── Scenario 11 ──────────────────────────────────────────────────────────────
// The game exiting on the host used to be read as "the user has finished" and restored
// the link within one check interval. It no longer is: the same end of session, whatever
// caused it, leaves the speed where it is until a client asks.
Scenario("S11  the game exiting on the host does not restore by itself");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(120));
    int applies = env.ApplyCount;

    mgr.OnSessionEnded();                         // the game quit on the host
    env.Advance(TimeSpan.FromMinutes(5));
    Check("S11", "still at the streaming speed", mgr.CurrentMbps == 1000, $"{mgr.CurrentMbps} Mbps");
    Check("S11", "and the adapter was never touched", env.ApplyCount == applies,
          $"{env.ApplyCount - applies} extra applies");
}

// ── Scenario 12 ──────────────────────────────────────────────────────────────
// There is no cap any more. Marcello's decision with the trade-off stated: the host waits
// for the client rather than second-guessing it, and a client that never returns leaves
// the link switched until something asks. Asserted so the removal is deliberate rather
// than something that quietly regresses back.
Scenario("S12  parked stays parked — no cap puts it back on its own");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(60));
    mgr.OnSessionEnded();

    env.Advance(TimeSpan.FromHours(2));
    Check("S12", "still switched two hours later", mgr.CurrentMbps == 1000 && mgr.IsSwitched,
          $"{mgr.CurrentMbps} Mbps");

    // …and the client asking is what ends it.
    mgr.RestoreNow("the user chose to restore");
    env.Advance(TimeSpan.FromSeconds(40));
    Check("S12", "and a client request still puts it back", mgr.CurrentMbps == 2500,
          $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 13 ──────────────────────────────────────────────────────────────
// The client said "I've finished" (RESTORE). This is the only signal that works for a
// Desktop session, and it must not wait for the cap.
Scenario("S13  an explicit client RESTORE puts the link back at once");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(60));
    mgr.OnSessionEnded();
    env.Advance(TimeSpan.FromSeconds(5));

    mgr.RestoreNow("the client stopped the session");
    env.Advance(TimeSpan.FromSeconds(40));
    Check("S13", "back to the original speed", mgr.CurrentMbps == 2500, $"{mgr.CurrentMbps} Mbps");
    Check("S13", "switched flag cleared", !mgr.IsSwitched);
    Check("S13", "sends usable when it reports idle",
          mgr.State == LinkSpeedState.Idle && !env.SendsBrokenAt(env.UtcNow),
          mgr.State.ToString());
}

// ── Scenario 14 ──────────────────────────────────────────────────────────────
// The stop arrives while the stream is somehow still live (server winding down).
Scenario("S14  an explicit RESTORE is still refused while the stream is live");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(30));
    int applies = env.ApplyCount;

    env.StreamLive = true;
    mgr.RestoreNow("the client stopped the session");
    env.Advance(TimeSpan.FromSeconds(20));
    Check("S14", "deferred, not executed", env.ApplyCount == applies && mgr.CurrentMbps == 1000,
          $"{mgr.CurrentMbps} Mbps");

    env.StreamLive = false;
    mgr.OnSessionEnded();
    env.Advance(TimeSpan.FromSeconds(120));
    Check("S14", "and happens once the stream really ends", mgr.CurrentMbps == 2500,
          $"{mgr.CurrentMbps} Mbps");
}

// ── Scenario 15 ──────────────────────────────────────────────────────────────
// Not the manager: the log vocabulary that drives it. None of the policy above runs if
// the end of a session is never recognised — which is exactly what happened on 28/07,
// when a game exited and the only line written was "Session ended".
Scenario("S15  end-of-session log lines are recognised (real lines, all four servers)");
{
    void Line(string what, string line, LogParser.StreamingEvent expected)
        => Check("S15", what, LogParser.ParseLogLine(line) == expected,
                 $"got {LogParser.ParseLogLine(line)}");

    // The one that was missing: the app exits, the server tears down, no CLIENT DISCONNECTED.
    Line("'Session ended' ends the session",
         "[2026-07-28 22:06:37.865]: Info: Session ended",
         LogParser.StreamingEvent.StreamStopped);
    Line("'CLIENT DISCONNECTED' still ends it",
         "[2026-07-28 21:37:06.785]: Info: CLIENT DISCONNECTED",
         LogParser.StreamingEvent.StreamStopped);
    Line("'CLIENT CONNECTED' starts it",
         "[2026-07-28 21:42:18.962]: Info: CLIENT CONNECTED",
         LogParser.StreamingEvent.StreamStarted);

    // Guards: two lines that contain the words but must not be read as an ending.
    // "New streaming session started" is deliberately *not* a start either — "streaming
    // session started" does not contain "stream started" as a substring, and CLIENT CONNECTED
    // is the reliable trigger (CLAUDE.md §9). All that matters here is that it is not a stop.
    Line("'New streaming session started' is NOT an ending",
         "[2026-07-28 22:06:49.958]: Info: New streaming session started [active sessions: 1]",
         LogParser.StreamingEvent.None);
    Line("'session_history: end_session' is NOT an ending",
         "[2026-07-28 22:06:37.865]: Info: session_history: end_session uuid=FCD70CF2",
         LogParser.StreamingEvent.None);
}

// ── Scenario 16 ──────────────────────────────────────────────────────────────
// App keeps its session open for 30 s after the disconnect so that a reconnect rejoins the
// same history row, and SessionActive used to stay set for all of it — so RequestSpeed
// turned every client away for half a minute after each session, which is exactly the
// window in which someone starts another game. The manager now hears about the disconnect
// when it happens; the session still closes later.
Scenario("S16  the link match works inside App's post-session grace period");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(120));

    // The invariant this must not cost us.
    Check("S16", "refused while the stream is live",
          mgr.RequestSpeed(100, "Foggy-Ally", null) == SpeedRequestResult.Busy);

    // CLIENT DISCONNECTED. App starts its 30 s timer and tells the manager now.
    mgr.OnStreamDisconnected();
    Check("S16", "session_active clears at the disconnect, not at the timeout",
          !mgr.SessionActive && mgr.ToNetInfoJson().Contains("\"session_active\":false"),
          mgr.SessionActive ? "still set" : "cleared");

    // Five seconds later the user launches again. The client sends SETSPEED on every launch
    // even when the link already matches (§38), so this is the ordinary case, not an edge one.
    env.Advance(TimeSpan.FromSeconds(5));
    Check("S16", "a relaunch inside the window is accepted",
          mgr.RequestSpeed(1000, "Foggy-Ally", null) == SpeedRequestResult.Accepted);

    // …and so is the half that actually renegotiates.
    int applies = env.ApplyCount;
    Check("S16", "and so is a request that really renegotiates",
          mgr.RequestSpeed(100, "Foggy-Ally", null) == SpeedRequestResult.Accepted);
    env.AdvanceUntilIdle(mgr);
    Check("S16", "which was applied", env.ApplyCount > applies && mgr.CurrentMbps == 100,
          $"{mgr.CurrentMbps} Mbps");
    Check("S16", "and sends are usable when it reports idle",
          mgr.State == LinkSpeedState.Idle && !env.SendsBrokenAt(env.UtcNow),
          mgr.State.ToString());
}

// ── Scenario 17 ──────────────────────────────────────────────────────────────
// The two ways that window must still say no. Clearing the flag earlier is only safe if
// neither of these regresses.
Scenario("S17  the grace window is not a hole in the guard");
{
    var (env, mgr) = Build(linkMbps: 2500);
    mgr.RequestSpeed(1000, "Foggy-Ally", null);
    env.AdvanceUntilIdle(mgr);
    mgr.OnSessionStarted();
    env.Advance(TimeSpan.FromSeconds(60));

    // (a) A reconnect inside the window resumes the same session. App skips its start path
    // there — that is what "reconnected within grace period" means — so the manager has to be
    // told separately, or it would believe no stream is live for the whole resumed session.
    mgr.OnStreamDisconnected();
    env.Advance(TimeSpan.FromSeconds(3));
    mgr.OnSessionStarted();
    Check("S17", "a resume inside the window counts as live again", mgr.SessionActive);
    Check("S17", "and is refused",
          mgr.RequestSpeed(100, "Foggy-Ally", null) == SpeedRequestResult.Busy);

    // (b) The server is still streaming but never logged its disconnect. The flag cannot see
    // that — it is log-derived — and the probe is the guard that does not depend on the log.
    mgr.OnStreamDisconnected();
    env.StreamLive = true;
    int applies = env.ApplyCount;
    Check("S17", "the probe still blocks with the flag clear",
          mgr.RequestSpeed(100, "Foggy-Ally", null) == SpeedRequestResult.Busy);
    env.Advance(TimeSpan.FromSeconds(30));
    Check("S17", "and nothing was applied", env.ApplyCount == applies && mgr.CurrentMbps == 1000,
          $"{mgr.CurrentMbps} Mbps");
}

// ── Report ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("LinkSpeedManager simulation — virtual time, production code");
Console.WriteLine($"settle window = {ReadConst("LinkSettleSeconds")}s, "
                + $"restore grace = {LinkSpeedManager.RestoreGraceSeconds}s "
                + "(the host restores only when asked — no parked cap, no unused-change timeout)");
Console.WriteLine("model: link down 7 s on a write, server sockets unusable for a further 12 s "
                + "(worst measured 27/07)");
foreach (var line in results) Console.WriteLine(line);
Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL ASSERTIONS PASSED" : $"{failures} ASSERTION(S) FAILED");

if (logSaved)
{
    // On a crash this does not run and the snapshot is left next to the log to restore by hand.
    File.Copy(logBackup, realLog, overwrite: true);
    File.Delete(logBackup);
    Console.WriteLine("debug.log restored");
}

return failures == 0 ? 0 : 1;

static string ReadConst(string name) =>
    typeof(LinkSpeedManager).GetField(name,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?.GetRawConstantValue()?.ToString() ?? "?";

static (FakeEnv, LinkSpeedManager) Build(long linkMbps, bool switchedInStore = false,
                                         string originalSetting = "", long originalMbps = 0)
{
    var store = new FakeStore();
    store.Set("NetworkAdapterName", "Ethernet");
    store.Set("AllowClientLinkControl", true);
    store.Set("StreamingMode", switchedInStore);
    store.Set("OriginalSpeed", originalSetting);
    store.Set("OriginalSpeedMbps", originalMbps.ToString());

    var env = new FakeEnv { LinkMbps = linkMbps };
    var mgr = new LinkSpeedManager(store, env)
    {
        LiveSessionProbe = () => env.StreamLive,
    };
    return (env, mgr);
}

// ─────────────────────────────────────────────────────────────────────────────

sealed class FakeStore : ILinkSpeedStore
{
    private readonly Dictionary<string, string> _d = new();
    public string Get(string key, string fallback) => _d.TryGetValue(key, out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback) => _d.TryGetValue(key, out var v) ? v == "true" : fallback;
    public void Set(string key, string value) => _d[key] = value;
    public void Set(string key, bool value) => _d[key] = value ? "true" : "false";
}

/// <summary>
/// A NIC and a clock. Writing the speed drops the link for <see cref="SwitchSeconds"/>, and the
/// streaming server's sockets stay unusable for <see cref="SocketRecoverySeconds"/> after the link
/// returns — the behaviour measured on 27/07 that the whole fix exists to respect.
/// </summary>
sealed class FakeEnv : ILinkSpeedEnvironment
{
    public const int SwitchSeconds = 7;           // observed 3.6 - 7.5 s
    public const int SocketRecoverySeconds = 12;  // observed 8.3 s and 12.1 s

    public readonly DateTime Start = new(2026, 7, 27, 17, 38, 0, DateTimeKind.Utc);
    private DateTime _now;
    public FakeEnv() { _now = Start; SendsUsableFromUtc = Start; }

    public DateTime UtcNow => _now;

    public long LinkMbps;
    public long? ForceSettleMbps;                 // the rate the link actually comes back at
    public bool StreamLive;

    public int ApplyCount;
    public DateTime SendsUsableFromUtc;

    private string _settingKey = "2.5 Gbps Full Duplex";

    private readonly List<Entry> _timers = new();
    private sealed class Entry
    {
        public DateTime Due; public Action? Callback;
        public TaskCompletionSource? Tcs; public bool Cancelled;
    }

    private static readonly List<LinkSpeedOption> Options = new()
    {
        new LinkSpeedOption(2500, true, "2.5 Gbps Full Duplex", "2500"),
        new LinkSpeedOption(1000, true, "1.0 Gbps Full Duplex", "1000"),
        new LinkSpeedOption(100,  true, "100 Mbps Full Duplex",  "100"),
    };

    public long GetCurrentMbps(string adapter) => LinkMbps;
    public List<LinkSpeedOption> GetSupportedSpeedOptions(string adapter) => Options;
    public LinkSpeedOption? GetCurrentSpeedSetting(string adapter)
        => Options.FirstOrDefault(o => o.DisplayKey == _settingKey);
    public List<string> GetManageableAdapterNames() => new() { "Ethernet" };
    public bool IsOnManagedAdapter(string adapter, IPAddress local) => true;

    public bool Apply(string adapter, string registryValue)
    {
        ApplyCount++;
        var opt = Options.First(o => o.RegistryValue == registryValue);
        _settingKey = opt.DisplayKey;

        LinkMbps = 0;                                   // the link drops immediately
        long comesBackAt = ForceSettleMbps ?? opt.Mbps;
        ScheduleOnce(TimeSpan.FromSeconds(SwitchSeconds), () =>
        {
            LinkMbps = comesBackAt;
            // Windows settles the interface; the server's long-lived UDP sockets keep failing
            // with WSAEINVAL until it does.
            SendsUsableFromUtc = _now.AddSeconds(SocketRecoverySeconds);
        });
        return true;
    }

    public bool SendsBrokenAt(DateTime t) => t < SendsUsableFromUtc;

    public Task Delay(int milliseconds)
    {
        var e = new Entry { Due = _now.AddMilliseconds(milliseconds), Tcs = new TaskCompletionSource() };
        lock (_timers) _timers.Add(e);
        return e.Tcs.Task;
    }

    public IDisposable ScheduleOnce(TimeSpan delay, Action action)
    {
        var e = new Entry { Due = _now + delay, Callback = action };
        lock (_timers) _timers.Add(e);
        return new Handle(e);
    }

    private sealed class Handle : IDisposable
    {
        private readonly Entry _e;
        public Handle(Entry e) => _e = e;
        public void Dispose() => _e.Cancelled = true;
    }

    /// <summary>Lets continuations run and register their next wait. The code between two waits
    /// is pure CPU work of microseconds, so a short real sleep is enough and keeps the harness
    /// free of a custom synchronization context.</summary>
    public void Quiesce() => Thread.Sleep(25);

    public void Advance(TimeSpan span)
    {
        // Before looking at the queue at all: work started with Task.Run (RequestSpeed,
        // RestoreNow) may not have registered its first wait yet. Without this the queue looks
        // empty, virtual time jumps past the whole scenario, and the timer that arrives a
        // microsecond later is scheduled beyond the horizon and never fires. That produced two
        // bogus failures on the first run of this harness.
        Quiesce();

        var target = _now + span;
        while (true)
        {
            Entry? next;
            lock (_timers)
            {
                _timers.RemoveAll(t => t.Cancelled);
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next != null) _timers.Remove(next);
            }
            if (next == null) { _now = target; Quiesce(); return; }

            _now = next.Due;
            if (next.Callback != null) next.Callback();
            else next.Tcs!.SetResult();
            Quiesce();
        }
    }

    public void AdvanceUntilIdle(LinkSpeedManager mgr)
    {
        for (int i = 0; i < 120; i++)
        {
            Advance(TimeSpan.FromSeconds(1));
            if (mgr.State == LinkSpeedState.Idle) return;
        }
    }
}
