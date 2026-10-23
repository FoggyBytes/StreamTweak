using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StreamTweak.Services;

namespace StreamTweak
{
    /// <summary>
    /// 10.0.0: StreamLight 8.0's Library tab — what the streaming server lists, and the one action
    /// a client can ask for beyond switches: sync the library, then restart the server so it shows
    /// the new list. SECURITY.md ("Sync and restart") is the contract; keep the two in step.
    ///
    ///   LIBRARY        → the list, the server's state, the job (read, gated like STATS)
    ///   LIBSYNC        → start the job (a verified signature AND Host management: the bridge checks)
    ///
    /// Nothing that travels names a file or a command: games by a hex id of their name, and LIBSYNC
    /// takes no argument at all. No covers: StreamLight draws its own box art, the same pictures as
    /// its host page — a thumbnail from here went grainy at shelf size (21/10/2026).
    /// </summary>
    public partial class App
    {
        // How long a finished job keeps another away: a restart cuts the host off for a minute.
        private static readonly TimeSpan LibrarySyncCooldown = TimeSpan.FromMinutes(2);
        // Past this, a server that does not answer again is reported, not waited for.
        private static readonly TimeSpan LibraryRestartLimit = TimeSpan.FromSeconds(60);

        // The job: idle · scanning · writing · stopping · starting · waiting · done · failed
        private readonly object _libJobLock = new();
        private string   _libPhase = "idle";
        private DateTime _libStartedUtc;
        private DateTime _libEndedUtc = DateTime.MinValue;
        private string   _libError = "";
        private List<string> _libAdded = new(), _libRemoved = new();

        // The server's service and its Steam sync, read at most every 15 s: the CIM query costs.
        private StreamingServerService? _libService;
        private bool     _libSteamSync;
        private DateTime _libServiceReadUtc = DateTime.MinValue;

        private void RegisterLibrary()
        {
            _bridge.LibraryProvider = AnswerLibrary;
            // 10.0: the Library page on the host runs the same job, with the same refusals.
            AppStateService.Instance.LibrarySyncStarter  = () => StartLibrarySync("on this host");
            // Clear library: the same job with a different first step, from the host only — the
            // bridge has no verb for it, and LIBSYNC still takes no argument.
            AppStateService.Instance.LibraryClearStarter = () => StartLibrarySync("on this host", clear: true);
            AppStateService.Instance.LibraryJobReader   = ReadLibraryJob;
        }

        /// <summary>The job as the host's Library page shows it: its phase, how long it has run,
        /// what it changed, why it failed, how long before another may start.</summary>
        private AppStateService.LibraryJob ReadLibraryJob()
        {
            lock (_libJobLock)
            {
                bool running = _libPhase is "scanning" or "writing" or "stopping" or "starting" or "waiting";
                double cooldown = _libEndedUtc == DateTime.MinValue ? 0
                                : Math.Max(0, (LibrarySyncCooldown - (DateTime.UtcNow - _libEndedUtc)).TotalSeconds);
                return new AppStateService.LibraryJob(
                    _libPhase,
                    _libPhase == "idle" ? TimeSpan.Zero : (running ? DateTime.UtcNow : _libEndedUtc) - _libStartedUtc,
                    _libAdded.ToList(), _libRemoved.ToList(), _libError, (int)Math.Ceiling(cooldown),
                    _libService?.ServerName ?? "");
            }
        }

