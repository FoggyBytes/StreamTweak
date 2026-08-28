using Microsoft.Management.Infrastructure;
using StreamTweak.Nvidia.Drs;
using StreamTweak.Nvidia.Helper;
using StreamTweak.Nvidia.Import;
using StreamTweak.Nvidia.Native;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak.Nvidia
{
    /// <summary>
    /// NVIDIA Sentinel core, built on the ported NVIDIA Profile Inspector DRS layer.
    /// Captures the global driver profile as a diff-from-default .nip snapshot,
    /// restores it authoritatively, and — when armed — auto-restores it whenever
    /// NVIDIA App resets the profile.
    ///
    /// Correctness: capture/restore go through the same code paths NVPI uses,
    /// including the DrsDecrypterService, so encrypted "internal" settings (the old
    /// "(not set)" bug) are handled correctly.
    ///
    /// Auto-restore detection is hybrid: a FileSystemWatcher on the DRS database
    /// (fast, 50–200 ms) plus a 5-second polling safety net. An anti-loop time gate
    /// (_suppressUntil) prevents our own DRS write from re-triggering a restore.
    /// </summary>
    public sealed class NvidiaSentinelService : IDisposable
    {
        private readonly DrsImportService _import;

        // Auto-restore machinery
        private FileSystemWatcher _watcher;
        private Timer _pollTimer;
        private Timer _debounceTimer;
        // Serializes ALL DRS operations (UI capture/restore can race the poll timer).
        // C# lock is reentrant, so nested calls (drift-read → restore) are fine.
        private readonly object _drsLock = new();
        private volatile bool _restoring;
        private DateTime _suppressUntil = DateTime.MinValue;
        private bool _disposed;

        private const int DebounceMs = 800;

        // Safety net only — the FileSystemWatcher on nvdrsdb*.bin is what actually catches a
        // reset, within a couple of hundred milliseconds. This poll exists for the cases the
        // watcher cannot see (it failed to start, or the whole Drs folder was replaced rather
        // than written into). At 5 s it was costing ~125 ms every 5 s = 2.5% of a CPU core
        // around the clock, four times everything else the app does at rest.
        private const int PollMs = 30_000;

        private const int SuppressSeconds = 2;

        // ── Backoff for a restore that cannot succeed ─────────────────────────
        //
        // A restore can fail permanently and for reasons outside the app: a snapshot captured
        // on a different driver, or a setting the driver refuses to let a non-elevated process
        // write. Retrying that on every poll achieves nothing, burns CPU and buries the log —
        // it ran 1594 times in one day before this existed, all failing, in silence.
        private const int StuckAfterFailures = 3;
        private const int MaxBackoffMinutes  = 30;

        private int      _consecutiveFailures;
        private DateTime _retryNotBefore = DateTime.MinValue;

        public bool IsNvidiaAvailable { get; }

        /// <summary>Driver version as float (e.g. 610.47), 0 when unavailable.</summary>
        public float DriverVersion => IsNvidiaAvailable ? DrsSettingsServiceBase.DriverVersion : 0f;

        /// <summary>"Game Ready" | "Studio" | "" — the installed driver package type (NvAPI).</summary>
        public string DriverType { get; private set; } = "";

        /// <summary>Driver release date (from Win32_VideoController), null if unavailable.</summary>
        public DateTime? DriverReleaseDate { get; private set; }

        /// <summary>Where the user's snapshot .nip lives. Defaulted in the ctor; overridable.</summary>
        public string SnapshotPath { get; set; }

        public bool AutoRestoreEnabled { get; private set; }

        /// <summary>Timestamp of the last (manual or automatic) restore, null if never.</summary>
        public DateTime? LastRestoreAt { get; private set; }

        /// <summary>Set by the UI host to persist LastRestoreAt across app restarts.</summary>
        public Action<DateTime?> PersistLastRestoreCallback { get; set; }

        /// <summary>Raised after an automatic restore that SUCCEEDED. Handlers must marshal to
        /// the UI thread. A failed restore raises <see cref="AutoRestoreStateChanged"/> instead —
        /// this event used to fire either way, so the UI reported a restore that never happened.</summary>
        public event EventHandler AutoRestorePerformed;

        /// <summary>
        /// True when auto-restore has failed <see cref="StuckAfterFailures"/> times in a row and
        /// is therefore backing off. The profile is NOT being protected while this is true, which
        /// is the whole reason it is surfaced rather than left in the log.
        /// </summary>
        public bool IsStuck { get; private set; }

        /// <summary>The driver's own words for the last failure, for the UI to show verbatim.</summary>
        public string StuckReason { get; private set; } = "";

        /// <summary>When the run of failures began, null when not stuck.</summary>
        public DateTime? StuckSince { get; private set; }

        /// <summary>When the next retry is due while stuck, null when not stuck.</summary>
        public DateTime? NextRetryAt => IsStuck && _retryNotBefore > DateTime.Now ? _retryNotBefore : null;

        /// <summary>Raised when <see cref="IsStuck"/> changes. Handlers must marshal to the UI thread.</summary>
        public event EventHandler AutoRestoreStateChanged;

        public NvidiaSentinelService()
        {
            try
            {
                var wrapper = NvapiDrsWrapper.Instance;

                bool nvapiReady = wrapper.NvAPI_Initialize != null
                                  && wrapper.SYS_GetDriverAndBranchVersion != null;

                if (nvapiReady)
                {
                    var decrypter = new DrsDecrypterService();
                    _import = new DrsImportService(decrypter);
                    IsNvidiaAvailable = true;

                    try { DriverType = ResolveDriverType(wrapper); } catch { }
                    try { DriverReleaseDate = ResolveDriverReleaseDate(); } catch { }
                }
            }
            catch
            {
                IsNvidiaAvailable = false;
                _import = null;
            }

            SnapshotPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamTweak", "nvidia-sentinel", "snapshot.nip");
        }

        public bool HasSnapshot => !string.IsNullOrEmpty(SnapshotPath) && File.Exists(SnapshotPath);

        // ── Capture ──────────────────────────────────────────────────────────────

        /// <summary>Captures the current global profile (diff-from-default) to SnapshotPath.</summary>
        public void CaptureSnapshot()
        {
            EnsureAvailable();
            lock (_drsLock)
            {
                RefreshSession();
                Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
                _import.ExportGlobalProfile(SnapshotPath);
            }
            // A fresh capture is the usual cure for a stuck sentinel — a snapshot taken on a
            // different driver is exactly what the driver refuses to write back.
            ResetFailureState();
        }

        /// <summary>Captures the current global profile (diff-from-default) to an explicit path.</summary>
        public void CaptureSnapshot(string filePath)
        {
            EnsureAvailable();
            lock (_drsLock)
            {
                RefreshSession();
                _import.ExportGlobalProfile(filePath);
            }
        }

        /// <summary>
        /// Deletes the saved snapshot. Auto-restore then has nothing to re-apply and
        /// sits idle (CheckDriftAndRestore early-returns when HasSnapshot is false).
        /// A new profile can be captured at any time.
        /// </summary>
        public void ClearSnapshot()
        {
            try
            {
                if (!string.IsNullOrEmpty(SnapshotPath) && File.Exists(SnapshotPath))
                    File.Delete(SnapshotPath);
                NvidiaRestoreLogger.LogInfo("Saved profile cleared by user.");
            }
            catch (Exception ex)
            {
                NvidiaRestoreLogger.LogError($"clear profile failed: {ex.Message}");
            }
        }

        /// <summary>Captures the current global diff in-memory (no file written).</summary>
        public List<NvidiaSnapshotEntry> ReadCurrentGlobalDiff()
        {
            EnsureAvailable();
            lock (_drsLock)
            {
                RefreshSession();
                var profile = _import.CreateGlobalProfileExport();
                return ToEntries(profile);
            }
        }

        /// <summary>
        /// Reads the driver's predefined (default) value for every global-profile
        /// setting that has one. settingId → default-value-string. Used by the UI to
        /// show "default: X" next to each captured (changed-from-default) setting.
        /// </summary>
        public Dictionary<uint, string> ReadGlobalDefaults()
        {
            EnsureAvailable();
            lock (_drsLock)
            {
                RefreshSession();
                return _import.ReadGlobalDefaults();
            }
        }

        // ── Restore ────────────────────────────────────────────────────────────────

        /// <summary>Restores the global profile from SnapshotPath. Returns empty on success.</summary>
        public string RestoreSnapshot()
        {
            EnsureAvailable();
            lock (_drsLock)
            {
                RefreshSession();
                return _import.ImportProfiles(SnapshotPath);
            }
        }

        /// <summary>Restores the global profile from an explicit .nip path.</summary>
        public string RestoreSnapshot(string filePath)
        {
            EnsureAvailable();
            lock (_drsLock)
            {
                RefreshSession();
                return _import.ImportProfiles(filePath);
            }
        }

        // ── Read / diff ──────────────────────────────────────────────────────────

        public static List<NvidiaSnapshotEntry> ReadSnapshot(string filePath)
        {
            var profiles = XMLHelper<Profiles>.DeserializeFromXMLFile(filePath);
            var profile = profiles.FirstOrDefault(p => p.Executeables == null || p.Executeables.Count == 0)
                          ?? profiles.FirstOrDefault();
            return profile == null ? new List<NvidiaSnapshotEntry>() : ToEntries(profile);
        }

        public bool HasDrifted(string snapshotFilePath)
            => ComputeDriftDescriptors(snapshotFilePath).Count > 0;

        // ── Auto-restore ───────────────────────────────────────────────────────────

        /// <summary>
        /// Arms/disarms auto-restore. When armed, immediately performs one drift check
        /// (so an already-reset profile is fixed on enable) and starts the watcher + poll.
        /// </summary>
        public void SetAutoRestoreEnabled(bool enabled)
        {
            if (!IsNvidiaAvailable) return;
            AutoRestoreEnabled = enabled;
            if (enabled)
            {
                // Re-arming is the user saying "try again": start from a clean slate rather
                // than inheriting a backoff from before.
                ResetFailureState();
                StartMonitoring();
                // Initial check off the caller's (UI) thread.
                Task.Run(() => SafeCheck("arm"));
            }
            else
            {
                StopMonitoring();
            }
        }

        public void LoadLastRestoreAt(DateTime at) => LastRestoreAt = at;

        private void StartMonitoring()
        {
            if (_disposed) return;

            // 5-second polling safety net (created once, armed here).
            _pollTimer ??= new Timer(_ => SafeCheck("poll"), null, Timeout.Infinite, Timeout.Infinite);
            _pollTimer.Change(PollMs, PollMs);

            // Debounce timer for watcher bursts (created once, fired one-shot per burst).
            _debounceTimer ??= new Timer(_ => SafeCheck("watcher"), null, Timeout.Infinite, Timeout.Infinite);

            if (_watcher == null)
            {
                try
                {
                    var drsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "NVIDIA Corporation", "Drs");
                    if (Directory.Exists(drsDir))
                    {
                        _watcher = new FileSystemWatcher(drsDir, "nvdrsdb*.bin")
                        {
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                                         | NotifyFilters.FileName | NotifyFilters.CreationTime,
                        };
                        _watcher.Changed += OnDrsFileEvent;
                        _watcher.Created += OnDrsFileEvent;
                        _watcher.Renamed += OnDrsFileEvent;
                        _watcher.EnableRaisingEvents = true;
                    }
                }
                catch
                {
                    // Watcher is best-effort; the 5s poll still covers drift.
                    _watcher = null;
                }
            }
            else
            {
                _watcher.EnableRaisingEvents = true;
            }
        }

        private void StopMonitoring()
        {
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_watcher != null)
                _watcher.EnableRaisingEvents = false;
        }

        private void OnDrsFileEvent(object sender, FileSystemEventArgs e)
        {
            // Coalesce bursts: (re)arm the one-shot debounce timer.
            try { _debounceTimer?.Change(DebounceMs, Timeout.Infinite); }
            catch { /* disposed */ }
        }

        private void SafeCheck(string reason)
        {
            try { CheckDriftAndRestore(reason); }
            catch (Exception ex) { NvidiaRestoreLogger.LogError($"auto-restore check failed ({reason}): {ex.Message}"); }
        }

        /// <summary>
        /// Records a failed restore and pushes the next attempt further out: 1, 2, 4, 8, 16 then
        /// 30 minutes. After <see cref="StuckAfterFailures"/> in a row the feature declares itself
        /// stuck, because at that point it is not protecting anything and the user is the only one
        /// who can act — the causes are all outside the app (a snapshot from another driver, or a
        /// setting the driver will not let an unelevated process write).
        /// </summary>
        private void NoteRestoreFailed(string reason)
        {
            _consecutiveFailures++;

            int minutes = Math.Min(1 << Math.Min(_consecutiveFailures - 1, 5), MaxBackoffMinutes);
            _retryNotBefore = DateTime.Now.AddMinutes(minutes);

            bool becameStuck = !IsStuck && _consecutiveFailures >= StuckAfterFailures;
            if (becameStuck)
            {
                IsStuck    = true;
                StuckSince = DateTime.Now;
                NvidiaRestoreLogger.LogError(
                    $"auto-restore stuck: {_consecutiveFailures} consecutive failures, retrying every {minutes} min at most");
            }

            if (IsStuck)
            {
                StuckReason = reason;
                AutoRestoreStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Clears the failure run. Also called when there is simply no drift left to
        /// undo, which is how a stuck sentinel recovers once the user re-captures the profile.</summary>
        private void NoteRestoreSucceeded()
        {
            _consecutiveFailures = 0;
            _retryNotBefore      = DateTime.MinValue;

            if (!IsStuck) return;

            IsStuck     = false;
            StuckReason = "";
            StuckSince  = null;
            NvidiaRestoreLogger.LogInfo("auto-restore recovered — the profile is being protected again");
            AutoRestoreStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Forgets a failure run so the next check starts clean. Called when the user changes
        /// something that plausibly fixes it — re-capturing the profile, or re-arming auto-restore.
        /// </summary>
        public void ResetFailureState() => NoteRestoreSucceeded();

        private void CheckDriftAndRestore(string reason)
        {
            if (!AutoRestoreEnabled || _restoring || _disposed) return;
            if (DateTime.Now < _suppressUntil) return;
            if (!HasSnapshot) return;
            // Backing off after repeated failures. Checked before the lock and before the drift
            // read, which is itself the expensive part — while stuck there is no point paying it.
            if (DateTime.Now < _retryNotBefore) return;

            lock (_drsLock)
            {
                if (!AutoRestoreEnabled || _restoring || _disposed) return;
                if (DateTime.Now < _suppressUntil) return;
                if (DateTime.Now < _retryNotBefore) return;

                var changed = ComputeDriftDescriptors(SnapshotPath);
                if (changed.Count == 0)
                {
                    // Nothing to put back: whatever was wrong is no longer showing.
                    NoteRestoreSucceeded();
                    return;
                }

                _restoring = true;
                string report;
                try
                {
                    report = RestoreSnapshot();
                }
                finally
                {
                    _restoring = false;
                    // Absorb the nvdrsdb*.bin change events caused by our own write.
                    _suppressUntil = DateTime.Now.AddSeconds(SuppressSeconds);
                }

                NvidiaRestoreLogger.LogRestore(
                    reason,
                    changed.Count,
                    string.Join(",", changed.Take(40)));

                // A non-empty report means the driver refused part of the import, so the
                // settings are NOT back. Recording it as a restore — which is what used to
                // happen — made a permanent failure look like a working feature.
                if (!string.IsNullOrWhiteSpace(report))
                {
                    NvidiaRestoreLogger.LogError($"restore reported issues: {report.Trim()}");
                    NoteRestoreFailed(report.Trim());
                    return;
                }

                NoteRestoreSucceeded();

                LastRestoreAt = DateTime.Now;
                PersistLastRestoreCallback?.Invoke(LastRestoreAt);

                AutoRestorePerformed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Returns "Name(0xID)" descriptors for every setting that differs between the
        /// saved snapshot and the live global profile (missing, changed, or newly added).
        /// Empty list ⇒ no drift.
        /// </summary>
        private List<string> ComputeDriftDescriptors(string snapshotFilePath)
        {
            EnsureAvailable();

            var saved = ReadSnapshot(snapshotFilePath).ToDictionary(e => e.SettingId, e => e);
            var current = ReadCurrentGlobalDiff().ToDictionary(e => e.SettingId, e => e);

            var changed = new List<string>();

            foreach (var kvp in saved)
            {
                if (!current.TryGetValue(kvp.Key, out var cur) ||
                    !string.Equals(cur.Value, kvp.Value.Value, StringComparison.Ordinal))
                {
                    changed.Add(Describe(kvp.Value));
                }
            }
            foreach (var kvp in current)
            {
                if (!saved.ContainsKey(kvp.Key))
                    changed.Add(Describe(kvp.Value));
            }

            return changed;
        }

        private static string Describe(NvidiaSnapshotEntry e)
            => (string.IsNullOrWhiteSpace(e.Name) ? e.SettingIdHex : e.Name) + "(" + e.SettingIdHex + ")";

        // ── helpers ──────────────────────────────────────────────────────────────

        private void EnsureAvailable()
        {
            if (!IsNvidiaAvailable || _import == null)
                throw new InvalidOperationException("NVIDIA driver / NVAPI is not available.");
        }

        /// <summary>Game Ready vs Studio via NvAPI_SYS_GetDisplayDriverInfo (bit1=Studio, bit2=GameReady).</summary>
        private static string ResolveDriverType(NvapiDrsWrapper wrapper)
        {
            var fn = wrapper.NvAPI_SYS_GetDisplayDriverInfo;
            if (fn == null) return "";

            var info = new NV_DISPLAY_DRIVER_INFO_V1 { version = NvapiDrsWrapper.NV_DISPLAY_DRIVER_INFO_VER1 };
            if (fn(ref info) != NvAPI_Status.NVAPI_OK) return "";

            bool studio    = ((info.bits >> 1) & 1u) == 1u;
            bool gameReady = ((info.bits >> 2) & 1u) == 1u;
            if (gameReady) return "Game Ready";
            if (studio)    return "Studio";
            return "";
        }

        /// <summary>
        /// Driver release date. Prefers NVIDIA App's cached metadata (the real public
        /// publish date, e.g. 2026-05-26) and falls back to Win32_VideoController's
        /// DriverDate (the INF/Device-Manager build date, e.g. 2026-05-19) when NVIDIA
        /// App isn't installed or its cache doesn't match the installed version.
        /// </summary>
        private static DateTime? ResolveDriverReleaseDate()
        {
            float ver = DrsSettingsServiceBase.DriverVersion;
            string verStr = ver > 0f
                ? ver.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : "";

            return ReadReleaseDateFromNvApp(verStr) ?? ReadDriverDateFromCim();
        }

        /// <summary>
        /// Reads the publish date from NVIDIA App's
        /// %LocalAppData%\NVIDIA Corporation\NVIDIA App\NvBackend\DriverRecommendations.dat.
        /// Only trusts the date when the cached entry's "version" matches the installed
        /// driver (otherwise the file may describe a newer, not-yet-installed driver).
        /// </summary>
        private static DateTime? ReadReleaseDateFromNvApp(string installedVersion)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NVIDIA Corporation", "NVIDIA App", "NvBackend", "DriverRecommendations.dat");
                if (!File.Exists(path)) return null;

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("updates", out var updates)
                    || updates.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return null;

                foreach (var u in updates.EnumerateArray())
                {
                    string? v = u.TryGetProperty("version", out var vp) ? vp.GetString() : null;
                    if (!string.IsNullOrEmpty(installedVersion)
                        && !string.Equals(v, installedVersion, StringComparison.Ordinal))
                        continue;   // entry is for a different (e.g. newer) driver

                    if (u.TryGetProperty("releaseDateTime", out var rp)
                        && rp.ValueKind == System.Text.Json.JsonValueKind.String
                        && DateTime.TryParse(rp.GetString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal
                                | System.Globalization.DateTimeStyles.AssumeUniversal,
                            out var dt))
                        return dt.Date;
                }
            }
            catch { }
            return null;
        }

        /// <summary>INF/Device-Manager driver date from Win32_VideoController (CIM). Null if unresolvable.</summary>
        private static DateTime? ReadDriverDateFromCim()
        {
            try
            {
                using var session = CimSession.Create(null);
                const string query =
                    "SELECT DriverDate, Name FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'";
                foreach (var inst in session.QueryInstances(@"root\cimv2", "WQL", query))
                {
                    using (inst)
                    {
                        if (inst.CimInstanceProperties["DriverDate"]?.Value is DateTime dt)
                            return dt;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Drops the cached global DRS session so the next access re-creates it and
        /// re-runs DRS_LoadSettings, reading the CURRENT on-disk profile. Without this,
        /// the long-lived cached session would never see NVIDIA App's external reset and
        /// drift would never be detected. Called inside _drsLock at the start of every
        /// public DRS operation. The reentrant nested session (import → ResetProfile)
        /// still works because the session is only refreshed between top-level operations.
        /// </summary>
        private static void RefreshSession() => DrsSessionScope.DestroyGlobalSession();

        private static List<NvidiaSnapshotEntry> ToEntries(Profile profile)
        {
            var list = new List<NvidiaSnapshotEntry>(profile.Settings.Count);
            foreach (var s in profile.Settings)
            {
                list.Add(new NvidiaSnapshotEntry
                {
                    SettingId = s.SettingId,
                    Name = s.SettingNameInfo ?? "",
                    Value = s.SettingValue ?? "",
                    ValueType = s.ValueType.ToString(),
                });
            }
            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AutoRestoreEnabled = false;
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
            try { _pollTimer?.Dispose(); } catch { }
            try { _debounceTimer?.Dispose(); } catch { }
            _watcher = null;
            _pollTimer = null;
            _debounceTimer = null;
        }
    }
}
