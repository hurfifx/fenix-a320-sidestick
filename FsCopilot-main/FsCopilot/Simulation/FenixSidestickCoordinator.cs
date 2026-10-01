namespace FsCopilot.Simulation;

using System.Diagnostics;
using System.Globalization;
using Connection;
using Network;
using ReactiveUI;

public sealed class FenixSidestickCoordinator : IDisposable
{
    private const double StickRange = 1000.0;
    private const double AxisRange = 16383.0;
    private const double AxisNeutralDeadzoneNorm = 0.05;
    private const double DualInputThresholdNorm = 0.08;
    private const int OutputFlashPeriodMs = 500;
    private static readonly TimeSpan InputSampleInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MasterOutputWatchdogInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan StickVisualWriteInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan DiagnosticSampleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DiagnosticRecentInputWindow = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan InputStaleTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TakeoverStaleTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LatchHoldTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan DualInputAudioRepeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InjectedAxisIgnoreWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SimTakeoverFallbackHold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AutoTakeoverInferWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan NativeTakeoverClickPassthrough = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan PriorityAudioDelay = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan PriorityAudioPulseHold = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DualInputAudioPulseHold = TimeSpan.FromMilliseconds(100);

    private readonly SimClient _sim;
    private readonly INetwork _net;
    private readonly MasterSwitch _masterSwitch;
    private readonly CrewSideState _crewSideState;
    private readonly BehaviorControlOverrideState _behaviorControlOverrideState;
    private readonly LocalJoystickService _localJoystickService;
    private readonly FenixSidestickProfileState _profileState;
    private readonly FenixSidestickSessionState _sessionState;
    private readonly CompositeDisposable _d = new();
    private readonly SerialDisposable _localStream = new();
    private readonly EventLoopScheduler _controlScheduler = new(CreateControlThread);
    private readonly EventLoopScheduler _networkSendScheduler = new(CreateNetworkSendThread);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly object _networkGate = new();

    private SidestickSideInput _captInput = SidestickSideInput.Empty(CrewSide.Captain);
    private SidestickSideInput _foInput = SidestickSideInput.Empty(CrewSide.FirstOfficer);

    private PriorityState _priorityState = PriorityState.Normal;
    private bool _prevCaptTakeoverPressed;
    private bool _prevFoTakeoverPressed;
    private bool _prevDualInputActive;
    private bool _connected;
    private bool _enabled;
    private bool _bridgeReady;
    private bool _bridgeVersionMismatch;
    private bool _captureActive;
    private bool _duplicateSeatWarningLogged;
    private uint _inputSequence;
    private uint _outputSequence;
    private uint _statusSequence;
    private uint _lastRemoteOutputSequence;
    private uint _lastRemoteStatusSequence;
    private int _masterOutputRequestQueued;
    private int _inputNetworkSendQueued;
    private int _outputNetworkSendQueued;
    private SidestickInputPacket _latestInputForNetwork;
    private SidestickOutputPacket _latestOutputForNetwork;
    private long _captPriorityStartMs;
    private long _foPriorityStartMs;
    private long _nextDualInputAudioMs;
    private long _priorityLeftAudioUntilMs;
    private long _priorityRightAudioUntilMs;
    private long _dualInputAudioUntilMs;
    private bool _priorityLeftAudioOutputActive;
    private bool _priorityRightAudioOutputActive;
    private bool _dualInputAudioOutputActive;
    private bool _captLocalHardwareTakeoverPressed;
    private bool _foLocalHardwareTakeoverPressed;
    private InjectedAxisState _lastInjectedAileron;
    private InjectedAxisState _lastInjectedElevator;
    private ObservedAxisState _lastObservedPitch;
    private ObservedAxisState _lastObservedRoll;
    private AppliedOutputState _lastAppliedOutput;
    private SimAxisState _lastSimAxis;
    private StickVisualState _lastStickVisuals;
    private StickVisualOutputState _lastWrittenStickVisuals;
    private int _lastBridgeControlState;
    private long _priorityLeftAudioScheduledMs;
    private long _priorityRightAudioScheduledMs;
    private long _captNativeTakeoverPassthroughUntilMs;
    private long _foNativeTakeoverPassthroughUntilMs;
    private long _lastStickVisualWriteMs = -1_000_000;
    private bool _forceStickVisualWrite = true;

