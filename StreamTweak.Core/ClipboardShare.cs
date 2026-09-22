using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Host side of the clipboard shared with StreamLight over the bridge (8.7.0, §79).
    ///
    /// <para>Text only, up to <see cref="MaxTextBytes"/> of UTF-8, and only with an approved
    /// client that is streaming: the client opens with CLIPKEY at the start of a stream, the
    /// host answers with a fresh AES-256 key wrapped RSA-OAEP(SHA-256) for the certificate that
    /// client enrolled with, and from then on every clipboard in either direction travels
    /// AES-GCM-sealed with that key. Without a key CLIPSET and CLIPGET are refused, and the key
    /// dies with the stream (CLIPEND, or <see cref="KeyIdleMs"/> without a word from the client
    /// — StreamLight polls STATS every second while it streams).</para>
    ///
    /// <para>The host never pushes. It reports its clipboard sequence number inside STATS
    /// (App.xaml.cs), and the client asks with CLIPGET when that number moves.</para>
    ///
    /// <para>⚠️ Nothing here logs clipboard CONTENT, at any level — only lengths and outcomes.
    /// Apollo and Vibepollo log the payload of their clipboard packet at info level; a password
    /// sent that way ends up in the server log.</para>
    /// </summary>
    public sealed class ClipboardShare : IDisposable
    {
        /// <summary>
        /// "Share clipboard with StreamLight" (Clients page). On by default: an approved device is
        /// already trusted with power commands, and the client side starts OFF, so nothing moves
        /// until someone turns it on on the device they hold (§79.6).
        /// </summary>
        public static bool Enabled { get; set; } = true;

        public const int MaxTextBytes = 32 * 1024;

        // A password received from a client lives on this clipboard at most this long.
        private const int SensitiveLifetimeMs = 60_000;

        // A key with no authenticated traffic from its client for this long is dropped.
        private const int KeyIdleMs = 30_000;

        // Plaintext header: [version][flags], then the UTF-8 text.
        private const byte WireVersion   = 1;
        private const byte FlagSensitive = 0x01;

        private sealed class ClientKey
        {
            public byte[] Key = Array.Empty<byte>();
            public long LastSeenMs;
        }

        private readonly object _lock = new();
        private readonly Dictionary<string, ClientKey> _keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly ClipboardThread _clip = new();
        private readonly Timer _sweep;
        private readonly Timer _sensitiveTimer;

        // The password we wrote for a client: the clipboard sequence number right after the write,
        // and whose it was. Zero when there is none. Cleared only if the clipboard still holds it.
        private uint _sensitiveSeq;
        private string? _sensitiveOwner;

        public ClipboardShare()
        {
            _sweep          = new Timer(_ => Sweep(), null, 5_000, 5_000);
            _sensitiveTimer = new Timer(_ => _ = ClearSensitiveAsync("60 s elapsed"), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>The system clipboard sequence number, for the "clip" field of STATS.</summary>
        public static uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

        // ── Bridge verbs ───────────────────────────────────────────────────────

        /// <summary>
        /// CLIPKEY: issues a new session key for this client, wrapped for its certificate by
        /// <paramref name="wrapForClient"/>. Replaces any key the client already had.
        /// </summary>
        public string HandleKey(string clientId, Func<byte[], string?> wrapForClient)
        {
            if (!Enabled) return "ERR_NOT_ALLOWED";

            byte[] key = RandomNumberGenerator.GetBytes(32);
            string? wrapped = wrapForClient(key);
            if (wrapped == null)
            {
                CryptographicOperations.ZeroMemory(key);
                return "ERR";
            }
            lock (_lock)
            {
                if (_keys.TryGetValue(clientId, out var old))
                    CryptographicOperations.ZeroMemory(old.Key);
                _keys[clientId] = new ClientKey { Key = key, LastSeenMs = NowMs };
            }
            DebugLogger.Log($"ClipboardShare: session key issued to {clientId}");
            return "KEY " + wrapped;
        }

        /// <summary>CLIPSET: the client's clipboard, sealed. An empty text means the client's
        /// clipboard was emptied — the cue to drop a password we are holding for it.</summary>
        public async Task<string> HandleSetAsync(string clientId, string? sealedB64)
        {
            if (!Enabled) return "ERR_NOT_ALLOWED";
            byte[]? key = KeyFor(clientId);
            if (key == null) return "ERR_NO_KEY";
            if (!TryOpen(key, sealedB64, Aad("C2H", clientId), out bool sensitive, out string text))
            {
                DebugLogger.Log($"ClipboardShare: CLIPSET from {clientId} did not authenticate");
                return "ERR";
            }

            int bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > MaxTextBytes) return "ERR_TOO_LARGE";

            if (text.Length == 0)
            {
                await ClearSensitiveAsync("emptied on the client", clientId);
                return "OK";
            }
            if (LockState.IsLocked()) return "ERR_LOCKED";

            uint seq = await _clip.RunAsync(owner => WriteText(owner, EnsureCrLf(text), sensitive));
            if (seq == 0) return "ERR_BUSY";

            lock (_lock)
            {
                _sensitiveSeq   = sensitive ? seq : 0;
                _sensitiveOwner = sensitive ? clientId : null;
            }
            _sensitiveTimer.Change(sensitive ? SensitiveLifetimeMs : Timeout.Infinite, Timeout.Infinite);
            DebugLogger.Log($"ClipboardShare: {bytes} bytes from {clientId}{(sensitive ? " (sensitive)" : "")}");
            return "OK";
        }

        /// <summary>CLIPGET: this clipboard, sealed for the client — or why there is nothing to send.</summary>
        public async Task<string> HandleGetAsync(string clientId)
        {
            if (!Enabled) return "ERR_NOT_ALLOWED";
            byte[]? key = KeyFor(clientId);
            if (key == null) return "ERR_NO_KEY";
            if (LockState.IsLocked()) return "ERR_LOCKED";

            Snapshot snap = await _clip.RunAsync(ReadText);
            switch (snap.Kind)
            {
                case SnapshotKind.Busy:     return "ERR_BUSY";
                case SnapshotKind.Empty:    return "EMPTY";
                case SnapshotKind.Own:      return "OWN";
                case SnapshotKind.NoText:   return "NOTEXT";
                case SnapshotKind.TooLarge: return "ERR_TOO_LARGE";
            }

            int bytes = Encoding.UTF8.GetByteCount(snap.Text);
            if (bytes > MaxTextBytes) return "ERR_TOO_LARGE";

            string sealedB64 = Seal(key, snap.Text, snap.Sensitive, Aad("H2C", clientId));
            DebugLogger.Log($"ClipboardShare: {bytes} bytes to {clientId}{(snap.Sensitive ? " (sensitive)" : "")}");
            // The sequence number travels with the text so the client can skip a clipboard it
            // already has — it asks again on every focus loss, not only when STATS says so.
            return $"CLIP {snap.Seq} {sealedB64}";
        }

        /// <summary>CLIPEND: the stream is over. Drops the key and any password held for the client.</summary>
        public async Task<string> HandleEndAsync(string clientId)
        {
            DropKey(clientId);
            await ClearSensitiveAsync("stream ended", clientId);
            return "OK";
        }

        /// <summary>Any authenticated command from the client keeps its key alive.</summary>
        public void Touch(string clientId)
        {
            lock (_lock)
            {
                if (_keys.TryGetValue(clientId, out var k))
                    k.LastSeenMs = NowMs;
            }
        }

        // ── Keys ───────────────────────────────────────────────────────────────

        private byte[]? KeyFor(string clientId)
        {
            lock (_lock)
            {
                if (!_keys.TryGetValue(clientId, out var k)) return null;
                k.LastSeenMs = NowMs;
                return k.Key;
            }
        }

        private bool DropKey(string clientId)
        {
            lock (_lock)
            {
                if (!_keys.Remove(clientId, out var k)) return false;
                CryptographicOperations.ZeroMemory(k.Key);
            }
            DebugLogger.Log($"ClipboardShare: session key of {clientId} dropped");
            return true;
        }

        private void Sweep()
        {
            List<string> idle = new();
            lock (_lock)
            {
                long now = NowMs;
                foreach (var (id, k) in _keys)
                    if (now - k.LastSeenMs > KeyIdleMs) idle.Add(id);
            }
            foreach (string id in idle)
            {
                if (DropKey(id))
                    _ = ClearSensitiveAsync("client went quiet", id);
            }
        }

        // Empties the clipboard if it still holds the password we wrote — and, when an owner is
        // named, only if that password was written for that client. Anything the user copied
        // since has a different sequence number and is left alone.
        private async Task ClearSensitiveAsync(string why, string? owner = null)
        {
            uint seq;
            lock (_lock)
            {
                if (_sensitiveSeq == 0) return;
                if (owner != null && !string.Equals(owner, _sensitiveOwner, StringComparison.OrdinalIgnoreCase)) return;
                seq = _sensitiveSeq;
                _sensitiveSeq = 0;
                _sensitiveOwner = null;
            }
            _sensitiveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            bool cleared = await _clip.RunAsync(o => ClearIfUnchanged(o, seq));
            if (cleared) DebugLogger.Log($"ClipboardShare: password cleared ({why})");
        }

        // ── Sealing ────────────────────────────────────────────────────────────

        // Binds each message to its direction and client, so one can't be replayed the other way.
        // ⚠️ StreamLight builds the same string from the uniqueId it signs AUTH1 with.
        private static byte[] Aad(string direction, string clientId) =>
            Encoding.UTF8.GetBytes($"StreamLight-Clipboard/1 {direction} {clientId}");

        // base64( nonce[12] | ciphertext | tag[16] ), plaintext = [version][flags] + UTF-8 text.
        private static string Seal(byte[] key, string text, bool sensitive, byte[] aad)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] plain = new byte[2 + body.Length];
            plain[0] = WireVersion;
            plain[1] = sensitive ? FlagSensitive : (byte)0;
            body.CopyTo(plain, 2);

            byte[] sealedBytes = new byte[12 + plain.Length + 16];
            Span<byte> nonce = sealedBytes.AsSpan(0, 12);
            RandomNumberGenerator.Fill(nonce);
            using (var gcm = new AesGcm(key, 16))
                gcm.Encrypt(nonce, plain, sealedBytes.AsSpan(12, plain.Length), sealedBytes.AsSpan(12 + plain.Length, 16), aad);

            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(body);
            return Convert.ToBase64String(sealedBytes);
        }

        private static bool TryOpen(byte[] key, string? sealedB64, byte[] aad, out bool sensitive, out string text)
        {
            sensitive = false;
            text = "";
            if (string.IsNullOrWhiteSpace(sealedB64)) return false;

            byte[] sealedBytes;
            try { sealedBytes = Convert.FromBase64String(sealedB64.Trim()); }
            catch (FormatException) { return false; }
            if (sealedBytes.Length < 12 + 2 + 16) return false;

            int plainLen = sealedBytes.Length - 12 - 16;
            byte[] plain = new byte[plainLen];
            try
            {
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(12, plainLen),
                            sealedBytes.AsSpan(12 + plainLen, 16), plain, aad);
            }
            catch (CryptographicException) { return false; }

            if (plain[0] != WireVersion) return false;
            sensitive = (plain[1] & FlagSensitive) != 0;
            text = Encoding.UTF8.GetString(plain, 2, plainLen - 2);
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }

        // Windows apps expect CRLF; a bare LF pastes as one long line in some of them (Apollo does
        // the same before writing its clipboard).
        private static string EnsureCrLf(string s)
        {
            if (s.IndexOf('\n') < 0) return s;
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\n' && (i == 0 || s[i - 1] != '\r')) sb.Append('\r');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static long NowMs => Environment.TickCount64;

        // ── The clipboard itself (runs on ClipboardThread) ─────────────────────

        private enum SnapshotKind { Text, Empty, Own, NoText, TooLarge, Busy }

        private readonly record struct Snapshot(SnapshotKind Kind, string Text = "", bool Sensitive = false, uint Seq = 0);

        private static readonly uint FmtOwnTag      = NativeMethods.RegisterClipboardFormatW("FoggyBytes.ClipboardSync");
        private static readonly uint FmtExclude     = NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        private static readonly uint FmtHistory     = NativeMethods.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        private static readonly uint FmtCloud       = NativeMethods.RegisterClipboardFormatW("CanUploadToCloudClipboard");
        private static readonly uint FmtViewerIgnore = NativeMethods.RegisterClipboardFormatW("Clipboard Viewer Ignore");

        private static bool Open(IntPtr owner)
        {
            // Another app may hold the clipboard for a moment; ~100 ms, then give up (ERR_BUSY).
            for (int i = 0; i < 10; i++)
            {
                if (NativeMethods.OpenClipboard(owner)) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static Snapshot ReadText(IntPtr owner)
        {
            if (!Open(owner)) return new Snapshot(SnapshotKind.Busy);
            try
            {
                if (NativeMethods.CountClipboardFormats() == 0) return new Snapshot(SnapshotKind.Empty);
                if (NativeMethods.IsClipboardFormatAvailable(FmtOwnTag)) return new Snapshot(SnapshotKind.Own);
                if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT)) return new Snapshot(SnapshotKind.NoText);

                IntPtr h = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                if (h == IntPtr.Zero) return new Snapshot(SnapshotKind.NoText);
                long maxChars = (long)NativeMethods.GlobalSize(h) / 2;
                IntPtr p = NativeMethods.GlobalLock(h);
                if (p == IntPtr.Zero) return new Snapshot(SnapshotKind.NoText);
                string text;
                try
                {
                    // Read no more than can possibly fit: past MaxTextBytes characters the UTF-8
                    // form is too large anyway, and a 50 MB copy should not be marshalled to find out.
                    int limit = (int)Math.Min(maxChars, MaxTextBytes + 1);
                    text = Marshal.PtrToStringUni(p, limit);
                    int nul = text.IndexOf('\0');
                    if (nul >= 0) text = text.Substring(0, nul);
                    else if (maxChars > MaxTextBytes) return new Snapshot(SnapshotKind.TooLarge);
                }
                finally { NativeMethods.GlobalUnlock(h); }

                if (text.Length == 0) return new Snapshot(SnapshotKind.Empty);

                // Any one marker is enough: KeePass, KeePassXC and Firefox set the first; Bitwarden,
                // Proton Pass and Chrome only the history/cloud pair (§79.5).
                bool sensitive = NativeMethods.IsClipboardFormatAvailable(FmtExclude)
                              || NativeMethods.IsClipboardFormatAvailable(FmtViewerIgnore)
                              || ReadDword(FmtHistory) == 0
                              || ReadDword(FmtCloud) == 0;
                // Read while the clipboard is open, so it names exactly this content.
                return new Snapshot(SnapshotKind.Text, text, sensitive, NativeMethods.GetClipboardSequenceNumber());
            }
            finally { NativeMethods.CloseClipboard(); }
        }

        // -1 when the format is absent or unreadable.
        private static long ReadDword(uint format)
        {
            if (!NativeMethods.IsClipboardFormatAvailable(format)) return -1;
            IntPtr h = NativeMethods.GetClipboardData(format);
            if (h == IntPtr.Zero || (long)NativeMethods.GlobalSize(h) < 4) return -1;
            IntPtr p = NativeMethods.GlobalLock(h);
            if (p == IntPtr.Zero) return -1;
            try { return (uint)Marshal.ReadInt32(p); }
            finally { NativeMethods.GlobalUnlock(h); }
        }

        // Returns the clipboard sequence number after the write, or 0 when it failed.
        private static uint WriteText(IntPtr owner, string text, bool sensitive)
        {
            if (!Open(owner)) return 0;
            try
            {
                if (!NativeMethods.EmptyClipboard()) return 0;
                byte[] utf16 = new byte[(text.Length + 1) * 2];
                Encoding.Unicode.GetBytes(text, 0, text.Length, utf16, 0);
                if (!Put(NativeMethods.CF_UNICODETEXT, utf16)) return 0;
                CryptographicOperations.ZeroMemory(utf16);

                // Our own mark: the change this write causes is recognised on the next CLIPGET
                // (OWN), so the text never bounces back to the client that sent it.
                Put(FmtOwnTag, new byte[4]);
                if (sensitive)
                {
                    // Out of Win+V history, out of the cloud clipboard, ignored by clipboard monitors.
                    Put(FmtExclude, new byte[4]);
                    Put(FmtViewerIgnore, new byte[4]);
                    Put(FmtHistory, new byte[4]);
                    Put(FmtCloud, new byte[4]);
                }
            }
            finally { NativeMethods.CloseClipboard(); }
            return NativeMethods.GetClipboardSequenceNumber();
        }

        private static bool Put(uint format, byte[] data)
        {
            IntPtr g = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (g == IntPtr.Zero) return false;
            IntPtr p = NativeMethods.GlobalLock(g);
            if (p == IntPtr.Zero) { NativeMethods.GlobalFree(g); return false; }
            Marshal.Copy(data, 0, p, data.Length);
            NativeMethods.GlobalUnlock(g);
            if (NativeMethods.SetClipboardData(format, g) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(g);   // ownership passes to the system only on success
                return false;
            }
            return true;
        }

        private static bool ClearIfUnchanged(IntPtr owner, uint seq)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != seq) return false;
            if (!Open(owner)) return false;
            try
            {
                // Checked again inside: someone may have copied between the two reads.
                if (NativeMethods.GetClipboardSequenceNumber() != seq) return false;
                return NativeMethods.EmptyClipboard();
            }
            finally { NativeMethods.CloseClipboard(); }
        }

        public void Dispose()
        {
            _sweep.Dispose();
            _sensitiveTimer.Dispose();
            // Best effort: a password we wrote must not outlive StreamTweak.
            uint seq;
            lock (_lock) { seq = _sensitiveSeq; _sensitiveSeq = 0; }
            if (seq != 0)
            {
                try { _clip.RunAsync(o => ClearIfUnchanged(o, seq)).Wait(1000); } catch { }
            }
            lock (_lock)
            {
                foreach (var k in _keys.Values) CryptographicOperations.ZeroMemory(k.Key);
                _keys.Clear();
            }
            _clip.Dispose();
        }

        /// <summary>
        /// A thread that owns the clipboard for us: OpenClipboard wants a window to own what
        /// EmptyClipboard + SetClipboardData put there, and a window needs a thread that pumps its
        /// messages — Windows SENDS the owner WM_DESTROYCLIPBOARD when another app empties the
        /// clipboard, and a thread that never pumps would hang that app until it times out.
        /// </summary>
        private sealed class ClipboardThread : IDisposable
        {
            private readonly ConcurrentQueue<Action> _work = new();
            private readonly AutoResetEvent _wake = new(false);
            private readonly Thread _thread;
            private volatile bool _stop;
            private IntPtr _hwnd;

            public ClipboardThread()
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "ClipboardShare" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            public Task<T> RunAsync<T>(Func<IntPtr, T> work)
            {
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _work.Enqueue(() =>
                {
                    try { tcs.SetResult(work(_hwnd)); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                _wake.Set();
                return tcs.Task;
            }

            private void Run()
            {
                // A message-only window: no taskbar, no broadcasts, just an owner for the clipboard.
                _hwnd = NativeMethods.CreateWindowExW(0, "STATIC", "StreamTweak clipboard", 0, 0, 0, 0, 0,
                                                      NativeMethods.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                    DebugLogger.Log($"ClipboardShare: owner window not created (error {Marshal.GetLastWin32Error()})");

                IntPtr[] handles = { _wake.SafeWaitHandle.DangerousGetHandle() };
                while (!_stop)
                {
                    NativeMethods.MsgWaitForMultipleObjectsEx(1, handles, NativeMethods.INFINITE,
                                                              NativeMethods.QS_ALLINPUT, NativeMethods.MWMO_INPUTAVAILABLE);
                    while (NativeMethods.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
                    {
                        NativeMethods.TranslateMessage(ref msg);
                        NativeMethods.DispatchMessageW(ref msg);
                    }
                    while (_work.TryDequeue(out var item))
                        item();
                }
                if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
            }

            public void Dispose()
            {
                _stop = true;
                _wake.Set();
                _thread.Join(1000);
                _wake.Dispose();
            }
        }

        private static class NativeMethods
        {
            public const uint CF_UNICODETEXT = 13;
            public const uint GMEM_MOVEABLE  = 0x0002;
            public const uint INFINITE       = 0xFFFFFFFF;
            public const uint QS_ALLINPUT    = 0x04FF;
            public const uint MWMO_INPUTAVAILABLE = 0x0004;
            public const uint PM_REMOVE      = 0x0001;
            public static readonly IntPtr HWND_MESSAGE = new(-3);

            [StructLayout(LayoutKind.Sequential)]
            public struct MSG
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam;
                public IntPtr lParam;
                public uint time;
                public int ptX;
                public int ptY;
                public uint lPrivate;
            }

            [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
            [DllImport("user32.dll")] public static extern bool CloseClipboard();
            [DllImport("user32.dll")] public static extern bool EmptyClipboard();
            [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint uFormat);
            [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
            [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
            [DllImport("user32.dll")] public static extern int CountClipboardFormats();
            [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormatW(string lpszFormat);

            [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
            [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr hMem);
            [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr hMem);
            [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr hMem);
            [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr hMem);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
                                                        int x, int y, int nWidth, int nHeight,
                                                        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
            [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
            [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
            [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
            [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        }
    }
}
