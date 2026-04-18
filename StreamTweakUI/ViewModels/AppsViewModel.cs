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

        public void Load()
        {
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

        public async Task AddAsync()
        {
            var picker = new FileOpenPicker();
            if (App.MainWindow is { } win)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(win));

            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string path = file.Path;
            if (Apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
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
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AppsFilePath)!);
                var list = Apps.Select(a => a.ToManagedApp()).ToList();
                string tmp = AppsFilePath + ".tmp";
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, AppsFilePath, overwrite: true);
            }
            catch { }
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
