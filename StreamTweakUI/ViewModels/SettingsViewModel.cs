using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    public sealed class SettingsViewModel : ViewModelBase
    {
        // ── About ─────────────────────────────────────────────────────────────

        public string AppVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "6.0.0";

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
            string sessionsPath = Path.Combine(DataFolderPath, "sessions.json");
            try
            {
                if (File.Exists(sessionsPath))
                    File.Delete(sessionsPath);
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
}
