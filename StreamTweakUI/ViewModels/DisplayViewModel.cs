using System.Collections.ObjectModel;
using StreamTweak;

namespace StreamTweak.ViewModels
{
    // ── Per-monitor observable wrapper ────────────────────────────────────────

    public sealed class MonitorEntry : ViewModelBase
    {
        public MonitorInfo Info { get; }

        public string FriendlyName => Info.FriendlyName;
        public string ResolutionText => Info.Width > 0 && Info.Height > 0
            ? $"{Info.Width} × {Info.Height}   {Info.RefreshRateHz} Hz"
            : "Resolution unknown";
        public bool HdrSupported => Info.HdrSupported;
        public bool IsVirtual => Info.IsVirtual;

        private string _hdrStateText = string.Empty;
        public string HdrStateText
        {
            get => _hdrStateText;
            set => SetProperty(ref _hdrStateText, value);
        }

        private string _hdrStateColorHex = "#99FFFFFF";
        public string HdrStateColorHex
        {
            get => _hdrStateColorHex;
            set => SetProperty(ref _hdrStateColorHex, value);
        }

        private bool _hdrEnabled;
        public bool HdrEnabled
        {
            get => _hdrEnabled;
            set
            {
                if (SetProperty(ref _hdrEnabled, value))
                    RefreshHdrText();
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public MonitorEntry(MonitorInfo info)
        {
            Info = info;
            _hdrEnabled = info.HdrEnabled;
            RefreshHdrText();
        }

        private void RefreshHdrText()
        {
            if (!HdrSupported)
            {
                HdrStateText     = "HDR not supported";
                HdrStateColorHex = "#66FFFFFF";
            }
            else if (_hdrEnabled)
            {
                HdrStateText     = "HDR  ON";
                HdrStateColorHex = "#FF4ade80";
            }
            else
            {
                HdrStateText     = "HDR  OFF";
                HdrStateColorHex = "#99FFFFFF";
            }
        }
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class DisplayViewModel : ViewModelBase
    {
        // ── Monitor list ──────────────────────────────────────────────────────

        public ObservableCollection<MonitorEntry> Monitors { get; } = new();

        // ── Auto HDR ──────────────────────────────────────────────────────────

        private bool _autoHdrEnabled;
        public bool AutoHdrEnabled
        {
            get => _autoHdrEnabled;
            set
            {
                if (!SetProperty(ref _autoHdrEnabled, value)) return;
                _ = ApplyAutoHdrAsync(value);
            }
        }

        private bool _showAutoHdrWarning;
        public bool ShowAutoHdrWarning
        {
            get => _showAutoHdrWarning;
            private set => SetProperty(ref _showAutoHdrWarning, value);
        }

        // ── Loading / error ───────────────────────────────────────────────────

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        private string _errorText = string.Empty;
        public string ErrorText
        {
            get => _errorText;
            private set => SetProperty(ref _errorText, value);
        }

        public bool HasError => !string.IsNullOrEmpty(_errorText);

        // ── Guard against re-entrant Auto HDR setter ──────────────────────────
        private bool _autoHdrBusy;

        // ── Public API ────────────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            IsLoading = true;
            ErrorText = string.Empty;
            OnPropertyChanged(nameof(HasError));

            try
            {
                var monitors = await HdrService.GetMonitorsAsync();
                bool autoHdr = await HdrService.GetAutoHdrAsync();

                Monitors.Clear();
                foreach (var m in monitors)
                    Monitors.Add(new MonitorEntry(m));

                _autoHdrBusy = true;
                _autoHdrEnabled = autoHdr;
                OnPropertyChanged(nameof(AutoHdrEnabled));
                _autoHdrBusy = false;

                RefreshAutoHdrWarning();
            }
            catch (Exception ex)
            {
                ErrorText = $"Error reading display info: {ex.Message}";
                OnPropertyChanged(nameof(HasError));
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task ToggleMonitorHdrAsync(MonitorEntry entry, bool enable)
        {
            if (entry.IsBusy) return;
            entry.IsBusy = true;
            try
            {
                await HdrService.SetHdrAsync(entry.Info.AdapterId, entry.Info.TargetId, enable);
                entry.Info.HdrEnabled = enable;
                entry.HdrEnabled = enable;
                RefreshAutoHdrWarning();
            }
            catch (Exception ex)
            {
                // Revert toggle on failure
                entry.HdrEnabled = !enable;
                ErrorText = $"Could not change HDR: {ex.Message}";
                OnPropertyChanged(nameof(HasError));
            }
            finally
            {
                entry.IsBusy = false;
            }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private async Task ApplyAutoHdrAsync(bool enable)
        {
            if (_autoHdrBusy) return;
            await HdrService.SetAutoHdrAsync(enable);
            RefreshAutoHdrWarning();
        }

        private void RefreshAutoHdrWarning()
        {
            bool anyHdrOn = Monitors.Any(m => m.HdrEnabled);
            ShowAutoHdrWarning = _autoHdrEnabled && !anyHdrOn;
        }
    }
}