    public FenixSidestickCoordinator(
        SimClient sim,
        INetwork net,
        MasterSwitch masterSwitch,
        CrewSideState crewSideState,
        BehaviorControlOverrideState behaviorControlOverrideState,
        LocalJoystickService localJoystickService,
        FenixSidestickProfileState profileState,
        FenixSidestickSessionState sessionState)
    {
        _sim = sim;
        _net = net;
        _masterSwitch = masterSwitch;
        _crewSideState = crewSideState;
        _behaviorControlOverrideState = behaviorControlOverrideState;
        _localJoystickService = localJoystickService;
        _profileState = profileState;
        _sessionState = sessionState;

        net.RegisterPacket<SidestickInputPacket, SidestickInputPacket.Codec>();
        net.RegisterPacket<SidestickOutputPacket, SidestickOutputPacket.Codec>();
        net.RegisterPacket<SidestickStatusPacket, SidestickStatusPacket.Codec>();

        var aircraftIsFenix = sim.Aircraft
            .Select(FenixAirbusSupport.IsSupportedAircraft)
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        var profileEnabled = profileState.WhenAnyValue(state => state.Enabled)
            .StartWith(profileState.Enabled)
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        var featureEnabled = Observable.CombineLatest(
                aircraftIsFenix,
                profileEnabled,
                (isFenix, isProfileEnabled) => isFenix && isProfileEnabled)
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        var dynamicAuthorityEnabled = profileState.WhenAnyValue(state => state.DynamicAuthorityEnabled)
            .StartWith(profileState.DynamicAuthorityEnabled)
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        var sessionConnected = net.Peers
            .Select(peers => peers.Count > 0)
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        var bridgeReady = sim.WasmReady
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        var bridgeVersionMismatch = sim.WasmVersionMismatch
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        _d.Add(_localStream);
        _d.Add(_controlScheduler);
        _d.Add(_networkSendScheduler);

        _d.Add(featureEnabled
            .Subscribe(enabled =>
            {
                _enabled = enabled;
                _sessionState.LocalReady = enabled;
                UpdateControlCaptureOverride();
                if (!enabled) ResetState(clearRemoteInputs: true);
            }));

        _d.Add(sessionConnected
            .Subscribe(connected =>
            {
                _connected = connected;
                if (!connected)
                    _sessionState.ResetRemote();
                UpdateControlCaptureOverride();
                if (!connected) ResetState(clearRemoteInputs: true);
            }));

        _d.Add(dynamicAuthorityEnabled
            .Subscribe(enabled => _sessionState.LocalDynamicAuthorityEnabled = enabled));

        _d.Add(bridgeReady
            .Subscribe(ready =>
            {
                _bridgeReady = ready;
                UpdateControlCaptureOverride();
            }));

        _d.Add(bridgeVersionMismatch
            .Subscribe(mismatch =>
            {
                _bridgeVersionMismatch = mismatch;
                UpdateControlCaptureOverride();
            }));

        _d.Add(Observable.CombineLatest(
                featureEnabled,
                sessionConnected,
                bridgeReady,
                bridgeVersionMismatch,
                crewSideState.WhenAnyValue(state => state.SelectedSide)
                    .StartWith(crewSideState.SelectedSide),
                profileState.WhenAnyValue(state => state.ControllerIndex)
                    .StartWith(profileState.ControllerIndex),
                profileState.WhenAnyValue(state => state.TakeoverButtonIndex)
                    .StartWith(profileState.TakeoverButtonIndex),
                (enabled, connected, ready, mismatch, side, controllerIndex, takeoverButtonIndex) =>
                    new LocalCaptureConfig(
                        Enabled: enabled,
                        Connected: connected,
                        BridgeReady: ready,
                        BridgeVersionMismatch: mismatch,
                        Side: side,
                        ControllerIndex: controllerIndex,
                        TakeoverButtonIndex: takeoverButtonIndex))
            .DistinctUntilChanged()
            .Subscribe(config =>
            {
                if (_captureActive != config.Active)
                {
                    _captureActive = config.Active;
                    Log.Information(
                        "[Sidestick] Client capture {State} Enabled={Enabled} Connected={Connected} BridgeReady={BridgeReady} BridgeMismatch={BridgeMismatch}",
                        config.Active ? "enabled" : "disabled",
                        config.Enabled,
                        config.Connected,
                        config.BridgeReady,
                        config.BridgeVersionMismatch);
                }

                ResetState(clearRemoteInputs: true);
                _duplicateSeatWarningLogged = false;
                _localStream.Disposable = Disposable.Empty;

                if (!config.Active)
                    return;

                _localStream.Disposable = CreateLocalInputStream(
                        config.Side,
                        config.ControllerIndex,
                        config.TakeoverButtonIndex)
                    .Subscribe(packet =>
                    {
                        UpdateSideInput(packet, isLocal: true);
                        TickMasterIfActive();
                        QueueLatestInputForNetwork(packet);
                    });
            }));

        _d.Add(net.Stream<SidestickInputPacket>()
            .Subscribe(packet =>
            {
                if (!_enabled || !_connected) return;
                if (packet.Side == _crewSideState.SelectedSide)
                {
                    if (!_duplicateSeatWarningLogged)
                    {
                        _duplicateSeatWarningLogged = true;
                        Log.Warning("[Sidestick] Ignoring remote {Side} input because both clients appear to use the same seat", packet.Side);
                    }
                    return;
                }

                UpdateSideInput(packet, isLocal: false);
                RequestMasterOutput();
            }));

        _d.Add(Observable.Interval(MasterOutputWatchdogInterval, _controlScheduler)
            .Where(_ => UsesCapturedControl() && _masterSwitch.IsMaster)
            .Subscribe(_ => TickMaster()));

        _d.Add(net.Stream<SidestickOutputPacket>()
            .Where(_ => _enabled && _connected && !_masterSwitch.IsMaster)
            .Subscribe(packet =>
            {
                if (packet.Sequence <= _lastRemoteOutputSequence)
                    return;

                _lastRemoteOutputSequence = packet.Sequence;
                ApplyOutput(packet);
            }));

        _d.Add(Observable.Interval(TimeSpan.FromMilliseconds(250), _controlScheduler)
            .StartWith(0L)
            .Where(_ => _connected && _masterSwitch.IsMaster)
            .Subscribe(_ => QueueStatusForNetwork(new SidestickStatusPacket(
                Sequence: unchecked(++_statusSequence),
                DynamicAuthorityEnabled: _profileState.DynamicAuthorityEnabled))));

        _d.Add(net.Stream<SidestickStatusPacket>()
            .Where(_ => _connected && !_masterSwitch.IsMaster)
            .Subscribe(packet =>
            {
                if (packet.Sequence <= _lastRemoteStatusSequence)
                    return;

                _lastRemoteStatusSequence = packet.Sequence;
                _sessionState.RemoteReady = true;
                _sessionState.RemoteDynamicAuthorityEnabled = packet.DynamicAuthorityEnabled;
            }));

        _d.Add(Observable.CombineLatest(
                _sim.Stream("A:ELEVATOR POSITION", "Position 16k")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                _sim.Stream("A:AILERON POSITION", "Position 16k")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                _sim.Stream("A:RUDDER POSITION", "Position 16k")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                (pitchRaw, rollRaw, rudderRaw) => new SimAxisState(
                    Pitch: Clamp1(pitchRaw / AxisRange),
                    Roll: Clamp1(rollRaw / AxisRange),
                    Rudder: Clamp1(rudderRaw / AxisRange),
                    LastUpdateMs: _clock.ElapsedMilliseconds))
            .Subscribe(state =>
            {
                lock (_gate)
                    _lastSimAxis = state;
            }));

        _d.Add(Observable.CombineLatest(
                _sim.Stream("L:N_FC_SIDESTICK_CAPT_PITCH", "Number")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                _sim.Stream("L:N_FC_SIDESTICK_CAPT_BANK", "Number")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                _sim.Stream("L:N_FC_SIDESTICK_FO_PITCH", "Number")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                _sim.Stream("L:N_FC_SIDESTICK_FO_BANK", "Number")
                    .Select(value => ToDouble(value))
                    .StartWith(0.0),
                (captPitch, captRoll, foPitch, foRoll) => new StickVisualState(
                    CaptPitch: Clamp1(captPitch / StickRange),
                    CaptRoll: Clamp1(captRoll / StickRange),
                    FoPitch: Clamp1(foPitch / StickRange),
                    FoRoll: Clamp1(foRoll / StickRange),
                    LastUpdateMs: _clock.ElapsedMilliseconds))
            .Subscribe(state =>
            {
                lock (_gate)
                    _lastStickVisuals = state;
            }));

        _d.Add(_sim.Stream("L:FSC_CONTROL", "Number")
            .Select(value => Convert.ToInt32(Math.Round(ToDouble(value), MidpointRounding.AwayFromZero)))
            .StartWith(0)
            .Subscribe(state =>
            {
                lock (_gate)
                    _lastBridgeControlState = state;
            }));

        _d.Add(Observable.Interval(DiagnosticSampleInterval, _controlScheduler)
            .Where(_ => _enabled && (_connected || UsesCapturedControl()))
            .Subscribe(_ => LogDiagnosticSnapshot()));
    }

    public void Dispose()
    {
        _behaviorControlOverrideState.UseCapturedControl = false;
        _d.Dispose();
        _localStream.Dispose();
    }

