using System.Runtime.Versioning;
using System.Text.Json;

namespace StreamTweakService;

/// <summary>
/// Drives Windows Update Agent (WUA) from the LocalSystem service: scan → classify →
/// download → install → (reboot if required). Used by the remote "Update host" feature
/// (StreamLight) via the named pipe.
///
/// Runs WUA through late-bound COM (<c>dynamic</c> + ProgIDs) on purpose: it avoids a
/// WUApiLib COM interop reference, which would break the Linux CI builds that compile the
/// Windows-targeted TFM (see CLAUDE.md §7). Everything is wrapped so it never throws to the
/// pipe thread.
///
/// One job at a time (WUA itself is single-instance on the machine). The manager is a
/// process-wide singleton; the pipe handlers call <see cref="StartCheck"/> /
/// <see cref="StartInstall"/> (fire-and-forget) and poll <see cref="GetStateJson"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateManager
{
    public static readonly WindowsUpdateManager Instance = new();
    private WindowsUpdateManager() { }

    // ── UpdateClassification GUIDs (WSUS standard) ──────────────────────────
    private const string CatCritical   = "E6CF1350-C01B-414D-A61F-263D14D133B4";
    private const string CatSecurity   = "0FA1201D-4330-4FA8-8AE9-B877473B6441";
    private const string CatDefinition = "E0789628-CE08-4437-BE74-2495B842F43B";
    private const string CatUpgrade    = "3689BDC8-B205-4CE4-AE71-B6164B3A92A8";

    // Phases reported to the client (must match the StreamLight state machine).
    public const string PhaseIdle        = "IDLE";
    public const string PhaseChecking    = "CHECKING";
    public const string PhaseCheckReady  = "CHECK_READY";
    public const string PhaseNoUpdates   = "NO_UPDATES";
    public const string PhaseDownloading = "DOWNLOADING";
    public const string PhaseInstalling  = "INSTALLING";
    public const string PhaseRebooting   = "REBOOTING";
    public const string PhaseDone        = "DONE";
    public const string PhaseError       = "ERROR";

    private sealed record UpdateInfo(string Title, string Kb, string Category, long SizeMb, bool Selectable);

    private readonly object _lock = new();
    private string _phase = PhaseIdle;
    private int _percent = -1;
    private string _message = "";
    private string _error = "";
    private bool _busy;
    private List<UpdateInfo> _list = new();

    // Cached COM search result from the last check, reused by install so the installed
    // set matches what the user saw. WUA objects are agile (MTA), safe across thread-pool
    // threads. Held as dynamic — the manager lives for the process lifetime.
    private dynamic? _searchResult;

    // When the cached search completed. A WUA search result held for a long time can go
    // stale (WUA service recycle, updates superseded), making Install() throw an opaque
    // COM error. If the cached result is older than this, the install re-scans first so
    // it works with fresh COM objects instead of failing with a cryptic message.
    private DateTime _searchCompletedUtc = DateTime.MinValue;
    private static readonly TimeSpan SearchStaleAfter = TimeSpan.FromMinutes(10);

    private void SetState(string phase, int percent, string message, string error = "")
    {
        lock (_lock)
        {
            _phase = phase;
            _percent = percent;
            _message = message;
            _error = error;
        }
    }

    /// <summary>Compact JSON snapshot for the UPDATEPROGRESS poll.</summary>
    public string GetStateJson()
    {
        lock (_lock)
        {
            var payload = new
            {
                phase = _phase,
                percent = _percent,
                message = _message,
                error = _error,
                updates = _list.Select(u => new
                {
                    title = u.Title,
                    kb = u.Kb,
                    category = u.Category,
                    sizeMb = u.SizeMb,
                    selectable = u.Selectable,
                }).ToArray(),
                counts = new
                {
                    security = _list.Count(u => u.Category is "security" or "critical"),
                    defender = _list.Count(u => u.Category == "defender"),
                    optional = _list.Count(u => u.Category == "optional"),
                    upgrade  = _list.Count(u => u.Category == "upgrade"),
                },
            };
            return JsonSerializer.Serialize(payload);
        }
    }

    /// <summary>Starts an async scan. No-op (returns false) if a job is already running.</summary>
    public bool StartCheck()
    {
        lock (_lock)
        {
            if (_busy) return false;
            _busy = true;
            _list = new();
            _searchResult = null;
        }
        SetState(PhaseChecking, -1, "Checking for updates…");
        _ = Task.Run(RunCheck);
        return true;
    }

    /// <summary>Starts an async download+install of the cached scan filtered by scope
    /// ("SEC" = security+critical+defender, "ALL" = everything except feature upgrades).
    /// No-op if a job is running or no check has completed.</summary>
    public bool StartInstall(string scope)
    {
        lock (_lock)
        {
            if (_busy) return false;
            if (_searchResult == null) return false;
            _busy = true;
        }
        SetState(PhaseDownloading, 0, "Preparing…");
        _ = Task.Run(() => RunInstall(scope));
        return true;
    }

    // ── WUA worker bodies ───────────────────────────────────────────────────

    private void RunCheck()
    {
        try
        {
            Refresh();   // populates _searchResult / _list / _searchCompletedUtc under lock

            lock (_lock) { _busy = false; }

            int found;
            lock (_lock) found = _list.Count(u => u.Selectable);
            if (found == 0)
                SetState(PhaseNoUpdates, -1, "The host is already up to date.");
            else
                SetState(PhaseCheckReady, -1, $"{found} update(s) found.");
        }
        catch (Exception ex)
        {
            lock (_lock) { _busy = false; }
            SetState(PhaseError, -1, "Couldn't check for updates.", Short(ex.Message));
        }
    }

    /// <summary>
    /// Runs a fresh WUA search and refreshes the cached result, classified list, and
    /// completion timestamp. Used by the check and by the install when the cached result
    /// is stale. Returns the live COM search result.
    /// </summary>
    private dynamic Refresh()
    {
        dynamic session = NewCom("Microsoft.Update.Session");
        session.ClientApplicationID = "StreamTweak";
        dynamic searcher = session.CreateUpdateSearcher();
        // Software + driver updates not yet installed and not hidden.
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");

        var list = new List<UpdateInfo>();
        int count = result.Updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item[i];
            string category = Classify(u);
            long sizeMb = 0;
            try { sizeMb = (long)((decimal)u.MaxDownloadSize / (1024 * 1024)); } catch { }
            string kb = "";
            try { if (u.KBArticleIDs.Count > 0) kb = "KB" + u.KBArticleIDs.Item[0]; } catch { }
            // Feature upgrades are surfaced for visibility but never installed remotely.
            bool selectable = category != "upgrade";
            list.Add(new UpdateInfo((string)u.Title, kb, category, sizeMb, selectable));
        }

        lock (_lock)
        {
            _searchResult = result;
            _list = list;
            _searchCompletedUtc = DateTime.UtcNow;
        }
        return result;
    }

    private void RunInstall(string scope)
    {
        // Pick a fresh-enough search result. A cached result older than SearchStaleAfter
        // (or none) is re-scanned up front so Install() never runs against stale COM
        // objects. If the cached result turns out to be stale mid-use anyway, the COM
        // exception is caught below and the install retried once against a fresh scan.
        dynamic? cached;
        bool stale;
        lock (_lock)
        {
            cached = _searchResult;
            stale  = cached == null || DateTime.UtcNow - _searchCompletedUtc > SearchStaleAfter;
        }

        try
        {
            dynamic result = stale ? Refresh() : cached!;
            try
            {
                InstallFromResult(result, scope);
            }
            catch (System.Runtime.InteropServices.COMException) when (!stale)
            {
                // The cached COM objects went stale between the check and now — re-scan
                // once and retry the install against fresh objects.
                SetState(PhaseDownloading, 0, "Refreshing the update list…");
                InstallFromResult(Refresh(), scope);
            }
        }
        catch (Exception ex)
        {
            lock (_lock) { _busy = false; }
            SetState(PhaseError, -1, "Update failed.", Short(ex.Message));
        }
    }

    private void InstallFromResult(dynamic result, string scope)
    {
        {
            bool all = string.Equals(scope, "ALL", StringComparison.OrdinalIgnoreCase);

            // Build the install set from the search result, filtered by scope and
            // never including feature upgrades.
            dynamic toProcess = NewCom("Microsoft.Update.UpdateColl");
            long totalBytes = 0;
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = result.Updates.Item[i];
                string category = Classify(u);
                if (category == "upgrade") continue;                 // never remotely
                if (!all && category is not ("security" or "critical" or "defender")) continue;

                try { if (!u.EulaAccepted) u.AcceptEula(); } catch { }
                toProcess.Add(u);
                try { totalBytes += (long)(decimal)u.MaxDownloadSize; } catch { }
            }

            if (toProcess.Count == 0)
            {
                lock (_lock) { _busy = false; }
                SetState(PhaseNoUpdates, -1, "Nothing to install for the chosen scope.");
                return;
            }

            // Download one at a time so we can report a real size-weighted percentage
            // without depending on fragile COM progress callbacks from a service.
            dynamic session = NewCom("Microsoft.Update.Session");
            session.ClientApplicationID = "StreamTweak";
            long doneBytes = 0;
            int total = toProcess.Count;
            for (int i = 0; i < total; i++)
            {
                dynamic u = toProcess.Item[i];
                dynamic one = NewCom("Microsoft.Update.UpdateColl");
                one.Add(u);
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = one;
                int pct = totalBytes > 0 ? (int)(doneBytes * 100 / totalBytes) : (int)(i * 100L / total);
                SetState(PhaseDownloading, pct, $"Downloading {i + 1} of {total}…");
                downloader.Download();
                try { doneBytes += (long)(decimal)u.MaxDownloadSize; } catch { }
            }

            // Install the whole set in one call so WUA handles ordering/dependencies.
            // Install progress isn't reliably pollable without COM callbacks, so this phase
            // is reported without a percentage (the client shows an indeterminate bar).
            SetState(PhaseInstalling, -1, $"Installing {total} update(s)…");
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = toProcess;
            dynamic installResult = installer.Install();

            bool rebootRequired = false;
            try { rebootRequired = (bool)installResult.RebootRequired; } catch { }

            lock (_lock) { _busy = false; }

            if (rebootRequired)
            {
                SetState(PhaseRebooting, 100, "Restarting the host to finish…");
                // Give the client a moment to poll REBOOTING before the box goes down.
                RebootHost();
            }
            else
            {
                SetState(PhaseDone, 100, "Updates installed. No restart needed.");
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static dynamic NewCom(string progId)
    {
        Type t = Type.GetTypeFromProgID(progId, throwOnError: true)!;
        return Activator.CreateInstance(t)!;
    }

    // Returns "security" | "critical" | "defender" | "upgrade" | "optional".
    private static string Classify(dynamic update)
    {
        bool isCritical = false, isSecurity = false, isDefinition = false, isUpgrade = false;
        try
        {
            int n = update.Categories.Count;
            for (int i = 0; i < n; i++)
            {
                string id = ((string)update.Categories.Item[i].CategoryID).ToUpperInvariant();
                if (id == CatCritical)   isCritical = true;
                else if (id == CatSecurity)   isSecurity = true;
                else if (id == CatDefinition) isDefinition = true;
                else if (id == CatUpgrade)    isUpgrade = true;
            }
        }
        catch { }

        if (isUpgrade) return "upgrade";
        if (isSecurity) return "security";
        if (isCritical) return "critical";
        if (isDefinition) return "defender";

        // Defender platform/engine updates carry only the generic "Updates"
        // classification (not "Definition Updates"), so they'd fall to "optional" and be
        // skipped under the SEC ("Security + Defender") scope — the user then has to pick
        // "All updates" to get them. Catch them by title so any Defender-related update
        // lands in the "defender" bucket and installs under SEC as expected.
        try
        {
            string title = ((string)update.Title).ToUpperInvariant();
            if (title.Contains("DEFENDER") || title.Contains("ANTIMALWARE")
                || title.Contains("SECURITY INTELLIGENCE"))
                return "defender";
        }
        catch { }

        return "optional";
    }

    private static void RebootHost()
    {
        try
        {
            // Full System32 path (not bare "shutdown") to defeat a hijacked PATH.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = "/r /t 10",   // delay so the client sees the REBOOTING state
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { }
    }

    private static string Short(string s) => s.Length > 200 ? s.Substring(0, 200) : s;
}
