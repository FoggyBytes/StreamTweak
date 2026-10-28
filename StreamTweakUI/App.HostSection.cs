using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StreamTweak.Nvidia;
using StreamTweak.Services;

namespace StreamTweak
{
    /// <summary>
    /// 10.0.0: what StreamLight 8.0's host section reads and writes over the bridge.
    ///
    /// Three reads (DASHBOARD, APPS, NVIDIA), one per tab of the section, each saying whether the
    /// asking device holds Host management; and three writes (SETAUDIO, SETAPP, SETSENTINEL),
    /// which the bridge lets through only for a verified signature from a device that holds it.
    /// The writes are switches over state that already exists on this host — never a path, never
    /// a command line. Adding an app, saving a Sentinel profile or picking an audio device stay
    /// on this host (plan 8.0 / 10.0, "Sicurezza").
    /// </summary>
    public partial class App
    {
        // The device sending this session's telemetry, as the last signed SESSIONDATA named it.
        private readonly object _sessionClientLock = new();
        private string?  _sessionClientId;
        private string?  _sessionClientName;
        private DateTime _sessionClientSeenUtc = DateTime.MinValue;
        private string?  _sessionClientRecordedFor;   // the session id already written

        // The Sentinel profile is a .nip on disk: read once per write of the file, not per request.
        private string?  _nvProfileCachePath;
        private DateTime _nvProfileCacheStamp;
        private JsonObject? _nvProfileCache;

        // The GPU's own name, read once: it does not change while the app runs.
        private string? _gpuName;

        private void RegisterHostSection()
        {
            var v = typeof(App).Assembly.GetName().Version;
            _bridge.HostVersion       = v == null ? null : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
            _bridge.DashboardProvider = BuildDashboardJsonAsync;
            _bridge.AppsProvider      = BuildAppsJsonAsync;
            _bridge.NvidiaProvider    = id => Task.FromResult(BuildNvidiaJson(id));
            _bridge.HostSettingWriter = ApplyHostSetting;
            _bridge.SessionsProvider  = AnswerSessions;
            RegisterLibrary();
            AppStateService.Instance.StreamingClientProvider = StreamingClientName;
        }

        /// <summary>The device streaming now, as "Streaming now" on Status shows it: from its
        /// telemetry of the last 15 s, so null for a client that sends none (Moonlight, Artemis…).</summary>
        private string? StreamingClientName()
        {
            if (SessionLogger.ActiveSessionId == null) return null;
            lock (_sessionClientLock)
                return DateTime.UtcNow - _sessionClientSeenUtc < TimeSpan.FromSeconds(15) ? _sessionClientName : null;
        }

        private bool HostManagementFor(string? clientId) =>
            clientId != null && AppStateService.Instance.BridgeAuth?.HasHostManagement(clientId) == true;

        // ── Session client ───────────────────────────────────────────────────

        /// <summary>
        /// Called for every SESSIONDATA. Remembers the device for "Streaming now" and names the
        /// session's client in the history once per session (<see cref="SessionLogger.RecordClient"/>).
        /// A batch that arrives before the session exists is retried by the next one, a second later.
        /// </summary>
        private void NoteSessionClient(string? clientId)
        {
            if (clientId == null) return;
            string? name = AppStateService.Instance.BridgeAuth?.GetClientName(clientId);
            string? sessionId = SessionLogger.ActiveSessionId;
            bool record;
            lock (_sessionClientLock)
            {
                _sessionClientId      = clientId;
                _sessionClientName    = name;
                _sessionClientSeenUtc = DateTime.UtcNow;
                record = sessionId != null && _sessionClientRecordedFor != sessionId;
            }
            if (record && SessionLogger.RecordClient(sessionId!, clientId, name))
                lock (_sessionClientLock) _sessionClientRecordedFor = sessionId;
        }

        // ── DASHBOARD ────────────────────────────────────────────────────────

