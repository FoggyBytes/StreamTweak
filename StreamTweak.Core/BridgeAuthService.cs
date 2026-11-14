using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StreamTweak
{
    /// <summary>
    /// Result of an ENROLL request.
    /// </summary>
    public enum EnrollResult { Pending, Approved, Denied }

    /// <summary>
    /// A StreamLight client that has contacted the bridge. Persisted to
    /// %LOCALAPPDATA%\StreamTweak\bridgeclients.json. The trust is the client's
    /// existing Moonlight identity certificate (reused — no separate PIN).
    /// </summary>
    public sealed class BridgeClient
    {
        public string UniqueId    { get; set; } = "";
        public string Fingerprint { get; set; } = "";   // SHA-256 of the cert (hex)
        public string CertPem     { get; set; } = "";    // public cert, used to verify signatures
        public string Name        { get; set; } = "";    // friendly label (IP-derived)
        public string Status      { get; set; } = "pending"; // pending | approved | denied
        public string? ApprovedUtc { get; set; }
        public string? LastSeenUtc { get; set; }
        public string? Pin         { get; set; }   // 4-digit confirmation PIN shown while pending
        /// <summary>What the device is, picked by the user on the Clients page (9.0):
        /// desktop | handheld | phone | minipc | tablet. Null until picked. Host-side only —
        /// StreamLight neither sends nor reads it.</summary>
        public string? DeviceKind  { get; set; }
        /// <summary>10.0.0: may this device change the host's settings from StreamLight's host
        /// section (managed apps, spatial audio, Sentinel)? Without it the section is read-only.
        /// Null only on a record written before 10.0.0 — <see cref="BridgeAuthService"/> turns that
        /// into true for an approved device on load (it could already power the host off, so
        /// withholding the harmless switches protected nothing) and remembers it for the one-time
        /// notice. A device approved from 10.0.0 on gets an explicit value.</summary>
        public bool? HostManagement { get; set; }
        /// <summary>10.0.0: Host management was granted by the upgrade, not by a choice on this
        /// host. Drives the one-time notice on the Clients page; cleared when it is dismissed or
        /// the switch is touched.</summary>
        public bool HostManagementByUpgrade { get; set; }
    }

    /// <summary>
    /// Authenticates StreamLight clients on the TCP bridge (port 47998).
    ///
    /// Scheme (see design doc): trust-on-first-use enrollment of the client's
    /// existing Moonlight certificate, then per-command verification of an
    /// RSA-SHA256 (PKCS#1 v1.5) signature over "uniqueId\nunixMillis\ncommand".
    /// A short timestamp window plus a replay cache bound replay attacks.
    ///
    /// This closes the unauthenticated-LAN exposure of the bridge: only a client
    /// the user has explicitly approved on the host can issue commands or read
    /// host metrics / the game list.
    /// </summary>
    public sealed class BridgeAuthService
    {
        private const int  MaxPendingClients = 8;
        private const long ClockSkewMs       = 60_000;   // accept ts within ±60 s
        private const int  MaxCertPemBytes   = 8 * 1024;

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "bridgeclients.json");

        private readonly object _lock = new();
        private List<BridgeClient> _clients = new();

        // Anti-replay: signature(base64) -> received unix-ms. Pruned every few seconds (10.4.2: it
        // was a full pass over the table at every command).
        private readonly Dictionary<string, long> _seenSignatures = new();
        private long _lastPruneMs;
        private const long PruneEveryMs = 10_000;

        // The public key of each approved certificate, parsed once (10.4.2) — a client sends several
        // signed commands a second and each one parsed the PEM again. Keyed by the PEM itself, so a
        // changed certificate can never be answered with an old key; emptied when the list is
        // saved. An RSA object is not safe for concurrent use: every use holds its lock.
        private readonly Dictionary<string, RSA> _keys = new(StringComparer.Ordinal);
        private readonly object _keysLock = new();

        private RSA? KeyFor(string certPem)
        {
            lock (_keysLock)
            {
                if (_keys.TryGetValue(certPem, out var known)) return known;
                using var cert = X509Certificate2.CreateFromPem(certPem);
                var rsa = cert.GetRSAPublicKey();
                if (rsa != null) _keys[certPem] = rsa;
                return rsa;
            }
        }

        // Not disposed: a verification on another thread may be holding one. The finalizer frees them.
        private void ForgetKeys()
        {
            lock (_keysLock) _keys.Clear();
        }

        /// <summary>Raised (background thread) when a NEW client requests approval.</summary>
        public event Action<BridgeClient>? ApprovalRequested;

        /// <summary>Raised whenever the client list changes (UI refresh).</summary>
        public event Action? ClientsChanged;

        public BridgeAuthService() => Load();

        // ── Enrollment ─────────────────────────────────────────────────────────

        /// <summary>
        /// Registers (or re-checks) a client by its Moonlight certificate.
        /// A brand-new client is stored as Pending and an approval event is raised.
        /// </summary>
        public EnrollResult Enroll(string uniqueId, string certPemBase64, string remoteName, string pin)
        {
            if (string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(certPemBase64))
                return EnrollResult.Denied;

            string certPem;
            try
            {
                byte[] raw = Convert.FromBase64String(certPemBase64);
                if (raw.Length == 0 || raw.Length > MaxCertPemBytes) return EnrollResult.Denied;
                certPem = Encoding.UTF8.GetString(raw);
            }
            catch { return EnrollResult.Denied; }

            string fingerprint;
            try
            {
                using var cert = X509Certificate2.CreateFromPem(certPem);
                using var rsa = cert.GetRSAPublicKey();
                if (rsa == null || rsa.KeySize < 2048) return EnrollResult.Denied;
                fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
            }
            catch { return EnrollResult.Denied; }

            BridgeClient? newlyPending = null;
            EnrollResult result;

            lock (_lock)
            {
                var existing = _clients.FirstOrDefault(c =>
                    string.Equals(c.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    // A different certificate for a known uniqueId must NOT inherit trust:
                    // reset to pending and re-prompt (defends against a hijacked uniqueId).
                    if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Fingerprint = fingerprint;
                        existing.CertPem     = certPem;
                        existing.Name        = string.IsNullOrWhiteSpace(remoteName) ? existing.Name : remoteName;
                        existing.Status      = "pending";
                        existing.ApprovedUtc = null;
                        existing.Pin         = pin;
                        // A new certificate is a new device as far as trust goes: the permission
                        // is chosen again at the next approval.
                        existing.HostManagement          = false;
                        existing.HostManagementByUpgrade = false;
                        newlyPending = existing;
                        result = EnrollResult.Pending;
                    }
                    else
                    {
                        // A still-pending client re-polls every couple of seconds; refresh
                        // its PIN and display name so the latest values are shown (and so an
                        // entry created by an older build self-heals on the next poll).
                        if (existing.Status == "pending")
                        {
                            existing.Pin = pin;
                            if (!string.IsNullOrWhiteSpace(remoteName)) existing.Name = remoteName;
                        }
                        result = existing.Status switch
                        {
                            "approved" => EnrollResult.Approved,
                            "denied"   => EnrollResult.Denied,
                            _          => EnrollResult.Pending,
                        };
                    }
                }
                else
                {
                    // Cap the pending list so a hostile peer cannot spam it unbounded.
                    int pending = _clients.Count(c => c.Status == "pending");
                    if (pending >= MaxPendingClients)
                    {
                        var oldest = _clients.FirstOrDefault(c => c.Status == "pending");
                        if (oldest != null) _clients.Remove(oldest);
                    }

                    var client = new BridgeClient
                    {
                        UniqueId    = uniqueId,
                        Fingerprint = fingerprint,
                        CertPem     = certPem,
                        Name        = string.IsNullOrWhiteSpace(remoteName) ? uniqueId : remoteName,
                        Status      = "pending",
                        Pin         = pin,
                        HostManagement = false,
                    };
                    _clients.Add(client);
                    newlyPending = client;
                    result = EnrollResult.Pending;
                }

                Save();
            }

            if (newlyPending != null)
            {
                ApprovalRequested?.Invoke(newlyPending);
                ClientsChanged?.Invoke();
            }
            return result;
        }

        // ── Per-command verification ─────────────────────────────────────────

        /// <summary>
        /// Verifies the per-command signature for an approved client.
        /// Returns false for unknown/unapproved clients, bad signatures,
        /// out-of-window timestamps, or replays.
        /// </summary>
        // `reason` says why a check failed, for the log only — never for the reply, which stays a
        // bare ERR_UNAUTHORIZED. 10.4.3 (StreamTweak #17): a client the host had approved, with the
        // same certificate, saw every signed command refused, and the log said only "rejected
        // unauthorized command" — nothing to tell a clock that is off from a key that does not
        // match. The clock case carries the measured difference.
        public bool TryVerify(string uniqueId, long tsMillis, string signatureBase64, string command,
                              out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(signatureBase64))
            {
                reason = "malformed AUTH1 line";
                return false;
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long skewMs = tsMillis - nowMs;
            if (Math.Abs(skewMs) > ClockSkewMs)
            {
                reason = $"client clock {(skewMs > 0 ? "ahead of" : "behind")} this PC by "
                       + $"{Math.Abs(skewMs) / 1000.0:0.0} s (allowed ±{ClockSkewMs / 1000} s)";
                return false;
            }

            BridgeClient? client;
            string? knownStatus;
            lock (_lock)
            {
                var known = _clients.FirstOrDefault(c =>
                    string.Equals(c.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                knownStatus = known?.Status;
                client = known != null && known.Status == "approved" ? known : null;
            }
            if (client == null)
            {
                reason = knownStatus == null ? $"unknown client {uniqueId}"
                                             : $"client {uniqueId} is {knownStatus}, not approved";
                return false;
            }

            byte[] sig;
            try { sig = Convert.FromBase64String(signatureBase64); }
            catch
            {
                reason = "signature is not base64";
                return false;
            }

            byte[] payload = Encoding.UTF8.GetBytes($"{uniqueId}\n{tsMillis}\n{command}");
            bool ok;
            try
            {
                var rsa = KeyFor(client.CertPem);
                if (rsa == null)
                {
                    reason = "approved certificate has no RSA key";
                    return false;
                }
                lock (rsa) { ok = rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
            }
            catch (Exception ex)
            {
                reason = $"verification failed: {ex.GetType().Name}";
                return false;
            }
            if (!ok)
            {
                reason = "signature does not match the approved certificate";
                return false;
            }

            lock (_lock)
            {
                PruneSeen(nowMs);
                if (_seenSignatures.ContainsKey(signatureBase64))
                {
                    reason = "replay (this exact signature was already used)";
                    return false;
                }
                _seenSignatures[signatureBase64] = nowMs;
                client.LastSeenUtc = DateTime.UtcNow.ToString("o");
            }
            return true;
        }

        private void PruneSeen(long nowMs)
        {
            if (_seenSignatures.Count == 0) return;
            if (nowMs - _lastPruneMs < PruneEveryMs && nowMs >= _lastPruneMs) return;
            _lastPruneMs = nowMs;
            var stale = _seenSignatures
                .Where(kv => nowMs - kv.Value > ClockSkewMs * 2)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in stale) _seenSignatures.Remove(k);
        }

        /// <summary>
        /// Encrypts <paramref name="data"/> for an approved client's certificate — RSA-OAEP with
        /// SHA-256 for both the digest and MGF1 — and returns it as base64. Only the client can
        /// open it, with the private key it pairs with. Null for an unknown or unapproved client.
        /// Used to hand out the clipboard session key (§79).
        ///
        /// ⚠️ StreamLight must decrypt with the SAME parameters: <c>rsa_oaep_md</c> AND
        /// <c>rsa_mgf1_md</c> both SHA-256. A mismatch fails without a useful error.
        /// </summary>
        public string? WrapForClient(string uniqueId, byte[] data)
        {
            BridgeClient? client;
            lock (_lock)
            {
                client = _clients.FirstOrDefault(c =>
                    string.Equals(c.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase)
                    && c.Status == "approved");
            }
            if (client == null) return null;
            try
            {
                using var cert = X509Certificate2.CreateFromPem(client.CertPem);
                using var rsa = cert.GetRSAPublicKey();
                if (rsa == null) return null;
                return Convert.ToBase64String(rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256));
            }
            catch { return null; }
        }

        // ── Management (UI) ──────────────────────────────────────────────────

        /// <summary>Friendly label of an approved client, for host-side status text.
        /// Null when the id is unknown.</summary>
        public string? GetClientName(string uniqueId)
        {
            lock (_lock)
            {
                return _clients.FirstOrDefault(c =>
                    string.Equals(c.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase))?.Name;
            }
        }

        public IReadOnlyList<BridgeClient> GetClients()
        {
            lock (_lock) return _clients.Select(Clone).ToList();
        }

        /// <summary>Sets what kind of device a client is (its icon); null clears it.</summary>
        public void SetDeviceKind(string uniqueId, string? kind)
        {
            lock (_lock)
            {
                var c = _clients.FirstOrDefault(x =>
                    string.Equals(x.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                if (c == null || c.DeviceKind == kind) return;
                c.DeviceKind = kind;
                Save();
            }
            ClientsChanged?.Invoke();
        }

        /// <summary>Approves a device. <paramref name="hostManagement"/> is the choice made in the
        /// approval dialog (10.0.0); the plain Approve of the Clients page grants it off.</summary>
        public void Approve(string uniqueId, bool hostManagement = false)
        {
            lock (_lock)
            {
                var c = _clients.FirstOrDefault(x =>
                    string.Equals(x.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                if (c == null) return;
                c.HostManagement          = hostManagement;
                c.HostManagementByUpgrade = false;
            }
            SetStatus(uniqueId, "approved");
        }

        public void Deny(string uniqueId)    => SetStatus(uniqueId, "denied");

        /// <summary>Whether an APPROVED device may change the host's settings from StreamLight.
        /// False for unknown, pending or denied devices.</summary>
        public bool HasHostManagement(string uniqueId)
        {
            lock (_lock)
            {
                var c = _clients.FirstOrDefault(x =>
                    string.Equals(x.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                return c is { Status: "approved", HostManagement: true };
            }
        }

        public void SetHostManagement(string uniqueId, bool enabled)
        {
            lock (_lock)
            {
                var c = _clients.FirstOrDefault(x =>
                    string.Equals(x.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                if (c == null) return;
                if (c.HostManagement == enabled && !c.HostManagementByUpgrade) return;
                c.HostManagement          = enabled;
                c.HostManagementByUpgrade = false;
                Save();
            }
            ClientsChanged?.Invoke();
        }

        /// <summary>Devices that got Host management from the 10.0.0 upgrade and have not been
        /// looked at since — the one-time notice on the Clients page lists them.</summary>
        public IReadOnlyList<BridgeClient> GetHostManagementByUpgrade()
        {
            lock (_lock)
                return _clients.Where(c => c.Status == "approved" && c.HostManagementByUpgrade)
                               .Select(Clone).ToList();
        }

        /// <summary>The notice was dismissed: the grants stay, the notice goes.</summary>
        public void DismissHostManagementNotice()
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var c in _clients.Where(c => c.HostManagementByUpgrade))
                {
                    c.HostManagementByUpgrade = false;
                    changed = true;
                }
                if (changed) Save();
            }
            if (changed) ClientsChanged?.Invoke();
        }

        public void Revoke(string uniqueId)
        {
            lock (_lock)
            {
                _clients.RemoveAll(c =>
                    string.Equals(c.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                Save();
            }
            ClientsChanged?.Invoke();
        }

        private void SetStatus(string uniqueId, string status)
        {
            lock (_lock)
            {
                var c = _clients.FirstOrDefault(x =>
                    string.Equals(x.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
                if (c == null) return;
                c.Status = status;
                c.ApprovedUtc = status == "approved" ? DateTime.UtcNow.ToString("o") : null;
                Save();
            }
            ClientsChanged?.Invoke();
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private void Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                var data = JsonSerializer.Deserialize<List<BridgeClient>>(File.ReadAllText(StatePath));
                if (data != null) _clients = data;
            }
            catch { _clients = new(); }

            // 10.0.0: a record without the field predates Host management. An approved device
            // gets it on, a pending or denied one off; the grant is remembered for the notice.
            bool migrated = false;
            foreach (var c in _clients.Where(c => c.HostManagement == null))
            {
                c.HostManagement          = c.Status == "approved";
                c.HostManagementByUpgrade = c.Status == "approved";
                migrated = true;
            }
            if (migrated) Save();
        }

        private void Save()
        {
            ForgetKeys();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(_clients, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, StatePath, overwrite: true);
            }
            catch { }
        }

        private static BridgeClient Clone(BridgeClient c) => new()
        {
            UniqueId    = c.UniqueId,
            Fingerprint = c.Fingerprint,
            CertPem     = c.CertPem,
            Name        = c.Name,
            Status      = c.Status,
            ApprovedUtc = c.ApprovedUtc,
            LastSeenUtc = c.LastSeenUtc,
            Pin         = c.Pin,
            DeviceKind  = c.DeviceKind,
            HostManagement          = c.HostManagement,
            HostManagementByUpgrade = c.HostManagementByUpgrade,
        };
    }
}