    private IObservable<SidestickInputPacket> CreateLocalInputStream(CrewSide side, int controllerIndex, int takeoverButtonIndex)
    {
        var prefix = side == CrewSide.Captain ? "CAPT" : "FO";
        var simTakeoverPressed = _sim.Stream($"L:S_FC_{prefix}_INST_DISCONNECT", "Number")
            .Select(value => Math.Abs(ToDouble(value)) > 0.5)
            .StartWith(false);
        var lastSimTakeoverPressed = false;
        var simTakeoverFallbackUntilMs = 0L;
        var autoTakeoverInferUntilMs = 0L;
        var lastLocalButtons = 0;
        var inferredTakeoverButtonIndex = -1;
        var lastEffectiveTakeoverPressed = false;
        var xAxisTrusted = false;
        var yAxisTrusted = false;
        var loggedRailGuard = false;

        return Observable.Interval(InputSampleInterval, _controlScheduler)
            .StartWith(0L)
            .CombineLatest(simTakeoverPressed, (_, simPressed) => simPressed)
            .Select(simPressed =>
            {
                var nowMs = _clock.ElapsedMilliseconds;
                double effectivePitch = 0;
                double effectiveRoll = 0;
                var localTakeoverPressed = false;
                var localSampleAvailable = false;
                var localButtonAvailable = takeoverButtonIndex >= 0;
                var localButtons = 0;

                if (_localJoystickService.TryReadSample(controllerIndex, out var sample))
                {
                    localSampleAvailable = true;
                    localButtons = sample.Buttons;

                    if (!sample.XAxisAtHardRail)
                        xAxisTrusted = true;
                    if (!sample.YAxisAtHardRail)
                        yAxisTrusted = true;

                    var rawPitch = yAxisTrusted || !sample.YAxisAtHardRail ? Clamp1(-sample.YAxis) : 0;
                    var rawRoll = xAxisTrusted || !sample.XAxisAtHardRail ? Clamp1(-sample.XAxis) : 0;
                    if ((!xAxisTrusted && sample.XAxisAtHardRail) || (!yAxisTrusted && sample.YAxisAtHardRail))
                    {
                        if (!loggedRailGuard)
                        {
                            Log.Warning(
                                "[Sidestick] Selected joystick {ControllerIndex} reports sidestick axes at a hard rail; holding untrusted axes neutral until they move through range",
                                controllerIndex);
                            loggedRailGuard = true;
                        }
                    }

                    effectivePitch = ApplyInputProfile(rawPitch, AxisKind.Elevator);
                    effectiveRoll = ApplyInputProfile(rawRoll, AxisKind.Aileron);
                    localTakeoverPressed = IsConfiguredButtonPressed(localButtons, takeoverButtonIndex);

                    lock (_gate)
                    {
                        UpdateObservedAxis(AxisKind.Elevator, new ObservedAxisState(
                            RawValue: sample.RawY,
                            NormalizedValue: effectivePitch,
                            IgnoredAsInjected: false,
                            LastUpdateMs: nowMs));
                        UpdateObservedAxis(AxisKind.Aileron, new ObservedAxisState(
                            RawValue: sample.RawX,
                            NormalizedValue: effectiveRoll,
                            IgnoredAsInjected: false,
                            LastUpdateMs: nowMs));

                        SetLocalHardwareTakeoverPressed(side, false);
                    }
                }

                if (simPressed && !lastSimTakeoverPressed)
                {
                    simTakeoverFallbackUntilMs = nowMs + (long)SimTakeoverFallbackHold.TotalMilliseconds;
                    autoTakeoverInferUntilMs = nowMs + (long)AutoTakeoverInferWindow.TotalMilliseconds;
                }
                else if (!simPressed && lastSimTakeoverPressed)
                {
                    simTakeoverFallbackUntilMs = nowMs;
                }

                lastSimTakeoverPressed = simPressed;

                if (!localButtonAvailable && localSampleAvailable)
                {
                    if (inferredTakeoverButtonIndex < 0
                        && nowMs < autoTakeoverInferUntilMs
                        && TryInferAutoTakeoverButton(
                            side,
                            lastLocalButtons,
                            localButtons,
                            ref inferredTakeoverButtonIndex))
                    {
                        // Intentionally left empty. The helper updates the inferred button and logs
                        // only when the mapping actually changes.
                    }

                    if (inferredTakeoverButtonIndex >= 0)
                        localTakeoverPressed = IsConfiguredButtonPressed(localButtons, inferredTakeoverButtonIndex);
                }

                lock (_gate)
                    SetLocalHardwareTakeoverPressed(side, localSampleAvailable && localTakeoverPressed);

                lastLocalButtons = localSampleAvailable ? localButtons : 0;

                // In automatic mode, the Fenix AP-disconnect Lvar provides the initial edge while
                // the inferred local hardware button keeps the hold stable. With an explicit local
                // mapping selected, trust that mapping directly.
                var effectiveTakeoverPressed = localButtonAvailable && localSampleAvailable
                    ? localTakeoverPressed
                    : localTakeoverPressed || nowMs < simTakeoverFallbackUntilMs;
                var effectiveTakeoverPressEdge = effectiveTakeoverPressed && !lastEffectiveTakeoverPressed;
                lastEffectiveTakeoverPressed = effectiveTakeoverPressed;

                if (effectiveTakeoverPressEdge)
                    MaybeRequestDynamicAuthority(side);

                return new SidestickInputPacket(
                    Side: side,
                    Sequence: unchecked(++_inputSequence),
                    Pitch: (float)effectivePitch,
                    Roll: (float)effectiveRoll,
                    TakeoverPressed: effectiveTakeoverPressed);
            });
    }

    private void TickMasterIfActive()
    {
        if (UsesCapturedControl() && _masterSwitch.IsMaster)
            TickMaster();
    }

    private void RequestMasterOutput()
    {
        if (!UsesCapturedControl() || !_masterSwitch.IsMaster)
            return;

        if (Interlocked.Exchange(ref _masterOutputRequestQueued, 1) != 0)
            return;

        _controlScheduler.Schedule(() =>
        {
            Interlocked.Exchange(ref _masterOutputRequestQueued, 0);
            TickMasterIfActive();
        });
    }

    private void TickMaster()
    {
        SidestickOutputPacket output;
        AxisPair merged;

        lock (_gate)
        {
            var nowMs = _clock.ElapsedMilliseconds;
            var capt = GetEffectiveInput(_captInput, nowMs);
            var fo = GetEffectiveInput(_foInput, nowMs);
            var previousPriorityState = _priorityState;
            var previousDualInputActive = _prevDualInputActive;

            var captPressEdge = capt.TakeoverPressed && !_prevCaptTakeoverPressed;
            var foPressEdge = fo.TakeoverPressed && !_prevFoTakeoverPressed;
            var captRelease = !capt.TakeoverPressed && _prevCaptTakeoverPressed;
            var foRelease = !fo.TakeoverPressed && _prevFoTakeoverPressed;

            TakePriorityTransitions(
                nowMs,
                capt,
                fo,
                captPressEdge,
                foPressEdge,
                captRelease,
                foRelease,
                out var priorityLeftAudioPulse,
                out var priorityRightAudioPulse);

            var captActive = IsStickActive(capt);
            var foActive = IsStickActive(fo);
            var dualInputActive = _priorityState == PriorityState.Normal && captActive && foActive;

            if ((_priorityState == PriorityState.CaptLatched && IsRemoteSeatStale(CrewSide.Captain, nowMs))
                || (_priorityState == PriorityState.FoLatched && IsRemoteSeatStale(CrewSide.FirstOfficer, nowMs)))
            {
                _priorityState = PriorityState.Normal;
            }

            merged = MergeAxes(_priorityState, capt, fo);

            var dualInputAudioPulse = false;
            if (dualInputActive)
            {
                if (!_prevDualInputActive || nowMs >= _nextDualInputAudioMs)
                {
                    dualInputAudioPulse = true;
                    _nextDualInputAudioMs = nowMs + (long)DualInputAudioRepeat.TotalMilliseconds;
                }
            }
            else
            {
                _nextDualInputAudioMs = 0;
            }

            var flashOn = dualInputActive && ((nowMs / OutputFlashPeriodMs) % 2 == 0);
            var captPriority = _priorityState is PriorityState.CaptHeld or PriorityState.CaptLatched;
            var foPriority = _priorityState is PriorityState.FoHeld or PriorityState.FoLatched;

            output = new SidestickOutputPacket(
                Sequence: unchecked(++_outputSequence),
                CaptPitch: (float)capt.Pitch,
                CaptRoll: (float)capt.Roll,
                FoPitch: (float)fo.Pitch,
                FoRoll: (float)fo.Roll,
                MergedPitch: (float)merged.Pitch,
                MergedRoll: (float)merged.Roll,
                CaptTakeoverPressed: capt.TakeoverPressed,
                FoTakeoverPressed: fo.TakeoverPressed,
                CaptGreen: dualInputActive ? flashOn : captPriority && foActive,
                FoGreen: dualInputActive ? flashOn : foPriority && captActive,
                CaptRedArrow: foPriority,
                FoRedArrow: captPriority,
                PriorityLeftAudioPulse: priorityLeftAudioPulse,
                PriorityRightAudioPulse: priorityRightAudioPulse,
                DualInputAudioPulse: dualInputAudioPulse);

            _prevCaptTakeoverPressed = capt.TakeoverPressed;
            _prevFoTakeoverPressed = fo.TakeoverPressed;
            _prevDualInputActive = dualInputActive;

            if (previousPriorityState != _priorityState || dualInputActive != previousDualInputActive)
            {
                Log.Information(
                    "[Sidestick] State={State} Dual={Dual} Capt=({CaptPitch:0.###},{CaptRoll:0.###},PB:{CaptPb}) Fo=({FoPitch:0.###},{FoRoll:0.###},PB:{FoPb}) Merged=({MergedPitch:0.###},{MergedRoll:0.###})",
                    _priorityState,
                    dualInputActive,
                    capt.Pitch,
                    capt.Roll,
                    capt.TakeoverPressed,
                    fo.Pitch,
                    fo.Roll,
                    fo.TakeoverPressed,
                    merged.Pitch,
                    merged.Roll);
            }
        }

        ApplyOutput(output);
        if (_connected)
            QueueLatestOutputForNetwork(output);
    }

