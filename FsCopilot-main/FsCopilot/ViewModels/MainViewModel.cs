namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Connection;
using Network;
using ReactiveUI;
using Simulation;

public class MainViewModel : ReactiveObject, IDisposable
{
    private static readonly HashSet<string> FenixSidestickVars = new(StringComparer.OrdinalIgnoreCase)
    {
        "L:N_FC_SIDESTICK_CAPT_PITCH",
        "L:N_FC_SIDESTICK_CAPT_BANK",
        "L:N_FC_SIDESTICK_FO_PITCH",
        "L:N_FC_SIDESTICK_FO_BANK",
        // Keep the native Fenix sidestick priority pushbuttons local. Mirroring these
        // Lvars into the opposite cockpit reintroduces a control conflict during FO priority.
        "L:S_FC_CAPT_INST_DISCONNECT",
        "L:S_FC_FO_INST_DISCONNECT"
    };

    private readonly CompositeDisposable _d = new();
    private readonly MasterSwitch _masterSwitch;
    private readonly CrewSideState _crewSideState;
    private readonly LocalJoystickService _localJoystickService;
    private readonly FenixSidestickProfileState _fenixSidestickProfileState;
    private readonly FenixSidestickSessionState _fenixSidestickSessionState;
    private readonly FenixSidestickCoordinator _fenixSidestickCoordinator;

    private MainTab _selectedTab = MainTab.Session;
    private string _aircraft = string.Empty;
    private string _connectionCode = string.Empty;
    private bool _isBusy;
    private bool _connected;
    private bool _showTakeControl;
    private bool _newProfileAvailable;
    private string _fenixJoystickStatus = "No joystick selected.";
    private string _fenixJoystickPreview = "Live input preview unavailable.";
    private string _fenixTakeoverButtonStatus = "Takeover button preview unavailable.";
    private string _fenixRuntimeStatus = "Sidestick runtime status unavailable.";
    private double _fenixRollMarkerLeft = 103;
    private double _fenixRollMarkerTop = 103;
    private double _fenixPitchMarkerLeft = 103;
    private double _fenixPitchMarkerTop = 103;
    private ViewErrors _errors = ViewErrors.None;

    private string Aircraft
    {
        set
        {
            _aircraft = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
            this.RaisePropertyChanged(nameof(FenixAircraftLoaded));
            this.RaisePropertyChanged(nameof(FenixLoadedAircraftText));
            this.RaisePropertyChanged(nameof(FenixSidestickActivationText));
            this.RaisePropertyChanged(nameof(FenixOperationalStatusText));
            this.RaisePropertyChanged(nameof(FenixOperationalArmed));
            this.RaisePropertyChanged(nameof(FenixOperationalStandby));
            this.RaisePropertyChanged(nameof(FenixOperationalDisabled));
        }
    }

