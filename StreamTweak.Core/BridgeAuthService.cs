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

        // Anti-replay: signature(base64) -> received unix-ms. Pruned on each verify.
        private readonly Dictionary<string, long> _seenSignatures = new();

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
        public bool TryVerify(string uniqueId, long tsMillis, string signatureBase64, string command)
        {
            if (string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(signatureBase64))
                return false;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Math.Abs(nowMs - tsMillis) > ClockSkewMs) return false;

            BridgeClient? client;
            lock (_lock)
            {
                client = _clients.FirstOrDefault(c =>
                    string.Equals(c.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase)
                    && c.Status == "approved");
            }
            if (client == null) return false;

            byte[] sig;
            try { sig = Convert.FromBase64String(signatureBase64); }
            catch { return false; }

            byte[] payload = Encoding.UTF8.GetBytes($"{uniqueId}\n{tsMillis}\n{command}");
            bool ok;
            try
            {
                using var cert = X509Certificate2.CreateFromPem(client.CertPem);
                using var rsa = cert.GetRSAPublicKey();
                if (rsa == null) return false;
                ok = rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch { return false; }
            if (!ok) return false;

            lock (_lock)
            {
                PruneSeen(nowMs);
                if (_seenSignatures.ContainsKey(signatureBase64)) return false; // replay
                _seenSignatures[signatureBase64] = nowMs;
                client.LastSeenUtc = DateTime.UtcNow.ToString("o");
            }
            return true;
        }

        private void PruneSeen(long nowMs)
        {
            if (_seenSignatures.Count == 0) return;
            var stale = _seenSignatures
                .Where(kv => nowMs - kv.Value > ClockSkewMs * 2)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in stale) _seenSignatures.Remove(k);
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

        public void Approve(string uniqueId) => SetStatus(uniqueId, "approved");
        public void Deny(string uniqueId)    => SetStatus(uniqueId, "denied");

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
        }

        private void Save()
        {
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
        };
    }
}
