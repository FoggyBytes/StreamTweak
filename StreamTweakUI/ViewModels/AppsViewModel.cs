using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using StreamTweak;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StreamTweak.ViewModels
{
    // ── Per-app observable entry ──────────────────────────────────────────────

    public sealed class AppEntry : ViewModelBase
    {
        public string Name { get; }
        public string Path { get; }

        private bool _autoManage;
        public bool AutoManage
        {
            get => _autoManage;
            set => SetProperty(ref _autoManage, value);
        }

        private BitmapImage? _icon;
        public BitmapImage? Icon
        {
            get => _icon;
            set => SetProperty(ref _icon, value);
        }

        public AppEntry(ManagedApp app)
        {
            Name       = app.Name;
            Path       = app.Path;
            _autoManage = app.AutoManage;
        }

        public ManagedApp ToManagedApp() => new()
        {
            Name = Name,
            Path = Path,
            AutoManage = _autoManage
        };
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public sealed class AppsViewModel : ViewModelBase
    {
        private static readonly string AppsFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "managedapps.json");

        // ── App list ──────────────────────────────────────────────────────────

        public ObservableCollection<AppEntry> Apps { get; } = new();

        private AppEntry? _selectedApp;
        public AppEntry? SelectedApp
        {
            get => _selectedApp;
            set
            {
                SetProperty(ref _selectedApp, value);
                OnPropertyChanged(nameof(HasSelection));
            }
        }

        public bool HasSelection => _selectedApp != null;

        // ── Status bar ────────────────────────────────────────────────────────

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

        // 10.0.0: StreamLight can flip an app's management over the bridge while this page is
        // open. The flag is then taken from disk without being written back.
        private Microsoft.UI.Dispatching.DispatcherQueue? _ui;
        private bool _subscribed;
        private bool _applyingExternal;

        private void OnExternalChange() => _ui?.TryEnqueue(() =>
        {
            var onDisk = ManagedAppController.List();
            _applyingExternal = true;
            try
            {
                foreach (var entry in Apps)
                {
                    var app = onDisk.FirstOrDefault(a => string.Equals(a.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
                    if (app != null) entry.AutoManage = app.AutoManage;
                }
            }
            finally { _applyingExternal = false; }
        });

        /// <summary>Stops following changes made over the bridge (page navigated away).</summary>
        public void Unsubscribe()
        {
            if (!_subscribed) return;
            ManagedAppController.Changed -= OnExternalChange;
            _subscribed = false;
        }

        public void Load()
        {
            if (!_subscribed)
            {
                _ui ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                ManagedAppController.Changed += OnExternalChange;
                _subscribed = true;
            }
            try
            {
                if (!File.Exists(AppsFilePath)) return;
                var list = JsonSerializer.Deserialize<List<ManagedApp>>(
                    File.ReadAllText(AppsFilePath)) ?? new();

                Apps.Clear();
                foreach (var app in list)
                {
                    var entry = new AppEntry(app);
                    entry.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(AppEntry.AutoManage)) Save();
                    };
                    Apps.Add(entry);
                }

                // Load icons on background thread pool, marshal back to UI via DispatcherQueue
                _ = LoadIconsAsync();
            }
            catch { /* file read errors are non-fatal */ }
        }

        /// <summary>
        /// 10.0: the second way to add an app, after the programs running now — any executable,
        /// so one does not have to be started first to be added.
        /// </summary>
        public async Task BrowseAsync()
        {
            var picker = new FileOpenPicker();
            if (App.MainWindow is { } win)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(win));

            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await AddAsync(file.Path, System.IO.Path.GetFileNameWithoutExtension(file.Path));
        }

        /// <summary>One program running in this session with a window of its own: what Add app
        /// offers first (plan 10.0, Apps).</summary>
        public sealed record RunningProgram(string Name, string Path);

        /// <summary>
        /// The programs with a visible window right now, one per executable, minus the ones already
        /// in the list, Windows' own and StreamTweak itself. A process this account cannot open
        /// (elevated, protected) is skipped: its path is what the list is keyed on.
        /// </summary>
        public List<RunningProgram> RunningPrograms()
        {
            var known = new HashSet<string>(Apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            int self = Environment.ProcessId;
            var found = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id == self || proc.MainWindowHandle == IntPtr.Zero) continue;
                        string? path = proc.MainModule?.FileName;
                        if (string.IsNullOrEmpty(path) || known.Contains(path) || found.ContainsKey(path)) continue;
                        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        string name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                                    : !string.IsNullOrWhiteSpace(info.ProductName)     ? info.ProductName.Trim()
                                    : System.IO.Path.GetFileNameWithoutExtension(path);
                        found[path] = new RunningProgram(name, path);
                    }
                    catch { /* gone, or not ours to open */ }
                }
            }
            return found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static Task<BitmapImage?> IconFor(string path) => LoadIconForPathAsync(path);

        public async Task AddAsync(string path, string name)
        {
            if (Apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;

            var app = new ManagedApp { Name = name, Path = path };
            var entry = new AppEntry(app);
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppEntry.AutoManage)) Save();
            };
            Apps.Add(entry);
            Save();

            // Load icon for the new entry
            entry.Icon = await LoadIconForPathAsync(path);
        }

        public void RemoveSelected()
        {
            if (_selectedApp == null) return;
            Apps.Remove(_selectedApp);
            SelectedApp = null;
            Save();
        }

        public void KillSelected()
        {
            if (_selectedApp == null) return;
            try
            {
                bool found = ManagedAppController.KillAllByPath(_selectedApp.Path);
                ShowStatus(found
                    ? $"Terminated \"{_selectedApp.Name}\"."
                    : $"No running process found for \"{_selectedApp.Name}\".",
                    isError: !found);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not terminate \"{_selectedApp.Name}\": {ex.Message}", isError: true);
            }
        }

        public async Task RestartSelectedAsync()
        {
            if (_selectedApp == null) return;
            string path = _selectedApp.Path;
            string name = _selectedApp.Name;

            // Kill if running; not finding a process is fine — we still launch below.
            bool wasRunning = false;
            try { wasRunning = ManagedAppController.KillAllByPath(path); }
            catch (Exception ex)
            {
                ShowStatus($"Could not terminate \"{name}\": {ex.Message}", isError: true);
                return;
            }

            // Brief pause only when we actually killed a process, so it has time to exit.
            if (wasRunning)
                await Task.Delay(1500);

            if (!File.Exists(path))
            {
                ShowStatus($"Executable not found: {path}", isError: true);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                ShowStatus($"Started \"{name}\".", isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not start \"{name}\": {ex.Message}", isError: true);
            }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void Save()
        {
            if (_applyingExternal) return;
            // Through the controller, under the same lock as the bridge's writes (10.0.0).
            ManagedAppController.SaveAll(Apps.Select(a => a.ToManagedApp()).ToList());
        }

        private async Task LoadIconsAsync()
        {
            // Snapshot the list — avoid enumerating the ObservableCollection on background thread
            var snapshot = Apps.ToList();
            foreach (var entry in snapshot)
                entry.Icon = await LoadIconForPathAsync(entry.Path);
        }

        private static async Task<BitmapImage?> LoadIconForPathAsync(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 20);
                if (thumbnail == null) return null;
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumbnail);
                return bmp;
            }
            catch { return null; }
        }

        private void ShowStatus(string text, bool isError)
        {
            StatusText    = text;
            StatusIsError = isError;
            HasStatus     = true;
        }
    }
}
