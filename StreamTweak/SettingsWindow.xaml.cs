using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace StreamTweak
{
    public partial class SettingsWindow : Window
    {
        [DllImport("DwmApi")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;

        private readonly string configFilePath;
        private Dictionary<string, string>? currentAdapterSpeeds;
        private bool isStreamingMode = false;
        private string originalSpeed = string.Empty;
        private bool isAutoStreamingEnabled = true;
        private bool _updateAvailable = false;
        private string currentAdapterName = string.Empty;

        public event EventHandler? SpeedApplied;
        public event EventHandler? StreamingModeChanged;
        public event EventHandler? AutoStreamingEnabledChanged;
        public event EventHandler? AudioMonitorEnabledChanged;
        public event EventHandler? AudioDeviceChanged;
        public event EventHandler? AudioFormatChanged;

        private bool _isAudioMonitorEnabled = false;
        private string _audioOutputDevice = "Steam Streaming Speakers";
        private SpatialAudioFormat _audioSpatialFormat = SpatialAudioFormat.DolbyAtmos;
        private bool _audioDeviceComboPopulating = false;

        private static readonly SolidColorBrush StreamingStartBrush    = new(Color.FromRgb(168, 213, 162));
        private static readonly SolidColorBrush StreamingStopBrush     = new(Color.FromRgb(244, 168, 168));
        private static readonly SolidColorBrush StreamingDisabledBrush = new(Color.FromRgb(180, 180, 180));
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        private List<MonitorInfo> _currentMonitors = new();
        private bool _hdrToggleBusy = false;
        private bool _autoHdrBusy = false;

        private List<ManagedApp> _managedApps = new();
        private string _managedAppsFilePath = string.Empty;

        public SettingsWindow()
        {
            InitializeComponent();

            this.Icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    @"Resources\streamtweak.ico"), UriKind.Absolute));

            string appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamTweak");
            Directory.CreateDirectory(appFolder);
            configFilePath = Path.Combine(appFolder, "config.json");
            _managedAppsFilePath = Path.Combine(appFolder, "managedapps.json");

            this.SourceInitialized += (_, _) =>
            {
                UpdateTitleBarTheme();
                var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                hwndSource?.AddHook(WndProc);
            };

            ApplySystemAccentColor();
            LoadConfig();
            LoadManagedApps();
            LoadNetworkAdapters();
            RefreshSessionHistory();
            RefreshStreamingAppInfo();
            LoadAudioDevicesAsync();
            PopulateAboutInfo();
            _ = CheckForUpdatesAsync();
        }

        // ─── Public sync methods called from App.xaml.cs ───────────────────

        public void SyncStreamingState(bool streamingActive, string originalSpeedKey)
        {
            isStreamingMode = streamingActive;
            originalSpeed = originalSpeedKey;
            UpdateStreamingButtonAppearance();
        }

        public void SyncAutoStreamingState(bool enabled)
        {
            isAutoStreamingEnabled = enabled;
            AutoStreamingToggle.IsChecked = enabled;
        }

        public void RefreshCurrentSpeedDisplay()
        {
            if (!string.IsNullOrEmpty(currentAdapterName))
                UpdateCurrentSpeedDisplay(currentAdapterName);
        }

        public void RefreshSessionHistory()
        {
            var sessions = SessionLogger.Load();
            if (sessions.Count == 0)
            {
                SessionHistoryList.ItemsSource = null;
                NoHistoryLabel.Visibility = Visibility.Visible;
            }
            else
            {
                NoHistoryLabel.Visibility = Visibility.Collapsed;
                SessionHistoryList.ItemsSource = sessions;
            }
        }

        // ─── Theme ──────────────────────────────────────────────────────────

        private void ApplySystemAccentColor()
        {
            var brand = Color.FromRgb(190, 84, 56);   // #BE5438
            var hover = Color.FromRgb(160, 54, 26);   // #A0361A

            var brush = new SolidColorBrush(brand);
            brush.Freeze();
            var hoverBrush = new SolidColorBrush(hover);
            hoverBrush.Freeze();

            this.Resources["AccentColor"]      = brush;
            this.Resources["AccentHoverColor"] = hoverBrush;
            Application.Current.Resources["AccentColor"]      = brush;
            Application.Current.Resources["AccentHoverColor"] = hoverBrush;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void HomeTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Visible;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Collapsed;
            HomeTabButton.Style      = (Style)this.Resources["TabButtonActive"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];
            PopulateAboutInfo();
            _ = CheckForUpdatesAsync();
            _ = RefreshHomeHdrAsync();
        }

        private void NetworkTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Visible;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Collapsed;
            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButtonActive"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];
        }

        private void AudioTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Visible;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Collapsed;
            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButtonActive"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];
            if (!string.IsNullOrEmpty(_audioOutputDevice))
                RefreshAudioCapabilitiesAsync(_audioOutputDevice);
        }

        private void LogsTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Visible;
            GlossaryPanel.Visibility = Visibility.Collapsed;
            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButtonActive"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];
            RefreshStreamingAppInfo();
        }

        private void GlossaryTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Visible;
            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButtonActive"];
        }


        private void ApplyDarkTheme()
        {
            this.Resources["WindowBackground"]        = new SolidColorBrush(Color.FromArgb(0x19, 255, 255, 255));
            this.Resources["SidebarBackground"]       = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            this.Resources["PanelBackground"]         = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
            this.Resources["TextForeground"]          = new SolidColorBrush(Colors.White);
            this.Resources["BorderColor"]             = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
            this.Resources["SecondaryTextForeground"] = new SolidColorBrush(Color.FromRgb(186, 186, 186));
            this.Resources["WarningForeground"]       = new SolidColorBrush(Color.FromRgb(255, 193, 7));
            Application.Current.Resources["PanelBackground"]  = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
            Application.Current.Resources["TextForeground"]   = new SolidColorBrush(Colors.White);
            Application.Current.Resources["BorderColor"]      = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
            UpdateTitleBarTheme();
        }

        // ─── Session indicator (called from App.xaml.cs) ────────────────────

        private System.Windows.Media.Animation.Storyboard? _homeSessionPulse;

        private static readonly SolidColorBrush PillOnBrush  = new(Color.FromRgb(0xBE, 0x54, 0x38));
        private static readonly SolidColorBrush PillOffBrush = new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        private bool _homeHdrEnabled;
        private bool _homeAutoHdrEnabled;

        private void SetPill(Border pill, TextBlock txt, bool on, string onLabel)
        {
            pill.Background = on ? PillOnBrush : PillOffBrush;
            txt.Text        = on ? onLabel : "Off";
        }

        public void SetSessionActive(bool active) => SetHomeSessionActive(active);

        public void SetHomeSessionActive(bool active)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (active)
                {
                    HomeSessionDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    HomeSessionText.Text = "Active";
                    if (_homeSessionPulse == null)
                    {
                        var anim = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = 1.0, To = 0.25,
                            Duration = TimeSpan.FromSeconds(0.85),
                            AutoReverse = true,
                            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                        };
                        System.Windows.Media.Animation.Storyboard.SetTarget(anim, HomeSessionDot);
                        System.Windows.Media.Animation.Storyboard.SetTargetProperty(
                            anim, new PropertyPath("Opacity"));
                        _homeSessionPulse = new System.Windows.Media.Animation.Storyboard();
                        _homeSessionPulse.Children.Add(anim);
                    }
                    _homeSessionPulse.Begin();
                }
                else
                {
                    _homeSessionPulse?.Stop();
                    HomeSessionDot.Fill = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
                    HomeSessionDot.Opacity = 1.0;
                    HomeSessionText.Text = "Inactive";
                }
            });
        }

        private void UpdateTitleBarTheme()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, new[] { 1 }, 4);
            }
            catch { }
        }

