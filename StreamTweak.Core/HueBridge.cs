using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>One entertainment area of a Hue bridge, as <see cref="HueBridge.ReadAsync"/> found it.</summary>
    public sealed record HueArea(string Name, int Lights, bool Active, string OwnerApp, string OwnerDevice);

    /// <summary>What a read of the bridge returned. <see cref="Unauthorized"/> means the key is no
    /// longer accepted (removed from the Hue app); <see cref="Error"/> anything else that failed.</summary>
    public sealed class HueReading
    {
        public bool Ok { get; init; }
        public bool Unauthorized { get; init; }
        public string Error { get; init; } = "";
        public IReadOnlyList<HueArea> Areas { get; init; } = Array.Empty<HueArea>();
        public HueArea? ActiveArea => Areas.FirstOrDefault(a => a.Active);
    }

    /// <summary>
    /// 10.1.0: the Philips Hue bridge, read-only — SECURITY.md, "Hue Sync (10.1.0)".
    ///
    /// <para><b>Why at all.</b> A bridge streams to ONE entertainment area at a time (Hue Sync's own
    /// bridge.json says <c>MaxNoStreamingSessions: 1</c>), and Hue Sync takes the lights the moment it
    /// starts syncing. Measured 03/11/2026: the host's area ("PC", one light) and the client's ("TV
    /// Soggiorno", three) share no light, yet the host's Hue Sync starting closes the client's stream,
    /// and the client's does not get it back by itself. So "are the lights free?" means "is ANY area of
    /// the bridge streaming?", not the host's area alone.</para>
    ///
    /// <para><b>What it asks.</b> Two v1 reads: <c>/groups</c> (the entertainment groups, their lights,
    /// <c>stream.active</c> and <c>stream.owner</c>) and <c>/config</c>, whose whitelist turns the owner
    /// into the app's name — "Hue Sync#FOGGY-STREAM", the app and the PC it runs on. The v2 API says
    /// which area is active too, but names the streamer only by an id with nothing to resolve it
    /// against. Nothing is ever written to the bridge apart from asking it for a key when paired; the
    /// whitelist's keys never leave this class.</para>
    ///
    /// <para><b>Whose bridge.</b> Its certificate must be issued by Signify — one of the two Hue root
    /// CAs below — and named after the bridge id the bridge itself reports (CN = "C42996FFFEC45CFA" on
    /// the BSB003). The certificate seen at pairing is pinned: a different one later is refused,
    /// unless it is a re-issue of the same bridge's (Signify's, same id), which is taken over.</para>
    ///
    /// <para>⚠️ 10.1.1 (security review, 05/11/2026): until then the name was the only check before
    /// pairing, and a name is whatever the certificate's maker writes — any device answering the mDNS
    /// query first could present itself as a bridge with a certificate of its own making.</para>
    /// </summary>
    public sealed class HueBridge : IDisposable
    {
        public string Address { get; }
        /// <summary>SHA-256 of the bridge's certificate (hex), pinned at pairing. Null before.</summary>
        public string? PinnedCert { get; private set; }
        public string BridgeId { get; private set; } = "";
        public string Model { get; private set; } = "";
        /// <summary>Why the last <see cref="PairAsync"/> gave no key: no_bridge · bad_cert · timeout.</summary>
        public string PairFailure { get; private set; } = "";
        /// <summary>A certificate that replaced the pinned one — the same bridge, re-issued by Signify
        /// (a firmware update) — for the caller to save as the new pin. Null when nothing changed.</summary>
        public string? RenewedCert { get; private set; }

        private readonly HttpClient _http;
        private readonly string? _expectedId;
        private X509Certificate2? _lastCert;
        private bool _lastFromSignify;

        /// <summary>
        /// Signify's two root CAs for Hue bridges: "root-bridge" (2017-2038, Philips Hue) and "Hue Root
        /// CA 01" (2025-2050, Signify Hue, with the May 2025 firmware 1971060010 that replaced the
        /// self-signed certificates). Published on the Hue developer site behind a login; these are
        /// the copies openHAB's Hue binding ships (huebridge_cacert.pem), and FOGGY's BSB003 verifies
        /// against the first (openssl verify and X509Chain, 05/11/2026). SHA-256: F0BD8E65…8D8ACF and
        /// D8B89448…FE0328B8.
        /// </summary>
        private const string SignifyRootsPem = @"-----BEGIN CERTIFICATE-----
MIICMjCCAdigAwIBAgIUO7FSLbaxikuXAljzVaurLXWmFw4wCgYIKoZIzj0EAwIw
OTELMAkGA1UEBhMCTkwxFDASBgNVBAoMC1BoaWxpcHMgSHVlMRQwEgYDVQQDDAty
b290LWJyaWRnZTAiGA8yMDE3MDEwMTAwMDAwMFoYDzIwMzgwMTE5MDMxNDA3WjA5
MQswCQYDVQQGEwJOTDEUMBIGA1UECgwLUGhpbGlwcyBIdWUxFDASBgNVBAMMC3Jv
b3QtYnJpZGdlMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjNw2tx2AplOf9x86
aTdvEcL1FU65QDxziKvBpW9XXSIcibAeQiKxegpq8Exbr9v6LBnYbna2VcaK0G22
jOKkTqOBuTCBtjAPBgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBhjAdBgNV
HQ4EFgQUZ2ONTFrDT6o8ItRnKfqWKnHFGmQwdAYDVR0jBG0wa4AUZ2ONTFrDT6o8
ItRnKfqWKnHFGmShPaQ7MDkxCzAJBgNVBAYTAk5MMRQwEgYDVQQKDAtQaGlsaXBz
IEh1ZTEUMBIGA1UEAwwLcm9vdC1icmlkZ2WCFDuxUi22sYpLlwJY81Wrqy11phcO
MAoGCCqGSM49BAMCA0gAMEUCIEBYYEOsa07TH7E5MJnGw557lVkORgit2Rm1h3B2
sFgDAiEA1Fj/C3AN5psFMjo0//mrQebo0eKd3aWRx+pQY08mk48=
-----END CERTIFICATE-----
-----BEGIN CERTIFICATE-----
MIIBzDCCAXOgAwIBAgICEAAwCgYIKoZIzj0EAwIwPDELMAkGA1UEBhMCTkwxFDAS
BgNVBAoMC1NpZ25pZnkgSHVlMRcwFQYDVQQDDA5IdWUgUm9vdCBDQSAwMTAgFw0y
NTAyMjUwMDAwMDBaGA8yMDUwMTIzMTIzNTk1OVowPDELMAkGA1UEBhMCTkwxFDAS
BgNVBAoMC1NpZ25pZnkgSHVlMRcwFQYDVQQDDA5IdWUgUm9vdCBDQSAwMTBZMBMG
ByqGSM49AgEGCCqGSM49AwEHA0IABFfOO0jfSAUXGQ9kjEDzyBrcMQ3ItyA5krE+
cyvb1Y3xFti7KlAad8UOnAx0FBLn7HZrlmIwm1QnX0fK3LPM13mjYzBhMB0GA1Ud
DgQWBBTF1pSpsCASX/z0VHLigxU2CAaqoTAfBgNVHSMEGDAWgBTF1pSpsCASX/z0
VHLigxU2CAaqoTAPBgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBBjAKBggq
hkjOPQQDAgNHADBEAiAk7duT+IHbOGO4UUuGLAEpyYejGZK9Z7V9oSfnvuQ5BQIg
IYSgwwxHXm73/JgcU9lAM6c8Bmu3UE3kBIUwBs1qXFw=
-----END CERTIFICATE-----";

        private static readonly X509Certificate2Collection SignifyRoots = LoadRoots();

        private static X509Certificate2Collection LoadRoots()
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPem(SignifyRootsPem);
            return roots;
        }

        /// <summary>Whether the certificate chains to one of Signify's roots, with what the bridge
        /// presented as intermediates. Names are not part of it: the bridge is reached by address.</summary>
        private static bool FromSignify(X509Certificate2 leaf, X509Chain? presented)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(SignifyRoots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (presented != null)
                foreach (var element in presented.ChainElements)
                    if (!element.Certificate.RawData.AsSpan().SequenceEqual(leaf.RawData))
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(leaf);
        }

        /// <param name="expectedId">The bridge id recorded at pairing: a re-issued certificate is taken
        /// over only when it carries this id. Null for a pairing older than 10.1.1, which then keeps
        /// its pin as it is.</param>
        public HueBridge(string address, string? pinnedCert, string? expectedId = null)
        {
            Address = address;
            PinnedCert = string.IsNullOrEmpty(pinnedCert) ? null : pinnedCert;
            _expectedId = string.IsNullOrEmpty(expectedId) ? null : expectedId;
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = CheckCertificate },
            };
            // ⚠️ Short on purpose: HUESYNC reads through here, and StreamLight gives a bridge reply
            // 3 s (StreamTweakBridge::ResponseTimeoutMs) — a slow bridge must answer "not readable"
            // inside that, not leave the client with nothing.
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(2200) };
        }

        public void Dispose() => _http.Dispose();

        // The bridge's certificate comes from Signify's own CA, which no system store knows. A pinned
        // one must match exactly — or be the same bridge's, re-issued by Signify (RenewedCert). Before
        // pairing, only a certificate from Signify is accepted, and PairAsync then checks its name
        // against the id the bridge reports before pinning it.
        private bool CheckCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        {
            if (cert == null) return false;
            var c2 = new X509Certificate2(cert);
            _lastCert = c2;
            string hash = Hash(c2);
            if (PinnedCert != null && string.Equals(hash, PinnedCert, StringComparison.OrdinalIgnoreCase)) return true;
            _lastFromSignify = FromSignify(c2, chain);
            if (PinnedCert == null) return _lastFromSignify;
            string cn = c2.GetNameInfo(X509NameType.SimpleName, false) ?? "";
            if (_lastFromSignify && _expectedId != null && string.Equals(cn, _expectedId, StringComparison.OrdinalIgnoreCase))
            {
                DebugLogger.Log($"[Hue] The bridge {cn} presents a new certificate from Signify — taken over as the pin");
                PinnedCert = hash;
                RenewedCert = hash;
                return true;
            }
            return false;
        }

        private static string Hash(X509Certificate2 c) => c.GetCertHashString(HashAlgorithmName.SHA256);

        // ── Finding the bridge ─────────────────────────────────────────────────

        /// <summary>The bridge Hue Sync on this PC uses: the <c>IpAddress</c> of its bridge.json, the
        /// one it was authorised on when there are several. Null when Hue Sync has none.</summary>
        public static string? AddressFromHueSync()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HueSync", "bridge.json");
                if (!File.Exists(path)) return null;
                // ⚠️ Not parsed as JSON: Hue Sync writes raw line breaks inside strings, which no JSON
                // parser accepts (measured 03/11/2026, line 19 of FOGGY's file). The address is read
                // with a pattern, preferring the bridge marked authorised a few lines below it.
                string text = File.ReadAllText(path);
                var found = new List<(string ip, bool authorized)>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             text, "\"IpAddress\"\\s*:\\s*\"(\\d{1,3}(?:\\.\\d{1,3}){3})\""))
                {
                    string ip = m.Groups[1].Value;
                    if (!IPAddress.TryParse(ip, out _)) continue;
                    string after = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                    found.Add((ip, System.Text.RegularExpressions.Regex.IsMatch(after, "\"IsAuthorized\"\\s*:\\s*true")));
                }
                return found.OrderByDescending(f => f.authorized).Select(f => f.ip).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>A bridge on the local network by mDNS (<c>_hue._tcp.local</c>): the address of the
        /// first device that answers about it. Null when nothing answers within the time given.</summary>
        public static async Task<string?> DiscoverAsync(TimeSpan wait, CancellationToken ct = default)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                byte[] query = BuildPtrQuery("_hue._tcp.local");
                await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
                byte[] label = Encoding.ASCII.GetBytes("_hue");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(wait);
                while (!cts.IsCancellationRequested)
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    if (r.Buffer.AsSpan().IndexOf(label) >= 0)
                        return r.RemoteEndPoint.Address.ToString();
                }
            }
            catch { }
            return null;
        }

        private static byte[] BuildPtrQuery(string name)
        {
            var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };   // id 0, flags 0, 1 question
            foreach (var part in name.Split('.'))
            {
                b.Add((byte)part.Length);
                b.AddRange(Encoding.ASCII.GetBytes(part));
            }
            // End, type PTR, class IN with the QU bit: answer straight to this socket, not to the group.
            b.AddRange(new byte[] { 0, 0, 12, 0x80, 1 });
            return b.ToArray();
        }

        // ── Pairing ────────────────────────────────────────────────────────────

        /// <summary>The bridge's id and model, from the one call that needs no key.</summary>
        public async Task<bool> ReadIdentityAsync(CancellationToken ct = default)
        {
            try
            {
                var o = JsonNode.Parse(await _http.GetStringAsync($"https://{Address}/api/0/config", ct)) as JsonObject;
                BridgeId = o?["bridgeid"]?.GetValue<string>() ?? "";
                Model    = o?["modelid"]?.GetValue<string>() ?? "";
                return BridgeId.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Asks the bridge for a key until its button is pressed or <paramref name="window"/> runs out.
        /// Returns the key, with <see cref="PinnedCert"/> set; null when the button was not pressed, or
        /// the certificate does not carry the bridge's id.
        /// </summary>
        public async Task<string?> PairAsync(string deviceType, TimeSpan window, CancellationToken ct = default)
        {
            PairFailure = "no_bridge";
            if (!await ReadIdentityAsync(ct) || _lastCert == null)
            {
                // TLS refused by CheckCertificate: something answered, but not with Signify's certificate.
                if (_lastCert != null && !_lastFromSignify)
                {
                    DebugLogger.Log($"[Hue] {Address} does not present a certificate from Signify (a bridge on software older than May 2025 does not either: update it in the Hue app) — not pairing");
                    PairFailure = "bad_cert";
                }
                return null;
            }
            string cn = _lastCert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
            if (!string.Equals(cn, BridgeId, StringComparison.OrdinalIgnoreCase))
            {
                DebugLogger.Log($"[Hue] Certificate name '{cn}' is not the bridge id '{BridgeId}': not pairing");
                PairFailure = "bad_cert";
                return null;
            }
            PairFailure = "timeout";
            string certHash = Hash(_lastCert);
            string body = new JsonObject { ["devicetype"] = deviceType }.ToJsonString();
            var deadline = DateTime.UtcNow + window;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    using var resp = await _http.PostAsync($"https://{Address}/api", new StringContent(body, Encoding.UTF8, "application/json"), ct);
                    var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonArray;
                    string? key = arr?[0]?["success"]?["username"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key) && _lastCert != null && Hash(_lastCert) == certHash)
                    {
                        PinnedCert = certHash;
                        PairFailure = "";
                        return key;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { }
                try { await Task.Delay(2000, ct); } catch { break; }
            }
            return null;
        }

        // ── Reading ────────────────────────────────────────────────────────────

        /// <summary>The bridge's entertainment areas, which one is streaming and which app opened it.</summary>
        public async Task<HueReading> ReadAsync(string key, CancellationToken ct = default)
        {
            try
            {
                var groups = JsonNode.Parse(await _http.GetStringAsync($"https://{Address}/api/{key}/groups", ct));
                if (groups is JsonArray err)   // [{"error":{"type":1,"description":"unauthorized user"}}]
                    return new HueReading { Unauthorized = err[0]?["error"]?["type"]?.GetValue<int>() == 1, Error = "unauthorized" };
                var config = JsonNode.Parse(await _http.GetStringAsync($"https://{Address}/api/{key}/config", ct)) as JsonObject;
                var whitelist = config?["whitelist"] as JsonObject;

                var areas = new List<HueArea>();
                foreach (var kv in (groups as JsonObject) ?? new JsonObject())
                {
                    if (kv.Value is not JsonObject g || g["type"]?.GetValue<string>() != "Entertainment") continue;
                    bool active = g["stream"]?["active"]?.GetValue<bool>() == true;
                    string app = "", device = "";
                    string? owner = g["stream"]?["owner"]?.GetValue<string>();
                    if (active && owner != null && whitelist?[owner]?["name"]?.GetValue<string>() is string full)
                    {
                        int hash = full.IndexOf('#');
                        app    = hash < 0 ? full : full[..hash];
                        device = hash < 0 ? "" : full[(hash + 1)..];
                    }
                    areas.Add(new HueArea(g["name"]?.GetValue<string>() ?? "", (g["lights"] as JsonArray)?.Count ?? 0, active, app, device));
                }
                return new HueReading { Ok = true, Areas = areas };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new HueReading { Error = ex.Message };
            }
        }
    }
}
