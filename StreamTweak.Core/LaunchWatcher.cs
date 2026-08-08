using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamTweak
{
    /// <summary>
    /// What the host can say about the app the streaming server just launched.
    /// Reported to the client over the bridge (GAMESTATE) so it can keep its launch
    /// curtain up until the game is actually on screen, instead of dropping the user
    /// into a Windows desktop that is still reconfiguring itself.
    /// </summary>
    public enum LaunchPhase
    {
        /// <summary>No session, or nothing launched yet.</summary>
        Idle,
        /// <summary>The server executed the command; no window of the target has appeared yet.</summary>
        Launching,
        /// <summary>A window of the target exists but isn't yet filling the screen (splash, launcher, small window).</summary>
        GameWindow,
        /// <summary>The target's window is in the foreground and covers the display — safe to reveal.</summary>
        Ready,
        /// <summary>
        /// Something that isn't the game has held the screen for a while: a launcher asking for a
        /// login, a store dialog, an updater. The client must reveal — the whole point of hiding
        /// the host was to spare the user a desktop they don't need, not to hide the one window
        /// they have to click.
        /// </summary>
        NeedsAttention,
        /// <summary>Nothing appeared within the cap. The client reveals anyway.</summary>
        Timeout,
        /// <summary>The launched app has no command to run (the Desktop entry), so there is nothing to wait for.</summary>
        NotApplicable
    }

    /// <summary>
    /// What the streaming server logged when it started an app. Produced by
    /// <see cref="StreamingLogMonitor"/>, consumed by <see cref="LaunchWatcher"/>.
    /// </summary>
    public sealed class AppLaunchInfo
    {
        /// <summary>The raw command as logged: a full exe path, or a protocol URL like steam://rungameid/….</summary>
        public string Command { get; init; } = "";

        /// <summary>True when <see cref="Command"/> is a real executable path we can follow directly.</summary>
        public bool IsExecutable { get; init; }

        /// <summary>True for the Desktop entry, which has no command at all.</summary>
        public bool IsDesktop { get; init; }
    }

    /// <summary>
    /// Watches for the launched game's window and reports how far along the launch is.
    ///
    /// <para>This is deliberately <b>not</b> <see cref="SessionProcessMonitor"/>. That one samples
    /// every 5 seconds and answers "which games were played", which is a different question with a
    /// different tolerance for being late. Here we gate what the user sees, so we sample four times
    /// a second, and we look at <i>windows</i> rather than processes: a launcher process exists
    /// almost immediately while the game window does not, and it is the window that decides whether
    /// showing the stream is useful or embarrassing.</para>
    ///
    /// <para>Two target strategies, chosen by what the server logged. A real executable
    /// (Epic, GOG, EA, manually added games, Steam Big Picture) is followed directly by its folder.
    /// A protocol URL — Steam games arrive as <c>steam://rungameid/…</c>, where there is no process
    /// to follow — is resolved through apps.json to the app name, and from there to the install
    /// directory in the game library. When neither resolves we report
    /// <see cref="LaunchPhase.NotApplicable"/> and the client shows no curtain at all: guessing
    /// would trade a visible desktop for a screen frozen on a lie.</para>
    /// </summary>
    public sealed class LaunchWatcher : IDisposable
    {
        // Four samples a second. The window we are waiting for appears in one frame, and the
        // difference between noticing it now and noticing it in 250 ms is the difference between
        // a clean reveal and a visible flash of whatever was underneath.
        private const int PollIntervalMs = 250;

        /// <summary>
        /// How long we keep waiting for the game's window before giving up and letting the client
        /// reveal. A curtain that hides a working stream is worse than the problem it solves, so
        /// this must always fire.
        /// </summary>
        public const int ReadyTimeoutSeconds = 90;

        // Heuristics, deliberately named and tunable — they decide when a window "counts".
        // A window smaller than this fraction of its monitor is a splash screen, an updater or a
        // tooltip, not the game.
        private const double MinWindowAreaFraction = 0.20;
        // Foreground plus this much of the monitor means the game owns the screen. Not 1.0: a
        // borderless window can miss the taskbar strip, and some games letterbox themselves.
        private const double ReadyAreaFraction = 0.60;

        /// <summary>
        /// How long a window that isn't ours may hold the foreground before we conclude the host
        /// is asking for something. Measured against the real case that produced it: launching
        /// Diablo IV opened the Battle.net client on its store page and waited for a login, and the
        /// curtain sat over it for the full 90-second cap — hiding the one thing the user needed
        /// to see. Lowering the cap instead would have been wrong: a legitimate cold start took 27
        /// seconds on the same machine.
        /// <para>Deliberately not size-gated. A launcher window is "big" on a 1080p monitor and
        /// small on a 4K one, so any area threshold would work on one host and fail on another —
        /// and being in the foreground is already the statement that it wants attention.</para>
        /// <para>Twenty seconds, raised from ten once the launches were measured. At ten it fired
        /// during ordinary cold starts — <c>steamwebhelper</c> held the screen for a moment while
        /// 007 First Light loaded, and the curtain lifted 1.5 s before the game arrived. Every
        /// successful launch observed reached <see cref="LaunchPhase.GameWindow"/> or
        /// <see cref="LaunchPhase.Ready"/> within 17.8 s, while a launcher genuinely waiting for a
        /// login waits forever — so the gap between the two is where this number belongs. Reaching
        /// GameWindow also clears the counter, which protects the slower launches on its own.</para>
        /// <para>⚠️ That 17.8 s is not headroom, it is the whole margin: Black Flag through
        /// Ubisoft Connect reached GameWindow at 17.8 s on 29/07 and was reported as needing
        /// attention at 18.0 s on 02/08. Two tenths of a second decided which way it went, on the
        /// same game and the same host. Timing alone was never going to hold this up, which is why
        /// the decision now rests on whether the target's process exists — see
        /// <c>_targetProcessSeen</c>.</para>
        /// </summary>
        private const double ForeignForegroundSeconds = 20;

        /// <summary>
        /// The same wait, for a window that <em>wasn't on screen when the launch started</em>.
        /// <para>Half, because it is a much stronger statement. A store client in the foreground
        /// might be a helper passing through — <c>steamwebhelper</c> is what forced the twenty
        /// above — but a window that appeared after we launched, took the screen and kept it is
        /// not passing through: it is the thing the launch turned into. Cyberpunk's REDlauncher
        /// sat on a login prompt through both timings, and the measured cost of the difference
        /// was the user reaching for the manual override four seconds before the automatic one
        /// would have fired (29/07, client log).</para>
        /// <para>Ten and not less: a game's own splash can hold the screen briefly before the
        /// real window replaces it, and that window usually belongs to the target — which never
        /// reaches this test at all, because a matching window is answered before it.</para>
        /// <para>⚠️ The reasoning above was disproven on 02/08, and now holds only because
        /// something else is checked first. Ubisoft Connect's window <em>is</em> new, <em>does</em>
        /// take the screen, and <em>is</em> passing through — it starts the game itself and steps
        /// aside — so it lifted the curtain at 18.0 s on a launch that was going perfectly well.
        /// Nothing about the window distinguished it from Battle.net sitting on Play; only whether
        /// the game's process had appeared did. This rule is now reached only while
        /// <c>_targetProcessSeen</c> is still false, which removes the passing-through case before
        /// any timing is consulted. Ten seconds is right for what remains: a launcher that has
        /// started nothing.</para>
        /// </summary>
        private const double NewWindowForegroundSeconds = 10;

        /// <summary>
        /// When to say "still here, still waiting, and here is what's on screen" — once, with the
        /// list of windows considered. Kept ahead of <see cref="ForeignForegroundSeconds"/> so a
        /// launch that stalls is described before anything acts on it.
        /// </summary>
        private const double WaitingHeartbeatSeconds = 25;

        /// <summary>
        /// How often, in ticks, to ask whether the target's process exists yet. Four ticks is
        /// about a second — the window sweep stays at 250 ms because it decides the reveal, but
        /// this one only answers "has the launcher delivered", which nothing needs sooner.
        /// <para>It stops entirely once the answer is yes, so a normal launch pays for a handful
        /// of scans in total.</para>
        /// </summary>
        private const int ProcessScanEveryTicks = 4;

        private readonly object _lock = new();
        private Timer? _timer;
        private bool _disposed;

        // Resolved target. Any of these may be set; a process matches if it satisfies one.
        private string? _targetExeFile;      // "HogwartsLegacy.exe"
        private string? _targetDirectory;    // install dir, with trailing separator
        private string? _targetProcessName;  // Xbox/UWP games, whose path is unreadable
        private string _targetStore = "";    // which store's client counts as part of this launch

        private LaunchPhase _phase = LaunchPhase.Idle;
        private string _gameName = "";
        private DateTime _launchedAtUtc = DateTime.MinValue;
        private bool _tickErrorLogged;
        private bool _waitingLogged;

        // Set while a window that isn't the target has held the foreground continuously.
        private DateTime _foreignSinceUtc = DateTime.MinValue;
        private string _foregroundName = "";
        private bool _foregroundIsNew;

        // Every visible top-level window at the instant the launch began. Assigned once per launch
        // and never mutated afterwards, so the sampling thread reads the reference without copying.
        //
        // ⚠️ READ BOTH PARAGRAPHS BEFORE CHANGING HOW THIS IS USED. An earlier attempt used a
        // snapshot like this one to SUBTRACT — a foreground window that already existed was ruled
        // out — and it broke the case the foreground check was written for: launching Diablo IV
        // brought up a Battle.net client that was already open and focused from an earlier session,
        // so the one window the user had to act on was precisely the one being ignored, and the
        // curtain sat over it for the full 90 seconds. That version was removed.
        //
        // This one only ADDS. A foreground window counts if it is the store's own client (as
        // before) OR if it wasn't there when we started. The store-client path is untouched, so
        // nothing that resolves today can stop resolving; the snapshot can only make the curtain
        // lift sooner. It exists for the launcher a game brings with it — Cyberpunk's REDlauncher
        // lives in AppData, belongs to no store, matches no install directory, and left the wait
        // to expire at ninety seconds (observed 29/07). "It wasn't on screen before we launched"
        // is the one thing all such launchers have in common.
        //
        // Why HWNDs and not process ids: an application that was already running can open a new
        // window — the update dialog of a launcher that was sitting in the tray — and that dialog
        // is exactly what we must catch. It is also why the desktop never triggers this: explorer's
        // window is in the snapshot, and it is what holds the foreground on an unattended host.
        //
        // The two failure modes are not symmetric. Revealing too eagerly costs the user a glimpse
        // of the host desktop, which is exactly what they get today with no curtain at all.
        // Revealing too late leaves them staring at a blank screen while the host waits for a
        // click they can't see. When in doubt, reveal.
        private HashSet<IntPtr>? _windowsAtLaunch;

        /*
         * Whether the target's own process has been seen running. Sticky: the question is "did
         * the launcher deliver", and once the answer is yes it cannot become no again.
         *
         * This is what stops the curtain from lifting on a launcher that is doing its job.
         * Watching windows alone cannot tell "Ubisoft Connect passing the baton" from
         * "Battle.net waiting on Play" — both open a new window of a known store client and both
         * keep the screen — so no threshold separates them: raising it helps the first and
         * penalises the second by the same amount. Whether the game's process exists does
         * separate them, and it is a fact rather than a guess about timing, which is the only
         * kind of answer that survives a different host.
         *
         * Measured on 02/08: Black Flag through Ubisoft Connect revealed at 18.0 s with
         * `upc` in the foreground, because upc's window is new and the ten-second rule is
         * shorter than the twenty-second store-client one, so the better-informed rule never
         * got to speak. By then Ubisoft Connect had already started the game.
         */
        private bool _targetProcessSeen;
        private int  _processScanTick;

        /// <summary>
        /// Called when the streaming server logged an app launch. Resolves the target and starts
        /// sampling. Safe to call again mid-session: launching a second game restarts the watch.
        /// </summary>
        public void OnAppLaunched(AppLaunchInfo info)
        {
            if (_disposed || info == null) return;

            // Taken before the lock and before anything else: it has to describe the screen as it
            // was when the launch started, and every microsecond spent first is a window that could
            // open in the meantime and be mistaken for one that was always there.
            HashSet<IntPtr> windowsBefore = SnapshotVisibleWindows();

            lock (_lock)
            {
                _windowsAtLaunch = windowsBefore;

                _launchedAtUtc = DateTime.UtcNow;
                _tickErrorLogged = false;
                _waitingLogged = false;
                _foreignSinceUtc = DateTime.MinValue;
                _foregroundName = "";
                _foregroundIsNew = false;
                _targetProcessSeen = false;
                _processScanTick = 0;
                _targetExeFile = null;
                _targetDirectory = null;
                _targetProcessName = null;
                _targetStore = "";
                _gameName = "";

                if (info.IsDesktop)
                {
                    // The Desktop entry runs nothing, so there is no window to wait for and the
                    // desktop IS the content. Anything else would leave the curtain up forever.
                    _phase = LaunchPhase.NotApplicable;
                    StopTimerLocked();
                    DebugLogger.Log("[Launch] Desktop session — no curtain applicable");
                    return;
                }

                ResolveTargetLocked(info);

                if (string.Equals(_targetStore, "Battle.net", StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * Battle.net gets no curtain at all, on purpose.
                     *
                     * Its games launch the *client* rather than the game (see the Battle.net
                     * notes in GameLibraryScanner): the user always has to press Play, and the
                     * thing they must press is the very window a curtain would be covering. So
                     * there is never anything to wait for — which is exactly the Desktop case,
                     * and it is answered the same way.
                     *
                     * This also removes a state machine that could not work here: the launch
                     * target *is* the launcher, so the launcher's own window matches the target
                     * and is read as "the game has a window", while the game itself lives in a
                     * different folder and never matches at all. Field-tested 03/08 — Diablo IV
                     * sat behind the curtain until the cap. Special-casing one store is a
                     * smaller price than a rule that has to be right about a launcher which is
                     * simultaneously the target and the obstacle.
                     */
                    _phase = LaunchPhase.NotApplicable;
                    StopTimerLocked();
                    DebugLogger.Log($"[Launch] '{_gameName}' is a Battle.net title — the client " +
                                    "always needs a click, so no curtain");
                    return;
                }

                if (_targetExeFile == null && _targetDirectory == null && _targetProcessName == null)
                {
                    // A custom app, a script, something that never opens a window: we have no way
                    // to tell when it is "ready", so we say so instead of stalling the client.
                    _phase = LaunchPhase.NotApplicable;
                    StopTimerLocked();
                    DebugLogger.Log($"[Launch] '{info.Command}' — no target resolved, no curtain");
                    return;
                }

                _phase = LaunchPhase.Launching;
                DebugLogger.Log($"[Launch] watching for '{_gameName}' " +
                                $"(exe={_targetExeFile ?? "-"}, dir={_targetDirectory ?? "-"}, " +
                                $"proc={_targetProcessName ?? "-"})");

                _timer?.Dispose();
                _timer = new Timer(Tick, null, PollIntervalMs, PollIntervalMs);
            }
        }

        /// <summary>
        /// Session over: stop sampling and forget the target.
        /// <para><paramref name="stopDetectedUtc"/> is when the disconnect was <b>seen</b>, not when
        /// the session was finally wound up — the two are a grace period apart, and in that gap the
        /// next launch has usually already armed us. Without this guard the previous session's
        /// bookkeeping wipes the launch that is currently in progress: observed live, where a
        /// Desktop session ending at 15:20 cleared a Diablo IV launch armed at 15:21:28, two
        /// seconds after it started. The client saw the host answer "idle" and revealed, and the
        /// host logged nothing at all, because going idle is silent.</para>
        /// </summary>
        public void OnSessionEnded(DateTime stopDetectedUtc = default)
        {
            lock (_lock)
            {
                if (stopDetectedUtc != default &&
                    _launchedAtUtc != DateTime.MinValue &&
                    _launchedAtUtc > stopDetectedUtc)
                {
                    DebugLogger.Log("[Launch] keeping the current launch: it started after the " +
                                    "session that just ended");
                    return;
                }

                StopTimerLocked();
                _phase = LaunchPhase.Idle;
                _gameName = "";
                _targetExeFile = _targetDirectory = _targetProcessName = null;
                _targetProcessSeen = false;
                _launchedAtUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// The GAMESTATE payload. Reports facts only — phase, game, elapsed, the cap — and leaves
        /// the wording to the client, where it can be translated in context (same split the
        /// link-speed handshake already uses).
        /// </summary>
        public string ToJson()
        {
            LaunchPhase phase;
            string game, foreground;
            long elapsedMs;

            lock (_lock)
            {
                phase = _phase;
                game = _gameName;
                foreground = _phase == LaunchPhase.NeedsAttention ? _foregroundName : "";
                elapsedMs = _launchedAtUtc == DateTime.MinValue
                    ? 0
                    : (long)(DateTime.UtcNow - _launchedAtUtc).TotalMilliseconds;
            }

            return string.Format(CultureInfo.InvariantCulture,
                "{{\"v\":1,\"phase\":\"{0}\",\"game\":\"{1}\",\"foreground\":\"{2}\"," +
                "\"elapsed_ms\":{3},\"limit_ms\":{4}}}",
                PhaseToWire(phase), JsonEscape(game), JsonEscape(foreground),
                elapsedMs, ReadyTimeoutSeconds * 1000);
        }

        private static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string PhaseToWire(LaunchPhase p) => p switch
        {
            LaunchPhase.Launching      => "launching",
            LaunchPhase.GameWindow     => "game_window",
            LaunchPhase.Ready          => "ready",
            LaunchPhase.NeedsAttention => "needs_attention",
            LaunchPhase.Timeout        => "timeout",
            LaunchPhase.NotApplicable  => "not_applicable",
            _                          => "idle"
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock) StopTimerLocked();
        }

        private void StopTimerLocked()
        {
            _timer?.Dispose();
            _timer = null;
        }

        // ── target resolution ────────────────────────────────────────────────

        private void ResolveTargetLocked(AppLaunchInfo info)
        {
            // Strategy 1 — the server named a real executable. Follow its folder rather than the
            // binary alone: launchers hand off to a differently-named process inside the same
            // install directory, and the folder survives that.
            if (info.IsExecutable)
            {
                try
                {
                    _targetExeFile = Path.GetFileName(info.Command);
                    string? dir = Path.GetDirectoryName(info.Command);
                    if (!string.IsNullOrEmpty(dir))
                        _targetDirectory = EnsureTrailingSeparator(dir);
                }
                catch { /* malformed path — fall through to the library lookup below */ }

                _gameName = SunshineSync.ResolveAppNameForExecutable(info.Command) ?? "";
            }

            // Strategy 2 — a protocol URL, or an executable we couldn't place. Ask apps.json which
            // app carries this exact command, then ask the library where that game lives.
            // Always runs when we have a name, even if strategy 1 already found the folder: the
            // library entry is also where the store comes from, and the store is what tells us
            // which launcher windows are part of this launch.
            {
                string? name = string.IsNullOrEmpty(_gameName)
                    ? SunshineSync.ResolveAppNameForCommand(info.Command)
                    : _gameName;

                if (!string.IsNullOrEmpty(name))
                {
                    if (string.IsNullOrEmpty(_gameName)) _gameName = name;

                    try
                    {
                        var entry = GameLibraryState.Current.Games
                            .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

                        if (entry != null)
                        {
                            _targetStore = entry.Store ?? "";

                            if (!string.IsNullOrWhiteSpace(entry.InstallDir) && Directory.Exists(entry.InstallDir))
                                _targetDirectory ??= EnsureTrailingSeparator(entry.InstallDir);

                            // Xbox/UWP games live under the protected WindowsApps folder where the
                            // process path can't be read at all, so the scanner records the process
                            // name from MicrosoftGame.config and that is all we can match on.
                            if (!string.IsNullOrWhiteSpace(entry.ProcessName))
                                _targetProcessName = entry.ProcessName;
                        }
                    }
                    catch (Exception ex) { DebugLogger.Log($"[Launch] library lookup failed: {ex.Message}"); }
                }
            }
        }

        private static string EnsureTrailingSeparator(string dir) =>
            dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

        // ── sampling ─────────────────────────────────────────────────────────

        private void Tick(object? state)
        {
            if (_disposed) return;

            try
            {
                Observation obs = Observe();
                bool processSeenNow = ObserveTargetProcess();
                DateTime now = DateTime.UtcNow;
                bool dumpCandidates = false;
                bool logDelivered = false;

                lock (_lock)
                {
                    if (_phase is LaunchPhase.Idle or LaunchPhase.NotApplicable) return;

                    if (obs.Phase == LaunchPhase.Ready)
                    {
                        SetPhaseLocked(LaunchPhase.Ready);
                        StopTimerLocked();   // nothing left to watch
                        return;
                    }

                    if (obs.Phase == LaunchPhase.GameWindow)
                    {
                        _foreignSinceUtc = DateTime.MinValue;
                        SetPhaseLocked(LaunchPhase.GameWindow);
                        return;
                    }

                    // Nothing of ours on screen yet — but is the process already running? If it
                    // is, the launch is under way and the window is merely late, so nothing on
                    // screen can mean "the host needs you" any more.
                    if (processSeenNow && !_targetProcessSeen)
                    {
                        _targetProcessSeen = true;
                        _foreignSinceUtc = DateTime.MinValue;
                        logDelivered = true;
                    }

                    // Nothing of ours on screen. Is something else holding it?
                    //
                    // Suppressed for good once the process has been seen: from that moment the
                    // only questions left are "is it on screen yet" and "have we waited too
                    // long", and both are answered elsewhere. This is the rule that lifted the
                    // curtain onto Ubisoft Connect while it was starting the game.
                    if (obs.ForeignForeground && !_targetProcessSeen)
                    {
                        if (_foreignSinceUtc == DateTime.MinValue)
                        {
                            _foreignSinceUtc = now;
                            _foregroundName = obs.ForegroundName;
                            _foregroundIsNew = obs.ForegroundIsNew;
                        }
                        // Which rule caught it decides how long we give it. Read from the first
                        // observation, like the name beside it: if the foreground changes hands
                        // between two foreign windows the clock keeps running, which is the
                        // existing behaviour and the right one — the screen has been someone
                        // else's the whole time either way.
                        else if ((now - _foreignSinceUtc).TotalSeconds >=
                                     (_foregroundIsNew ? NewWindowForegroundSeconds
                                                       : ForeignForegroundSeconds) &&
                                 _phase != LaunchPhase.NeedsAttention)
                        {
                            // Say it once and keep watching: if the game does turn up afterwards
                            // we still report it, and the name in the log is how we find out what
                            // was in the way.
                            SetPhaseLocked(LaunchPhase.NeedsAttention);
                        }
                    }
                    else
                    {
                        _foreignSinceUtc = DateTime.MinValue;
                    }

                    // A single heartbeat partway through. Silence in the log is ambiguous — it
                    // reads the same whether we are patiently waiting, or the tick thread died,
                    // or the target never resolved — and telling those apart has cost three test
                    // rounds. One line settles it, and names whatever is on screen instead.
                    double waited = (now - _launchedAtUtc).TotalSeconds;
                    if (!_waitingLogged && waited >= WaitingHeartbeatSeconds)
                    {
                        _waitingLogged = true;
                        DebugLogger.Log($"[Launch] still waiting after {waited:F1}s" +
                                        (string.IsNullOrEmpty(_gameName) ? "" : $" ({_gameName})") +
                                        $" — foreground: '{(string.IsNullOrEmpty(obs.ForegroundAny) ? "?" : obs.ForegroundAny)}'");
                        dumpCandidates = true;
                    }

                    // Still nothing. Give up once, loudly, so the client stops waiting.
                    if (waited >= ReadyTimeoutSeconds)
                    {
                        SetPhaseLocked(LaunchPhase.Timeout);
                        StopTimerLocked();
                    }
                }

                // Outside the lock: this walks the desktop again and writes several lines.
                if (logDelivered)
                    DebugLogger.Log($"[Launch] target process is running — the launcher delivered; " +
                                    $"no longer watching for windows that want attention");
                if (dumpCandidates) DumpCandidates();
            }
            catch (Exception ex)
            {
                // Once per launch, not four times a second: a persistent failure here would
                // otherwise bury the log it is meant to help us read.
                if (!_tickErrorLogged)
                {
                    _tickErrorLogged = true;
                    DebugLogger.Log($"[Launch] tick failed: {ex}");
                }
            }
        }

        /// <summary>
        /// Once per launch, when the wait has gone on long enough to be suspicious: writes out
        /// every window the sweep considered, with the process path it managed to read and how
        /// much of its monitor it covers. Answers the only question that matters when a launch
        /// stalls with the game visibly on screen — was the window not seen, not attributed, or
        /// merely judged too small?
        /// </summary>
        private void DumpCandidates()
        {
            string? exeFile, dir, procName;
            HashSet<IntPtr>? windowsAtLaunch;
            lock (_lock)
            {
                exeFile = _targetExeFile;
                dir = _targetDirectory;
                procName = _targetProcessName;
                windowsAtLaunch = _windowsAtLaunch;
            }

            DebugLogger.Log($"[Launch]   target: exe={exeFile ?? "-"} dir={dir ?? "-"} proc={procName ?? "-"}");

            IntPtr fg = GetForegroundWindow();
            int shown = 0;

            bool Callback(IntPtr hwnd, IntPtr _)
            {
                if (shown >= 12) return false;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (!GetWindowRect(hwnd, out RECT r)) return true;

                long w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 200 || h < 150) return true;
                if (!GetMonitorSize(hwnd, out long area, out long mw, out long mh) || area <= 0) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                string? path = TryGetProcessPath(pid);
                double frac = (double)(w * h) / area;

                // Both rectangles, and where the window sits: a fraction that makes no sense is
                // the two being measured in different coordinate spaces, and only the raw numbers
                // can say which of the two is the odd one.
                shown++;
                DebugLogger.Log($"[Launch]   window {w}x{h} at {r.Left},{r.Top}" +
                                $" vs monitor {mw}x{mh} ({frac:P0})" +
                                (hwnd == fg ? " [foreground]" : "") +
                                $" pid={pid} path={path ?? "<unreadable>"}" +
                                $" match={Matches(path, pid, exeFile, dir, procName)}" +
                                // Says which windows the snapshot rule considers new. When a
                                // reveal fires on the wrong thing, this is the line that names it.
                                $" new={(windowsAtLaunch != null && !windowsAtLaunch.Contains(hwnd))}");
                return true;
            }

            EnumWindowsProc proc = Callback;
            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);
        }

        private void SetPhaseLocked(LaunchPhase phase)
        {
            if (_phase == phase) return;
            double secs = (DateTime.UtcNow - _launchedAtUtc).TotalSeconds;
            _phase = phase;

            // The foreground owner is the diagnostic: when a launch never resolves, this line says
            // whether something was in the way (a launcher, a login) or whether the game was there
            // all along and we simply failed to recognise its window.
            string who = phase == LaunchPhase.NeedsAttention && !string.IsNullOrEmpty(_foregroundName)
                ? $" — foreground: '{_foregroundName}'" +
                  (_foregroundIsNew ? " (opened after the launch)" : " (store client)")
                : "";

            DebugLogger.Log($"[Launch] {PhaseToWire(phase)} after {secs:F1}s" +
                            (string.IsNullOrEmpty(_gameName) ? "" : $" ({_gameName})") + who);
        }

        /// <summary>One sweep of the desktop: what we found, and who owns the screen meanwhile.</summary>
        private readonly struct Observation
        {
            public Observation(LaunchPhase phase, bool foreignForeground, string foregroundName,
                               string foregroundAny, bool foregroundIsNew)
            {
                Phase = phase;
                ForeignForeground = foreignForeground;
                ForegroundName = foregroundName;
                ForegroundAny = foregroundAny;
                ForegroundIsNew = foregroundIsNew;
            }

            public LaunchPhase Phase { get; }

            /// <summary>
            /// Something belonging to this launch is holding the screen and wants a click: the
            /// store's own client, or a window that opened after we started.
            /// </summary>
            public bool ForeignForeground { get; }

            /// <summary>Its process name, when <see cref="ForeignForeground"/> is set.</summary>
            public string ForegroundName { get; }

            /// <summary>
            /// Which of the two rules caught it. Diagnostics only — but the one that says whether
            /// a reveal came from the store list or from the snapshot, which is the difference
            /// between a rule that is working and one that is firing on the wrong thing.
            /// </summary>
            public bool ForegroundIsNew { get; }

            /// <summary>
            /// Whatever owns the screen, store client or not. Diagnostics only: it never decides
            /// anything, it just makes the log able to answer "what was in the way?".
            /// </summary>
            public string ForegroundAny { get; }
        }

        /// <summary>
        /// Walks the visible top-level windows once and reports the best thing it found:
        /// Ready if the target owns the foreground and fills its monitor, GameWindow if a window
        /// of the target merely exists, Launching if nothing matched — plus whether some other
        /// application is sitting in the foreground while we wait.
        /// </summary>
        private Observation Observe()
        {
            string? exeFile, dir, procName;
            string store;
            HashSet<IntPtr>? windowsAtLaunch;
            lock (_lock)
            {
                exeFile = _targetExeFile;
                dir = _targetDirectory;
                procName = _targetProcessName;
                store = _targetStore;
                windowsAtLaunch = _windowsAtLaunch;
            }

            IntPtr foreground = GetForegroundWindow();
            var pathCache = new Dictionary<uint, string?>();
            LaunchPhase best = LaunchPhase.Launching;

            bool Callback(IntPtr hwnd, IntPtr _)
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (!GetWindowRect(hwnd, out RECT rect)) return true;

                long w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0) return true;

                if (!GetMonitorArea(hwnd, out long monitorArea) || monitorArea <= 0) return true;

                // Clamped, because the two rectangles are not always in the same coordinate
                // space. On a host with a scaled display — and especially while the streaming
                // server has a virtual display alongside the real one — a window can measure
                // several times its monitor: 3840x2160 against a 1920x1080 monitor was observed
                // live. A window at least as large as its monitor covers it, whatever the cause,
                // so 1.0 is the honest answer and anything above it is noise.
                double fraction = Math.Min(1.0, (double)(w * h) / monitorArea);
                if (fraction < MinWindowAreaFraction) return true;   // splash / updater / tooltip

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                if (!pathCache.TryGetValue(pid, out string? exePath))
                {
                    exePath = TryGetProcessPath(pid);
                    pathCache[pid] = exePath;
                }

                if (!Matches(exePath, pid, exeFile, dir, procName)) return true;

                if (hwnd == foreground && fraction >= ReadyAreaFraction)
                {
                    best = LaunchPhase.Ready;
                    return false;   // best possible answer, stop walking
                }

                if (best != LaunchPhase.Ready) best = LaunchPhase.GameWindow;
                return true;
            }

            EnumWindowsProc proc = Callback;
            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);

            // Who has the screen while we wait? Not "anything that isn't the game" — that fired on
            // whatever window happened to be focused, and on this host the streaming machine is
            // also the desk you work at, so the answer was a text editor at ten seconds into every
            // single launch. It has to be something belonging to *this* launch: the store's own
            // client. Battle.net asking for a login, Steam showing a cloud-save conflict, the EA
            // updater — those are the host asking for a click. A leftover browser window is not.
            //
            // A dialog from neither (a missing DLL, a Windows error box) still isn't caught here;
            // the 90-second cap remains the backstop for everything we can't name.
            //
            // NOTE — everything here must be a *read* of window-manager state. Anything that
            // messages the owning thread (GetWindowText and friends send WM_GETTEXT synchronously)
            // blocks forever when that process isn't pumping, which is precisely the state a
            // launcher is in while it downloads. An earlier version asked for the title here and
            // hung the whole watcher: no phases, and no timeout either, because the tick thread
            // never came back. GetWindowRect and GetWindowThreadProcessId are safe; titles are not.
            bool foreign = false, foregroundIsNew = false;
            string foregroundName = "", foregroundAny = "";
            if (best != LaunchPhase.Ready && foreground != IntPtr.Zero &&
                IsWindowVisible(foreground) && IsSubstantialWindow(foreground))
            {
                GetWindowThreadProcessId(foreground, out uint fgPid);
                if (fgPid != 0)
                {
                    if (!pathCache.TryGetValue(fgPid, out string? fgPath))
                    {
                        fgPath = TryGetProcessPath(fgPid);
                        pathCache[fgPid] = fgPath;
                    }

                    if (!Matches(fgPath, fgPid, exeFile, dir, procName))
                    {
                        string fgName = fgPath != null
                            ? Path.GetFileNameWithoutExtension(fgPath)
                            : TryGetProcessName(fgPid);

                        foregroundAny = fgName;

                        // Either it belongs to the store this game came from, or it wasn't on
                        // screen when we started. See the note on _windowsAtLaunch: this is an
                        // OR and must stay one — the earlier version that turned the snapshot
                        // into a filter on the store check is what broke Battle.net logins.
                        bool appearedSinceLaunch =
                            windowsAtLaunch != null && !windowsAtLaunch.Contains(foreground);

                        if (IsStoreClient(fgName, store) || appearedSinceLaunch)
                        {
                            foreign = true;
                            foregroundName = fgName;
                            foregroundIsNew = appearedSinceLaunch;
                        }
                    }
                }
            }

            return new Observation(best, foreign, foregroundName, foregroundAny, foregroundIsNew);
        }

        /// <summary>
        /// Whether the target's process is running, sampled at most once every
        /// <see cref="ProcessScanEveryTicks"/> ticks and not at all once the answer is yes.
        /// Returns false for "not due" as well as "not there" — the caller only ever ORs the
        /// result into a sticky flag, so the two are the same to it.
        /// </summary>
        private bool ObserveTargetProcess()
        {
            string? exeFile, dir, procName;
            string store;

            lock (_lock)
            {
                if (_targetProcessSeen) return true;
                if (_phase is LaunchPhase.Idle or LaunchPhase.NotApplicable) return false;
                if (++_processScanTick < ProcessScanEveryTicks) return false;
                _processScanTick = 0;

                exeFile = _targetExeFile;
                dir = _targetDirectory;
                procName = _targetProcessName;
                store = _targetStore;
            }

            return TargetProcessRunning(exeFile, dir, procName, store);
        }

        /// <summary>
        /// True when some running process is the game this launch was for.
        /// <para>Cheapest question first: a known executable or process name is answered by
        /// <c>GetProcessesByName</c>, which asks the kernel for that one name instead of walking
        /// every process and reading its path. Only the install-directory case needs the full
        /// sweep, and only that case pays for the path reads.</para>
        /// <para>⚠️ Support executables are excluded, and that is load-bearing rather than tidy:
        /// <c>UbisoftGameLauncher64.exe</c> lives <em>inside the game's own install directory</em>,
        /// so without this the launcher itself would answer "the game is running" the instant it
        /// started — and a genuine login prompt would then never be reported at all. The list is
        /// the same one <see cref="IsStoreClient"/> reads, asked the third way round.</para>
        /// </summary>
        private static bool TargetProcessRunning(string? exeFile, string? dir, string? procName,
                                                 string store)
        {
            HashSet<string> supportExes = SessionProcessMonitor.SupportExesFor(store);

            // Xbox/UWP: the path is unreadable, the name is all there is.
            if (procName != null && !supportExes.Contains(procName) && AnyProcessNamed(procName))
                return true;

            if (exeFile != null)
            {
                string bare = Path.GetFileNameWithoutExtension(exeFile);
                if (!string.IsNullOrEmpty(bare) && !supportExes.Contains(bare) && AnyProcessNamed(bare))
                    return true;
            }

            if (dir == null) return false;

            // The expensive one, and the only one that needs it: anything living under the
            // game's folder counts, because a launcher hands off to a differently-named binary
            // there — the same reason the window sweep follows the folder and not the exe.
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        // Session 0 is the service session: a game never runs there, and every
                        // one of those processes fails the path read. See the note on
                        // SessionProcessMonitor.IsServiceSessionProcess — skipping them is what
                        // keeps this from throwing ~70 times a scan.
                        if (SessionProcessMonitor.IsServiceSessionProcess(p)) continue;
                        if (supportExes.Contains(p.ProcessName)) continue;

                        string? path = TryGetProcessPath((uint)p.Id);
                        if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { /* process exited mid-sweep — expected */ }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { /* GetProcesses() failed — treat as "not seen", the caller will ask again */ }

            return false;
        }

        private static bool AnyProcessNamed(string bareName)
        {
            try
            {
                var found = System.Diagnostics.Process.GetProcessesByName(bareName);
                foreach (var p in found) { try { p.Dispose(); } catch { } }
                return found.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// True when <paramref name="processName"/> is the store client (or one of its helpers)
        /// for the store this game came from — the only foreign windows that belong to the launch.
        /// <para>The list is <see cref="SessionProcessMonitor.SupportExesFor"/>, already curated
        /// for the opposite question ("don't credit this as the game played"). An empty store, or
        /// a store with no entry, means nothing qualifies and only the timeout can end the wait —
        /// which is the right answer when we can't tell what we're looking at.</para>
        /// </summary>
        private static bool IsStoreClient(string processName, string store)
        {
            if (string.IsNullOrEmpty(processName) || string.IsNullOrEmpty(store)) return false;
            return SessionProcessMonitor.SupportExesFor(store).Contains(processName);
        }

        /// <summary>
        /// Every visible top-level window right now, by handle. No size filter on purpose: the
        /// comparison later asks "was this handle on screen before the launch", and a window that
        /// was small then and substantial now is a window that changed — which is the case we want
        /// to catch, not exclude.
        /// <para>Reads only, as everywhere in this class: <c>GetWindowText</c> and friends send a
        /// synchronous message to the owning thread and block forever when that process isn't
        /// pumping, which is exactly the state a launcher is in while it downloads.</para>
        /// </summary>
        private static HashSet<IntPtr> SnapshotVisibleWindows()
        {
            var set = new HashSet<IntPtr>();

            bool Callback(IntPtr hwnd, IntPtr _)
            {
                if (IsWindowVisible(hwnd) && !IsIconic(hwnd)) set.Add(hwnd);
                return true;
            }

            EnumWindowsProc proc = Callback;
            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);

            return set;
        }

        /// <summary>
        /// A window big enough to be something the user is meant to look at. Absolute pixels on
        /// purpose, not a fraction of the monitor: a launcher's login window is the same size on a
        /// 1080p screen and on a 4K one, so a relative threshold would pass on one host and fail
        /// on the other.
        /// </summary>
        private static bool IsSubstantialWindow(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT r)) return false;
            return (r.Right - r.Left) >= 400 && (r.Bottom - r.Top) >= 300;
        }

        private static string TryGetProcessName(uint pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return "?"; }
        }

        private static bool Matches(string? exePath, uint pid, string? exeFile, string? dir, string? procName)
        {
            if (exePath != null)
            {
                if (dir != null && exePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
                if (exeFile != null &&
                    string.Equals(Path.GetFileName(exePath), exeFile, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Path unreadable (Xbox/UWP under WindowsApps, or a protected process): the process
            // name is the only handle we have left.
            if (procName != null)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (string.Equals(p.ProcessName, procName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { /* process gone between enumeration and lookup */ }
            }

            return false;
        }

        private static string? TryGetProcessPath(uint pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;   // elevated or protected — expected, not an error
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }

        private static bool GetMonitorArea(IntPtr hwnd, out long area) =>
            GetMonitorSize(hwnd, out area, out _, out _);

        private static bool GetMonitorSize(IntPtr hwnd, out long area, out long width, out long height)
        {
            area = width = height = 0;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            width  = info.rcMonitor.Right - info.rcMonitor.Left;
            height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            if (width <= 0 || height <= 0) return false;

            area = width * height;
            return true;
        }

        // ── Win32 ────────────────────────────────────────────────────────────

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);


        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
                                                             StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