    private ViewErrors Errors
    {
        get => _errors;
        set
        {
            _errors = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool Connected
    {
        get => _connected;
        set => this.RaiseAndSetIfChanged(ref _connected, value);
    }

    public bool ShowTakeControl
    {
        get => _showTakeControl;
        set => this.RaiseAndSetIfChanged(ref _showTakeControl, value);
    }

    public string ConnectionCode
    {
        get => _connectionCode;
        set => this.RaiseAndSetIfChanged(ref _connectionCode, value);
    }

    public bool NewProfileAvailable
    {
        get => _newProfileAvailable;
        set => this.RaiseAndSetIfChanged(ref _newProfileAvailable, value);
    }

    public bool IsCaptainSide
    {
        get => _crewSideState.SelectedSide == CrewSide.Captain;
        set
        {
            if (!value) return;
            _crewSideState.SelectedSide = CrewSide.Captain;
            RaiseCrewSideChanged();
        }
    }

    public bool IsFirstOfficerSide
    {
        get => _crewSideState.SelectedSide == CrewSide.FirstOfficer;
        set
        {
            if (!value) return;
            _crewSideState.SelectedSide = CrewSide.FirstOfficer;
            RaiseCrewSideChanged();
        }
    }

    public string PeerId { get; init; }
    public string ClientName { get; init; }
    public bool IsSessionTabSelected => _selectedTab == MainTab.Session;
    public bool IsA320TabSelected => _selectedTab == MainTab.A320;
    public bool ShowSessionContent => IsSessionTabSelected;
    public bool ShowA320Content => IsA320TabSelected;
    public bool FenixAircraftLoaded => FenixAirbusSupport.IsSupportedAircraft(_aircraft);
    public string FenixLoadedAircraftText =>
        string.IsNullOrWhiteSpace(_aircraft)
            ? "Current aircraft: waiting for Microsoft Flight Simulator."
            : $"Current aircraft: {_aircraft}";
    public string FenixSidestickActivationText =>
        !FenixSidestickProfileEnabled
            ? $"A320 sidestick mode is disabled. It only arms when {FenixAirbusSupport.DisplayName} is loaded and both connected clients enable the module."
            : FenixAircraftLoaded
                ? $"A320 sidestick capture is ready locally for {FenixAirbusSupport.DisplayName}. It engages when both connected clients have the module enabled and the bridge ready."
                : $"A320 sidestick mode is enabled, but it stays idle until {FenixAirbusSupport.DisplayName} is loaded.";
    public string FenixOperationalStatusText =>
        !FenixSidestickProfileEnabled
            ? "Feature disabled. FS Copilot will not capture sidestick input."
            : FenixAircraftLoaded
                ? "Feature ready. FS Copilot takes over Airbus sidestick merge logic only after both connected clients are ready."
                : $"Feature standing by. FS Copilot leaves your normal controls alone until {FenixAirbusSupport.DisplayName} is loaded.";
    public bool FenixOperationalArmed => FenixSidestickProfileEnabled && FenixAircraftLoaded;
    public bool FenixOperationalStandby => FenixSidestickProfileEnabled && !FenixAircraftLoaded;
    public bool FenixOperationalDisabled => !FenixSidestickProfileEnabled;
    public bool FenixConsentOverlayVisible => !FenixSidestickProfileEnabled;
    public bool FenixFloatingConsentVisible => ShowA320Content && FenixConsentOverlayVisible;
    public double FenixSettingsOpacity => FenixConsentOverlayVisible ? 0.32 : 1.0;
    public bool FenixConsentAccepted => _fenixSidestickProfileState.ConsentAccepted;
    public bool FenixSetupChecklistVisible =>
        FenixSidestickProfileEnabled && !_fenixSidestickProfileState.SetupChecklistDismissed;
    public string FenixConsentTitle => _fenixSidestickProfileState.ConsentAccepted
        ? "A320 sidestick mode is currently disabled"
        : "Fenix A320 sidestick mode";
    public string FenixConsentBody => _fenixSidestickProfileState.ConsentAccepted
        ? "Re-enable the Airbus sidestick layer whenever you are back in a Fenix cockpit. Outside the Fenix A319 / A320 / A321 family, FS Copilot leaves your normal controls alone."
        : "This feature is experimental. It replaces the normal single-stream Fenix sidestick path with FS Copilot takeover, merging and priority logic. You can disable it again at any time from this tab.";
    public string FenixConsentButtonText => _fenixSidestickProfileState.ConsentAccepted
        ? "Enable A320 mode again"
        : "I agree, enable A320 mode";
    public string FenixExpectedEfbControlSide =>
        _crewSideState.SelectedSide == CrewSide.Captain ? "Left" : "Right";
    public string FenixSetupSeatText =>
        $"In the Fenix EFB on this client, open Sim Settings > Controls and set the control position to {FenixExpectedEfbControlSide}.";
    public string FenixSetupJoystickText =>
        "On first setup, choose the local joystick below. You can change the device, curve and takeover source at any time later.";
    public string FenixDynamicAuthorityText =>
        FenixDynamicAuthorityEditable
            ? "Optional latency reduction. When a pilot presses the sidestick priority button, FS Copilot can hand control ownership to that side instead of waiting for a full relay loop. The other client will follow this setting automatically."
            : "Controlled by the current FS Copilot control owner. This client mirrors the remote dynamic-authority setting automatically.";
    public string FenixSetupDismissText => "I checked the Fenix EFB side and my joystick selection.";
    public IReadOnlyList<Point> FenixRollCurvePoints => BuildCurvePolylinePoints(
        _fenixSidestickProfileState.RollDeadzone,
        _fenixSidestickProfileState.RollSensitivity,
        _fenixSidestickProfileState.RollExpo,
        _fenixSidestickProfileState.InvertRoll);
    public IReadOnlyList<Point> FenixPitchCurvePoints => BuildCurvePolylinePoints(
        _fenixSidestickProfileState.PitchDeadzone,
        _fenixSidestickProfileState.PitchSensitivity,
        _fenixSidestickProfileState.PitchExpo,
        _fenixSidestickProfileState.InvertPitch);

    public bool FenixSidestickProfileEnabled
    {
        get => _fenixSidestickProfileState.Enabled;
        set => _fenixSidestickProfileState.Enabled = value;
    }

    public double FenixPitchDeadzone
    {
        get => _fenixSidestickProfileState.PitchDeadzone;
        set => _fenixSidestickProfileState.PitchDeadzone = value;
    }

    public double FenixRollDeadzone
    {
        get => _fenixSidestickProfileState.RollDeadzone;
        set => _fenixSidestickProfileState.RollDeadzone = value;
    }

    public double FenixPitchSensitivity
    {
        get => _fenixSidestickProfileState.PitchSensitivity;
        set => _fenixSidestickProfileState.PitchSensitivity = value;
    }

    public double FenixRollSensitivity
    {
        get => _fenixSidestickProfileState.RollSensitivity;
        set => _fenixSidestickProfileState.RollSensitivity = value;
    }

    public double FenixPitchExpo
    {
        get => _fenixSidestickProfileState.PitchExpo;
        set => _fenixSidestickProfileState.PitchExpo = value;
    }

    public double FenixRollExpo
    {
        get => _fenixSidestickProfileState.RollExpo;
        set => _fenixSidestickProfileState.RollExpo = value;
    }

    public bool FenixInvertPitch
    {
        get => _fenixSidestickProfileState.InvertPitch;
        set => _fenixSidestickProfileState.InvertPitch = value;
    }

    public bool FenixInvertRoll
    {
        get => _fenixSidestickProfileState.InvertRoll;
        set => _fenixSidestickProfileState.InvertRoll = value;
    }

    public int FenixControllerIndex
    {
        get => _fenixSidestickProfileState.ControllerIndex;
        set
        {
            if (value < 0)
                return;

            _fenixSidestickProfileState.ControllerIndex = value;
            UpdateFenixJoystickStatus();
        }
    }

    public int FenixTakeoverButtonIndex
    {
        get => _fenixSidestickProfileState.TakeoverButtonIndex;
        set
        {
            _fenixSidestickProfileState.TakeoverButtonIndex = value;
            this.RaisePropertyChanged(nameof(FenixTakeoverButtonSelectionIndex));
            UpdateFenixJoystickStatus();
        }
    }

    public int FenixTakeoverButtonSelectionIndex
    {
        get => _fenixSidestickProfileState.TakeoverButtonIndex + 1;
        set
        {
            _fenixSidestickProfileState.TakeoverButtonIndex = Math.Clamp(value - 1, -1, 31);
            this.RaisePropertyChanged(nameof(FenixTakeoverButtonIndex));
            UpdateFenixJoystickStatus();
        }
    }

    public bool FenixDynamicAuthorityEnabled
    {
        get => _fenixSidestickCoordinator.GetEffectiveDynamicAuthorityEnabled();
        set
        {
            if (!FenixDynamicAuthorityEditable)
                return;

            _fenixSidestickProfileState.DynamicAuthorityEnabled = value;
        }
    }

    public bool FenixDynamicAuthorityEditable =>
        _fenixSidestickCoordinator.IsDynamicAuthorityEditable();

    public string FenixJoystickStatus
    {
        get => _fenixJoystickStatus;
        private set => this.RaiseAndSetIfChanged(ref _fenixJoystickStatus, value);
    }

    public string FenixJoystickPreview
    {
        get => _fenixJoystickPreview;
        private set => this.RaiseAndSetIfChanged(ref _fenixJoystickPreview, value);
    }

    public string FenixTakeoverButtonStatus
    {
        get => _fenixTakeoverButtonStatus;
        private set => this.RaiseAndSetIfChanged(ref _fenixTakeoverButtonStatus, value);
    }

    public string FenixRuntimeStatus
    {
        get => _fenixRuntimeStatus;
        private set => this.RaiseAndSetIfChanged(ref _fenixRuntimeStatus, value);
    }

    public double FenixRollMarkerLeft
    {
        get => _fenixRollMarkerLeft;
        private set => this.RaiseAndSetIfChanged(ref _fenixRollMarkerLeft, value);
    }

    public double FenixRollMarkerTop
    {
        get => _fenixRollMarkerTop;
        private set => this.RaiseAndSetIfChanged(ref _fenixRollMarkerTop, value);
    }

    public double FenixPitchMarkerLeft
    {
        get => _fenixPitchMarkerLeft;
        private set => this.RaiseAndSetIfChanged(ref _fenixPitchMarkerLeft, value);
    }

    public double FenixPitchMarkerTop
    {
        get => _fenixPitchMarkerTop;
        private set => this.RaiseAndSetIfChanged(ref _fenixPitchMarkerTop, value);
    }

    public ObservableCollection<JoystickDeviceOption> FenixJoystickDevices { get; } = [];
    public ObservableCollection<ButtonOption> FenixTakeoverButtons { get; } = [];

    public string Version => App.Version;
    public string ErrorMessage =>
        _errors.HasFlag(ViewErrors.Failed) ? "Failed to connect." :
        _errors.HasFlag(ViewErrors.NotRunning) ? "Microsoft Flight Simulator is not running!" :
        _errors.HasFlag(ViewErrors.NotLoadedBridge) ? "Bridge package is not loaded. Check your Community folder." :
        _errors.HasFlag(ViewErrors.BridgeMismatch) ? "Bridge version mismatch. Update Community package." :
        _errors.HasFlag(ViewErrors.NotSupported) ? $"No profile available for the {_aircraft}." :
        _errors.HasFlag(ViewErrors.Rejected) ? "Both sides must use the same FS Copilot version." :
        _errors.HasFlag(ViewErrors.Conflict) ? "Conflict detected with YourControls package." :
        string.Empty;

    public ObservableCollection<Connection> Connections { get; set; } = [];
    public ReactiveCommand<Unit, Unit> JoinCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveCommand { get; }
    public ReactiveCommand<Unit, Unit> TakeControlCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectSessionTabCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectA320TabCommand { get; }
    public ReactiveCommand<Unit, Unit> EnableFenixSidestickProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DisableFenixSidestickProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissFenixSetupChecklistCommand { get; }
    public ReactiveCommand<string, Unit> ResetFenixAxisSettingCommand { get; }

    public MainViewModel(string peerId,
        string name,
        INetwork net,
        SimClient sim,
        MasterSwitch masterSwitch,
        Coordinator coordinator,
        Updater updater,
        CrewSideState crewSideState,
        LocalJoystickService localJoystickService,
        FenixSidestickProfileState fenixSidestickProfileState,
        FenixSidestickCoordinator fenixSidestickCoordinator,
        FenixSidestickSessionState fenixSidestickSessionState)
    {
        _masterSwitch = masterSwitch;
        _crewSideState = crewSideState;
        _localJoystickService = localJoystickService;
        _fenixSidestickProfileState = fenixSidestickProfileState;
        _fenixSidestickCoordinator = fenixSidestickCoordinator;
        _fenixSidestickSessionState = fenixSidestickSessionState;
        ClientName = name;
        PeerId = peerId;

        FenixTakeoverButtons.Add(new ButtonOption(-1, "Automatic from local Fenix red button"));
        for (var index = 0; index < 32; index++)
            FenixTakeoverButtons.Add(new ButtonOption(index, $"Button {index + 1}"));

        sim.Aircraft
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(aircraft => Aircraft = aircraft)
            .DisposeWith(_d);

        var definitions = new BehaviorSubject<Definitions?>(null);
        sim.Aircraft
            .Select(FenixAirbusSupport.GetDefinitionProfileName)
            .Select(Definitions.Load)
            .Select(FilterDefinitions)
            .Subscribe(defs => definitions.OnNext(defs))
            .DisposeWith(_d);

        crewSideState.WhenAnyValue(state => state.SelectedSide)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RaiseCrewSideChanged())
            .DisposeWith(_d);

        fenixSidestickProfileState.Changed
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RaiseFenixSidestickProfileChanged())
            .DisposeWith(_d);

        _fenixSidestickSessionState.Changed
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RaiseFenixDynamicAuthorityStateChanged())
            .DisposeWith(_d);