        private async Task<string> BuildDashboardJsonAsync(string? clientId)
        {
            var o = new JsonObject
            {
                ["v"]               = 1,
                ["host_management"] = HostManagementFor(clientId),
                ["nvidia"]          = _nvidiaSentinel?.IsNvidiaAvailable == true,
            };

            // Streaming now. The device comes from its telemetry, so a client that sends none
            // (Moonlight, Artemis…) streams with an empty name: "another device".
            // ⚠️ 10.0.6: a stream LIVE, not a session open. The session outlives the client's
            // disconnect by a thirty-second grace period, and for that half minute StreamLight's
            // section showed the game just quit as still streaming — to "this device", then, once
            // the telemetry name aged out, to "another device" (28/10/2026, §99.4). The link
            // manager's flag is the live span: the server's connect to its disconnect.
            bool active = SessionLogger.ActiveSessionId != null && (_linkSpeed == null || _linkSpeed.SessionActive);
            string? who = null, whoId = null;
            lock (_sessionClientLock)
            {
                if (active && DateTime.UtcNow - _sessionClientSeenUtc < TimeSpan.FromSeconds(15))
                {
                    who   = _sessionClientName;
                    whoId = _sessionClientId;
                }
            }
            o["streaming"] = new JsonObject
            {
                ["active"]    = active,
                ["client"]    = who ?? "",
                ["client_id"] = whoId ?? "",
                ["game"]      = active ? SessionLogger.CurrentGameName(TimeSpan.FromSeconds(30)) ?? "" : "",
            };

            // Spatial audio: the switch is StreamLight's to flip, device and format stay here.
            bool enabled = ConfigService.GetBool("AudioMonitorEnabled", false);
            var format   = ConfigService.Get("AudioSpatialFormat", "DolbyAtmos") == "WindowsSonic"
                ? SpatialAudioFormat.WindowsSonic : SpatialAudioFormat.DolbyAtmos;
            string device = ConfigService.Get("AudioOutputDevice", "Steam Streaming Speakers");
            bool available = false, isActive = false;
            try
            {
                var (dolby, sonic) = await DolbyAudioMonitor.GetSpatialAudioCapabilitiesAsync(device);
                available = format == SpatialAudioFormat.DolbyAtmos ? dolby : sonic;
                if (available) isActive = await DolbyAudioMonitor.IsFormatActiveAsync(device, format);
            }
            catch { }
            o["audio"] = new JsonObject
            {
                ["enabled"]   = enabled,
                ["format"]    = format == SpatialAudioFormat.DolbyAtmos ? "Dolby Atmos for Headphones" : "Windows Sonic for Headphones",
                ["device"]    = device,
                ["available"] = available,
                ["active"]    = isActive,
                ["status"]    = AppStateService.Instance.CurrentSpatialAudioStatus ?? "",
            };

            return o.ToJsonString();
        }

        // ── APPS ─────────────────────────────────────────────────────────────

        private async Task<string> BuildAppsJsonAsync(string? clientId)
        {
            var apps = new JsonArray();
            foreach (var a in ManagedAppController.List())
            {
                bool found = false;
                try { found = File.Exists(a.Path); } catch { }
                apps.Add(new JsonObject
                {
                    ["id"]      = ManagedAppController.IdOf(a),
                    ["name"]    = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileNameWithoutExtension(a.Path) : a.Name,
                    ["managed"] = a.AutoManage,
                    // A path that is gone can still be switched, but the device says why it does nothing.
                    ["found"]   = found,
                    // The exe's own icon, a small PNG in base64 ("" when it has none). The path stays
                    // here: only the picture travels.
                    ["icon"]    = found ? await AppIconPngAsync(a.Path) : "",
                });
            }
            return new JsonObject
            {
                ["v"]               = 1,
                ["host_management"] = HostManagementFor(clientId),
                ["apps"]            = apps,
            }.ToJsonString();
        }

