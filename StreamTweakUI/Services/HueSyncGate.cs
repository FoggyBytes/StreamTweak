using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace StreamTweak.Services
{
    /// <summary>
    /// 10.1.0: starts Hue Sync on this host only while the lights are free — SECURITY.md, "Hue Sync
    /// (10.1.0)".
    ///
    /// <para>A Hue bridge streams to one area at a time and a displaced Hue Sync does not resume
    /// (measured 03/11/2026: the client's stopped for good when the host's started). So the host
    /// must not START Hue Sync while another device syncs; closing it afterwards gives nothing back.
    /// With <c>HueHold</c> on, StreamTweak takes the place of Hue Sync's own start with Windows and
    /// starts it at three moments — StreamTweak's start, a session's end, once a minute while waiting
    /// — each time only if the bridge says no area is streaming (or, with "this device", none opened
    /// by that device's Hue Sync).</para>
    ///
    /// <para>⚠️ It never closes Hue Sync: closing it when a session starts is Setup › Apps, unchanged.
    /// And it fails open — a bridge that cannot be read starts Hue Sync, as Windows would have.</para>
    /// </summary>
    public sealed class HueSyncGate
    {
        public static HueSyncGate Instance { get; } = new();

        public event Action? Changed;

        // ── State the page and the bridge read ──────────────────────────────────
        public bool Paired => _key != null;
        public bool Pairing { get; private set; }
        /// <summary>"" · ok · timeout · no_bridge · bad_cert — the last pairing's outcome.</summary>
        public string PairResult { get; private set; } = "";
        public string BridgeModel => ConfigService.Get("HueBridgeModel", "");
        public bool Hold => ConfigService.GetBool("HueHold", false);
        /// <summary>"any" or "this"; with "this", <see cref="GiveWayClient"/> is the device that chose it.</summary>
        public string GiveWay => ConfigService.Get("HueGiveWay", "any") == "this" ? "this" : "any";
        public string GiveWayClient => ConfigService.Get("HueGiveWayClient", "");
        public bool Waiting { get; private set; }
        public HueReading? LastReading { get; private set; }

        private string? _key;
        private HueBridge? _bridge;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private Timer? _waitTimer;
        private DateTime _readAt = DateTime.MinValue;
        private Func<string, string?>? _clientName;
        // One start at a time: StreamTweak's start, the minute's check and a rule change can meet.
        private int _starting;

        private HueSyncGate() { }

        /// <summary>Called once, after the bridge's providers are wired (App.xaml.cs).</summary>
        public void Start(Func<string, string?> clientName)
        {
            _clientName = clientName;
            _key = Unprotect(ConfigService.Get("HueBridgeKey", ""));
            string addr = ConfigService.Get("HueBridgeAddress", "");
            string cert = ConfigService.Get("HueBridgeCert", "");
            // ⚠️ Never without the pinned certificate: an empty one would accept any (CheckCertificate).
            if (_key != null && addr.Length > 0 && cert.Length > 0)
                _bridge = new HueBridge(addr, cert);
            else
                _key = null;
            _ = TryStartAsync("StreamTweak started");
        }

        // ── Hue Sync on this PC ─────────────────────────────────────────────────

        /// <summary>Where Hue Sync's installer put it: the Uninstall entry named "Hue Sync", else Program Files.</summary>
        public static string? HueSyncPath()
        {
            foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (root == null) continue;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using var k = root.OpenSubKey(name);
                        if (k?.GetValue("DisplayName") is not string dn || !dn.Contains("Hue Sync", StringComparison.OrdinalIgnoreCase)) continue;
                        if (k.GetValue("InstallLocation") is string loc && loc.Length > 0)
                        {
                            string exe = Path.Combine(loc, "HueSync.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch { }
            }
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hue Sync", "HueSync.exe");
            return File.Exists(fallback) ? fallback : null;
        }

        public static bool IsHueSyncPath(string path) =>
            string.Equals(Path.GetFileName(path), "HueSync.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(path), "HueSyncStarter.exe", StringComparison.OrdinalIgnoreCase);

        public static bool HueSyncRunning()
        {
            var p = Process.GetProcessesByName("HueSync");
            foreach (var x in p) x.Dispose();
            return p.Length > 0;
        }

        /// <summary>Whether Hue Sync still starts with Windows by itself: the startup task of its package
        /// (Settings › Apps › Startup), or a Run entry. Read only — never changed here.</summary>
        public static bool HueSyncStartsWithWindows()
        {
            try
            {
                using var apps = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData");
                if (apps != null)
                    foreach (string pkg in apps.GetSubKeyNames().Where(n => n.StartsWith("Signify.HueSync_", StringComparison.OrdinalIgnoreCase)))
                    {
                        using var task = apps.OpenSubKey(pkg + @"\HueSyncStartupId");
                        // StartupTaskState: 2 Enabled, 4 EnabledByPolicy.
                        if (task?.GetValue("State") is int s && (s == 2 || s == 4)) return true;
                    }
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (run != null && run.GetValueNames().Any(n => (run.GetValue(n) as string ?? "").Contains("HueSync", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }
            return false;
        }

        // ── The gate ────────────────────────────────────────────────────────────

        /// <summary>Starts Hue Sync if the rule lets it now, or waits (and checks again every minute).</summary>
        public async Task TryStartAsync(string why, bool sessionEnded = false)
        {
            if (Interlocked.Exchange(ref _starting, 1) == 1) return;
            try { await TryStartCoreAsync(why, sessionEnded); }
            finally { Interlocked.Exchange(ref _starting, 0); }
        }

        private async Task TryStartCoreAsync(string why, bool sessionEnded)
        {
            if (!Hold || !Paired) { SetWaiting(false); return; }
            if (HueSyncRunning()) { SetWaiting(false); return; }
            // During a stream, Setup › Apps may have closed it on purpose: the session's end decides
            // (called with sessionEnded, since the session may not be closed yet at that point).
            if (!sessionEnded && SessionLogger.ActiveSessionId != null) { SetWaiting(true); return; }

            // A bridge that does not answer gets three tries, 3 s apart, before Hue Sync starts
            // anyway: right after a wake the network can come up a moment after StreamTweak, and a
            // start on the first miss would take the lights from the device the rule protects.
            var reading = await ReadAsync(force: true);
            for (int i = 0; i < 2 && reading != null && !reading.Ok && !reading.Unauthorized; i++)
            {
                await Task.Delay(3000);
                reading = await ReadAsync(force: true);
            }
            if (HueSyncRunning()) { SetWaiting(false); return; }
            if (reading != null && reading.Ok && Holds(reading))
            {
                if (!Waiting)
                    DebugLogger.Log($"[Hue] {why}: lights in use by {Describe(reading.ActiveArea)} — Hue Sync waits");
                SetWaiting(true);
                return;
            }
            string? exe = HueSyncPath();
            if (exe == null) { DebugLogger.Log("[Hue] Hue Sync not found on this PC"); SetWaiting(false); return; }
            // HueSyncStarter.exe beside it is what Hue Sync's own start with Windows runs: it starts
            // the app in the background, in the tray, without a window (Marcello, 03/11/2026).
            string starter = Path.Combine(Path.GetDirectoryName(exe)!, "HueSyncStarter.exe");
            if (File.Exists(starter)) exe = starter;
            DebugLogger.Log(reading != null && reading.Ok
                ? $"[Hue] {why}: lights free — starting Hue Sync"
                : $"[Hue] {why}: bridge not readable ({reading?.Error}) — starting Hue Sync anyway");
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); }
            catch (Exception ex) { DebugLogger.Log($"[Hue] Could not start Hue Sync: {ex.Message}"); }
            SetWaiting(false);
        }

        /// <summary>Whether the rule keeps Hue Sync from starting, given what the bridge reads now.</summary>
        public bool Holds(HueReading reading)
        {
            var area = reading.ActiveArea;
            if (area == null) return false;
            // Our own Hue Sync, a stream left from before a restart: nothing to give way to.
            if (string.Equals(area.OwnerDevice, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
            if (GiveWay == "any") return true;
            return SameDevice(area.OwnerDevice, _clientName?.Invoke(GiveWayClient));
        }

        private static bool SameDevice(string bridgeName, string? enrolledName)
        {
            const string prefix = "StreamLight @ ";
            if (string.IsNullOrEmpty(enrolledName) || string.IsNullOrEmpty(bridgeName)) return false;
            if (enrolledName.StartsWith(prefix, StringComparison.Ordinal)) enrolledName = enrolledName[prefix.Length..];
            return string.Equals(bridgeName.Trim(), enrolledName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Describe(HueArea? a) =>
            a == null ? "nobody" : $"{(a.OwnerApp.Length > 0 ? a.OwnerApp : "an app")}{(a.OwnerDevice.Length > 0 ? " on " + a.OwnerDevice : "")} ({a.Name})";

        private void SetWaiting(bool waiting)
        {
            if (waiting && _waitTimer == null)
                _waitTimer = new Timer(_ => _ = TryStartAsync("Waiting"), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            else if (!waiting && _waitTimer != null)
            {
                _waitTimer.Dispose();
                _waitTimer = null;
            }
            if (Waiting != waiting) { Waiting = waiting; Changed?.Invoke(); }
        }

        // ── The bridge ──────────────────────────────────────────────────────────

        /// <summary>A reading at most 3 s old (the client asks every few seconds while its tab is up).</summary>
        public async Task<HueReading?> ReadAsync(bool force = false)
        {
            if (!Paired || _bridge == null) return null;
            if (!force && LastReading != null && DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(3)) return LastReading;
            await _lock.WaitAsync();
            try
            {
                if (!force && LastReading != null && DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(3)) return LastReading;
                var r = await _bridge.ReadAsync(_key!);
                if (r.Unauthorized)
                {
                    DebugLogger.Log("[Hue] The bridge no longer accepts StreamTweak's key — unpaired");
                    Forget();
                }
                LastReading = r;
                _readAt = DateTime.UtcNow;
                return r;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Starts the 60 s pairing. Returns OK, ERR_BUSY or ERR_NO_BRIDGE at once; the
        /// outcome goes to <see cref="PairResult"/>.</summary>
        public async Task<string> PairAsync()
        {
            if (Pairing) return "ERR_BUSY";
            // mDNS under 2 s: HUEPAIR answers inside the client's 3 s.
            string? addr = HueBridge.AddressFromHueSync() ?? await HueBridge.DiscoverAsync(TimeSpan.FromMilliseconds(1800));
            if (addr == null) { PairResult = "no_bridge"; Changed?.Invoke(); return "ERR_NO_BRIDGE"; }
            Pairing = true;
            PairResult = "";
            Changed?.Invoke();
            _ = Task.Run(async () =>
            {
                using var b = new HueBridge(addr, null);
                string? key = await b.PairAsync($"StreamTweak#{Environment.MachineName}", TimeSpan.FromSeconds(60));
                if (key != null && b.PinnedCert != null)
                {
                    ConfigService.Set("HueBridgeAddress", addr);
                    ConfigService.Set("HueBridgeCert", b.PinnedCert);
                    ConfigService.Set("HueBridgeModel", b.Model);
                    ConfigService.Set("HueBridgeKey", Protect(key));
                    // Swapped under the read lock, so a read in flight never meets a disposed client.
                    await _lock.WaitAsync();
                    try
                    {
                        _bridge?.Dispose();
                        _bridge = new HueBridge(addr, b.PinnedCert);
                        _key = key;
                        LastReading = null;
                    }
                    finally { _lock.Release(); }
                    PairResult = "ok";
                    DebugLogger.Log($"[Hue] Paired with the bridge at {addr} ({b.Model})");
                }
                else
                {
                    PairResult = b.PairFailure.Length > 0 ? b.PairFailure : "timeout";
                    DebugLogger.Log($"[Hue] Pairing with {addr} ended: {PairResult}");
                }
                Pairing = false;
                Changed?.Invoke();
                if (PairResult == "ok") await TryStartAsync("Paired");
            });
            return "OK";
        }

        /// <summary>Forgets the key (host only). The entry stays on the bridge until removed in the Hue app.</summary>
        public void Forget()
        {
            ConfigService.Set("HueBridgeKey", "");
            ConfigService.Set("HueBridgeCert", "");
            _key = null;
            _bridge?.Dispose();
            _bridge = null;
            LastReading = null;
            SetWaiting(false);
            Changed?.Invoke();
        }

        public void SetHold(bool on)
        {
            ConfigService.Set("HueHold", on);
            Changed?.Invoke();
            _ = on ? TryStartAsync("Rule turned on") : Task.Run(() => SetWaiting(false));
        }

        /// <summary>"any", or "this" with the device that chose it (from the bridge or the host's own page).</summary>
        public void SetGiveWay(string mode, string? clientId)
        {
            ConfigService.Set("HueGiveWay", mode == "this" ? "this" : "any");
            if (mode == "this") ConfigService.Set("HueGiveWayClient", clientId ?? "");
            Changed?.Invoke();
            _ = TryStartAsync("Rule changed");
        }

        /// <summary>One line for the page and the client: what Hue Sync on this host is doing.</summary>
        public string HostState()
        {
            bool running = HueSyncRunning();
            var area = LastReading?.ActiveArea;
            if (running && area != null && string.Equals(area.OwnerDevice, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return "syncing";
            if (running) return "running";
            return Waiting ? "waiting" : "stopped";
        }

        public static string ModelName(string model) => model switch
        {
            "BSB003" => "Hue Bridge Pro",
            "BSB002" or "BSB001" => "Hue Bridge",
            _ => "Hue bridge",
        };

        // ── HUESYNC ─────────────────────────────────────────────────────────────

        public async Task<string> BuildJsonAsync(string? clientId, bool hostManagement)
        {
            var r = await ReadAsync();
            var areas = new JsonArray();
            foreach (var a in r?.Areas ?? Array.Empty<HueArea>())
                areas.Add(new JsonObject
                {
                    ["name"] = a.Name, ["lights"] = a.Lights, ["active"] = a.Active,
                    ["app"] = a.OwnerApp, ["device"] = a.OwnerDevice,
                });
            string? me = clientId == null ? null : _clientName?.Invoke(clientId);
            return new JsonObject
            {
                ["v"] = 1,
                ["host_management"] = hostManagement,
                ["this_device"] = me ?? "",
                ["host_device"] = Environment.MachineName,
                ["installed"] = HueSyncPath() != null,
                ["state"] = HostState(),
                ["startup_task"] = HueSyncStartsWithWindows(),
                ["bridge"] = new JsonObject
                {
                    ["paired"] = Paired, ["pairing"] = Pairing, ["pair_result"] = PairResult,
                    ["readable"] = r?.Ok == true, ["model"] = ModelName(BridgeModel),
                },
                ["areas"] = areas,
                ["rules"] = new JsonObject
                {
                    ["hold"] = Hold, ["give_way"] = GiveWay,
                    ["give_way_device"] = GiveWay == "this" ? _clientName?.Invoke(GiveWayClient) ?? "" : "",
                    ["give_way_mine"] = GiveWay == "this" && clientId != null && clientId == GiveWayClient,
                },
            }.ToJsonString();
        }

        // ── DPAPI (CurrentUser), without a package ─────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        private static byte[]? Dpapi(byte[] input, bool protect)
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
            var outBlob = new DataBlob();
            try
            {
                Marshal.Copy(input, 0, inBlob.pbData, input.Length);
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "StreamTweak Hue", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
                if (!ok) return null;
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static string Protect(string s) =>
            Convert.ToBase64String(Dpapi(System.Text.Encoding.UTF8.GetBytes(s), true) ?? Array.Empty<byte>());

        private static string? Unprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return null;
            try
            {
                var plain = Dpapi(Convert.FromBase64String(b64), false);
                return plain == null || plain.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }
    }
}