    private void QueueLatestInputForNetwork(SidestickInputPacket packet)
    {
        if (!_enabled || !_connected)
            return;

        lock (_networkGate)
            _latestInputForNetwork = packet;

        if (Interlocked.Exchange(ref _inputNetworkSendQueued, 1) != 0)
            return;

        _networkSendScheduler.Schedule(() =>
        {
            Interlocked.Exchange(ref _inputNetworkSendQueued, 0);
            SidestickInputPacket latest;
            lock (_networkGate)
                latest = _latestInputForNetwork;

            if (_enabled && _connected)
                _net.SendAll(latest, unreliable: true);
        });
    }

    private void QueueLatestOutputForNetwork(SidestickOutputPacket packet)
    {
        if (!_enabled || !_connected)
            return;

        lock (_networkGate)
            _latestOutputForNetwork = packet;

        if (Interlocked.Exchange(ref _outputNetworkSendQueued, 1) != 0)
            return;

        _networkSendScheduler.Schedule(() =>
        {
            Interlocked.Exchange(ref _outputNetworkSendQueued, 0);
            SidestickOutputPacket latest;
            lock (_networkGate)
                latest = _latestOutputForNetwork;

            if (_enabled && _connected)
                _net.SendAll(latest, unreliable: true);
        });
    }

    private void QueueStatusForNetwork(SidestickStatusPacket packet)
    {
        _networkSendScheduler.Schedule(() =>
        {
            if (_connected && _masterSwitch.IsMaster)
                _net.SendAll(packet, unreliable: true);
        });
    }

    private void TakePriorityTransitions(
        long nowMs,
        SidestickSideInput capt,
        SidestickSideInput fo,
        bool captPressEdge,
        bool foPressEdge,
        bool captRelease,
        bool foRelease,
        out bool priorityLeftAudioPulse,
        out bool priorityRightAudioPulse)
    {
        var triggerPriorityLeftAudio = false;
        var triggerPriorityRightAudio = false;

        void TakeCaptainPriority()
        {
            _priorityState = PriorityState.CaptHeld;
            _captPriorityStartMs = nowMs;
            triggerPriorityLeftAudio = true;
        }

        void TakeFoPriority()
        {
            _priorityState = PriorityState.FoHeld;
            _foPriorityStartMs = nowMs;
            triggerPriorityRightAudio = true;
        }

        switch (_priorityState)
        {
            case PriorityState.Normal:
                if (captPressEdge && foPressEdge)
                {
                    if (fo.LastPressEdgeMs >= capt.LastPressEdgeMs) TakeFoPriority();
                    else TakeCaptainPriority();
                }
                else if (captPressEdge)
                {
                    TakeCaptainPriority();
                }
                else if (foPressEdge)
                {
                    TakeFoPriority();
                }
                break;

            case PriorityState.CaptHeld:
                if (foPressEdge)
                {
                    TakeFoPriority();
                }
                else if (capt.TakeoverPressed && nowMs - _captPriorityStartMs > (long)LatchHoldTimeout.TotalMilliseconds)
                {
                    _priorityState = PriorityState.CaptLatched;
                }
                else if (captRelease)
                {
                    _priorityState = PriorityState.Normal;
                }
                break;

            case PriorityState.FoHeld:
                if (captPressEdge)
                {
                    TakeCaptainPriority();
                }
                else if (fo.TakeoverPressed && nowMs - _foPriorityStartMs > (long)LatchHoldTimeout.TotalMilliseconds)
                {
                    _priorityState = PriorityState.FoLatched;
                }
                else if (foRelease)
                {
                    _priorityState = PriorityState.Normal;
                }
                break;

            case PriorityState.CaptLatched:
                if (captPressEdge || foPressEdge)
                {
                    _priorityState = PriorityState.Normal;
                    _captPriorityStartMs = 0;
                    _foPriorityStartMs = 0;
                }
                break;

            case PriorityState.FoLatched:
                if (captPressEdge || foPressEdge)
                {
                    _priorityState = PriorityState.Normal;
                    _captPriorityStartMs = 0;
                    _foPriorityStartMs = 0;
                }
                break;
        }

        priorityLeftAudioPulse = triggerPriorityLeftAudio;
        priorityRightAudioPulse = triggerPriorityRightAudio;
    }