        // Icons by path and the exe's write time: read once, again only when the file changes.
        private readonly Dictionary<string, (DateTime stamp, string png)> _appIcons = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The icon Explorer shows for an exe, as a 48 px PNG in base64 — the same thumbnail the
        /// Apps page of this app draws, re-encoded as PNG so StreamLight can show it from a data URL.
        /// "" when Windows has none.
        /// </summary>
        private async Task<string> AppIconPngAsync(string path)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); } catch { return ""; }
            lock (_appIcons)
                if (_appIcons.TryGetValue(path, out var hit) && hit.stamp == stamp) return hit.png;

            string png = "";
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 48);
                if (thumb != null)
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumb);
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                    using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                    var bytes = new byte[output.Size];
                    using var reader = new Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(bytes);
                    png = Convert.ToBase64String(bytes);
                }
            }
            catch { png = ""; }

            lock (_appIcons) _appIcons[path] = (stamp, png);
            return png;
        }

        // ── NVIDIA ───────────────────────────────────────────────────────────

        private string BuildNvidiaJson(string? clientId)
        {
            var svc = _nvidiaSentinel;
            var o = new JsonObject
            {
                ["v"]               = 1,
                ["host_management"] = HostManagementFor(clientId),
                ["present"]         = svc?.IsNvidiaAvailable == true,
            };
            if (svc == null || !svc.IsNvidiaAvailable) return o.ToJsonString();

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string type = svc.DriverType;
            o["driver"] = (string.IsNullOrEmpty(type) ? "NVIDIA Driver" : $"NVIDIA {type} Driver")
                        + (svc.DriverVersion > 0f ? " " + svc.DriverVersion.ToString("0.00", inv) : "");
            o["driver_date"] = svc.DriverReleaseDate is { } d ? d.ToString("dd/MM/yyyy", inv) : "";
            o["gpu"] = _gpuName ??= ReadNvidiaGpuName();
            o["profile"] = ReadProfileSummary(svc.SnapshotPath);
            o["sentinel"] = new JsonObject
            {
                ["enabled"]      = svc.AutoRestoreEnabled,
                ["stuck"]        = svc.IsStuck,
                ["last_restore"] = svc.LastRestoreAt is { } at ? at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", inv) : "",
            };
            return o.ToJsonString();
        }

        /// <summary>The NVIDIA adapter's name ("NVIDIA GeForce RTX 5090") from Win32_VideoController,
        /// or "" when none is found. StreamLight shows it in the NVIDIA tab; the pairing data it used
        /// before is often empty.</summary>
        private static string ReadNvidiaGpuName()
        {
            try
            {
                using var session = Microsoft.Management.Infrastructure.CimSession.Create(null);
                foreach (var gpu in session.QueryInstances(@"root\cimv2", "WQL", "SELECT Name FROM Win32_VideoController"))
                {
                    string? name = gpu.CimInstanceProperties["Name"]?.Value as string;
                    if (!string.IsNullOrWhiteSpace(name) && name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        return name.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>The saved Sentinel profile as the NVIDIA page lists it: only the settings with a
        /// readable value (the same filter as <c>CapturedSettingVM.IsReadable</c>), and of those not
        /// the ones whose value is "N/A". <c>count</c> is the number listed, so the heading and the
        /// list agree.</summary>
        private JsonObject ReadProfileSummary(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new JsonObject { ["saved"] = false };

            DateTime stamp = File.GetLastWriteTime(path);
            lock (_sessionClientLock)
            {
                if (_nvProfileCache != null && _nvProfileCachePath == path && _nvProfileCacheStamp == stamp)
                    return (JsonObject)_nvProfileCache.DeepClone();
            }

            var settings = new JsonArray();
            try
            {
                var entries = NvidiaSentinelService.ReadSnapshot(path);
                NvidiaSettingCatalog.Warm();
                foreach (var e in entries)
                {
                    string? label = NvidiaSettingCatalog.GetValueLabel(e.SettingId, e.Value);
                    if (label == null || label.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase)) continue;
                    settings.Add(new JsonObject
                    {
                        ["name"]  = string.IsNullOrWhiteSpace(e.Name) ? e.SettingIdHex : e.Name,
                        ["value"] = label,
                    });
                }
            }
            catch { return new JsonObject { ["saved"] = false, ["error"] = true }; }

            var summary = new JsonObject
            {
                ["saved"]    = true,
                ["saved_at"] = stamp.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                ["count"]    = settings.Count,
                ["settings"] = settings,
            };
            lock (_sessionClientLock)
            {
                _nvProfileCachePath  = path;
                _nvProfileCacheStamp = stamp;
                _nvProfileCache      = (JsonObject)summary.DeepClone();
            }
            return summary;
        }

        // ── SESSIONS / SESSION / SERIES / COVER ──────────────────────────────

        // The finished sessions, newest first, parsed once per write of sessions.json: a page, a
        // report and a track are asked one after the other, and the file holds every series of
        // every session.
        private readonly object _historyLock = new();
        private DateTime _historyStamp = DateTime.MinValue;
        private List<SessionEntry> _history = new();

        private List<SessionEntry> History()
        {
            DateTime stamp = SessionLogger.LastWriteUtc;
            lock (_historyLock)
            {
                if (stamp != _historyStamp)
                {
                    _history = SessionLogger.Load()
                        .Where(s => s.EndTime != null && !s.IsDebugSession)
                        .OrderByDescending(s => s.StartTime)
                        .ToList();
                    _historyStamp = stamp;
                }
                return _history;
            }
        }

        private static bool IsMine(SessionEntry s, string? clientId) =>
            clientId != null && string.Equals(s.ClientId, clientId, StringComparison.OrdinalIgnoreCase);

        private SessionEntry? FindSession(string id)
        {
            var all = History();
            return all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // The track names StreamLight asks for → the series behind them.
        private static List<float>? SeriesOf(SessionEntry s, string track) => track switch
        {
            "HL"    => s.HostLatencyTimeSeries,
            "RTT"   => s.RttTimeSeries,
            "DROPS" => s.DropsTimeSeries,
            "BR"    => s.BitrateTimeSeries,
            "DEC"   => s.DecodeTimeSeries,
            "GPU"   => s.HostGpuTimeSeries,
            "POWER" => s.HostPowerTimeSeries,
            // 10.0.4: the other series of the two host tracks, drawn as strips under GPU and Power
            // (StreamLight 8.0.1). Listed apart in SESSION's "subtracks", so an 8.0.0 client,
            // which shows every name in "tracks" as a tab, never sees them.
            "ENC"    => s.HostEncTimeSeries,
            "CPU"    => s.HostCpuTimeSeries,
            "PLIMIT" => s.HostPowerLimitTimeSeries,
            "TEMP"   => s.HostGpuTempTimeSeries,
            "PCAP"   => s.HostPowerCappedTimeSeries,
            _       => null,
        };
        private static readonly string[] SeriesTracks = { "HL", "RTT", "DROPS", "BR", "DEC", "GPU", "POWER" };
        private static readonly string[] SubTracks    = { "ENC", "CPU", "PLIMIT", "TEMP", "PCAP" };

        private static JsonArray Checks(SessionEntry s)
        {
            var q = s.QualityStats;
            if (q == null || q.SampleCount < 2) return new JsonArray(0, 0, 0, 0);
            var p = QualityGradeCalculator.EvaluateParts(q, q.TargetFps);
            return new JsonArray((int)p.Drops, (int)p.Rtt, (int)p.HostLatency, (int)p.LateFrames);
        }

        private static string Iso(DateTime t) => t.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        private static JsonObject Summary(SessionEntry s, string? clientId)
        {
            var games = new JsonArray();
            foreach (var g in s.GamesDetected ?? new List<string>()) games.Add(g);
            return new JsonObject
            {
                ["id"]     = s.Id,
                ["start"]  = Iso(s.StartTime),
                ["end"]    = Iso(s.EndTime ?? s.StartTime),
                ["games"]  = games,
                ["client"] = s.ClientName ?? "",
                ["mine"]   = IsMine(s, clientId),
                ["grade"]  = (int)(s.Grade ?? QualityGrade.NoData),
                ["checks"] = Checks(s),
                ["codec"]  = EncoderSummary.Of(s.StreamSpans, s.EndTime).Primary ?? "",
                ["fps"]    = s.QualityStats?.TargetFps ?? 0,
            };
        }

        /// <summary>
        /// The Sessions tab of StreamLight 8.0 (10.0.0). Read-only, and nothing here names a file:
        /// sessions travel by id, covers as thumbnails.
        ///   SESSIONS &lt;offset&gt; &lt;count&gt; ALL|MINE → {v,total,total_all,total_mine,items[summary]}
        ///   SESSION &lt;id&gt;                      → summary + stats, game and stream spans, tracks,
        ///                                         subtracks, enc_bypass[], enc_bypass_all (10.0.4)
        ///   SERIES &lt;id&gt; &lt;track&gt;             → {v,track,points[]} — a track or a subtrack
        ///   COVER &lt;id&gt; &lt;n&gt;                  → {v,cover} (base64 PNG, "" when there is none)
        /// </summary>
        private string AnswerSessions(string verb, string arg, string? clientId)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string[] w = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (verb)
            {
                case "SESSIONS":
                {
                    if (w.Length != 3 || !int.TryParse(w[0], out int offset) || !int.TryParse(w[1], out int count)
                            || offset < 0 || count < 1 || count > 100 || (w[2] != "ALL" && w[2] != "MINE"))
                        return "ERR";
                    var all  = History();
                    var mine = all.Where(s => IsMine(s, clientId)).ToList();
                    var list = w[2] == "MINE" ? mine : all;
                    var items = new JsonArray();
                    foreach (var s in list.Skip(offset).Take(count)) items.Add(Summary(s, clientId));
                    return new JsonObject
                    {
                        ["v"] = 1, ["total"] = list.Count, ["total_all"] = all.Count,
                        ["total_mine"] = mine.Count, ["items"] = items,
                    }.ToJsonString();
                }

                case "SESSION":
                {
                    if (w.Length != 1) return "ERR";
                    var s = FindSession(w[0]);
                    if (s == null) return "ERR_UNKNOWN_SESSION";
                    var o = Summary(s, clientId);
                    o["v"] = 1;
                    var q = s.QualityStats;
                    if (q != null)
                    {
                        o["stats"] = new JsonObject
                        {
                            ["fps_avg"] = q.FpsAvg, ["rtt"] = q.RttAvgMs, ["rtt_max"] = q.RttMaxMs,
                            ["jitter"] = q.JitterAvgMs, ["drops"] = q.DropRatePct, ["decode"] = q.DecodeAvgMs,
                            ["bitrate"] = q.BitrateAvgMbps, ["target"] = q.TargetBitrateMbps,
                            ["host_lat"] = q.HostLatencyAvgMs, ["host_lat_max"] = q.HostLatencyMaxMs,
                            ["late"] = q.HostLatencyOverBudgetPct, ["gpu"] = q.HostGpuAvg,
                            ["gpu_temp"] = q.HostGpuTempAvg, ["cpu"] = q.HostCpuAvg,
                        };
                    }
                    var spans = new JsonArray();
                    foreach (var g in s.GameSpans ?? new List<GameSpan>())
                        spans.Add(new JsonObject { ["name"] = g.Name, ["start"] = Iso(g.Start), ["end"] = Iso(g.End) });
                    o["game_spans"] = spans;
                    var streams = new JsonArray();
                    foreach (var st in s.StreamSpans ?? new List<StreamSpan>())
                        streams.Add(new JsonObject
                        {
                            ["start"] = Iso(st.Start), ["end"] = Iso(st.End ?? s.EndTime ?? st.Start),
                            ["codec"] = string.IsNullOrEmpty(st.Encoder) ? "" : EncoderNames.Label(st.Encoder),
                        });
                    o["stream_spans"] = streams;
                    var tracks = new JsonArray();
                    foreach (var t in SeriesTracks)
                        if (SeriesOf(s, t) is { Count: > 0 }) tracks.Add(t);
                    o["tracks"] = tracks;
                    var subtracks = new JsonArray();
                    foreach (var t in SubTracks)
                        if (SeriesOf(s, t) is { Count: > 0 }) subtracks.Add(t);
                    o["subtracks"] = subtracks;
                    // The streams whose encoder the Enc series cannot see (PyroWave on the shaders,
                    // software on the CPU — §82): the client shows Enc as n/a over them, with the
                    // encoder's name.
                    var bypass = new JsonArray();
                    foreach (var st in s.StreamSpans ?? new List<StreamSpan>())
                        if (!string.IsNullOrWhiteSpace(st.Encoder) && !EncoderNames.UsesVideoEncodeEngine(st.Encoder))
                            bypass.Add(new JsonObject
                            {
                                ["start"] = Iso(st.Start), ["end"] = Iso(st.End ?? s.EndTime ?? st.Start),
                                ["label"] = EncoderNames.Label(st.Encoder),
                            });
                    o["enc_bypass"] = bypass;
                    o["enc_bypass_all"] = EncoderSummary.Of(s.StreamSpans, s.EndTime).AllBypass;
                    return o.ToJsonString();
                }

                case "SERIES":
                {
                    if (w.Length != 2) return "ERR";
                    var s = FindSession(w[0]);
                    if (s == null) return "ERR_UNKNOWN_SESSION";
                    var data = SeriesOf(s, w[1]);
                    if (data == null) return "ERR";
                    // Two decimals are more than any track is read to; they keep the line short.
                    var points = new JsonArray();
                    foreach (float f in data) points.Add(Math.Round(f, 2));
                    return new JsonObject { ["v"] = 1, ["track"] = w[1], ["points"] = points }.ToJsonString();
                }

                case "COVER":
                {
                    if (w.Length != 2 || !int.TryParse(w[1], out int n) || n < 0) return "ERR";
                    var s = FindSession(w[0]);
                    if (s == null) return "ERR_UNKNOWN_SESSION";
                    var games = s.GamesDetected ?? new List<string>();
                    string cover = "";
                    if (n < games.Count)
                    {
                        string? path = LastSessionReport.CoverPathFor(s, games[n]);
                        // Nowhere on this PC: fetched again (10.0.4), waited for a short while —
                        // StreamLight gives up on a reply after 3 s. Late, it is there next time.
                        if (path == null)
                        {
                            string? snap = null;
                            s.GamesDetectedCoverPaths?.TryGetValue(games[n], out snap);
                            var fetch = CoverRecovery.RecoverAsync(games[n], snap);
                            if (fetch.Wait(TimeSpan.FromSeconds(1.5))) path = fetch.Result;
                        }
                        if (path != null) cover = LastSessionReport.ThumbnailBase64(path) ?? "";
                    }
                    return new JsonObject { ["v"] = 1, ["cover"] = cover }.ToJsonString();
                }
            }
            return "ERR";
        }

        // ── Writes ───────────────────────────────────────────────────────────

        /// <summary>
        /// SETAUDIO ON|OFF, SETAPP &lt;id&gt; ON|OFF, SETSENTINEL ON|OFF. The bridge has already checked
        /// the signature and Host management. Replies OK, or ERR_UNSUPPORTED / ERR_UNKNOWN_APP /
        /// ERR_NO_PROFILE / ERR when the request does not make sense on this host.
        /// </summary>
        private string ApplyHostSetting(string verb, string arg)
        {
            string[] words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            static bool? Switch(string w) => w == "ON" ? true : w == "OFF" ? false : null;

            switch (verb)
            {
                case "SETAUDIO":
                {
                    if (words.Length != 1 || Switch(words[0]) is not bool on) return "ERR";
                    // Same path as the Audio page's switch, on the UI thread where the monitor lives.
                    _dispatcher.TryEnqueue(() =>
                    {
                        ConfigService.Set("AudioMonitorEnabled", on);
                        AppStateService.Instance.SetAudioMonitorEnabledAction?.Invoke(on);
                    });
                    return "OK";
                }

                case "SETAPP":
                {
                    if (words.Length != 2 || Switch(words[1]) is not bool on) return "ERR";
                    // The id is hex; anything else cannot name an app here.
                    if (words[0].Length != 16 || !words[0].All(Uri.IsHexDigit)) return "ERR";
                    return ManagedAppController.SetAutoManage(words[0], on) ? "OK" : "ERR_UNKNOWN_APP";
                }

                case "LIBSYNC":
                    // No argument is accepted: the client cannot choose what is synced or restarted.
                    return words.Length == 0 ? StartLibrarySync() : "ERR";

                case "DELETESESSION":
                {
                    // 10.0.6: StreamLight's Sessions tab deletes a session as the Sessions page does.
                    // An id, nothing else: letters, digits and dashes (the bridge upper-cases the line,
                    // and ids are compared without case). Never the session still open.
                    if (words.Length != 1 || words[0].Length > 64
                            || !words[0].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return "ERR";
                    if (FindSession(words[0]) == null) return "ERR_UNKNOWN_SESSION";
                    bool deleted = SessionLogger.DeleteSession(words[0]);
                    DebugLogger.Log($"[Sessions] DELETESESSION {words[0]} → {(deleted ? "deleted" : "not found")}");
                    return deleted ? "OK" : "ERR_UNKNOWN_SESSION";
                }

                case "SETSENTINEL":
                {
                    if (words.Length != 1 || Switch(words[0]) is not bool on) return "ERR";
                    var svc = _nvidiaSentinel;
                    if (svc == null || !svc.IsNvidiaAvailable) return "ERR_UNSUPPORTED";
                    // Arming without a saved profile would protect nothing: the profile is saved here.
                    if (on && !svc.HasSnapshot) return "ERR_NO_PROFILE";
                    _dispatcher.TryEnqueue(() =>
                    {
                        svc.SetAutoRestoreEnabled(on);
                        ConfigService.Set("NvidiaAutoRestore", on);
                    });
                    return "OK";
                }
            }
            return "ERR";
        }
    }
}
