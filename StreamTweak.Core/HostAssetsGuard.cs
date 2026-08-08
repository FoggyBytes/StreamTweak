using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Puts StreamTweak's Desktop and Steam tiles back after the streaming server overwrites
    /// them.
    ///
    /// <para><b>Why this exists.</b> Updating Vibeshine (or Sunshine, or any of the forks)
    /// reinstalls its own <c>assets\desktop.png</c> and <c>assets\steam.png</c>. The
    /// <c>*_backup.png</c> files from the original swap survive, so everything StreamTweak
    /// looked at still said "applied" while the tiles on screen were the server's again —
    /// the setting was on and doing nothing, which is the worst of the three states.</para>
    ///
    /// <para><b>Watcher and poll, not one or the other.</b> The watcher catches the normal
    /// case immediately; the poll catches what watchers miss — a directory replaced wholesale
    /// rather than written into, and the handle going stale when an installer removes and
    /// recreates the folder. The same pairing NVIDIA Sentinel uses, for the same reason.</para>
    ///
    /// <para>Identity is the file's hash, not its timestamp: an installer writes a fresh
    /// timestamp on a file that may or may not differ, and re-copying on every touch would
    /// mean writing into Program Files for no reason.</para>
    /// </summary>
    public sealed class HostAssetsGuard : IDisposable
    {
        private static readonly string[] TileNames = { "desktop.png", "steam.png" };

        /// <summary>Set by the UI from config: false means the user is not using our tiles.</summary>
        public Func<bool>? EnabledProvider { get; set; }

        /// <summary>Where the bundled replacements live (…\Resources).</summary>
        private readonly string _sourceDir;

        /// <summary>Re-resolved on each poll: the server can be reinstalled elsewhere.</summary>
        private readonly Func<string?> _assetsDirProvider;

        private readonly Func<string, string, string, Task<bool>> _applyAsync;

        private FileSystemWatcher? _watcher;
        private Timer? _poll;
        private string? _watchedDir;
        private int _working;          // 0/1, guards against overlapping restores
        private DateTime _quietUntil;  // debounce window after our own writes
        private bool _disposed;

        private const int PollMs      = 60_000;
        private const int DebounceMs  = 2_500;

        public HostAssetsGuard(string sourceDir,
                               Func<string?> assetsDirProvider,
                               Func<string, string, string, Task<bool>> applyAsync)
        {
            _sourceDir         = sourceDir;
            _assetsDirProvider = assetsDirProvider;
            _applyAsync        = applyAsync;
        }

        public void Start()
        {
            if (_disposed) return;
            _poll = new Timer(_ => _ = CheckAsync(), null, 5_000, PollMs);
        }

        /// <summary>
        /// Called by the UI right after the user swaps or restores by hand, so the guard picks
        /// up the new state without waiting for a poll — and does not immediately "repair" a
        /// restore the user just asked for.
        /// </summary>
        public void NudgeAfterUserAction()
        {
            _quietUntil = DateTime.UtcNow.AddSeconds(5);
            _ = CheckAsync();
        }

        private async Task CheckAsync()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _working, 1) == 1) return;

            try
            {
                if (EnabledProvider?.Invoke() != true) { StopWatching(); return; }

                string? assets = _assetsDirProvider();
                if (string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) { StopWatching(); return; }

                EnsureWatching(assets);

                if (DateTime.UtcNow < _quietUntil) return;

                string desktopSrc = Path.Combine(_sourceDir, "desktop.png");
                string steamSrc   = Path.Combine(_sourceDir, "steam.png");
                if (!File.Exists(desktopSrc) || !File.Exists(steamSrc)) return;

                bool stale = !SameFile(Path.Combine(assets, "desktop.png"), desktopSrc)
                          || !SameFile(Path.Combine(assets, "steam.png"),   steamSrc);
                if (!stale) return;

                DebugLogger.Log("[HostAssets] host tiles no longer ours — restoring them");

                // Our own writes land in this folder, so hold the watcher off for a moment
                // rather than letting it re-trigger on the copies we are about to make.
                _quietUntil = DateTime.UtcNow.AddMilliseconds(DebounceMs);

                bool ok = await _applyAsync(assets, desktopSrc, steamSrc);
                DebugLogger.Log(ok
                    ? "[HostAssets] tiles restored"
                    : "[HostAssets] could not restore the tiles (is the service running?)");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[HostAssets] check failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _working, 0);
            }
        }

        private void EnsureWatching(string assets)
        {
            if (_watcher != null && string.Equals(_watchedDir, assets, StringComparison.OrdinalIgnoreCase))
                return;

            StopWatching();
            try
            {
                var w = new FileSystemWatcher(assets, "*.png")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                                 | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                w.Changed += OnAssetTouched;
                w.Created += OnAssetTouched;
                w.Renamed += OnAssetTouched;
                w.EnableRaisingEvents = true;

                _watcher    = w;
                _watchedDir = assets;
            }
            catch (Exception ex)
            {
                // A missing or locked folder is not worth a retry storm: the poll comes back.
                DebugLogger.Log($"[HostAssets] cannot watch '{assets}': {ex.Message}");
            }
        }

        private void OnAssetTouched(object sender, FileSystemEventArgs e)
        {
            string name = e.Name ?? "";
            foreach (var tile in TileNames)
            {
                if (name.EndsWith(tile, StringComparison.OrdinalIgnoreCase))
                {
                    // An installer writes in bursts; let it finish before looking.
                    _ = Task.Delay(DebounceMs).ContinueWith(_ => CheckAsync());
                    return;
                }
            }
        }

        private void StopWatching()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnAssetTouched;
                    _watcher.Created -= OnAssetTouched;
                    _watcher.Renamed -= OnAssetTouched;
                    _watcher.Dispose();
                }
            }
            catch { }
            _watcher    = null;
            _watchedDir = null;
        }

        /// <summary>Content comparison. A missing target counts as different.</summary>
        private static bool SameFile(string a, string b)
        {
            try
            {
                if (!File.Exists(a) || !File.Exists(b)) return false;
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (fa.Length != fb.Length) return false;      // cheap reject before hashing

                using var sa = File.OpenRead(a);
                using var sb = File.OpenRead(b);
                return Convert.ToHexString(SHA256.HashData(sa))
                    == Convert.ToHexString(SHA256.HashData(sb));
            }
            catch { return false; }
        }

        public void Dispose()
        {
            _disposed = true;
            StopWatching();
            try { _poll?.Dispose(); } catch { }
            _poll = null;
        }
    }
}