    private void ApplyOutput(SidestickOutputPacket output)
    {
        var nowMs = _clock.ElapsedMilliseconds;
        double preservedRudder;
        AppliedOutputState previousApplied;
        var suppressLocalNativeTakeover = false;
        string? localTakeoverDisconnectVar = null;
        var captPressEdge = false;
        var foPressEdge = false;
        var captReleaseEdge = false;
        var foReleaseEdge = false;
        var useAutomaticTakeoverSource = false;
        var shouldWriteStickVisuals = false;
        lock (_gate)
        {
            previousApplied = _lastAppliedOutput;
            preservedRudder = _lastSimAxis.Rudder;
            captPressEdge = output.CaptTakeoverPressed && !previousApplied.CaptTakeoverPressed;
            foPressEdge = output.FoTakeoverPressed && !previousApplied.FoTakeoverPressed;
            captReleaseEdge = !output.CaptTakeoverPressed && previousApplied.CaptTakeoverPressed;
            foReleaseEdge = !output.FoTakeoverPressed && previousApplied.FoTakeoverPressed;
            useAutomaticTakeoverSource = _profileState.TakeoverButtonIndex < 0;
            if (useAutomaticTakeoverSource)
            {
                if (_crewSideState.SelectedSide == CrewSide.Captain && captPressEdge)
                    _captNativeTakeoverPassthroughUntilMs = nowMs + (long)NativeTakeoverClickPassthrough.TotalMilliseconds;
                else if (_crewSideState.SelectedSide == CrewSide.FirstOfficer && foPressEdge)
                    _foNativeTakeoverPassthroughUntilMs = nowMs + (long)NativeTakeoverClickPassthrough.TotalMilliseconds;

                if (_crewSideState.SelectedSide == CrewSide.Captain
                    && (_captLocalHardwareTakeoverPressed || captPressEdge || captReleaseEdge))
                {
                    suppressLocalNativeTakeover = nowMs >= _captNativeTakeoverPassthroughUntilMs;
                    localTakeoverDisconnectVar = "S_FC_CAPT_INST_DISCONNECT";
                }
                else if (_crewSideState.SelectedSide == CrewSide.FirstOfficer
                    && (_foLocalHardwareTakeoverPressed || foPressEdge || foReleaseEdge))
                {
                    suppressLocalNativeTakeover = nowMs >= _foNativeTakeoverPassthroughUntilMs;
                    localTakeoverDisconnectVar = "S_FC_FO_INST_DISCONNECT";
                }
            }

            var visualWriteDue = nowMs - _lastStickVisualWriteMs >= (long)StickVisualWriteInterval.TotalMilliseconds;
            shouldWriteStickVisuals = (_forceStickVisualWrite || HaveStickVisualsChanged(_lastWrittenStickVisuals, output))
                && visualWriteDue;
            if (shouldWriteStickVisuals)
            {
                _lastStickVisualWriteMs = nowMs;
                _forceStickVisualWrite = false;
                _lastWrittenStickVisuals = new StickVisualOutputState(
                    output.CaptPitch,
                    output.CaptRoll,
                    output.FoPitch,
                    output.FoRoll);
            }
        }

        var surfaces = new Surfaces
        {
            AilPos = ToAxisValue(-output.MergedRoll),
            ElevPos = ToAxisValue(-output.MergedPitch),
            // Preserve the live sim rudder axis so captured sidestick mode only owns
            // elevator/aileron and does not block pedals / nosewheel-linked yaw controls.
            RudPos = ToAxisValue(preservedRudder),
            SessionId = 1,
            TimeMs = unchecked((uint)_clock.ElapsedMilliseconds)
        };
        var injectedAileronRaw = -surfaces.AilPos;
        var injectedElevatorRaw = -surfaces.ElevPos;
        _sim.SetLatestHighPriority(surfaces);

        var useDelayedSyntheticPriorityAudio = useAutomaticTakeoverSource;
        if (output.PriorityLeftAudioPulse)
        {
            if (useDelayedSyntheticPriorityAudio)
                _priorityLeftAudioScheduledMs = nowMs + (long)PriorityAudioDelay.TotalMilliseconds;
            else
                _priorityLeftAudioScheduledMs = 0;
        }

        if (output.PriorityRightAudioPulse)
        {
            if (useDelayedSyntheticPriorityAudio)
                _priorityRightAudioScheduledMs = nowMs + (long)PriorityAudioDelay.TotalMilliseconds;
            else
                _priorityRightAudioScheduledMs = 0;
        }

        var triggerPriorityLeftAudioNow = false;
        if (_priorityLeftAudioScheduledMs != 0 && nowMs >= _priorityLeftAudioScheduledMs)
        {
            triggerPriorityLeftAudioNow = output.CaptTakeoverPressed || output.CaptGreen || output.FoRedArrow;
            _priorityLeftAudioScheduledMs = 0;
        }

        var triggerPriorityRightAudioNow = false;
        if (_priorityRightAudioScheduledMs != 0 && nowMs >= _priorityRightAudioScheduledMs)
        {
            triggerPriorityRightAudioNow = output.FoTakeoverPressed || output.FoGreen || output.CaptRedArrow;
            _priorityRightAudioScheduledMs = 0;
        }

        var priorityLeftAudioActive = UpdateAudioPulseWindow(
            ref _priorityLeftAudioUntilMs,
            !useDelayedSyntheticPriorityAudio
                ? output.PriorityLeftAudioPulse
                : triggerPriorityLeftAudioNow,
            nowMs,
            PriorityAudioPulseHold);
        var priorityRightAudioActive = UpdateAudioPulseWindow(
            ref _priorityRightAudioUntilMs,
            !useDelayedSyntheticPriorityAudio
                ? output.PriorityRightAudioPulse
                : triggerPriorityRightAudioNow,
            nowMs,
            PriorityAudioPulseHold);
        var dualInputAudioActive = UpdateAudioPulseWindow(
            ref _dualInputAudioUntilMs,
            output.DualInputAudioPulse,
            nowMs,
            DualInputAudioPulseHold);

        var shouldWriteAlerts = HaveAlertOutputsChanged(previousApplied, output);

        if (UsesCapturedControl())
        {
            lock (_gate)
            {
                _lastInjectedAileron = default;
                _lastInjectedElevator = default;
            }
        }
        else
        {
            RememberInjectedAxis(AxisKind.Aileron, injectedAileronRaw);
            RememberInjectedAxis(AxisKind.Elevator, injectedElevatorRaw);
        }
        lock (_gate)
        {
            _lastAppliedOutput = new AppliedOutputState(
                CaptPitch: output.CaptPitch,
                CaptRoll: output.CaptRoll,
                FoPitch: output.FoPitch,
                FoRoll: output.FoRoll,
                MergedPitch: output.MergedPitch,
                MergedRoll: output.MergedRoll,
                CaptTakeoverPressed: output.CaptTakeoverPressed,
                FoTakeoverPressed: output.FoTakeoverPressed,
                CaptGreen: output.CaptGreen,
                FoGreen: output.FoGreen,
                CaptRedArrow: output.CaptRedArrow,
                FoRedArrow: output.FoRedArrow,
                PriorityLeftAudioPulse: priorityLeftAudioActive,
                PriorityRightAudioPulse: priorityRightAudioActive,
                DualInputAudioPulse: dualInputAudioActive,
                InjectedElevatorRaw: injectedElevatorRaw,
                InjectedAileronRaw: injectedAileronRaw,
                LastUpdateMs: nowMs);
        }

        if (shouldWriteStickVisuals)
        {
            var sticksCmd = string.Create(
                CultureInfo.InvariantCulture,
                $"{-output.CaptPitch * StickRange:0.###} (>L:N_FC_SIDESTICK_CAPT_PITCH) " +
                $"{-output.CaptRoll * StickRange:0.###} (>L:N_FC_SIDESTICK_CAPT_BANK) " +
                $"{-output.FoPitch * StickRange:0.###} (>L:N_FC_SIDESTICK_FO_PITCH) " +
                $"{-output.FoRoll * StickRange:0.###} (>L:N_FC_SIDESTICK_FO_BANK)");
            _sim.Execute(sticksCmd);
        }

        if (shouldWriteAlerts)
        {
            var alertsCmd =
                $"{BoolToInt(output.CaptGreen)} (>L:I_FC_SIDESTICK_PRIORITY_CAPT) " +
                $"{BoolToInt(output.FoGreen)} (>L:I_FC_SIDESTICK_PRIORITY_FO) " +
                $"{BoolToInt(output.CaptRedArrow)} (>L:I_FC_SIDESTICK_PRIORITY_CAPT_ARROW) " +
                $"{BoolToInt(output.FoRedArrow)} (>L:I_FC_SIDESTICK_PRIORITY_FO_ARROW)";
            _sim.Execute(alertsCmd);
        }

        if (suppressLocalNativeTakeover && localTakeoverDisconnectVar != null)
            _sim.Execute($"0 (>L:{localTakeoverDisconnectVar})");
        UpdateAudioOutput("A320_Audio:Priority_Left", priorityLeftAudioActive, ref _priorityLeftAudioOutputActive);
        UpdateAudioOutput("A320_Audio:Priority_Right", priorityRightAudioActive, ref _priorityRightAudioOutputActive);
        UpdateAudioOutput("A320_Audio:Dual_Input", dualInputAudioActive, ref _dualInputAudioOutputActive);
    }

    private void UpdateControlCaptureOverride() =>
        _behaviorControlOverrideState.UseCapturedControl =
            _enabled && _bridgeReady && !_bridgeVersionMismatch;

    private void ResetState(bool clearRemoteInputs)
    {
        var shouldClearCockpitState = false;

        lock (_gate)
        {
            _priorityState = PriorityState.Normal;
            _prevCaptTakeoverPressed = false;
            _prevFoTakeoverPressed = false;
            _prevDualInputActive = false;
            _captPriorityStartMs = 0;
            _foPriorityStartMs = 0;
            _nextDualInputAudioMs = 0;
            _priorityLeftAudioUntilMs = 0;
            _priorityRightAudioUntilMs = 0;
            _dualInputAudioUntilMs = 0;
            _priorityLeftAudioScheduledMs = 0;
            _priorityRightAudioScheduledMs = 0;
            _captNativeTakeoverPassthroughUntilMs = 0;
            _foNativeTakeoverPassthroughUntilMs = 0;
            _lastRemoteOutputSequence = 0;
            _captLocalHardwareTakeoverPressed = false;
            _foLocalHardwareTakeoverPressed = false;
            _lastInjectedAileron = default;
            _lastInjectedElevator = default;
            _lastObservedPitch = default;
            _lastObservedRoll = default;
            _lastAppliedOutput = default;
            _lastSimAxis = default;
            _lastStickVisuals = default;
            _lastWrittenStickVisuals = default;
            _lastBridgeControlState = 0;
            _lastStickVisualWriteMs = -1_000_000;
            _forceStickVisualWrite = true;

            if (!clearRemoteInputs) return;

            _captInput = SidestickSideInput.Empty(CrewSide.Captain);
            _foInput = SidestickSideInput.Empty(CrewSide.FirstOfficer);
            shouldClearCockpitState = true;
        }

        if (!shouldClearCockpitState) return;

        ApplyOutput(new SidestickOutputPacket(
            Sequence: 0,
            CaptPitch: 0,
            CaptRoll: 0,
            FoPitch: 0,
            FoRoll: 0,
            MergedPitch: 0,
            MergedRoll: 0,
            CaptTakeoverPressed: false,
            FoTakeoverPressed: false,
            CaptGreen: false,
            FoGreen: false,
            CaptRedArrow: false,
            FoRedArrow: false,
            PriorityLeftAudioPulse: false,
            PriorityRightAudioPulse: false,
            DualInputAudioPulse: false));

        _priorityLeftAudioOutputActive = false;
        _priorityRightAudioOutputActive = false;
        _dualInputAudioOutputActive = false;
    }