        private static string GameId(string name) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant())))[..16];

        private StreamingServerService? LibraryService(bool fresh = false)
        {
            lock (_libJobLock)
            {
                if (!fresh && DateTime.UtcNow - _libServiceReadUtc < TimeSpan.FromSeconds(15)) return _libService;
            }
            var svc = StreamingServer.ServiceFor(SunshineSync.FindAppsJsonPath());
            bool steam = svc != null && StreamingServer.SteamSyncOn(svc);
            lock (_libJobLock)
            {
                _libService = svc;
                _libSteamSync = steam;
                _libServiceReadUtc = DateTime.UtcNow;
            }
            return svc;
        }

        // ── Reads ────────────────────────────────────────────────────────────

        private string AnswerLibrary(string verb, string arg, string? clientId)
        {
            var state = GameLibraryState.Current;
            var svc = LibraryService();
            bool steamSync;
            string phase, error;
            DateTime started, ended;
            List<string> added, removed;
            lock (_libJobLock)
            {
                steamSync = _libSteamSync;
                phase = _libPhase; error = _libError;
                started = _libStartedUtc; ended = _libEndedUtc;
                added = _libAdded.ToList(); removed = _libRemoved.ToList();
            }
            bool running = phase is "scanning" or "writing" or "stopping" or "starting" or "waiting";

            var games = new JsonArray();
            foreach (var g in state.Games)
                games.Add(new JsonObject
                {
                    ["id"] = GameId(g.Name), ["name"] = g.Name, ["store"] = g.Store,
                    ["state"] = g.IsManual ? "manual" : g.Enabled ? "listed" : "off",
                });

            var own = new JsonArray();
            foreach (string name in ServerOwnEntries()) own.Add(name);

            string serverState = svc == null ? "missing"
                               : phase is "stopping" or "starting" or "waiting" ? "restarting"
                               : StreamingServer.State(svc.Name) switch { "Running" => "running", "Stopped" => "stopped", "" => "unknown", _ => "restarting" };

            double cooldown = ended == DateTime.MinValue ? 0
                            : Math.Max(0, (LibrarySyncCooldown - (DateTime.UtcNow - ended)).TotalSeconds);

            return new JsonObject
            {
                ["v"] = 1,
                ["host_management"] = HostManagementFor(clientId),
                ["server"] = svc?.ServerName ?? "",
                ["server_state"] = serverState,
                ["last_sync"] = state.LastSyncUtc is DateTime t ? Iso(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : "",
                ["steam_sync"] = steamSync,
                // Every game kept out: the excluded ones in the list (state "off") and those removed
                // before 10.0, which survive only as keys without a name.
                ["excluded"] = state.Games.Count(g => !g.Enabled) + state.ExcludedGames.Count,
                ["own"] = own,
                ["games"] = games,
                ["game_running"] = _launchWatcher.RunningLaunchedGame() ?? "",
                ["streaming"] = running ? false : LogParser.HasActiveStreamSockets(),
                ["job"] = new JsonObject
                {
                    ["phase"] = phase,
                    ["elapsed_ms"] = phase == "idle" ? 0 : (long)((running ? DateTime.UtcNow : ended) - started).TotalMilliseconds,
                    ["added"] = new JsonArray(added.Select(n => (JsonNode)n).ToArray()),
                    ["removed"] = new JsonArray(removed.Select(n => (JsonNode)n).ToArray()),
                    ["error"] = error,
                    ["cooldown_s"] = (int)Math.Ceiling(cooldown),
                },
            }.ToJsonString();
        }

        // The entries of the server's own (Desktop, Steam Big Picture…): every app in apps.json
        // StreamTweak did not write. Names only.
        private static List<string> ServerOwnEntries()
        {
            var names = new List<string>();
            try
            {
                string? path = SunshineSync.FindAppsJsonPath();
                if (path == null) return names;
                if (JsonNode.Parse(File.ReadAllText(path))?["apps"] is JsonArray apps)
                    foreach (var a in apps)
                        if (a?["_streamtweak_managed"] == null && a?["name"]?.GetValue<string>() is string n)
                            names.Add(n);
            }
            catch { }
            return names;
        }

        // ── LIBSYNC ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the job, or says why not. Returns at once: the client follows it through LIBRARY.
        /// With <paramref name="clear"/> (the host's Clear library) the first step empties the list
        /// instead of syncing it; the refusals and the restart are the same.
        /// </summary>
        private string StartLibrarySync(string askedBy = "by a client", bool clear = false)
        {
            lock (_libJobLock)
            {
                if (_libPhase is "scanning" or "writing" or "stopping" or "starting" or "waiting") return "ERR_BUSY";
                if (_libEndedUtc != DateTime.MinValue && DateTime.UtcNow - _libEndedUtc < LibrarySyncCooldown) return "ERR_BUSY";
            }
            if (LogParser.HasActiveStreamSockets()) return "ERR_STREAMING";
            if (_launchWatcher.RunningLaunchedGame() != null) return "ERR_GAME_RUNNING";
            var svc = LibraryService(fresh: true);
            if (svc == null) return "ERR_NO_SERVER";

            lock (_libJobLock)
            {
                _libPhase = clear ? "writing" : "scanning";
                _libStartedUtc = DateTime.UtcNow;
                _libError = "";
                _libAdded = new(); _libRemoved = new();
            }
            DebugLogger.Log($"[Library] {(clear ? "clear" : "sync")} and restart of {svc.ServerName} asked {askedBy}");
            _ = Task.Run(() => RunLibraryJobAsync(svc, clear));
            return "OK";
        }

        private void LibraryPhase(string phase) { lock (_libJobLock) _libPhase = phase; }

        private void LibraryEnd(string phase, string error = "")
        {
            lock (_libJobLock)
            {
                _libPhase = phase;
                _libError = error;
                _libEndedUtc = DateTime.UtcNow;
            }
            DebugLogger.Log($"[Library] job {phase}" + (error.Length > 0 ? $": {error}" : ""));
        }

        private async Task RunLibraryJobAsync(StreamingServerService svc, bool clear = false)
        {
            try
            {
                static HashSet<string> Listed() => new(GameLibraryState.Current.Games
                    .Where(g => g.Enabled).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
                var before = Listed();
                if (clear)
                {
                    // 1-2. Clear library: everything StreamTweak wrote leaves the server's list.
                    if (await GameLibraryService.ClearAsync() is string failure)
                    {
                        LibraryEnd("failed", failure);
                        return;
                    }
                }
                else
                {
                    // 1-2. The sync — excluded games stay out.
                    string result = await GameLibraryService.PerformSyncAsync();
                    // PerformSyncAsync reports in prose; a finished sync is the one that says "Done".
                    if (!result.StartsWith("Done", StringComparison.Ordinal))
                    {
                        LibraryEnd("failed", result.Split('\n')[0]);
                        return;
                    }
                }
                LibraryPhase("writing");
                var after = Listed();
                lock (_libJobLock)
                {
                    _libAdded   = after.Except(before, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
                    _libRemoved = before.Except(after, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
                }

                // 3-4. The restart, by the service. A client may have connected during the scan.
                if (LogParser.HasActiveStreamSockets())
                {
                    LibraryEnd("failed", "streaming");
                    return;
                }
                LibraryPhase("stopping");
                var restart = Task.Run(() => SpeedChanger.RestartStreamingServer(svc.Name));
                bool sawStopped = false;
                while (!restart.IsCompleted)
                {
                    string st = StreamingServer.State(svc.Name);
                    if (st == "Stopped") sawStopped = true;
                    else if (sawStopped) LibraryPhase("starting");
                    await Task.WhenAny(restart, Task.Delay(500));
                }
                string reply = await restart;
                if (reply != "OK")
                {
                    LibraryEnd("failed", reply.StartsWith("ERROR:") ? reply[6..] : reply);
                    return;
                }

                // 5. Back when the server answers again, not when its service says it runs.
                LibraryPhase("waiting");
                var limit = DateTime.UtcNow + LibraryRestartLimit;
                while (DateTime.UtcNow < limit)
                {
                    if (StreamingServer.IsAnswering(svc)) { LibraryEnd("done"); return; }
                    await Task.Delay(1000);
                }
                LibraryEnd("failed", "no answer");
            }
            catch (Exception ex)
            {
                LibraryEnd("failed", ex.Message);
            }
        }
    }
}
