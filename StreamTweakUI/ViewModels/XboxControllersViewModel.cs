using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Gaming.Input;

namespace StreamTweak.ViewModels
{
    /// <summary>One connected controller and its charge.</summary>
    public sealed class XboxPadRow
    {
        public string Name { get; init; } = "";
        /// <summary>0 Critical · 1 Low · 2 Medium · 3 Full · -1 not known.</summary>
        public int Level { get; init; } = -1;
        public bool Charging { get; init; }

        public string LevelText => Level switch { 3 => "Full", 2 => "Medium", 1 => "Low", 0 => "Critical", _ => "Unknown" };
        public Visibility ChargingVisibility => Charging ? Visibility.Visible : Visibility.Collapsed;

        // As in StreamLight 8.7.0: amber only at Critical (under 20 %), the rest in the text's colour.
        public Brush LevelBrush => Level == 0 ? Res("STWarning") : Res("STTextPrimary");
        public Brush Cell0 => Cell(0);
        public Brush Cell1 => Cell(1);
        public Brush Cell2 => Cell(2);
        public Brush Cell3 => Cell(3);

        private Brush Cell(int i) => i <= Level ? LevelBrush : new SolidColorBrush(ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    }

    /// <summary>
    /// 10.3.0: This PC › Xbox controllers — variant A of docs/mockups/pad (Marcello, 10/11/2026).
    /// The rules (turn the controllers off when this PC shuts down / sleeps) are the service's,
    /// in HKLM, because they must hold with nobody signed in: these mirror what it answered and a
    /// change goes back through the pipe, undone on screen if it was not saved.
    ///
    /// The charge comes from Windows.Gaming.Input, read-only and made for many readers at once, so
    /// it never touches the service or the driver's admin device. ⚠️ Over the Xbox Wireless Adapter
    /// a controller reports four levels, not a percentage (GIP status 0x8A on FOGGY = level 2 of 3,
    /// Play &amp; Charge kit); Windows turns them into mWh out of 1000 (700 for Medium, measured on
    /// 10/11/2026). So the page shows the level, read off the ratio with margins that hold whatever
    /// the other three values are.
    /// </summary>
    public sealed class XboxControllersViewModel : ViewModelBase
    {
        private DispatcherQueue? _dq;
        private DispatcherQueueTimer? _timer;
        private bool _busy;

        public ObservableCollection<XboxPadRow> Pads { get; } = new();

        private bool _offAtShutdown;
        public bool OffAtShutdown
        {
            get => _offAtShutdown;
            set
            {
                if (_offAtShutdown == value) return;
                _offAtShutdown = value;
                OnPropertyChanged();
                _ = SaveAsync("shutdown", value);
            }
        }

        private bool _offAtSleep;
        public bool OffAtSleep
        {
            get => _offAtSleep;
            set
            {
                if (_offAtSleep == value) return;
                _offAtSleep = value;
                OnPropertyChanged();
                _ = SaveAsync("sleep", value);
            }
        }

        private bool _serviceUp;
        public bool ServiceUp
        {
            get => _serviceUp;
            private set { if (SetProperty(ref _serviceUp, value)) OnPropertyChanged(nameof(CanTurnOffNow)); }
        }

        private bool _adapter;
        private int _connected;

        public bool CanTurnOffNow => _serviceUp && !_busy && _connected > 0;

        public Visibility PadsVisibility => Pads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EmptyVisibility => Pads.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        private string _emptyText = "Checking…";
        public string EmptyText { get => _emptyText; private set => SetProperty(ref _emptyText, value); }