    private void SetLocalHardwareTakeoverPressed(CrewSide side, bool pressed)
    {
        if (side == CrewSide.Captain)
            _captLocalHardwareTakeoverPressed = pressed;
        else
            _foLocalHardwareTakeoverPressed = pressed;
    }

    private void UpdateSideInput(SidestickInputPacket packet, bool isLocal)
    {
        lock (_gate)
        {
            ref var slot = ref packet.Side == CrewSide.Captain ? ref _captInput : ref _foInput;
            if (!isLocal && packet.Sequence <= slot.Sequence)
                return;

            var nowMs = _clock.ElapsedMilliseconds;
            var pressEdgeMs = !slot.TakeoverPressed && packet.TakeoverPressed ? nowMs : slot.LastPressEdgeMs;
            slot = new SidestickSideInput(
                Side: packet.Side,
                Pitch: Clamp1(packet.Pitch),
                Roll: Clamp1(packet.Roll),
                TakeoverPressed: packet.TakeoverPressed,
                Sequence: packet.Sequence,
                LastUpdateMs: nowMs,
                LastPressEdgeMs: pressEdgeMs);
        }
    }

    private SidestickSideInput GetEffectiveInput(SidestickSideInput input, long nowMs)
    {
        if (input.Side == _crewSideState.SelectedSide)
            return input;

        var ageMs = nowMs - input.LastUpdateMs;
        if (ageMs <= (long)InputStaleTimeout.TotalMilliseconds
            && ageMs <= (long)TakeoverStaleTimeout.TotalMilliseconds)
            return input;

        return input with
        {
            Pitch = ageMs > (long)InputStaleTimeout.TotalMilliseconds ? 0 : input.Pitch,
            Roll = ageMs > (long)InputStaleTimeout.TotalMilliseconds ? 0 : input.Roll,
            TakeoverPressed = ageMs > (long)TakeoverStaleTimeout.TotalMilliseconds && input.TakeoverPressed
                ? false
                : input.TakeoverPressed
        };
    }

    private bool IsRemoteSeatStale(CrewSide side, long nowMs)
    {
        if (side == _crewSideState.SelectedSide)
            return false;

        var input = side == CrewSide.Captain ? _captInput : _foInput;
        return nowMs - input.LastUpdateMs > (long)TakeoverStaleTimeout.TotalMilliseconds;
    }

    private static AxisPair MergeAxes(PriorityState state, SidestickSideInput capt, SidestickSideInput fo) =>
        state switch
        {
            PriorityState.CaptHeld or PriorityState.CaptLatched => new(Clamp1(capt.Pitch), Clamp1(capt.Roll)),
            PriorityState.FoHeld or PriorityState.FoLatched => new(Clamp1(fo.Pitch), Clamp1(fo.Roll)),
            _ => new(Clamp1(capt.Pitch + fo.Pitch), Clamp1(capt.Roll + fo.Roll))
        };

    private static bool IsStickActive(SidestickSideInput input) =>
        Math.Abs(input.Pitch) > DualInputThresholdNorm || Math.Abs(input.Roll) > DualInputThresholdNorm;

    private double ApplyInputProfile(double value, AxisKind axisKind)
    {
        if (!_profileState.Enabled)
            return ApplyNeutralDeadzone(value);

        var deadzone = axisKind == AxisKind.Elevator
            ? Math.Clamp(_profileState.PitchDeadzone, 0.0, 0.95)
            : Math.Clamp(_profileState.RollDeadzone, 0.0, 0.95);
        var sensitivity = axisKind == AxisKind.Elevator
            ? _profileState.PitchSensitivity
            : _profileState.RollSensitivity;
        var expo = axisKind == AxisKind.Elevator
            ? _profileState.PitchExpo
            : _profileState.RollExpo;
        var invert = axisKind == AxisKind.Elevator
            ? _profileState.InvertPitch
            : _profileState.InvertRoll;

        var magnitude = Math.Abs(Clamp1(value));
        if (magnitude <= deadzone)
            return 0;

        var normalizedMagnitude = (magnitude - deadzone) / Math.Max(1e-6, 1.0 - deadzone);
        var curvedMagnitude = normalizedMagnitude * (1.0 - expo) + Math.Pow(normalizedMagnitude, 3.0) * expo;
        var shapedValue = Math.Sign(value) * Clamp1(curvedMagnitude * sensitivity);

        if (invert)
            shapedValue = -shapedValue;

        return Clamp1(shapedValue);
    }

    private void RememberInjectedAxis(AxisKind axisKind, int rawValue)
    {
        lock (_gate)
        {
            var state = new InjectedAxisState(
                RawValue: rawValue,
                IgnoreUntilMs: _clock.ElapsedMilliseconds + (long)InjectedAxisIgnoreWindow.TotalMilliseconds);

            if (axisKind == AxisKind.Aileron)
                _lastInjectedAileron = state;
            else
                _lastInjectedElevator = state;
        }
    }

    private bool ShouldIgnoreInjectedAxis(AxisKind axisKind, int rawValue, long nowMs)
    {
        var state = axisKind == AxisKind.Aileron ? _lastInjectedAileron : _lastInjectedElevator;
        return nowMs <= state.IgnoreUntilMs && Math.Abs(state.RawValue - rawValue) <= 1;
    }

    private bool UsesCapturedControl() =>
        _enabled && _behaviorControlOverrideState.UseCapturedControl;

    public bool IsDynamicAuthorityEditable() =>
        !_connected || _masterSwitch.IsMaster;

    public bool GetEffectiveDynamicAuthorityEnabled()
    {
        if (!_connected || _masterSwitch.IsMaster)
            return _profileState.DynamicAuthorityEnabled;

        return _sessionState.RemoteReady
            ? _sessionState.RemoteDynamicAuthorityEnabled
            : _profileState.DynamicAuthorityEnabled;
    }

    private void MaybeRequestDynamicAuthority(CrewSide side)
    {
        if (!_connected || !GetEffectiveDynamicAuthorityEnabled() || _masterSwitch.IsMaster)
            return;

        Log.Information(
            "[Sidestick] Dynamic authority transfer requested by {Side} priority button",
            side);
        _masterSwitch.TakeControl();
    }

    private void UpdateObservedAxis(AxisKind axisKind, ObservedAxisState state)
    {
        if (axisKind == AxisKind.Aileron)
            _lastObservedRoll = state;
        else
            _lastObservedPitch = state;
    }

