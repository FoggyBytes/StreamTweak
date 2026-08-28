using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    public sealed class SettingsViewModel : ViewModelBase
    {
        public SettingsViewModel()
        {
            // Re-fire INotifyPropertyChanged on the InfoBar-bound properties whenever
            // AppStateService publishes a new GitHub-release poll result.
            AppStateService.Instance.UpdateAvailabilityChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(UpdateMessage));
            };
        }

        // ── About ─────────────────────────────────────────────────────────────

        public string AppVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "8.2.0";

        // ── Update notice (mirrors AppStateService) ───────────────────────────
        // Rebroadcasts the centralized GitHub-release poll into properties the
        // SettingsView InfoBar can bind to. Constructor subscribes to the
        // singleton event; no unsubscribe needed because SettingsViewModel and
        // AppStateService both live for the entire app lifetime.

        public bool   IsUpdateAvailable => AppStateService.Instance.UpdateAvailable;
        public string UpdateMessage     =>
            AppStateService.Instance.UpdateAvailable
                ? $"Version {AppStateService.Instance.LatestVersion} is available on GitHub."
                : string.Empty;

        private string _streamLightVersion = "Checking…";
        public string StreamLightVersion
        {
            get => _streamLightVersion;
            private set => SetProperty(ref _streamLightVersion, value);
        }

        // ── Paths ─────────────────────────────────────────────────────────────

        public string DataFolderPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak");

        private string _logFolderPath = "Not detected";
        public string LogFolderPath
        {
            get => _logFolderPath;
            private set => SetProperty(ref _logFolderPath, value);
        }

        // ── Streaming server ──────────────────────────────────────────────────

        private static readonly Dictionary<string, string> _serverRepos = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Sunshine"]  = "https://github.com/LizardByte/Sunshine",
            ["Apollo"]    = "https://github.com/ClassicOldSong/Apollo",
            ["Vibeshine"] = "https://github.com/Nonary/vibeshine",
            ["Vibepollo"] = "https://github.com/Nonary/Vibepollo",
        };

        private string _serverName = "Not detected";
        public string ServerName
        {
            get => _serverName;
            private set
            {
                if (SetProperty(ref _serverName, value))
                {
                    OnPropertyChanged(nameof(ServerRepoUrl));
                    OnPropertyChanged(nameof(HasServerRepo));
                }
            }
        }

        public string ServerRepoUrl =>
            _serverRepos.TryGetValue(_serverName, out var url) ? url : string.Empty;

        public bool HasServerRepo => !string.IsNullOrEmpty(ServerRepoUrl);

        // ── Behavior ──────────────────────────────────────────────────────────

        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "StreamTweak";

        public bool StartWithWindows
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(RunValueName) != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    if (key == null) return;
                    if (value)
                    {
                        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                        if (!string.IsNullOrEmpty(exe))
                            key.SetValue(RunValueName, $"\"{exe}\" --minimized");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, throwOnMissingValue: false);
                    }
                }
                catch (Exception ex)
                {
                    ShowStatus($"Could not update startup setting: {ex.Message}", isError: true);
                }
                OnPropertyChanged();
            }
        }

        // ── Session history ───────────────────────────────────────────────────

        // The static on SessionLogger is the single source of truth (it is what the
        // close paths read); this property only mirrors it and persists the choice.
        public bool RecordOnlyGameSessions
        {
            get => SessionLogger.RecordOnlyGameSessions;
            set
            {
                if (SessionLogger.RecordOnlyGameSessions == value) return;
                SessionLogger.RecordOnlyGameSessions = value;
                ConfigService.Set("RecordOnlyGameSessions", value);
                OnPropertyChanged();
            }
        }

        // ── Debug mode ────────────────────────────────────────────────────────

        private bool _isDebugModeActive;
        public bool IsDebugModeActive
        {
            get => _isDebugModeActive;
            set => SetProperty(ref _isDebugModeActive, value);
        }

        public async Task ToggleDebugMode(bool activate)
        {
            if (activate)
            {
                var action = AppStateService.Instance.StartDebugModeAction;
                if (action != null)
                {
                    await action();
                    IsDebugModeActive = true;
                    ShowStatus("Debug session started. Check Home and Logs tabs.", isError: false);
                }
            }
            else
            {
                AppStateService.Instance.StopDebugModeAction?.Invoke();
                IsDebugModeActive = false;
                ShowStatus("Debug session stopped.", isError: false);
            }
        }

        // ── Bridge security (7.2.0) ───────────────────────────────────────────
        // Authentication between StreamTweak and StreamLight is now mandatory; the
        // toggle that allowed turning it off (BridgeRequireAuth) was removed in 7.2.0.

        public ObservableCollection<BridgeClientItem> BridgeClients { get; } = new();

        public bool HasNoBridgeClients => BridgeClients.Count == 0;

        // Show only the bare device name. Older enrollments were stored with a
        // "StreamLight @ " prefix (DecodeClientName no longer adds it); strip it here so
        // existing entries display cleanly too.
        private static string StripStreamLightPrefix(string? name)
        {
            const string prefix = "StreamLight @ ";
            if (string.IsNullOrEmpty(name)) return "";
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : name;
        }

        public void RefreshBridgeClients()
        {
            BridgeClients.Clear();
            var auth = AppStateService.Instance.BridgeAuth;
            if (auth != null)
            {
                foreach (var c in auth.GetClients())
                {
                    (string status, string color, string bg, string border) = c.Status switch
                    {
                        "approved" => ("Authorized",       "#4ade80", "#1F4ade80", "#4D4ade80"),
                        "denied"   => ("Denied",           "#ef4444", "#1Aef4444", "#40ef4444"),
                        _          => ("Pending approval", "#f59e0b", "#1Af59e0b", "#40f59e0b"),
                    };
                    BridgeClients.Add(new BridgeClientItem
                    {
                        UniqueId         = c.UniqueId,
                        Name             = StripStreamLightPrefix(c.Name),
                        StatusLabel      = status,
                        CanApprove       = c.Status == "pending" || c.Status == "denied",
                        StatusColorHex   = color,
                        StatusBgHex      = bg,
                        StatusBorderHex  = border,
                    });
                }
            }
            OnPropertyChanged(nameof(HasNoBridgeClients));
        }

        public void ApproveBridgeClient(string uniqueId)
        {
            AppStateService.Instance.BridgeAuth?.Approve(uniqueId);
            RefreshBridgeClients();
        }

        public void RevokeBridgeClient(string uniqueId)
        {
            AppStateService.Instance.BridgeAuth?.Revoke(uniqueId);
            RefreshBridgeClients();
        }

        // ── Status ────────────────────────────────────────────────────────────

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        private bool _hasStatus;
        public bool HasStatus
        {
            get => _hasStatus;
            set => SetProperty(ref _hasStatus, value);
        }

        private bool _statusIsError;
        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetProperty(ref _statusIsError, value);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Load()
        {
            // Restore debug-mode toggle state (survives tab navigation)
            IsDebugModeActive = AppStateService.Instance.IsDebugModeActive;

            // Bridge security state
            RefreshBridgeClients();

            // Streaming server info via LogParser
            try
            {
                var info = LogParser.FindStreamingAppInfo();
                ServerName    = info?.AppName ?? "Not detected";
                LogFolderPath = info?.LogFolderPath ?? "Not detected";
            }
            catch
            {
                ServerName    = "Not detected";
                LogFolderPath = "Not detected";
            }

            OnPropertyChanged(nameof(StartWithWindows));
            OnPropertyChanged(nameof(RecordOnlyGameSessions));

            // Fetch StreamLight latest release from GitHub (fire-and-forget)
            _ = LoadStreamLightVersionAsync();
        }

        private static readonly HttpClient _httpClient = new()
        {
            DefaultRequestHeaders = { { "User-Agent", "StreamTweak" } }
        };

        private async Task LoadStreamLightVersionAsync()
        {
            try
            {
                var json = await _httpClient.GetStringAsync(
                    "https://api.github.com/repos/FoggyBytes/StreamLight/releases/latest");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                StreamLightVersion = doc.RootElement.TryGetProperty("tag_name", out var tag)
                    ? tag.GetString() ?? "N/A"
                    : "N/A";
            }
            catch
            {
                StreamLightVersion = "Unavailable";
            }
        }

        public void OpenDataFolder()
        {
            try
            {
                Directory.CreateDirectory(DataFolderPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName        = DataFolderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not open folder: {ex.Message}", isError: true);
            }
        }

        public void OpenLogFolder()
        {
            string path = _logFolderPath;
            if (path == "Not detected" || !Directory.Exists(path))
            {
                ShowStatus("Streaming server log folder not found.", isError: true);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not open folder: {ex.Message}", isError: true);
            }
        }

        public void OpenDebugLog()
        {
            string logPath = Path.Combine(DataFolderPath, "debug.log");
            if (!File.Exists(logPath))
            {
                ShowStatus("debug.log not found — it is created when debug logging is active.", isError: false);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = logPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not open debug log: {ex.Message}", isError: true);
            }
        }

        public void ClearSessions()
        {
            try
            {
                SessionLogger.ClearAll();
                ShowStatus("Session history cleared.", isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not clear sessions: {ex.Message}", isError: true);
            }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void ShowStatus(string text, bool isError)
        {
            StatusText    = text;
            StatusIsError = isError;
            HasStatus     = true;
        }
    }

    /// <summary>
    /// Lightweight view item for one row in the Settings "Bridge clients" list.
    /// </summary>
    public sealed class BridgeClientItem
    {
        public string UniqueId    { get; init; } = "";
        public string Name        { get; init; } = "";
        public string StatusLabel { get; init; } = "";
        public bool   CanApprove  { get; init; }   // pending OR denied → can be (re)approved

        // Status pill colours (green Authorized / amber Pending / red Denied),
        // matching the app-wide badge palette.
        public string StatusColorHex  { get; init; } = "#9E9E9E";
        public string StatusBgHex     { get; init; } = "#1A9E9E9E";
        public string StatusBorderHex { get; init; } = "#409E9E9E";
    }
}
