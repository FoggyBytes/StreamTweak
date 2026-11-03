using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    /// <summary>One tile of the bridge: an entertainment area, lit while it streams.</summary>
    public sealed class HueAreaRow
    {
        public string Name { get; init; } = "";
        public string LightsText { get; init; } = "";
        public string WhoText { get; init; } = "";
        public bool IsActive { get; init; }
        public double TileOpacity => IsActive ? 1.0 : 0.75;
        public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IdleVisibility => IsActive ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One entry of "Give way to": any device, or one of the approved clients.</summary>
    public sealed class HueGiveWayChoice
    {
        public string Label { get; init; } = "";
        public string? ClientId { get; init; }
        public override string ToString() => Label;
    }

    /// <summary>
    /// 10.1.0: Setup › Hue Sync — the host's side of StreamLight 8.4.0's Hue Sync tab, laid out as its
    /// variant B (Marcello, 03/11/2026): the bridge's areas as tiles, a sentence on what the host will
    /// do, the rules in one card. Plus what only the host does: pairing and forgetting the bridge.
    /// </summary>
    public sealed class HueSyncViewModel : ViewModelBase
    {
        private readonly HueSyncGate _gate = HueSyncGate.Instance;
        private DispatcherQueue? _dq;
        private DispatcherQueueTimer? _timer;
        private bool _loading;

        public ObservableCollection<HueAreaRow> Areas { get; } = new();
        public ObservableCollection<HueGiveWayChoice> GiveWayChoices { get; } = new();

        private string _bridgeTitle = "Hue bridge";
        public string BridgeTitle { get => _bridgeTitle; private set => SetProperty(ref _bridgeTitle, value); }

        private bool _isPaired;
        public bool IsPaired { get => _isPaired; private set { if (SetProperty(ref _isPaired, value)) { OnPropertyChanged(nameof(PairedVisibility)); OnPropertyChanged(nameof(UnpairedVisibility)); } } }
        public Visibility PairedVisibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnpairedVisibility => IsPaired ? Visibility.Collapsed : Visibility.Visible;

        private bool _isPairing;
        public bool IsPairing { get => _isPairing; private set { if (SetProperty(ref _isPairing, value)) OnPropertyChanged(nameof(CanPair)); } }
        public bool CanPair => !IsPairing;

        private string _pairText = "";
        public string PairText { get => _pairText; private set => SetProperty(ref _pairText, value); }

        private string _verdictTitle = "";
        public string VerdictTitle { get => _verdictTitle; private set => SetProperty(ref _verdictTitle, value); }

        private string _verdictHint = "";
        public string VerdictHint { get => _verdictHint; private set { if (SetProperty(ref _verdictHint, value)) OnPropertyChanged(nameof(VerdictHintVisibility)); } }
        public Visibility VerdictHintVisibility => string.IsNullOrEmpty(VerdictHint) ? Visibility.Collapsed : Visibility.Visible;

        private bool _hold;
        public bool Hold
        {
            get => _hold;
            set
            {
                if (!SetProperty(ref _hold, value) || _loading) return;
                _gate.SetHold(value);
                StartupWarning = value && HueSyncGate.HueSyncStartsWithWindows();
            }
        }

        private HueGiveWayChoice? _giveWay;
        public HueGiveWayChoice? GiveWay
        {
            get => _giveWay;
            set
            {
                if (!SetProperty(ref _giveWay, value) || _loading || value == null) return;
                _gate.SetGiveWay(value.ClientId == null ? "any" : "this", value.ClientId);
            }
        }

        // Shown only with the rule on: then StreamTweak is the one that starts Hue Sync, and Hue
        // Sync's own start with Windows would beat it to the lights (Marcello, 03/11/2026).
        private bool _startupWarning;
        public bool StartupWarning { get => _startupWarning; private set => SetProperty(ref _startupWarning, value); }

        private bool _hueSyncMissing;
        public bool HueSyncMissing { get => _hueSyncMissing; private set => SetProperty(ref _hueSyncMissing, value); }

        public void Start(DispatcherQueue dq)
        {
            _dq = dq;
            _gate.Changed += OnGateChanged;
            _timer = dq.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(5);
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
        }

        public void Stop()
        {
            _gate.Changed -= OnGateChanged;
            _timer?.Stop();
            _timer = null;
        }

        private void OnGateChanged() => _dq?.TryEnqueue(Refresh);

        public async void Pair() => await _gate.PairAsync();

        public void Forget() => _gate.Forget();

        private async void Refresh()
        {
            var reading = await _gate.ReadAsync();
            _loading = true;
            try
            {
                IsPaired   = _gate.Paired;
                IsPairing  = _gate.Pairing;
                BridgeTitle = IsPaired ? HueSyncGate.ModelName(_gate.BridgeModel) : "Hue bridge";
                PairText = IsPairing ? "Press the button on the bridge now…"
                         : _gate.PairResult switch
                         {
                             "timeout"   => "The button was not pressed in time. Try again.",
                             "no_bridge" => "No Hue bridge found on this network.",
                             "bad_cert"  => "That device did not identify itself as a Hue bridge.",
                             _ => "Press Pair, then the button on the bridge within a minute.",
                         };
                HueSyncMissing = HueSyncGate.HueSyncPath() == null;

                Areas.Clear();
                foreach (var a in reading?.Areas ?? Array.Empty<HueArea>())
                    Areas.Add(new HueAreaRow
                    {
                        Name = a.Name,
                        LightsText = a.Lights == 0 ? "No lights" : a.Lights == 1 ? "1 light" : $"{a.Lights} lights",
                        WhoText = a.Active ? Owner(a) : "Not syncing",
                        IsActive = a.Active,
                    });

                Hold = _gate.Hold;
                StartupWarning = Hold && HueSyncGate.HueSyncStartsWithWindows();
                RebuildGiveWay();
                UpdateVerdict(reading);
            }
            finally { _loading = false; }
        }

        private static string Owner(HueArea a) =>
            (a.OwnerApp.Length > 0 ? a.OwnerApp : "An app") + (a.OwnerDevice.Length > 0 ? " on " + a.OwnerDevice : "");

        private void RebuildGiveWay()
        {
            var auth = AppStateService.Instance.BridgeAuth;
            var choices = new[] { new HueGiveWayChoice { Label = "Any device" } }
                .Concat((auth?.GetClients() ?? Array.Empty<BridgeClient>())
                    .Where(c => c.Status == "approved")
                    .Select(c => new HueGiveWayChoice { Label = Strip(c.Name), ClientId = c.UniqueId }))
                .ToList();
            if (GiveWayChoices.Select(c => c.ClientId).SequenceEqual(choices.Select(c => c.ClientId)) == false)
            {
                GiveWayChoices.Clear();
                foreach (var c in choices) GiveWayChoices.Add(c);
            }
            string want = _gate.GiveWay == "this" ? _gate.GiveWayClient : "";
            GiveWay = GiveWayChoices.FirstOrDefault(c => (c.ClientId ?? "") == want) ?? GiveWayChoices[0];
        }

        private static string Strip(string name) =>
            name.StartsWith("StreamLight @ ", StringComparison.Ordinal) ? name["StreamLight @ ".Length..] : name;

        private void UpdateVerdict(HueReading? reading)
        {
            string host = Environment.MachineName;
            string state = _gate.HostState();
            if (!IsPaired) { VerdictTitle = "Pair the bridge to see who has the lights"; VerdictHint = ""; return; }
            if (reading == null || !reading.Ok) { VerdictTitle = "The bridge does not answer"; VerdictHint = "Hue Sync starts as usual while it cannot be read"; return; }
            if (state == "syncing") { VerdictTitle = $"Hue Sync is syncing on {host}"; VerdictHint = ""; return; }
            if (!_gate.Hold) { VerdictTitle = "Hue Sync starts as it always did"; VerdictHint = "Turn on Wait while the lights are in use to let other devices keep them"; return; }
            if (_gate.Holds(reading)) { VerdictTitle = $"{host} waits: the lights are in use"; VerdictHint = "Hue Sync starts here when they are free"; return; }
            VerdictTitle = reading.ActiveArea == null ? $"Free: Hue Sync on {host} can start" : $"{host} takes the lights when Hue Sync starts here";
            VerdictHint = "";
        }
    }
}