    private void LogDiagnosticSnapshot()
    {
        ObservedAxisState localPitch;
        ObservedAxisState localRoll;
        SidestickSideInput capt;
        SidestickSideInput fo;
        AppliedOutputState applied;
        SimAxisState simAxis;
        StickVisualState visuals;
        int bridgeControlState;
        PriorityState priorityState;
        bool dualInputActive;
        bool isMaster;
        CrewSide seat;
        long nowMs;

        lock (_gate)
        {
            nowMs = _clock.ElapsedMilliseconds;
            localPitch = _lastObservedPitch;
            localRoll = _lastObservedRoll;
            capt = GetEffectiveInput(_captInput, nowMs);
            fo = GetEffectiveInput(_foInput, nowMs);
            applied = _lastAppliedOutput;
            simAxis = _lastSimAxis;
            visuals = _lastStickVisuals;
            bridgeControlState = _lastBridgeControlState;
            priorityState = _priorityState;
            dualInputActive = _prevDualInputActive;
            isMaster = _masterSwitch.IsMaster;
            seat = _crewSideState.SelectedSide;
        }

        var shouldLog =
            nowMs - localPitch.LastUpdateMs <= (long)DiagnosticRecentInputWindow.TotalMilliseconds ||
            nowMs - localRoll.LastUpdateMs <= (long)DiagnosticRecentInputWindow.TotalMilliseconds ||
            priorityState != PriorityState.Normal ||
            dualInputActive ||
            Math.Abs(capt.Pitch) > AxisNeutralDeadzoneNorm ||
            Math.Abs(capt.Roll) > AxisNeutralDeadzoneNorm ||
            Math.Abs(fo.Pitch) > AxisNeutralDeadzoneNorm ||
            Math.Abs(fo.Roll) > AxisNeutralDeadzoneNorm ||
            Math.Abs(applied.MergedPitch) > AxisNeutralDeadzoneNorm ||
            Math.Abs(applied.MergedRoll) > AxisNeutralDeadzoneNorm ||
            Math.Abs(simAxis.Pitch) > AxisNeutralDeadzoneNorm ||
            Math.Abs(simAxis.Roll) > AxisNeutralDeadzoneNorm ||
            Math.Abs(visuals.CaptPitch) > AxisNeutralDeadzoneNorm ||
            Math.Abs(visuals.CaptRoll) > AxisNeutralDeadzoneNorm ||
            Math.Abs(visuals.FoPitch) > AxisNeutralDeadzoneNorm ||
            Math.Abs(visuals.FoRoll) > AxisNeutralDeadzoneNorm;

        if (!shouldLog)
            return;

        Log.Debug(
            "[SidestickDiag] Seat={Seat} Master={Master} BridgeCtrl={BridgeCtrl} Priority={Priority} Dual={Dual} " +
            "LocalPitch={LocalPitchRaw}/{LocalPitchNorm:0.###}/ign:{LocalPitchIgnored} " +
            "LocalRoll={LocalRollRaw}/{LocalRollNorm:0.###}/ign:{LocalRollIgnored} " +
            "Capt=({CaptPitch:0.###},{CaptRoll:0.###},PB:{CaptPb}) Fo=({FoPitch:0.###},{FoRoll:0.###},PB:{FoPb}) " +
            "Merged=({MergedPitch:0.###},{MergedRoll:0.###}) Alerts=(Cg:{CaptGreen},Fg:{FoGreen},Ca:{CaptArrow},Fa:{FoArrow},Pl:{PriorityLeft},Pr:{PriorityRight},Di:{DualAudio}) Inj={InjectedElev}/{InjectedAil} " +
            "Sim=({SimPitch:0.###},{SimRoll:0.###}) VisualC=({VisualCaptPitch:0.###},{VisualCaptRoll:0.###}) VisualF=({VisualFoPitch:0.###},{VisualFoRoll:0.###})",
            seat,
            isMaster,
            bridgeControlState,
            priorityState,
            dualInputActive,
            localPitch.RawValue,
            localPitch.NormalizedValue,
            localPitch.IgnoredAsInjected,
            localRoll.RawValue,
            localRoll.NormalizedValue,
            localRoll.IgnoredAsInjected,
            capt.Pitch,
            capt.Roll,
            capt.TakeoverPressed,
            fo.Pitch,
            fo.Roll,
            fo.TakeoverPressed,
            applied.MergedPitch,
            applied.MergedRoll,
            applied.CaptGreen,
            applied.FoGreen,
            applied.CaptRedArrow,
            applied.FoRedArrow,
            applied.PriorityLeftAudioPulse,
            applied.PriorityRightAudioPulse,
            applied.DualInputAudioPulse,
            applied.InjectedElevatorRaw,
            applied.InjectedAileronRaw,
            simAxis.Pitch,
            simAxis.Roll,
            visuals.CaptPitch,
            visuals.CaptRoll,
            visuals.FoPitch,
            visuals.FoRoll);
    }

    private static int ToAxisValue(double normalizedValue)
    {
        var clamped = Clamp1(normalizedValue);
        return (int)Math.Round(clamped * AxisRange, MidpointRounding.AwayFromZero);
    }

    private static Thread CreateControlThread(ThreadStart start) => new(start)
    {
        IsBackground = true,
        Name = "FS Copilot A320 control",
        Priority = ThreadPriority.AboveNormal
    };

    private static Thread CreateNetworkSendThread(ThreadStart start) => new(start)
    {
        IsBackground = true,
        Name = "FS Copilot A320 network send",
        Priority = ThreadPriority.AboveNormal
    };

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static int BoolToInt(bool value) => value ? 1 : 0;

    private static int InferAutoTakeoverButton(int previousButtons, int currentButtons, int existingIndex)
    {
        var newlyPressedButtons = currentButtons & ~previousButtons;
        if (TryGetFirstPressedButtonIndex(newlyPressedButtons, out var nextIndex))
            return nextIndex;

        if (existingIndex >= 0 && IsConfiguredButtonPressed(currentButtons, existingIndex))
            return existingIndex;

        if (TryGetFirstPressedButtonIndex(currentButtons, out nextIndex))
            return nextIndex;

        return -1;
    }

    private static bool TryInferAutoTakeoverButton(
        CrewSide side,
        int previousButtons,
        int currentButtons,
        ref int inferredTakeoverButtonIndex)
    {
        var nextAutoButtonIndex = InferAutoTakeoverButton(previousButtons, currentButtons, inferredTakeoverButtonIndex);
        if (nextAutoButtonIndex == inferredTakeoverButtonIndex)
            return false;

        inferredTakeoverButtonIndex = nextAutoButtonIndex;
        if (inferredTakeoverButtonIndex >= 0)
        {
            Log.Information(
                "[Sidestick] Auto-mapped {Side} takeover button to local Button {Button}",
                side,
                inferredTakeoverButtonIndex + 1);
        }

        return true;
    }

    private static bool TryGetFirstPressedButtonIndex(int buttons, out int index)
    {
        for (var bit = 0; bit < 32; bit++)
        {
            if ((((uint)buttons >> bit) & 1u) == 0)
                continue;

            index = bit;
            return true;
        }

        index = -1;
        return false;
    }

    private static bool IsConfiguredButtonPressed(int buttons, int buttonIndex) =>
        buttonIndex >= 0 && buttonIndex < 32 && (((uint)buttons >> buttonIndex) & 1u) != 0;

    private void UpdateAudioOutput(string name, bool active, ref bool previousActive)
    {
        if (previousActive == active)
            return;

        previousActive = active;
        _sim.Execute($"{BoolToInt(active)} (>L:{name})");
    }

    private static bool UpdateAudioPulseWindow(ref long activeUntilMs, bool pulse, long nowMs, TimeSpan hold)
    {
        if (pulse)
            activeUntilMs = Math.Max(activeUntilMs, nowMs + (long)hold.TotalMilliseconds);

        if (nowMs >= activeUntilMs)
            return false;

        return true;
    }

    private static bool HaveStickVisualsChanged(StickVisualOutputState previousVisuals, SidestickOutputPacket output) =>
        Math.Abs(previousVisuals.CaptPitch - output.CaptPitch) > 0.0005
        || Math.Abs(previousVisuals.CaptRoll - output.CaptRoll) > 0.0005
        || Math.Abs(previousVisuals.FoPitch - output.FoPitch) > 0.0005
        || Math.Abs(previousVisuals.FoRoll - output.FoRoll) > 0.0005;

    private static bool HaveAlertOutputsChanged(AppliedOutputState previousApplied, SidestickOutputPacket output) =>
        previousApplied.CaptGreen != output.CaptGreen
        || previousApplied.FoGreen != output.FoGreen
        || previousApplied.CaptRedArrow != output.CaptRedArrow
        || previousApplied.FoRedArrow != output.FoRedArrow;

    private static double ApplyNeutralDeadzone(double value) =>
        Math.Abs(value) < AxisNeutralDeadzoneNorm ? 0 : value;

    private static double Clamp1(double value) => Math.Clamp(value, -1.0, 1.0);