        private string _lastText = "";
        public string LastText { get => _lastText; private set => SetProperty(ref _lastText, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        private bool _hasStatus;
        public bool HasStatus { get => _hasStatus; set => SetProperty(ref _hasStatus, value); }

        private bool _statusIsError;
        public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

        // ── Lifetime ─────────────────────────────────────────────────────────────

        public void Start(DispatcherQueue dq)
        {
            _dq = dq;
            RawGameController.RawGameControllerAdded += OnControllersChanged;
            RawGameController.RawGameControllerRemoved += OnControllersChanged;
            // The charge moves slowly and the adapter tells no one when it does: once every 30 s.
            _timer = dq.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(30);
            _timer.Tick += (_, _) => _ = RefreshAsync();
            _timer.Start();
            _ = RefreshAsync();
        }

        public void Stop()
        {
            RawGameController.RawGameControllerAdded -= OnControllersChanged;
            RawGameController.RawGameControllerRemoved -= OnControllersChanged;
            _timer?.Stop();
            _timer = null;
        }

        // Windows.Gaming.Input raises these on its own threads.
        private void OnControllersChanged(object? sender, RawGameController e) => _dq?.TryEnqueue(() => _ = RefreshAsync());

        // ── Reading ──────────────────────────────────────────────────────────────

        public async Task RefreshAsync()
        {
            string? json = await Task.Run(SpeedChanger.GetXboxControllerState);
            ReadState(json);
            ReadPads();
            EmptyText = !ServiceUp ? "The StreamTweak service is not answering."
                      : !_adapter ? "No Xbox Wireless Adapter on this PC."
                      : "No controller connected.";
            OnPropertyChanged(nameof(PadsVisibility));
            OnPropertyChanged(nameof(EmptyVisibility));
            OnPropertyChanged(nameof(CanTurnOffNow));
        }

        private void ReadState(string? json)
        {
            if (json == null) { ServiceUp = false; LastText = ""; _connected = 0; return; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                _offAtShutdown = root.GetProperty("offAtShutdown").GetBoolean();
                _offAtSleep = root.GetProperty("offAtSleep").GetBoolean();
                OnPropertyChanged(nameof(OffAtShutdown));
                OnPropertyChanged(nameof(OffAtSleep));
                _adapter = root.GetProperty("adapter").GetBoolean();
                _connected = root.GetProperty("connected").GetInt32();
                ServiceUp = true;

                string last = root.GetProperty("last").GetString() ?? "";
                if (last.Length > 0 && DateTime.TryParse(root.GetProperty("lastAt").GetString(), null,
                        DateTimeStyles.RoundtripKind, out DateTime at))
                {
                    int off = root.GetProperty("lastOff").GetInt32();
                    int failed = root.GetProperty("lastFailed").GetInt32();
                    string what = failed > 0 ? $"{off} turned off, {failed} failed"
                                : off == 0 ? "none connected"
                                : $"{off} turned off";
                    LastText = $"Last time: {last}, {at:g} — {what}";
                }
                else LastText = "";
            }
            catch
            {
                ServiceUp = false;
                LastText = "";
            }
        }

        private void ReadPads()
        {
            var found = ReadPadsFromWindows();
            Pads.Clear();
            for (int i = 0; i < found.Count; i++)
                Pads.Add(new XboxPadRow
                {
                    Name = PadName(i, found.Count),
                    Level = found[i].Level,
                    Charging = found[i].Charging,
                });
        }

        /// <summary>Windows names every one "Xbox One Game Controller": with two alike, a number.</summary>
        public static string PadName(int index, int count) => "Xbox Wireless Controller" + (count > 1 ? $" {index + 1}" : "");

        /// <summary>The wireless Microsoft controllers Windows sees, with their charge (0 Critical …
        /// 3 Full, -1 not known). A pad on a cable is left out (the cable powers it down with the PC),
        /// and so is a virtual one with no battery — such as the Xbox pad a streaming server makes for
        /// its client. Any thread; shared with Status's tile and the tray. Never throws.
        /// ⚠️ For a pad on Bluetooth Low Energy Windows.Gaming.Input's figure is wrong (100 of 1000 mWh
        /// for a pad Windows shows at 75 %, measured 11/11/2026): there the Bluetooth device's battery
        /// property is used, the one Settings shows. Levels as StreamLight 8.7.0: 85 / 50 / 20.</summary>
        public static List<(int Level, bool Charging)> ReadPadsFromWindows()
        {
            var found = new List<(int Level, bool Charging)>();
            try
            {
                var ble = BluetoothLePads();
                foreach (RawGameController c in RawGameController.RawGameControllers)
                {
                    if (c.HardwareVendorId != 0x045E || !c.IsWireless) continue;
                    var report = c.TryGetBatteryReport();
                    if (report == null || report.Status == Windows.System.Power.BatteryStatus.NotPresent) continue;
                    int percent = -1;
                    if (report.RemainingCapacityInMilliwattHours is int rem && report.FullChargeCapacityInMilliwattHours is int full && full > 0)
                        percent = Math.Clamp(rem * 100 / full, 0, 100);
                    int b = ble.FindIndex(p => p.Product == c.HardwareProductId);
                    if (b >= 0)
                    {
                        if (ble[b].Percent >= 0) percent = ble[b].Percent;
                        ble.RemoveAt(b);   // one per pad
                    }
                    int level = percent < 0 ? -1 : percent >= 85 ? 3 : percent >= 50 ? 2 : percent >= 20 ? 1 : 0;
                    bool charging = report.Status == Windows.System.Power.BatteryStatus.Charging;
                    if (report.Status == Windows.System.Power.BatteryStatus.Idle) level = 3;   // on a charger, full
                    found.Add((level, charging));
                }
            }
            catch
            {
                // Windows.Gaming.Input unavailable: no list, the rest works.
            }
            return found;
        }

        // ── Bluetooth LE pads: the battery Windows keeps for the Bluetooth device ──

        private static readonly Regex BleHidNode = new(
            @"^BTHLEDEVICE\\\{00001812-0000-1000-8000-00805F9B34FB\}_DEV_VID&02045E_PID&([0-9A-F]{4})_REV&[0-9A-F]{4}_([0-9A-F]{12})\\",
            RegexOptions.IgnoreCase);

        /// <summary>The Xbox pads connected over Bluetooth Low Energy (product id, battery % or -1).
        /// Connected = its HID-over-GATT node (service 0x1812) is present: the device node itself
        /// stays present while a paired pad is off.</summary>
        private static List<(ushort Product, int Percent)> BluetoothLePads()
        {
            var pads = new List<(ushort, int)>();
            var devices = PresentIds("BTHLE");
            foreach (string id in PresentIds("BTHLEDEVICE"))
            {
                var m = BleHidNode.Match(id);
                if (!m.Success) continue;
                ushort product = Convert.ToUInt16(m.Groups[1].Value, 16);
                string prefix = $"BTHLE\\DEV_{m.Groups[2].Value.ToUpperInvariant()}\\";
                int percent = -1;
                string? device = devices.FirstOrDefault(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (device != null && CM_Locate_DevNodeW(out uint inst, device, 0) == 0)
                {
                    var key = BluetoothBattery;
                    var buffer = new byte[4];
                    uint size = (uint)buffer.Length;
                    if (CM_Get_DevNode_PropertyW(inst, ref key, out uint type, buffer, ref size, 0) == 0
                        && type == DEVPROP_TYPE_BYTE && buffer[0] <= 100)
                        percent = buffer[0];
                }
                pads.Add((product, percent));
            }
            return pads;
        }

        private static List<string> PresentIds(string enumerator)
        {
            const uint flags = 0x00000001 | 0x00000100;   // FILTER_ENUMERATOR | FILTER_PRESENT
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, flags) != 0 || length == 0) return new();
                var buffer = new char[length];
                int cr = CM_Get_Device_ID_ListW(enumerator, buffer, length, flags);
                if (cr == 0x1A) continue;   // CR_BUFFER_SMALL: a device arrived meanwhile
                if (cr != 0) return new();
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            return new();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        // {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2 — a byte, 0–100.
        private static readonly DEVPROPKEY BluetoothBattery = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
        private const uint DEVPROP_TYPE_BYTE = 0x00000003;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

        // ── Actions ──────────────────────────────────────────────────────────────

        public async Task TurnOffNowAsync()
        {
            _busy = true;
            OnPropertyChanged(nameof(CanTurnOffNow));
            string? outcome = await Task.Run(SpeedChanger.TurnOffXboxControllersNow);
            _busy = false;
            if (outcome == null)
                ShowStatus("Could not turn the controllers off: the StreamTweak service did not answer.", true);
            else if (outcome.Contains("error") || outcome.StartsWith("failed"))
                ShowStatus($"Some controllers did not turn off ({outcome}). The service log has the details.", true);
            else
                ShowStatus(outcome == "no controller connected" ? "No controller connected." : "Controllers turned off.", false);
            await RefreshAsync();
        }

        private async Task SaveAsync(string which, bool on)
        {
            bool saved = await Task.Run(() => SpeedChanger.SetXboxControllerOption(which, on));
            if (saved) return;
            if (which == "shutdown") _offAtShutdown = !on; else _offAtSleep = !on;
            OnPropertyChanged(which == "shutdown" ? nameof(OffAtShutdown) : nameof(OffAtSleep));
            ShowStatus("The setting was not saved: the StreamTweak service did not answer.", true);
        }

        private void ShowStatus(string text, bool isError)
        {
            StatusText = text;
            StatusIsError = isError;
            HasStatus = true;
        }
    }
}
