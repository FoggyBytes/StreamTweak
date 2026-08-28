using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using StreamTweak.Nvidia;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    /// <summary>
    /// One captured global-profile setting in UI-bindable form. DisplayName falls
    /// back to the hex setting ID when the driver did not report a friendly name
    /// (friendly value-name mapping is a later phase).
    /// </summary>
    /// <summary>
    /// One captured setting shown as a single readable line: name · hex id · value label.
    /// Only settings whose value maps to a friendly catalog label are shown (IsReadable);
    /// raw internal values (DLSS sentinels, unmapped numbers) are hidden by design.
    /// </summary>
    public sealed class CapturedSettingVM
    {
        public CapturedSettingVM(NvidiaSnapshotEntry src)
        {
            DisplayName = string.IsNullOrWhiteSpace(src.Name) ? src.SettingIdHex : src.Name;
            HexId       = src.SettingIdHex;

            string? label = NvidiaSettingCatalog.GetValueLabel(src.SettingId, src.Value);
            IsReadable = label != null;
            ValueLabel = label ?? src.Value;
        }

        public string DisplayName { get; }
        public string HexId { get; }
        public string ValueLabel { get; }
        public bool IsReadable { get; }
    }

    /// <summary>
    /// ViewModel for NVIDIA Sentinel: capture the global driver profile to a .nip
    /// snapshot, restore it, list what was captured, and arm auto-restore (Phase 4).
    /// Friendly value names + default column come in Phase 3.
    /// </summary>
    public sealed class NvidiaProfileViewModel : ViewModelBase
    {
        private readonly NvidiaSentinelService? _svc;
        private readonly DispatcherQueue _ui;

        /// <summary>
        /// Persisted expand/collapse state of the CAPTURED SETTINGS panel. Bound
        /// TwoWay so it survives navigation (the NavigationView recreates this page
        /// every time) and app restarts. Backed directly by config.json.
        /// </summary>
        public bool CapturedExpanded
        {
            get => ConfigService.GetBool("NvidiaCapturedExpanded", true);
            set
            {
                if (value == ConfigService.GetBool("NvidiaCapturedExpanded", true)) return;
                ConfigService.Set("NvidiaCapturedExpanded", value);
                OnPropertyChanged();
            }
        }

        public NvidiaProfileViewModel()
        {
            _ui  = DispatcherQueue.GetForCurrentThread();
            _svc = AppStateService.Instance.NvidiaSentinel;

            GpuHeader = BuildGpuHeader(_svc);

            if (_svc != null)
            {
                _autoRestoreEnabled = _svc.AutoRestoreEnabled;
                _svc.AutoRestorePerformed  += OnAutoRestorePerformed;
                _svc.AutoRestoreStateChanged += OnAutoRestoreStateChanged;
            }

            UpdateLastRestoreText();
            UpdateStuckState();
        }

        public string GpuHeader { get; }

        /// <summary>e.g. "NVIDIA Game Ready Driver 610.47  ·  19/05/2026". Uses InvariantCulture
        /// for the version so the decimal separator is always a dot (610.47, not 610,47).</summary>
        private static string BuildGpuHeader(NvidiaSentinelService? svc)
        {
            if (svc == null || !svc.IsNvidiaAvailable) return "NVIDIA GPU";

            string type   = svc.DriverType;   // "Game Ready" / "Studio" / ""
            string prefix = string.IsNullOrEmpty(type)
                ? "NVIDIA Driver"
                : $"NVIDIA {type} Driver";

            string line = prefix;
            if (svc.DriverVersion > 0f)
                line += " " + svc.DriverVersion.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (svc.DriverReleaseDate is { } d)
                line += "  ·  " + d.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

            return line;
        }

        public ObservableCollection<CapturedSettingVM> CapturedSettings { get; } = new();

        // ── Snapshot state ─────────────────────────────────────────────────────

        private bool _hasSnapshot;
        public bool HasSnapshot
        {
            get => _hasSnapshot;
            private set => SetProperty(ref _hasSnapshot, value);
        }

        private int _capturedCount;
        public int CapturedCount
        {
            get => _capturedCount;
            private set
            {
                if (SetProperty(ref _capturedCount, value))
                    OnPropertyChanged(nameof(CapturedCountText));
            }
        }

        public string CapturedCountText => _capturedCount == 1
            ? "1 customized setting captured"
            : $"{_capturedCount} customized settings captured";

        private int _readableCount;
        /// <summary>How many captured settings have a human-readable value (shown in the panel).</summary>
        public int ReadableCount
        {
            get => _readableCount;
            private set
            {
                if (SetProperty(ref _readableCount, value))
                {
                    OnPropertyChanged(nameof(ReadableCountText));
                    OnPropertyChanged(nameof(HasReadableSettings));
                }
            }
        }

        public bool HasReadableSettings => _readableCount > 0;

        public string ReadableCountText => _readableCount == 1
            ? "1 readable setting"
            : $"{_readableCount} readable settings";

        private string _profileSavedText = "No profile saved yet.";
        public string ProfileSavedText
        {
            get => _profileSavedText;
            private set => SetProperty(ref _profileSavedText, value);
        }

        // ── Auto-restore ───────────────────────────────────────────────────────

        private bool _autoRestoreEnabled;
        public bool AutoRestoreEnabled
        {
            get => _autoRestoreEnabled;
            set
            {
                if (!SetProperty(ref _autoRestoreEnabled, value)) return;
                _svc?.SetAutoRestoreEnabled(value);
                ConfigService.Set("NvidiaAutoRestore", value);
                StatusMessage = value
                    ? "Auto-restore armed — your profile will be re-applied if NVIDIA App resets it."
                    : "Auto-restore disabled.";
            }
        }

        private string _lastRestoreText = "Never";
        public string LastRestoreText
        {
            get => _lastRestoreText;
            private set => SetProperty(ref _lastRestoreText, value);
        }

        // ── Busy / status ──────────────────────────────────────────────────────

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    OnPropertyChanged(nameof(IsNotBusy));
            }
        }

        /// <summary>Inverse of IsBusy — bound to button IsEnabled (no inverse-bool converter needed).</summary>
        public bool IsNotBusy => !_isBusy;

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (SetProperty(ref _statusMessage, value))
                    OnPropertyChanged(nameof(HasStatusMessage));
            }
        }

        public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            if (_svc != null) _autoRestoreEnabled = _svc.AutoRestoreEnabled;
            OnPropertyChanged(nameof(AutoRestoreEnabled));
            UpdateLastRestoreText();
            await LoadSnapshotAsync();
        }

        // ── Commands ───────────────────────────────────────────────────────────

        public async Task SaveCurrentAsync()
        {
            if (_svc == null || !_svc.IsNvidiaAvailable) return;
            IsBusy = true;
            StatusMessage = "Capturing current NVIDIA global profile…";
            try
            {
                await Task.Run(() => _svc.CaptureSnapshot());
                await LoadSnapshotAsync();
                StatusMessage = $"Captured {_capturedCount} customized settings.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Capture failed: {ex.Message}";
            }
            finally { IsBusy = false; }
        }

        public async Task RestoreAsync()
        {
            if (_svc == null || !_svc.IsNvidiaAvailable) return;
            if (!HasSnapshot) return;
            IsBusy = true;
            StatusMessage = "Restoring your saved profile…";
            try
            {
                string report = await Task.Run(() => _svc.RestoreSnapshot());
                StatusMessage = string.IsNullOrWhiteSpace(report)
                    ? "Profile restored to the captured state."
                    : "Restore completed with warnings — see nvidia-restore.log.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Restore failed: {ex.Message}";
            }
            finally { IsBusy = false; }
        }

        public async Task ClearAsync()
        {
            if (_svc == null || !HasSnapshot) return;
            IsBusy = true;
            try
            {
                _svc.ClearSnapshot();
                await LoadSnapshotAsync();   // file gone → HasSnapshot=false, list cleared
                StatusMessage = "Saved profile cleared.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Clear failed: {ex.Message}";
            }
            finally { IsBusy = false; }
        }

        public void OpenLogFile()
        {
            try
            {
                var path = NvidiaRestoreLogger.LogPath;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, string.Empty);
                }
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private async Task LoadSnapshotAsync()
        {
            CapturedSettings.Clear();
            string? path = _svc?.SnapshotPath;
            if (_svc == null || string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                HasSnapshot      = false;
                CapturedCount    = 0;
                ReadableCount    = 0;
                ProfileSavedText = "No profile saved yet.";
                return;
            }

            List<NvidiaSnapshotEntry>? entries = null;
            Exception? err = null;

            // Read the .nip and warm the catalog (XML parse + NGX version resolution)
            // off the UI thread, so the per-row label lookups below are fast dict reads.
            await Task.Run(() =>
            {
                try
                {
                    entries = NvidiaSentinelService.ReadSnapshot(path);
                    NvidiaSettingCatalog.Warm();
                }
                catch (Exception ex) { err = ex; }
            });

            if (err != null || entries == null)
            {
                HasSnapshot      = false;
                CapturedCount    = 0;
                ReadableCount    = 0;
                ProfileSavedText = "Could not read saved profile.";
                if (err != null) StatusMessage = $"Error reading snapshot: {err.Message}";
                return;
            }

            int readable = 0;
            foreach (var e in entries)
            {
                var vm = new CapturedSettingVM(e);
                if (!vm.IsReadable) continue;   // hide raw/internal values
                CapturedSettings.Add(vm);
                readable++;
            }

            CapturedCount    = entries.Count;
            ReadableCount    = readable;
            HasSnapshot      = true;
            ProfileSavedText = "Saved " + File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm");
        }

        private void UpdateLastRestoreText()
        {
            LastRestoreText = _svc?.LastRestoreAt is { } at
                ? at.ToLocalTime().ToString("dd/MM/yyyy  HH:mm:ss")
                : "Never";
        }

        private void OnAutoRestorePerformed(object? sender, EventArgs e)
        {
            _ui.TryEnqueue(() =>
            {
                UpdateLastRestoreText();
                StatusMessage = "Auto-restore performed — your profile was re-applied.";
            });
        }

        // ── Stuck state ────────────────────────────────────────────────────────

        private bool _isStuck;
        /// <summary>Auto-restore is armed but cannot put the profile back. Drives the warning.</summary>
        public bool IsStuck
        {
            get => _isStuck;
            private set => SetProperty(ref _isStuck, value);
        }

        private string _stuckMessage = "";
        public string StuckMessage
        {
            get => _stuckMessage;
            private set => SetProperty(ref _stuckMessage, value);
        }

        private void OnAutoRestoreStateChanged(object? sender, EventArgs e)
            => _ui.TryEnqueue(UpdateStuckState);

        private void UpdateStuckState()
        {
            if (_svc is not { IsStuck: true })
            {
                IsStuck      = false;
                StuckMessage = "";
                return;
            }

            // Say what is not happening, then the one thing that usually fixes it. The driver's
            // own wording is kept on the end because it is the only part that varies.
            string msg = "Your profile is not being protected. The driver refused to re-apply it, "
                       + "so auto-restore has stopped retrying. This usually means the saved profile "
                       + "was captured on a different driver version — save it again to fix it.";

            if (_svc.StuckSince is { } since)
                msg += $" Failing since {since.ToLocalTime():dd/MM/yyyy HH:mm}.";

            if (!string.IsNullOrWhiteSpace(_svc.StuckReason))
                msg += $"\n{_svc.StuckReason}";

            StuckMessage = msg;
            IsStuck      = true;
        }

        /// <summary>
        /// Unsubscribes from the singleton service's events. Called from the View's
        /// OnNavigatedFrom so navigating away doesn't leak this VM (a new VM is created
        /// on every navigation to the page).
        /// </summary>
        public void Cleanup()
        {
            if (_svc != null)
            {
                _svc.AutoRestorePerformed    -= OnAutoRestorePerformed;
                _svc.AutoRestoreStateChanged -= OnAutoRestoreStateChanged;
            }
        }
    }
}