    private enum PriorityState : byte
    {
        Normal = 0,
        CaptHeld = 1,
        FoHeld = 2,
        CaptLatched = 3,
        FoLatched = 4
    }

    private enum AxisKind : byte
    {
        Aileron = 0,
        Elevator = 1
    }

    private readonly record struct AxisPair(double Pitch, double Roll);

    private readonly record struct InjectedAxisState(int RawValue, long IgnoreUntilMs);

    private readonly record struct ObservedAxisState(
        int RawValue,
        double NormalizedValue,
        bool IgnoredAsInjected,
        long LastUpdateMs);

    private readonly record struct AppliedOutputState(
        double CaptPitch,
        double CaptRoll,
        double FoPitch,
        double FoRoll,
        double MergedPitch,
        double MergedRoll,
        bool CaptTakeoverPressed,
        bool FoTakeoverPressed,
        bool CaptGreen,
        bool FoGreen,
        bool CaptRedArrow,
        bool FoRedArrow,
        bool PriorityLeftAudioPulse,
        bool PriorityRightAudioPulse,
        bool DualInputAudioPulse,
        int InjectedElevatorRaw,
        int InjectedAileronRaw,
        long LastUpdateMs);

    private readonly record struct SimAxisState(
        double Pitch,
        double Roll,
        double Rudder,
        long LastUpdateMs);

    private readonly record struct StickVisualState(
        double CaptPitch,
        double CaptRoll,
        double FoPitch,
        double FoRoll,
        long LastUpdateMs);

    private readonly record struct StickVisualOutputState(
        double CaptPitch,
        double CaptRoll,
        double FoPitch,
        double FoRoll);

    private readonly record struct LocalCaptureConfig(
        bool Enabled,
        bool Connected,
        bool BridgeReady,
        bool BridgeVersionMismatch,
        CrewSide Side,
        int ControllerIndex,
        int TakeoverButtonIndex)
    {
        public bool Active => Enabled && BridgeReady && !BridgeVersionMismatch;
    }

    public readonly record struct RuntimeSnapshot(
        CrewSide Seat,
        bool IsMaster,
        bool SessionActive,
        bool DynamicAuthorityActive,
        int BridgeControlState,
        string PriorityState,
        bool DualInputActive,
        bool CaptTakeoverPressed,
        bool FoTakeoverPressed,
        double CaptPitch,
        double CaptRoll,
        double FoPitch,
        double FoRoll,
        double MergedPitch,
        double MergedRoll,
        bool CaptGreen,
        bool FoGreen,
        bool CaptRedArrow,
        bool FoRedArrow);

    public RuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (_gate)
        {
            var nowMs = _clock.ElapsedMilliseconds;
            var capt = GetEffectiveInput(_captInput, nowMs);
            var fo = GetEffectiveInput(_foInput, nowMs);
            var applied = _lastAppliedOutput;

            return new RuntimeSnapshot(
                Seat: _crewSideState.SelectedSide,
                IsMaster: _masterSwitch.IsMaster,
                SessionActive: _connected,
                DynamicAuthorityActive: _connected && GetEffectiveDynamicAuthorityEnabled(),
                BridgeControlState: _lastBridgeControlState,
                PriorityState: _priorityState.ToString(),
                DualInputActive: _prevDualInputActive,
                CaptTakeoverPressed: capt.TakeoverPressed,
                FoTakeoverPressed: fo.TakeoverPressed,
                CaptPitch: capt.Pitch,
                CaptRoll: capt.Roll,
                FoPitch: fo.Pitch,
                FoRoll: fo.Roll,
                MergedPitch: applied.MergedPitch,
                MergedRoll: applied.MergedRoll,
                CaptGreen: applied.CaptGreen,
                FoGreen: applied.FoGreen,
                CaptRedArrow: applied.CaptRedArrow,
                FoRedArrow: applied.FoRedArrow);
        }
    }

    private readonly record struct SidestickSideInput(
        CrewSide Side,
        double Pitch,
        double Roll,
        bool TakeoverPressed,
        uint Sequence,
        long LastUpdateMs,
        long LastPressEdgeMs)
    {
        public static SidestickSideInput Empty(CrewSide side) => new(side, 0, 0, false, 0, 0, 0);
    }

    private readonly record struct SidestickInputPacket(
        CrewSide Side,
        uint Sequence,
        float Pitch,
        float Roll,
        bool TakeoverPressed)
    {
        public sealed class Codec : IPacketCodec<SidestickInputPacket>
        {
            public void Encode(SidestickInputPacket packet, BinaryWriter bw)
            {
                bw.Write((byte)packet.Side);
                bw.Write(packet.Sequence);
                bw.Write(packet.Pitch);
                bw.Write(packet.Roll);
                bw.Write(packet.TakeoverPressed);
            }

            public SidestickInputPacket Decode(BinaryReader br) => new(
                Side: (CrewSide)br.ReadByte(),
                Sequence: br.ReadUInt32(),
                Pitch: br.ReadSingle(),
                Roll: br.ReadSingle(),
                TakeoverPressed: br.ReadBoolean());
        }
    }

    private readonly record struct SidestickOutputPacket(
        uint Sequence,
        float CaptPitch,
        float CaptRoll,
        float FoPitch,
        float FoRoll,
        float MergedPitch,
        float MergedRoll,
        bool CaptTakeoverPressed,
        bool FoTakeoverPressed,
        bool CaptGreen,
        bool FoGreen,
        bool CaptRedArrow,
        bool FoRedArrow,
        bool PriorityLeftAudioPulse,
        bool PriorityRightAudioPulse,
        bool DualInputAudioPulse)
    {
        public sealed class Codec : IPacketCodec<SidestickOutputPacket>
        {
            public void Encode(SidestickOutputPacket packet, BinaryWriter bw)
            {
                bw.Write(packet.Sequence);
                bw.Write(packet.CaptPitch);
                bw.Write(packet.CaptRoll);
                bw.Write(packet.FoPitch);
                bw.Write(packet.FoRoll);
                bw.Write(packet.MergedPitch);
                bw.Write(packet.MergedRoll);
                bw.Write(packet.CaptTakeoverPressed);
                bw.Write(packet.FoTakeoverPressed);
                bw.Write(packet.CaptGreen);
                bw.Write(packet.FoGreen);
                bw.Write(packet.CaptRedArrow);
                bw.Write(packet.FoRedArrow);
                bw.Write(packet.PriorityLeftAudioPulse);
                bw.Write(packet.PriorityRightAudioPulse);
                bw.Write(packet.DualInputAudioPulse);
            }

            public SidestickOutputPacket Decode(BinaryReader br) => new(
                Sequence: br.ReadUInt32(),
                CaptPitch: br.ReadSingle(),
                CaptRoll: br.ReadSingle(),
                FoPitch: br.ReadSingle(),
                FoRoll: br.ReadSingle(),
                MergedPitch: br.ReadSingle(),
                MergedRoll: br.ReadSingle(),
                CaptTakeoverPressed: br.ReadBoolean(),
                FoTakeoverPressed: br.ReadBoolean(),
                CaptGreen: br.ReadBoolean(),
                FoGreen: br.ReadBoolean(),
                CaptRedArrow: br.ReadBoolean(),
                FoRedArrow: br.ReadBoolean(),
                PriorityLeftAudioPulse: br.ReadBoolean(),
                PriorityRightAudioPulse: br.ReadBoolean(),
                DualInputAudioPulse: br.ReadBoolean());
        }
    }

    private readonly record struct SidestickStatusPacket(
        uint Sequence,
        bool DynamicAuthorityEnabled)
    {
        public sealed class Codec : IPacketCodec<SidestickStatusPacket>
        {
            public void Encode(SidestickStatusPacket packet, BinaryWriter bw)
            {
                bw.Write(packet.Sequence);
                bw.Write(packet.DynamicAuthorityEnabled);
            }

            public SidestickStatusPacket Decode(BinaryReader br) => new(
                Sequence: br.ReadUInt32(),
                DynamicAuthorityEnabled: br.ReadBoolean());
        }
    }
}