        localJoystickService.Devices
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateFenixJoystickDevices)
            .DisposeWith(_d);

        Observable.Interval(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateFenixJoystickPreview())
            .DisposeWith(_d);

        definitions.Subscribe(defs => Errors = defs is { Count: 0 }
                ? _errors | ViewErrors.NotSupported
                : _errors & ~ViewErrors.NotSupported).DisposeWith(_d);

        definitions
            .Where(defs => defs != null)
            .Subscribe(defs => coordinator.Load(defs!))
            .DisposeWith(_d);

        definitions
            .Where(defs => defs != null)
            .SelectMany(defs => Observable.FromAsync(ct => updater.Check(defs!.Name, ct))
                .Select(updatedAt => updatedAt != null && updatedAt > defs!.UpdatedAt))
            .Subscribe(updateAvailable => NewProfileAvailable =  updateAvailable).DisposeWith(_d);

        sim.Connected
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(connected => !connected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notRunning => Errors = notRunning
                ? _errors | ViewErrors.NotRunning
                : _errors & ~ViewErrors.NotRunning)
            .DisposeWith(_d);

        sim.WasmReady
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(connected => !connected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notLoaded => Errors = notLoaded
                ? _errors | ViewErrors.NotLoadedBridge
                : _errors & ~ViewErrors.NotLoadedBridge)
            .DisposeWith(_d);

        sim.WasmVersionMismatch
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notLoaded => Errors = notLoaded
                ? _errors | ViewErrors.BridgeMismatch
                : _errors & ~ViewErrors.BridgeMismatch)
            .DisposeWith(_d);

        sim.Conflict
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(conflict => Errors = conflict
                ? _errors | ViewErrors.Conflict
                : _errors & ~ViewErrors.Conflict)
            .DisposeWith(_d);

        net.Peers
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(peers =>
            {
                var i = 0;
                Connections.Clear();
                foreach (var peer in peers) Connections.Add(new(
                    PeerId: peer.PeerId,
                    Name: string.IsNullOrWhiteSpace(peer.Name) ? "Unknown" : peer.Name,
                    Ping: peer.Ping,
                    IsDirect: peer.Transport == Peer.TransportKind.Direct,
                    HasSeparatorAfter: i++ < peers.Count - 1
                ));
                Connected = Connections.Any();
                RaiseFenixDynamicAuthorityStateChanged();
            })
            .DisposeWith(_d);

        net.Peers
            .Select(p => p.Count)
            .DistinctUntilChanged()
            .Scan(
                seed: (Prev: 0, Curr: 0),
                accumulator: (state, curr) => (Prev: state.Curr, Curr: curr))
            .Subscribe(x =>
            {
                if (x.Curr > x.Prev) UiSounds.Play("connect");
                else if (x.Curr < x.Prev) UiSounds.Play("disconnect");
            })
            .DisposeWith(_d);

        masterSwitch.Master
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(isMaster => !isMaster)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isSlave =>
            {
                ShowTakeControl = isSlave;
                RaiseFenixDynamicAuthorityStateChanged();
            })
            .DisposeWith(_d);

        SelectSessionTabCommand = ReactiveCommand.Create(() => SelectTab(MainTab.Session));
        SelectA320TabCommand = ReactiveCommand.Create(() => SelectTab(MainTab.A320));
        EnableFenixSidestickProfileCommand = ReactiveCommand.Create(() =>
        {
            var firstConsent = !_fenixSidestickProfileState.ConsentAccepted;
            _fenixSidestickProfileState.ConsentAccepted = true;
            if (firstConsent)
                _fenixSidestickProfileState.SetupChecklistDismissed = false;
            _fenixSidestickProfileState.Enabled = true;
            SelectTab(MainTab.A320);
        });
        DisableFenixSidestickProfileCommand = ReactiveCommand.Create(() =>
        {
            _fenixSidestickProfileState.Enabled = false;
        });
        DismissFenixSetupChecklistCommand = ReactiveCommand.Create(() =>
        {
            _fenixSidestickProfileState.SetupChecklistDismissed = true;
        });
        ResetFenixAxisSettingCommand = ReactiveCommand.Create<string>(ResetFenixAxisSetting);

        JoinCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsBusy) return;
            if (ConnectionCode.Length != 8) return;

            Errors &= ~ViewErrors.Failed;
            IsBusy = true;
            ConnectionResult result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                masterSwitch.Join();
                result = await net.Connect(ConnectionCode, cts.Token);
            }
            finally
            {
                IsBusy = false;;
            }

            if (result == ConnectionResult.Failed)
            {
                Errors |= ViewErrors.Failed;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Errors &= ~ViewErrors.Failed);
            }
            else if (result == ConnectionResult.Rejected)
            {
                Errors |= ViewErrors.Rejected;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Errors &= ~ViewErrors.Rejected);
            }
        });

        LeaveCommand = ReactiveCommand.Create(() =>
        {
            net.Disconnect();
            masterSwitch.TakeControl();
        });

        TakeControlCommand = ReactiveCommand.Create(masterSwitch.TakeControl);

        DownloadProfileCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            if (definitions.Value == null) return;
            var cfg = await updater.Download(definitions.Value.Name, ct);
            if (cfg == null) return;
            var defs = Definitions.Save(definitions.Value.Name, cfg);
            definitions.OnNext(defs);
        });
    }

    public void Dispose() => _d.Dispose();

    private void RaiseCrewSideChanged()
    {
        this.RaisePropertyChanged(nameof(IsCaptainSide));
        this.RaisePropertyChanged(nameof(IsFirstOfficerSide));
        this.RaisePropertyChanged(nameof(FenixExpectedEfbControlSide));
        this.RaisePropertyChanged(nameof(FenixSetupSeatText));
    }

    private void RaiseFenixSidestickProfileChanged()
    {
        this.RaisePropertyChanged(nameof(FenixSidestickProfileEnabled));
        this.RaisePropertyChanged(nameof(FenixConsentOverlayVisible));
        this.RaisePropertyChanged(nameof(FenixFloatingConsentVisible));
        this.RaisePropertyChanged(nameof(FenixSettingsOpacity));
        this.RaisePropertyChanged(nameof(FenixConsentAccepted));
        this.RaisePropertyChanged(nameof(FenixSetupChecklistVisible));
        this.RaisePropertyChanged(nameof(FenixConsentTitle));
        this.RaisePropertyChanged(nameof(FenixConsentBody));
        this.RaisePropertyChanged(nameof(FenixConsentButtonText));
        this.RaisePropertyChanged(nameof(FenixSidestickActivationText));
        this.RaisePropertyChanged(nameof(FenixOperationalStatusText));
        this.RaisePropertyChanged(nameof(FenixOperationalArmed));
        this.RaisePropertyChanged(nameof(FenixOperationalStandby));
        this.RaisePropertyChanged(nameof(FenixOperationalDisabled));
        this.RaisePropertyChanged(nameof(FenixRollCurvePoints));
        this.RaisePropertyChanged(nameof(FenixPitchCurvePoints));
        this.RaisePropertyChanged(nameof(FenixPitchDeadzone));
        this.RaisePropertyChanged(nameof(FenixRollDeadzone));
        this.RaisePropertyChanged(nameof(FenixPitchSensitivity));
        this.RaisePropertyChanged(nameof(FenixRollSensitivity));
        this.RaisePropertyChanged(nameof(FenixPitchExpo));
        this.RaisePropertyChanged(nameof(FenixRollExpo));
        this.RaisePropertyChanged(nameof(FenixInvertPitch));
        this.RaisePropertyChanged(nameof(FenixInvertRoll));
        this.RaisePropertyChanged(nameof(FenixControllerIndex));
        this.RaisePropertyChanged(nameof(FenixTakeoverButtonIndex));
        this.RaisePropertyChanged(nameof(FenixTakeoverButtonSelectionIndex));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEnabled));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEditable));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityText));
        UpdateFenixJoystickStatus();
        UpdateFenixJoystickPreview();
    }

    private void RaiseFenixDynamicAuthorityStateChanged()
    {
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEnabled));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityEditable));
        this.RaisePropertyChanged(nameof(FenixDynamicAuthorityText));
    }

    private void SelectTab(MainTab tab)
    {
        if (_selectedTab == tab)
            return;

        _selectedTab = tab;
        this.RaisePropertyChanged(nameof(IsSessionTabSelected));
        this.RaisePropertyChanged(nameof(IsA320TabSelected));
        this.RaisePropertyChanged(nameof(ShowSessionContent));
        this.RaisePropertyChanged(nameof(ShowA320Content));
        this.RaisePropertyChanged(nameof(FenixFloatingConsentVisible));
    }

    private void UpdateFenixJoystickDevices(IReadOnlyList<LocalJoystickService.JoystickDeviceInfo> devices)
    {
        var byIndex = devices.ToDictionary(device => device.Index, device => device.Name);

        for (var index = 0; index < LocalJoystickService.MaxSupportedJoysticks; index++)
        {
            var displayName = byIndex.TryGetValue(index, out var name)
                ? name
                : $"Joystick {index}: not detected";
            var option = new JoystickDeviceOption(index, displayName);

            if (index >= FenixJoystickDevices.Count)
            {
                FenixJoystickDevices.Add(option);
            }
            else if (FenixJoystickDevices[index] != option)
            {
                FenixJoystickDevices[index] = option;
            }
        }

        while (FenixJoystickDevices.Count > LocalJoystickService.MaxSupportedJoysticks)
            FenixJoystickDevices.RemoveAt(FenixJoystickDevices.Count - 1);

        UpdateFenixJoystickStatus();
    }

    private void UpdateFenixJoystickStatus()
    {
        var selected = FenixJoystickDevices.FirstOrDefault(device => device.Index == _fenixSidestickProfileState.ControllerIndex);
        FenixJoystickStatus = selected == null
            ? $"Selected joystick {FenixControllerIndex}."
            : $"Selected device: {selected.DisplayName}";
        FenixTakeoverButtonStatus = _fenixSidestickProfileState.TakeoverButtonIndex < 0
            ? "Takeover source: automatic from the local Fenix red button. FS Copilot reads the real hardware press, suppresses the continued native Fenix action, and keeps takeover logic local to this client."
            : $"Takeover source: local Button {_fenixSidestickProfileState.TakeoverButtonIndex + 1}.";
    }

    private void UpdateFenixJoystickPreview()
    {
        if (_localJoystickService.TryReadSample(_fenixSidestickProfileState.ControllerIndex, out var sample))
        {
            var pressedButtons = DescribePressedButtons(sample.Buttons);
            var takeoverPressed = IsConfiguredButtonPressed(sample.Buttons, _fenixSidestickProfileState.TakeoverButtonIndex);
            UpdateCurveMarkers(sample);
            FenixJoystickPreview =
                $"Live input preview: pitch {-sample.YAxis:0.00}, roll {sample.XAxis:0.00}, buttons {pressedButtons}.";
            FenixTakeoverButtonStatus = _fenixSidestickProfileState.TakeoverButtonIndex < 0
                ? "Takeover source: automatic from the local Fenix red button. FS Copilot reads the real hardware press, suppresses the continued native Fenix action, and keeps takeover logic local to this client."
                : $"Takeover source local Button {_fenixSidestickProfileState.TakeoverButtonIndex + 1}: {(takeoverPressed ? "pressed" : "released")}.";
        }
        else
        {
            ResetCurveMarkers();
            FenixJoystickPreview = $"No live input detected for joystick {_fenixSidestickProfileState.ControllerIndex}.";
            FenixTakeoverButtonStatus = _fenixSidestickProfileState.TakeoverButtonIndex < 0
                ? "Takeover source: automatic from the local Fenix red button. FS Copilot reads the real hardware press, suppresses the continued native Fenix action, and keeps takeover logic local to this client."
                : $"Takeover source local Button {_fenixSidestickProfileState.TakeoverButtonIndex + 1}: unavailable.";
        }

        var runtime = _fenixSidestickCoordinator.GetRuntimeSnapshot();
        FenixRuntimeStatus =
            $"Runtime: {(runtime.IsMaster ? "Master" : "Slave")}, seat {runtime.Seat}, session {(runtime.SessionActive ? "armed" : "waiting")}, dynamic {(runtime.DynamicAuthorityActive ? "on" : "off")}, bridge {runtime.BridgeControlState}, priority {runtime.PriorityState}, dual {(runtime.DualInputActive ? "on" : "off")}, " +
            $"CAPT PB {(runtime.CaptTakeoverPressed ? "on" : "off")}, FO PB {(runtime.FoTakeoverPressed ? "on" : "off")}, merged pitch {runtime.MergedPitch:0.00}, roll {runtime.MergedRoll:0.00}, " +
            $"lights Cg:{runtime.CaptGreen} Fg:{runtime.FoGreen} Ca:{runtime.CaptRedArrow} Fa:{runtime.FoRedArrow}.";
    }

    private static string DescribePressedButtons(int buttons)
    {
        if (buttons == 0)
            return "none";

        var pressed = new List<string>();
        for (var index = 0; index < 32; index++)
        {
            if (IsConfiguredButtonPressed(buttons, index))
                pressed.Add((index + 1).ToString());
        }

        return pressed.Count == 0 ? "none" : string.Join(", ", pressed);
    }

    private static bool IsConfiguredButtonPressed(int buttons, int buttonIndex) =>
        buttonIndex >= 0 && buttonIndex < 32 && (((uint)buttons >> buttonIndex) & 1u) != 0;

    private void UpdateCurveMarkers(LocalJoystickService.LocalJoystickSample sample)
    {
        UpdateCurveMarker(
            input: sample.XAxis,
            deadzone: _fenixSidestickProfileState.RollDeadzone,
            sensitivity: _fenixSidestickProfileState.RollSensitivity,
            expo: _fenixSidestickProfileState.RollExpo,
            invert: _fenixSidestickProfileState.InvertRoll,
            setLeft: value => FenixRollMarkerLeft = value,
            setTop: value => FenixRollMarkerTop = value);

        UpdateCurveMarker(
            input: -sample.YAxis,
            deadzone: _fenixSidestickProfileState.PitchDeadzone,
            sensitivity: _fenixSidestickProfileState.PitchSensitivity,
            expo: _fenixSidestickProfileState.PitchExpo,
            invert: _fenixSidestickProfileState.InvertPitch,
            setLeft: value => FenixPitchMarkerLeft = value,
            setTop: value => FenixPitchMarkerTop = value);
    }

    private void ResetCurveMarkers()
    {
        FenixRollMarkerLeft = 103;
        FenixRollMarkerTop = 103;
        FenixPitchMarkerLeft = 103;
        FenixPitchMarkerTop = 103;
    }

    private static void UpdateCurveMarker(
        double input,
        double deadzone,
        double sensitivity,
        double expo,
        bool invert,
        Action<double> setLeft,
        Action<double> setTop)
    {
        const double canvasSize = 220.0;
        const double markerSize = 14.0;

        var shapedOutput = ShapeCurveValue(input, deadzone, sensitivity, expo, invert);
        var x = ((Math.Clamp(input, -1.0, 1.0) + 1.0) * 0.5) * canvasSize;
        var y = canvasSize - (((shapedOutput + 1.0) * 0.5) * canvasSize);

        setLeft(Math.Clamp(x - (markerSize / 2.0), 0.0, canvasSize - markerSize));
        setTop(Math.Clamp(y - (markerSize / 2.0), 0.0, canvasSize - markerSize));
    }

    private static Definitions FilterDefinitions(Definitions definitions) =>
        definitions.Name.Equals(FenixAirbusSupport.SharedDefinitionName, StringComparison.OrdinalIgnoreCase)
            ? definitions.Filter(def => !FenixSidestickVars.Contains(def.Get))
            : definitions;

    private static IReadOnlyList<Point> BuildCurvePolylinePoints(double deadzone, double sensitivity, double expo, bool invert)
    {
        const int samples = 41;
        const double canvasSize = 220.0;
        var points = new List<Point>(samples);

        for (var sampleIndex = 0; sampleIndex < samples; sampleIndex++)
        {
            var input = -1.0 + (sampleIndex / (double)(samples - 1)) * 2.0;
            var output = ShapeCurveValue(input, deadzone, sensitivity, expo, invert);
            var x = ((input + 1.0) * 0.5) * canvasSize;
            var y = canvasSize - (((output + 1.0) * 0.5) * canvasSize);
            points.Add(new Point(x, y));
        }

        return points;
    }

    private void ResetFenixAxisSetting(string? setting)
    {
        switch (setting)
        {
            case "roll-sensitivity":
                FenixRollSensitivity = FenixSidestickProfileState.DefaultSensitivity;
                break;
            case "roll-curve":
                FenixRollExpo = FenixSidestickProfileState.DefaultExpo;
                break;
            case "roll-deadzone":
                FenixRollDeadzone = FenixSidestickProfileState.DefaultDeadzone;
                break;
            case "pitch-sensitivity":
                FenixPitchSensitivity = FenixSidestickProfileState.DefaultSensitivity;
                break;
            case "pitch-curve":
                FenixPitchExpo = FenixSidestickProfileState.DefaultExpo;
                break;
            case "pitch-deadzone":
                FenixPitchDeadzone = FenixSidestickProfileState.DefaultDeadzone;
                break;
        }
    }

    private static double ShapeCurveValue(double input, double deadzone, double sensitivity, double expo, bool invert)
    {
        var clampedInput = Math.Clamp(input, -1.0, 1.0);
        var magnitude = Math.Abs(clampedInput);
        var effectiveDeadzone = Math.Clamp(deadzone, 0.0, 0.95);
        if (magnitude <= effectiveDeadzone)
            return 0.0;

        var normalizedMagnitude = (magnitude - effectiveDeadzone) / Math.Max(1e-6, 1.0 - effectiveDeadzone);
        var curvedMagnitude = normalizedMagnitude * (1.0 - expo) + Math.Pow(normalizedMagnitude, 3.0) * expo;
        var shaped = Math.Sign(clampedInput) * Math.Clamp(curvedMagnitude * sensitivity, -1.0, 1.0);
        return invert ? -shaped : shaped;
    }

    [Flags]
    private enum ViewErrors : byte
    {
        None            = 0b_0000_0000,
        Failed          = 0b_0000_0001,
        NotRunning      = 0b_0000_0010,
        NotSupported    = 0b_0000_0100,
        Rejected        = 0b_0000_1000,
        Conflict        = 0b_0001_0000,
        NotLoadedBridge = 0b_0010_0000,
        BridgeMismatch  = 0b_0100_0000
    }

    public record Connection(string PeerId, string Name, int Ping, bool IsDirect, bool HasSeparatorAfter)
    {
        public int QualityLevel
        {
            get
            {
                if (Ping > 800) return 5;
                if (Ping > 400) return 4;
                if (Ping > 200) return 3;
                if (Ping > 100) return 2;
                return 1;
            }
        }
    }

    public sealed record JoystickDeviceOption(int Index, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public sealed record ButtonOption(int Index, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private enum MainTab : byte
    {
        Session = 0,
        A320 = 1
    }
}