private void RefreshStreamingAppInfo()
        {
            try
            {
                var info = LogParser.FindStreamingAppInfo();
                if (info != null)
                {
                    StreamingAppFoundPanel.Visibility = Visibility.Visible;
                    StreamingAppNotFoundText.Visibility = Visibility.Collapsed;
                    StreamingAppNameText.Text = info.AppName;
                    StreamingAppLogPathText.Text = info.LogFolderPath ?? "Log folder not found";

                    HomeHostAppIcon.Source = ExtractExeIcon(info.ExePath);
                    HomeHostAppIcon.Visibility = HomeHostAppIcon.Source != null
                        ? Visibility.Visible : Visibility.Collapsed;
                    HomeAppNameText.Text = info.AppName;
                }
                else
                {
                    StreamingAppFoundPanel.Visibility = Visibility.Collapsed;
                    StreamingAppNotFoundText.Visibility = Visibility.Visible;

                    HomeHostAppIcon.Visibility = Visibility.Collapsed;
                    HomeAppNameText.Text = "Not detected";
                }
            }
            catch
            {
                StreamingAppFoundPanel.Visibility = Visibility.Collapsed;
                StreamingAppNotFoundText.Visibility = Visibility.Visible;

                HomeHostAppIcon.Visibility = Visibility.Collapsed;
                HomeAppNameText.Text = "Not detected";
            }
        }

        private static System.Windows.Media.ImageSource? ExtractExeIcon(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon == null) return null;
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { return null; }
        }

        private void StreamingAppLogPathText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string? path = StreamingAppLogPathText.Text;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            SessionLogger.ClearAll();
            RefreshSessionHistory();
        }

        private void SessionRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SessionEntry entry)
            {
                if (entry.Grade == null) return;
                SessionDetailOverlay.DataContext = entry;
                SessionDetailOverlay.Visibility = Visibility.Visible;

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var dur  = new Duration(TimeSpan.FromMilliseconds(200));

                var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
                SessionDetailOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                var scaleIn = new DoubleAnimation(0.96, 1.0, dur) { EasingFunction = ease };
                OverlayScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
            }
        }

        private void CloseDetailOverlay_Click(object sender, RoutedEventArgs e)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur  = new Duration(TimeSpan.FromMilliseconds(150));

            var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            fadeOut.Completed += (_, _) => SessionDetailOverlay.Visibility = Visibility.Collapsed;
            SessionDetailOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void Chart_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not SparklineControl sc) return;
            if (SessionDetailOverlay.DataContext is not SessionEntry entry) return;

            string? tag = sc.Tag?.ToString();

            IReadOnlyList<float>? data = tag switch
            {
                "Rtt"     => entry.RttTimeSeries,
                "Drops"   => entry.DropsTimeSeries,
                "Bitrate" => entry.BitrateTimeSeries,
                "Decode"  => entry.DecodeTimeSeries,
                _         => null
            };
            if (data == null || data.Count < 2) return;

            Brush color = tag switch
            {
                "Rtt"     => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                "Drops"   => new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
                "Bitrate" => new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)),
                "Decode"  => new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)),
                _         => Brushes.White
            };
            string label = tag switch
            {
                "Rtt"     => "RTT ms",
                "Drops"   => "Frame Drops",
                "Bitrate" => "Bitrate Mbps",
                "Decode"  => "Decode ms",
                _         => tag ?? string.Empty
            };

            new ChartDetailWindow(data, label, color, entry) { Owner = this }.Show();
            e.Handled = true;
        }

        private void PopulateAboutInfo()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            AboutVersionText.Text = version != null
                ? $"Version {version.Major}.{version.Minor}.{version.Build}"
                : "Version 3.0.0";

            string location = System.Reflection.Assembly.GetExecutingAssembly().Location;
            AboutBuildDateText.Text = File.Exists(location)
                ? $"Build: {File.GetLastWriteTime(location):dd MMM yyyy}"
                : string.Empty;

            UpdateStatusText.Text = string.Empty;
            UpdateStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
            UpdateStatusText.Opacity = 0.7;
            UpdateStatusText.FontWeight = FontWeights.Normal;
            UpdateStatusText.Cursor = System.Windows.Input.Cursors.Arrow;
            _updateAvailable = false;
        }

        private async Task CheckForUpdatesAsync()
        {
            UpdateStatusText.Text = "Checking for updates…";

            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("StreamTweak-UpdateCheck");
                string json = await _httpClient.GetStringAsync(
                    "https://api.github.com/repos/FoggyBytes/StreamTweak/releases/latest");

                using var doc = JsonDocument.Parse(json);
                string? tagName = doc.RootElement.GetProperty("tag_name").GetString();
                if (string.IsNullOrEmpty(tagName))
                {
                    UpdateStatusText.Text = "Could not check for updates.";
                    return;
                }

                string latestStr = tagName.TrimStart('v');
                var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

                if (current != null && Version.TryParse(latestStr, out var latest))
                {
                    var currentNorm = new Version(current.Major, current.Minor, current.Build);
                    if (latest > currentNorm)
                    {
                        UpdateStatusText.Text = $"⬆ Update available: v{latestStr}";
                        UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                        UpdateStatusText.FontWeight = FontWeights.Bold;
                        UpdateStatusText.Opacity = 1.0;
                        UpdateStatusText.Cursor = System.Windows.Input.Cursors.Hand;
                        _updateAvailable = true;
                    }
                    else
                    {
                        UpdateStatusText.Text = "✓ You have the latest version";
                        UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        UpdateStatusText.Opacity = 1.0;
                    }
                }
                else
                {
                    UpdateStatusText.Text = "Could not check for updates.";
                }
            }
            catch
            {
                UpdateStatusText.Text = "Could not check for updates.";
            }
        }

        private void UpdateStatusText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_updateAvailable) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/FoggyBytes/StreamTweak/releases/latest",
                UseShellExecute = true
            });
        }

        private void DonateButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://paypal.me/foggypunk",
                UseShellExecute = true
            });
        }

        private void GplBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.gnu.org/licenses/gpl-3.0",
                UseShellExecute = true
            });
        }

        private void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/FoggyBytes/StreamTweak",
                UseShellExecute = true
            });
        }

        // ─── Config

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(configFilePath)) { ApplyDarkTheme(); AutoStreamingToggle.IsChecked = true; return; }

                using var doc = JsonDocument.Parse(File.ReadAllText(configFilePath));
                var root = doc.RootElement;

                ApplyDarkTheme();

                if (root.TryGetProperty("StreamingMode", out var streamingEl))
                    isStreamingMode = streamingEl.GetBoolean();

                if (root.TryGetProperty("OriginalSpeed", out var origEl))
                    originalSpeed = origEl.GetString() ?? string.Empty;

                if (root.TryGetProperty("AutoStreamingEnabled", out var autoEl))
                    isAutoStreamingEnabled = autoEl.GetBoolean();

                if (root.TryGetProperty("AudioMonitorEnabled", out var audioEl))
                    _isAudioMonitorEnabled = audioEl.GetBoolean();

                if (root.TryGetProperty("AudioOutputDevice", out var devEl))
                    _audioOutputDevice = devEl.GetString() ?? "Steam Streaming Speakers";

                if (root.TryGetProperty("AudioSpatialFormat", out var fmtEl))
                    _audioSpatialFormat = fmtEl.GetString() == "WindowsSonic"
                        ? SpatialAudioFormat.WindowsSonic
                        : SpatialAudioFormat.DolbyAtmos;

                AutoStreamingToggle.IsChecked = isAutoStreamingEnabled;
            }
            catch { ApplyDarkTheme(); }
        }

        private void SaveConfig(bool saveAdapter) =>
            PatchConfig(d =>
            {
                if (saveAdapter && AdapterComboBox.SelectedItem is string adapter)
                    d["NetworkAdapterName"] = adapter;
            });

        private void SaveStreamingStateToConfig(bool streamingMode, string originalSpeedKey) =>
            PatchConfig(d => { d["StreamingMode"] = streamingMode; d["OriginalSpeed"] = originalSpeedKey; });

        private void SaveAutoStreamingStateToConfig() =>
            PatchConfig(d => d["AutoStreamingEnabled"] = isAutoStreamingEnabled);

        private void PatchConfig(Action<Dictionary<string, object>> patch)
        {
            try
            {
                string json = File.Exists(configFilePath) ? File.ReadAllText(configFilePath) : "{}";
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json)
                           ?? new Dictionary<string, object>();
                patch(data);
                File.WriteAllText(configFilePath,
                    JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
}

        // ─── Adapters ───────────────────────────────────────────────────────

        private void LoadNetworkAdapters()
        {
            AdapterComboBox.Items.Clear();
            List<string> physicalAdapters = new();

            try
            {
                using var session = CimSession.Create(null);
                var instances = session.QueryInstances(@"root\StandardCimv2", "WQL",
                    "SELECT * FROM MSFT_NetAdapter WHERE ConnectorPresent = True AND Virtual = False");
                foreach (var inst in instances)
                {
                    string name = inst.CimInstanceProperties["Name"].Value?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(name)) physicalAdapters.Add(name);
                }
            }
            catch
            {
                physicalAdapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(a => a.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    .Select(a => a.Name).ToList();
            }

            var activeAdapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => physicalAdapters.Contains(a.Name) && a.OperationalStatus == OperationalStatus.Up)
                .Select(a => a.Name).ToList();

            foreach (var a in activeAdapters) AdapterComboBox.Items.Add(a);

            // Restore saved adapter selection
            string savedAdapter = string.Empty;
            try
            {
                if (File.Exists(configFilePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(configFilePath));
                    if (doc.RootElement.TryGetProperty("NetworkAdapterName", out var el))
                        savedAdapter = el.GetString() ?? string.Empty;
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(savedAdapter) && AdapterComboBox.Items.Contains(savedAdapter))
                AdapterComboBox.SelectedItem = savedAdapter;
            else if (AdapterComboBox.Items.Count > 0)
                AdapterComboBox.SelectedIndex = 0;
            else
            {
                AdapterComboBox.Items.Add("No active physical adapters found");
                AdapterComboBox.SelectedIndex = 0;
                AdapterComboBox.IsEnabled = false;
                SpeedComboBox.IsEnabled = false;
                CurrentSpeedTextBlock.Text = "N/A";
            }

            UpdateStreamingButtonAppearance();
        }

        private void AdapterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = AdapterComboBox.SelectedItem?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(selected) && selected != "No active physical adapters found")
            {
                currentAdapterName = selected;
                LoadAdapterSpeeds(selected);
                UpdateCurrentSpeedDisplay(selected);
                UpdateStreamingButtonAppearance();
            }
        }

        private void UpdateCurrentSpeedDisplay(string adapterName)
        {
            try
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(a => a.Name == adapterName && a.OperationalStatus == OperationalStatus.Up);
                if (adapter != null)
                {
                    long mbps = adapter.Speed / 1_000_000;
                    CurrentSpeedTextBlock.Text = mbps >= 1000 ? $"{mbps / 1000.0:0.##} Gbps" : $"{mbps} Mbps";
                }
                else
                    CurrentSpeedTextBlock.Text = "Disconnected";
            }
            catch { CurrentSpeedTextBlock.Text = "Unknown"; }
        }

        private void LoadAdapterSpeeds(string adapterName)
        {
            SpeedComboBox.Items.Clear();
            SpeedComboBox.IsEnabled = true;

            currentAdapterSpeeds = NetworkManager.GetSupportedSpeeds(adapterName);

            if (currentAdapterSpeeds != null && currentAdapterSpeeds.Count > 0)
            {
                foreach (var key in currentAdapterSpeeds.Keys)
                    if (key != null && !IsSpeedAtOrBelow100Mbps(key)) SpeedComboBox.Items.Add(key);

                // Select the item that matches the actual current link speed
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
                long currentMbps = ni?.Speed / 1_000_000 ?? 0;

                int selectedIndex = 0;
                for (int i = 0; i < SpeedComboBox.Items.Count; i++)
                {
                    string item = SpeedComboBox.Items[i]?.ToString()?.ToLower() ?? string.Empty;
                    bool match = currentMbps >= 2000
                        ? item.Contains("2.5") || item.Contains("2500")
                        : currentMbps >= 900 && currentMbps <= 1100
                            ? (item.Contains("1") && item.Contains("gbps") && item.Contains("full"))
                            : item.Contains(currentMbps.ToString());
                    if (match) { selectedIndex = i; break; }
                }
                SpeedComboBox.SelectedIndex = selectedIndex;
            }
            else
            {
                SpeedComboBox.Items.Add("Speed detection not supported");
                SpeedComboBox.SelectedIndex = 0;
                SpeedComboBox.IsEnabled = false;
            }
        }

        // ─── Streaming Mode ─────────────────────────────────────────────────

        private void UpdateStreamingButtonAppearance()
        {
            if (isStreamingMode)
            {
                StreamingModeButton.Content = "Stop Streaming Mode";
                StreamingModeButton.Background = StreamingStopBrush;
                StreamingModeButton.Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                StreamingModeButton.IsEnabled = true;
                StreamingModeButton.Opacity = 1.0;
            }
            else
            {
                bool alreadyAt1G = IsCurrentSpeedAlready1G();
                StreamingModeButton.Content = "Start Streaming Mode";
                StreamingModeButton.IsEnabled = !alreadyAt1G;
                StreamingModeButton.Opacity = alreadyAt1G ? 0.4 : 1.0;
                StreamingModeButton.Background = alreadyAt1G ? StreamingDisabledBrush : StreamingStartBrush;
                StreamingModeButton.Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32));
            }
        }

        private static bool IsSpeedAtOrBelow100Mbps(string displayName)
        {
            var lower = displayName.ToLower();
            if (lower.Contains("gbps")) return false;
            int idx = lower.IndexOf("mbps");
            if (idx > 0)
            {
                var parts = lower[..idx].Trim().Split(' ');
                if (parts.Length > 0 && int.TryParse(parts[^1], out int mbps))
                    return mbps <= 100;
            }
            return false;
        }

        private bool IsCurrentSpeedAlready1G()
        {
            string adapterName = AdapterComboBox.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(adapterName)) return false;
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(a => a.Name == adapterName && a.OperationalStatus == OperationalStatus.Up);
            if (adapter != null)
            {
                long mbps = adapter.Speed / 1_000_000;
                return mbps >= 900 && mbps <= 1100;
            }
            return false;
        }

        private void ApplySpeedInternal(string speedKey)
        {
            if (currentAdapterSpeeds == null || !currentAdapterSpeeds.TryGetValue(speedKey, out string? registryValue)) return;

            string selectedAdapter = AdapterComboBox.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(selectedAdapter)) return;

            bool ok = SpeedChanger.Apply(selectedAdapter, registryValue);
            if (!ok) SpeedChanger.ApplyWithUac(selectedAdapter, registryValue);

            SpeedApplied?.Invoke(this, EventArgs.Empty);
        }

        private async Task RefreshSpeedAfterChange(string adapterName)
        {
            CurrentSpeedTextBlock.Text = "Negotiating...";
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                var adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(a => a.Name == adapterName && a.OperationalStatus == OperationalStatus.Up);
                if (adapter != null)
                {
                    long mbps = adapter.Speed / 1_000_000;
                    CurrentSpeedTextBlock.Text = mbps >= 1000 ? $"{mbps / 1000.0} Gbps" : $"{mbps} Mbps";
                    UpdateStreamingButtonAppearance();
                    return;
                }
            }
            CurrentSpeedTextBlock.Text = "Unknown";
            UpdateStreamingButtonAppearance();
        }

        private async void StreamingModeButton_Click(object sender, RoutedEventArgs e)
        {
            string selectedAdapter = AdapterComboBox.SelectedItem?.ToString() ?? string.Empty;

            if (!isStreamingMode)
            {
                if (SpeedComboBox.SelectedItem != null)
                    originalSpeed = SpeedComboBox.SelectedItem.ToString() ?? string.Empty;

                string? oneGbpsKey = null;
                if (currentAdapterSpeeds != null)
                {
                    foreach (var kvp in currentAdapterSpeeds)
                    {
                        string kl = kvp.Key.ToLower();
                        if (((kl.Contains("1 gbps") || kl.Contains("1gbps") || kl.Contains("1000"))
                             && kl.Contains("full")) || kvp.Value == "6")
                        { oneGbpsKey = kvp.Key; break; }
                    }
                }

                if (oneGbpsKey == null)
                {
                    MessageBox.Show("1 Gbps Full Duplex not found for this adapter.",
                        "Streaming Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SpeedComboBox.SelectedItem = oneGbpsKey;
                isStreamingMode = true;
                UpdateStreamingButtonAppearance();
                SaveStreamingStateToConfig(true, originalSpeed);
                SessionLogger.StartSession("Manual", originalSpeed);
                StreamingModeChanged?.Invoke(this, EventArgs.Empty);
                ApplySpeedInternal(oneGbpsKey);

                if (!string.IsNullOrEmpty(selectedAdapter))
                    await RefreshSpeedAfterChange(selectedAdapter);

                RefreshSessionHistory();
            }
            else
            {
                if (!string.IsNullOrEmpty(originalSpeed) && SpeedComboBox.Items.Contains(originalSpeed))
                {
                    SpeedComboBox.SelectedItem = originalSpeed;
                    ApplySpeedInternal(originalSpeed);
                }

                SessionLogger.EndSession();
                isStreamingMode = false;
                UpdateStreamingButtonAppearance();
                SaveStreamingStateToConfig(false, string.Empty);
                StreamingModeChanged?.Invoke(this, EventArgs.Empty);

                if (!string.IsNullOrEmpty(selectedAdapter))
                    await RefreshSpeedAfterChange(selectedAdapter);

                RefreshSessionHistory();
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (AdapterComboBox.SelectedItem == null || SpeedComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select both a network adapter and a target speed.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedAdapter = AdapterComboBox.SelectedItem.ToString() ?? string.Empty;
            string selectedSpeedKey = SpeedComboBox.SelectedItem.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(selectedAdapter) || string.IsNullOrEmpty(selectedSpeedKey) ||
                selectedAdapter == "No active physical adapters found") return;

            SaveConfig(saveAdapter: true);
            ApplySpeedInternal(selectedSpeedKey);
            await RefreshSpeedAfterChange(selectedAdapter);
        }

        private void AutoStreamingToggle_Changed(object sender, RoutedEventArgs e)
        {
            isAutoStreamingEnabled = AutoStreamingToggle.IsChecked ?? true;
            SaveAutoStreamingStateToConfig();
            AutoStreamingEnabledChanged?.Invoke(this, EventArgs.Empty);
            RefreshHomePanelUI();
        }

        private void SpatialAudioToggle_Changed(object sender, RoutedEventArgs e)
        {
            _isAudioMonitorEnabled = SpatialAudioToggle.IsChecked ?? false;
            SaveAudioMonitorStateToConfig();
            AudioMonitorEnabledChanged?.Invoke(this, EventArgs.Empty);
            RefreshHomePanelUI();
        }

        private void AudioFormatRadio_Changed(object sender, RoutedEventArgs e)
        {
            _audioSpatialFormat = DolbyFormatRadio.IsChecked == true
                ? SpatialAudioFormat.DolbyAtmos
                : SpatialAudioFormat.WindowsSonic;
            SaveAudioFormatToConfig();
            AudioFormatChanged?.Invoke(this, EventArgs.Empty);
            RefreshHomePanelUI();
        }

        private void AudioDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioDeviceComboPopulating) return;
            if (AudioDeviceComboBox.SelectedItem is string selected)
            {
                _audioOutputDevice = selected;
                SaveAudioDeviceToConfig();
                AudioDeviceChanged?.Invoke(this, EventArgs.Empty);
                RefreshAudioCapabilitiesAsync(_audioOutputDevice);
            }
        }

        private void SaveAudioMonitorStateToConfig() =>
            PatchConfig(d => d["AudioMonitorEnabled"] = _isAudioMonitorEnabled);

        private void SaveAudioDeviceToConfig() =>
            PatchConfig(d => d["AudioOutputDevice"] = _audioOutputDevice);

        private void SaveAudioFormatToConfig() =>
            PatchConfig(d => d["AudioSpatialFormat"] =
                _audioSpatialFormat == SpatialAudioFormat.DolbyAtmos ? "DolbyAtmos" : "WindowsSonic");

        public void SyncAudioMonitorState(bool enabled)
        {
            _isAudioMonitorEnabled = enabled;
            SpatialAudioToggle.IsChecked = enabled;
        }

        public string SelectedAudioDevice => _audioOutputDevice;
        public SpatialAudioFormat SelectedAudioFormat => _audioSpatialFormat;

        public void SyncDolbyMonitorStatus(string status)
        {
            DolbyStatusText.Text = status;
            if (status.StartsWith("✓"))
            {
                DolbyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                DolbyStatusText.Opacity = 1.0;
            }
            else
            {
                DolbyStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
                DolbyStatusText.Opacity = 0.7;
            }
        }

        public void UpdateHomeNicSpeed(string speedText)
        {
            Dispatcher.InvokeAsync(() =>
            {
                HomeNicSpeedText.Text       = speedText;
                HomeNicSpeedPill.Background = speedText == "—" ? PillOffBrush : PillOnBrush;
            });
        }

        public async Task RefreshHomeHdrAsync()
        {
            try
            {
                var monitors = await HdrService.GetMonitorsAsync();
                bool autoHdr = await HdrService.GetAutoHdrAsync();
                _homeHdrEnabled     = monitors.Exists(m => m.HdrEnabled);
                _homeAutoHdrEnabled = autoHdr;
                RefreshHomePanelUI();
            }
            catch { }
        }

        public void RefreshHomePanel(bool hdrEnabled, bool autoHdrEnabled,
                                     bool spatialEnabled, SpatialAudioFormat format,
                                     bool gameLibSync)
        {
            _homeHdrEnabled     = hdrEnabled;
            _homeAutoHdrEnabled = autoHdrEnabled;
            RefreshHomePanelUI();
        }

        private void RefreshHomePanelUI()
        {
            SetPill(HomeAutoStreamPill, HomeAutoStreamText, isAutoStreamingEnabled,            "On");
            SetPill(HomeHdrPill,        HomeHdrText,        _homeHdrEnabled,                   "On");
            SetPill(HomeAutoHdrPill,    HomeAutoHdrText,    _homeAutoHdrEnabled,               "On");
            SetPill(HomeGameLibPill,    HomeGameLibText,    GameLibraryState.Current.SyncEnabled, "On");

            if (_isAudioMonitorEnabled)
            {
                string label = _audioSpatialFormat == SpatialAudioFormat.DolbyAtmos
                    ? "Dolby Atmos" : "Windows Sonic";
                SetPill(HomeSpatialPill, HomeSpatialText, true, label);
            }
            else
            {
                SetPill(HomeSpatialPill, HomeSpatialText, false, "Off");
            }

            RefreshStreamingAppInfo();
        }

        private async void LoadAudioDevicesAsync()
        {
            _audioDeviceComboPopulating = true;
            try
            {
                var devices = await DolbyAudioMonitor.GetAudioOutputDevicesAsync();

                AudioDeviceComboBox.Items.Clear();
                foreach (var d in devices)
                    AudioDeviceComboBox.Items.Add(d);

                // Pre-select saved device, fall back to Steam Streaming Speakers, then first item
                string toSelect = devices.Contains(_audioOutputDevice)
                    ? _audioOutputDevice
                    : devices.FirstOrDefault(d => d.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase))
                      ?? devices.FirstOrDefault()
                      ?? string.Empty;

                if (!string.IsNullOrEmpty(toSelect))
                {
                    AudioDeviceComboBox.SelectedItem = toSelect;
                    _audioOutputDevice = toSelect;
                }
            }
            finally
            {
                _audioDeviceComboPopulating = false;
            }

            // Apply saved format to radio buttons
            DolbyFormatRadio.IsChecked = _audioSpatialFormat == SpatialAudioFormat.DolbyAtmos;
            SonicFormatRadio.IsChecked = _audioSpatialFormat == SpatialAudioFormat.WindowsSonic;

            // Check capabilities for the selected device
            if (!string.IsNullOrEmpty(_audioOutputDevice))
                RefreshAudioCapabilitiesAsync(_audioOutputDevice);
        }

        private async void RefreshAudioCapabilitiesAsync(string deviceName)
        {
            DolbyAccessIcon.Foreground = System.Windows.Media.Brushes.Gray;
            DolbyAccessText.Text = "Checking Dolby Atmos for Headphones\u2026";
            SonicAccessIcon.Foreground = System.Windows.Media.Brushes.Gray;
            SonicAccessText.Text = "Checking Windows Sonic for Headphones\u2026";

            var (dolby, sonic) = await DolbyAudioMonitor.GetSpatialAudioCapabilitiesAsync(deviceName);

            if (dolby)
            {
                DolbyAccessIcon.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                DolbyAccessText.Text = "Dolby Atmos for Headphones: available";
            }
            else
            {
                DolbyAccessIcon.Foreground = new SolidColorBrush(Color.FromRgb(220, 70, 50));
                DolbyAccessText.Text = "Dolby Atmos for Headphones: unavailable";
            }

            if (sonic)
            {
                SonicAccessIcon.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                SonicAccessText.Text = "Windows Sonic for Headphones: available";
            }
            else
            {
                SonicAccessIcon.Foreground = new SolidColorBrush(Color.FromRgb(220, 70, 50));
                SonicAccessText.Text = "Windows Sonic for Headphones: not available";
            }
        }
        public void RefreshDisplayPanelIfVisible()
        {
            if (DisplayPanel.Visibility == Visibility.Visible)
                RefreshDisplayPanelAsync();
        }

        private void DisplayTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Visible;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Collapsed;

            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButtonActive"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];

            RefreshDisplayPanelAsync();
        }

        // ─── Refresh — called on tab open and after every toggle ─────────────────────

        private async void RefreshDisplayPanelAsync()
        {
            DisplayLoadingText.Visibility = Visibility.Visible;
            MonitorStackPanel.Visibility = Visibility.Collapsed;
            AutoHdrSection.Visibility = Visibility.Collapsed;

            try
            {
                _currentMonitors = await HdrService.GetMonitorsAsync();
                bool autoHdr = await HdrService.GetAutoHdrAsync();

                _homeHdrEnabled     = _currentMonitors.Exists(m => m.HdrEnabled);
                _homeAutoHdrEnabled = autoHdr;
                RefreshHomePanelUI();

                List<MonitorInfo> toShow = _currentMonitors;

                BuildMonitorCards(toShow);

                // AutoHDR toggle
                _autoHdrBusy = true;
                AutoHdrToggle.IsChecked = autoHdr;
                _autoHdrBusy = false;

                bool anyHdrOn = _currentMonitors.Exists(m => m.HdrEnabled);
                AutoHdrWarning.Visibility = (autoHdr && !anyHdrOn)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                DisplayLoadingText.Visibility = Visibility.Collapsed;
                MonitorStackPanel.Visibility = Visibility.Visible;
                AutoHdrSection.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                DisplayLoadingText.Text = $"Error reading display info: {ex.Message}";
                DisplayLoadingText.Visibility = Visibility.Visible;
            }
        }

        // ─── Build monitor cards dynamically ─────────────────────────────────────────

        private void BuildMonitorCards(List<MonitorInfo> monitors)
        {
            MonitorStackPanel.Children.Clear();

            if (monitors.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "No active displays found.",
                    FontSize = 12,
                    Opacity = 0.6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 8),
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
                MonitorStackPanel.Children.Add(empty);
                return;
            }

            foreach (var m in monitors)
            {
                // ── outer card border ──
                var card = new Border
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(12, 10, 12, 10),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                };
                card.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
                card.SetResourceReference(Border.BorderBrushProperty, "BorderColor");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // ── left: name + info ──
                var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                var nameText = new TextBlock
                {
                    Text = m.FriendlyName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
                nameRow.Children.Add(nameText);

                if (m.IsVirtual)
                {
                    var chip = new Border
                    {
                        Margin = new Thickness(8, 0, 0, 0),
                        Padding = new Thickness(5, 1, 5, 1),
                        CornerRadius = new CornerRadius(4),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    chip.SetResourceReference(Border.BackgroundProperty, "AccentColor");
                    chip.Child = new TextBlock
                    {
                        Text = "Virtual",
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = System.Windows.Media.Brushes.White,
                    };
                    nameRow.Children.Add(chip);
                }

                leftStack.Children.Add(nameRow);

                string resText = (m.Width > 0 && m.Height > 0)
                    ? $"{m.Width} × {m.Height}   {m.RefreshRateHz} Hz"
                    : "Resolution unknown";

                var infoText = new TextBlock
                {
                    Text = resText,
                    FontSize = 11,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 3, 0, 0),
                };
                infoText.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
                leftStack.Children.Add(infoText);

                // HDR supported/not label
                string hdrLabel = m.HdrSupported
                    ? (m.HdrEnabled ? "HDR  ON" : "HDR  OFF")
                    : "HDR not supported";

                var hdrStateText = new TextBlock
                {
                    Text = hdrLabel,
                    FontSize = 11,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                hdrStateText.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
                if (m.HdrEnabled)
                {
                    hdrStateText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    hdrStateText.Opacity = 1.0;
                }
                leftStack.Children.Add(hdrStateText);

                Grid.SetColumn(leftStack, 0);
                grid.Children.Add(leftStack);

                // ── right: HDR toggle ──
                var toggle = new CheckBox
                {
                    Style = (Style)this.Resources["ToggleSwitchStyle"],
                    IsChecked = m.HdrEnabled,
                    IsEnabled = m.HdrSupported,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = m,           // carry MonitorInfo
                };
                toggle.Checked += HdrToggle_Changed;
                toggle.Unchecked += HdrToggle_Changed;

                Grid.SetColumn(toggle, 1);
                grid.Children.Add(toggle);

                card.Child = grid;
                MonitorStackPanel.Children.Add(card);
            }

            }

        // ─── HDR toggle handler ───────────────────────────────────────────────────────

        private async void HdrToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_hdrToggleBusy) return;
            if (sender is not CheckBox cb || cb.Tag is not MonitorInfo m) return;

            bool enable = cb.IsChecked ?? false;
            cb.IsEnabled = false;

            try
            {
                await HdrService.SetHdrAsync(m.AdapterId, m.TargetId, enable);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Could not change HDR state:\n{ex.Message}",
                    "StreamTweak — HDR",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);

                _hdrToggleBusy = true;
                cb.IsChecked = !enable;
                _hdrToggleBusy = false;
                cb.IsEnabled = true;
                return;
            }

            // Immediately update the in-memory model — do not wait for Windows
            m.HdrEnabled = enable;
            BuildMonitorCards(_currentMonitors);
            _homeHdrEnabled = _currentMonitors.Exists(x => x.HdrEnabled);
            RefreshHomePanelUI();

            // Update the Auto HDR warning
            bool anyHdrOn = _currentMonitors.Exists(x => x.HdrEnabled);
            bool autoHdrOn = AutoHdrToggle.IsChecked ?? false;
            AutoHdrWarning.Visibility = (autoHdrOn && !anyHdrOn)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─── AutoHDR toggle handler ───────────────────────────────────────────────────

        private async void AutoHdrToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_autoHdrBusy) return;

            bool enable = AutoHdrToggle.IsChecked ?? false;

            try
            {
                await HdrService.SetAutoHdrAsync(enable);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Could not change Auto HDR state:\n{ex.Message}",
                    "StreamTweak — Auto HDR",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);

                _autoHdrBusy = true;
                AutoHdrToggle.IsChecked = !enable;
                _autoHdrBusy = false;
                return;
            }

            bool anyHdrOn = _currentMonitors.Exists(m => m.HdrEnabled);
            AutoHdrWarning.Visibility = (enable && !anyHdrOn)
                ? Visibility.Visible
                : Visibility.Collapsed;
            _homeAutoHdrEnabled = enable;
            RefreshHomePanelUI();
        }

        // ─── Refresh button ───────────────────────────────────────────────────────────

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_NCACTIVATE    = 0x0086;

            if (msg == WM_DISPLAYCHANGE && DisplayPanel.Visibility == Visibility.Visible)
                RefreshDisplayPanelAsync();

            if (msg == WM_NCACTIVATE)
            {
                handled = true;
                return new IntPtr(1);
            }

            return IntPtr.Zero;
        }

        private void DisplayRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshDisplayPanelAsync();
        }

        // ─── App Tab ─────────────────────────────────────────────────────────

        private void AppTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            AppPanel.Visibility      = Visibility.Visible;
            GameLibPanel.Visibility  = Visibility.Collapsed;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Collapsed;
            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            AppTabButton.Style       = (Style)this.Resources["TabButtonActive"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButton"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];
        }

        private void AddAppButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Select executable",
                Filter = "Executables (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            string path = dialog.FileName;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);

            if (_managedApps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;

            _managedApps.Add(new ManagedApp { Name = name, Path = path });
            SaveManagedApps();
            RefreshManagedAppsList();
        }

        private void RemoveAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (ManagedAppsList.SelectedItem is not ManagedApp selected) return;
            _managedApps.Remove(selected);
            SaveManagedApps();
            RefreshManagedAppsList();
        }

        private void KillAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (ManagedAppsList.SelectedItem is not ManagedApp selected) return;
            string exeName = System.IO.Path.GetFileName(selected.Path);
            try
            {
                bool found = ManagedAppController.KillAllByPath(selected.Path);
                if (!found)
                    MessageBox.Show(
                        $"No running process found matching \"{exeName}\".\n\n" +
                        $"The app may already be closed, or its process name may differ " +
                        $"from the executable filename.\n\n" +
                        $"Tip: open Task Manager → Details tab and check the exact " +
                        $"Image Name of the app while it is running.",
                        "End now — process not found",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not terminate \"{exeName}\":\n{ex.Message}",
                    "End now", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RestartAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (ManagedAppsList.SelectedItem is not ManagedApp selected) return;
            string exeName = System.IO.Path.GetFileName(selected.Path);
            try
            {
                bool found = ManagedAppController.KillAllByPath(selected.Path);
                if (!found)
                {
                    MessageBox.Show(
                        $"No running process found matching \"{exeName}\".\n\n" +
                        $"The app may already be closed.",
                        "Restart — process not found",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not terminate \"{exeName}\":\n{ex.Message}",
                    "Restart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await Task.Delay(1500);

            if (!File.Exists(selected.Path))
            {
                MessageBox.Show($"Executable not found:\n{selected.Path}",
                    "Restart", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = selected.Path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start \"{exeName}\":\n{ex.Message}",
                    "Restart", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadManagedApps()
        {
            try
            {
                if (!File.Exists(_managedAppsFilePath)) return;
                string json = File.ReadAllText(_managedAppsFilePath);
                _managedApps = JsonSerializer.Deserialize<List<ManagedApp>>(json) ?? new List<ManagedApp>();
            }
            catch
            {
                _managedApps = new List<ManagedApp>();
            }
            RefreshManagedAppsList();
        }

        private void SaveManagedApps()
        {
            try
            {
                File.WriteAllText(_managedAppsFilePath,
                    JsonSerializer.Serialize(_managedApps, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void AppAutoManage_Changed(object sender, RoutedEventArgs e)
        {
            SaveManagedApps();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static ImageSource? ExtractAppIcon(string path)
        {
            if (!File.Exists(path)) return null;
            var shfi = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), 0x100 /* SHGFI_ICON | SHGFI_LARGEICON */);
            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DestroyIcon(shfi.hIcon); }
        }

        private void RefreshManagedAppsList()
        {
            foreach (var app in _managedApps)
                app.Icon ??= ExtractAppIcon(app.Path);
            ManagedAppsList.ItemsSource = null;
            ManagedAppsList.ItemsSource = _managedApps;
        }

        // ─── Game Library Tab ─────────────────────────────────────────────────

        private void GameLibTabButton_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility     = Visibility.Collapsed;
            NetworkPanel.Visibility  = Visibility.Collapsed;
            DisplayPanel.Visibility  = Visibility.Collapsed;
            AudioPanel.Visibility    = Visibility.Collapsed;
            AppPanel.Visibility      = Visibility.Collapsed;
            GameLibPanel.Visibility  = Visibility.Visible;
            LogsPanel.Visibility     = Visibility.Collapsed;
            GlossaryPanel.Visibility = Visibility.Collapsed;
            HomeTabButton.Style      = (Style)this.Resources["TabButton"];
            NetworkTabButton.Style   = (Style)this.Resources["TabButton"];
            DisplayTabButton.Style   = (Style)this.Resources["TabButton"];
            AudioTabButton.Style     = (Style)this.Resources["TabButton"];
            AppTabButton.Style       = (Style)this.Resources["TabButton"];
            GameLibTabButton.Style   = (Style)this.Resources["TabButtonActive"];
            LogsTabButton.Style      = (Style)this.Resources["TabButton"];
            GlossaryTabButton.Style  = (Style)this.Resources["TabButton"];
            RefreshGameLibPanel();
        }

        private void RefreshGameLibPanel()
        {
            var state = GameLibraryState.Current;
            GameLibSyncEnabledToggle.IsChecked = state.SyncEnabled;

            if (state.LastSyncUtc.HasValue)
            {
                var local = state.LastSyncUtc.Value.ToLocalTime();
                GameLibStatusLabel.Text =
                    $"{state.Games.Count} games.\nLast sync: {local:dd/MM/yyyy HH:mm}.";
            }
            else
            {
                GameLibStatusLabel.Text = state.SyncEnabled
                    ? "Sync enabled — will run at next startup."
                    : "Not yet synced.";
            }

            RefreshGameLibList();
        }

        private void RefreshGameLibList()
        {
            GameLibList.ItemsSource = null;
            GameLibList.ItemsSource = GameLibraryState.Current.Games;
        }

        private void GameLibSyncEnabled_Changed(object sender, RoutedEventArgs e)
        {
            var state = GameLibraryState.Current;
            state.SyncEnabled = GameLibSyncEnabledToggle.IsChecked == true;
            state.Save();
            RefreshHomePanelUI();
        }

        private async void SyncNow_Click(object sender, RoutedEventArgs e)
        {
            SyncNowButton.IsEnabled  = false;
            ClearSyncButton.IsEnabled = false;
            try
            {
                GameLibStatusLabel.Text = "Scanning game libraries…";
                string status = await GameLibraryService.PerformSyncAsync();
                GameLibStatusLabel.Text = status;
                RefreshGameLibList();
            }
            finally
            {
                SyncNowButton.IsEnabled  = true;
                ClearSyncButton.IsEnabled = true;
            }
        }

        private async void ClearSync_Click(object sender, RoutedEventArgs e)
        {
            SyncNowButton.IsEnabled  = false;
            ClearSyncButton.IsEnabled = false;
            try
            {
                GameLibStatusLabel.Text = "Clearing managed apps from Sunshine…";
                string status = await GameLibraryService.ClearSyncAsync();
                GameLibStatusLabel.Text = status;
                RefreshGameLibList();
            }
            finally
            {
                SyncNowButton.IsEnabled  = true;
                ClearSyncButton.IsEnabled = true;
            }
        }

        private void GameLibEntry_EnabledChanged(object sender, RoutedEventArgs e)
        {
            GameLibraryState.Current.Save();
        }

        private async void AddGame_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select game executable",
                Filter = "Executables (*.exe)|*.exe",
            };
            if (dlg.ShowDialog(this) != true) return;

            AddGameButton.IsEnabled = false;
            try
            {
                GameLibStatusLabel.Text = "Adding game…";
                string status = await GameLibraryService.AddManualGameAsync(dlg.FileName);
                GameLibStatusLabel.Text = status;
                RefreshGameLibList();
            }
            finally
            {
                AddGameButton.IsEnabled = true;
            }
        }

        private async void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.DataContext is not GameLibraryEntry entry) return;

            btn.IsEnabled = false;
            try
            {
                GameLibStatusLabel.Text = $"Removing \"{entry.Name}\"…";
                string status = await GameLibraryService.RemoveGameAsync(entry);
                GameLibStatusLabel.Text = status;
                RefreshGameLibList();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }
    }

    public class ManagedApp
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool AutoManage { get; set; } = true;

        [JsonIgnore]
        public ImageSource? Icon { get; set; }
    }

    // ── Quality grade → Color converter ──────────────────────────────────────

    public sealed class QualityGradeColorConverter : System.Windows.Data.IValueConverter
    {
        private static readonly SolidColorBrush Green  = new(Color.FromRgb(76, 175, 80));   // #4CAF50
        private static readonly SolidColorBrush Orange = new(Color.FromRgb(255, 152, 0));   // #FF9800
        private static readonly SolidColorBrush Red    = new(Color.FromRgb(244, 67, 54));   // #F44336
        private static readonly SolidColorBrush Gray   = new(Color.FromRgb(96, 96, 96));    // #606060

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is QualityGrade g ? g switch
            {
                QualityGrade.High   => Green,
                QualityGrade.Medium => Orange,
                QualityGrade.Low    => Red,
                _                   => Gray
            } : Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    // ── Quality grade → Label string converters ───────────────────────────────

    // Full label — used in the session detail popup badge
    public sealed class QualityGradeLabelConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is QualityGrade g ? g switch
            {
                QualityGrade.High   => "Streaming quality — Excellent",
                QualityGrade.Medium => "Streaming quality — Good",
                QualityGrade.Low    => "Streaming quality — Poor",
                _                   => "—"
            } : "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    // Short label — used in the session list Quality column
    public sealed class QualityGradeShortLabelConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is QualityGrade g ? g switch
            {
                QualityGrade.High   => "Excellent",
                QualityGrade.Medium => "Good",
                QualityGrade.Low    => "Poor",
                _                   => "—"
            } : "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    // ── StatRow — lightweight stat label + value(s) row for the detail panel ──

    public sealed class StatRow : System.Windows.Controls.Panel
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatRow),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty ValProperty =
            DependencyProperty.Register(nameof(Val), typeof(string), typeof(StatRow),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty Val2Property =
            DependencyProperty.Register(nameof(Val2), typeof(string), typeof(StatRow),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsArrange));

        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Val   { get => (string)GetValue(ValProperty);   set => SetValue(ValProperty,   value); }
        public string Val2  { get => (string)GetValue(Val2Property);  set => SetValue(Val2Property,  value); }

        private readonly TextBlock _lbl  = new() { FontSize = 13, Width = 80, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _val  = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _val2 = new() { FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        public StatRow()
        {
            Margin = new Thickness(0, 1, 0, 1);
            _lbl.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextForeground");
            _val.SetResourceReference(TextBlock.ForegroundProperty, "TextForeground");
            _val2.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextForeground");

            _lbl.SetBinding(TextBlock.TextProperty,  new System.Windows.Data.Binding(nameof(Label)) { Source = this });
            _val.SetBinding(TextBlock.TextProperty,  new System.Windows.Data.Binding(nameof(Val))   { Source = this });
            _val2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Val2))  { Source = this });

            Children.Add(_lbl);
            Children.Add(_val);
            Children.Add(_val2);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _lbl.Measure(availableSize);
            _val.Measure(new Size(Math.Max(0, availableSize.Width - 70), availableSize.Height));
            _val2.Measure(availableSize);
            double h = Math.Max(_lbl.DesiredSize.Height, Math.Max(_val.DesiredSize.Height, _val2.DesiredSize.Height));
            return new Size(availableSize.Width, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double remaining = Math.Max(0, finalSize.Width - 70 - _val2.DesiredSize.Width - 6);
            _lbl.Arrange( new Rect(0,              0, 70,       finalSize.Height));
            _val.Arrange( new Rect(70,             0, remaining, finalSize.Height));
            _val2.Arrange(new Rect(70 + remaining, 0, _val2.DesiredSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}
