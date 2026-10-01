namespace FsCopilot.Simulation;

using Connection;
using Network;
using ReactiveUI;

public class MasterSwitch : IDisposable
{
    private static readonly string PeerId = Guid.NewGuid().ToString();
    private const string InputOnlyAircraft = "airbus-a346-pro";

    private readonly BehaviorSubject<bool> _master = new(true);
    private readonly CompositeDisposable _d = new();

    public bool IsMaster => _master.Value;
    public IObservable<bool> Master => _master;

    public MasterSwitch(SimClient sim, INetwork net, BehaviorControlOverrideState behaviorControlOverrideState)
    {
        net.RegisterPacket<SetMaster, SetMaster.Codec>();
        var aircraft = sim.Aircraft.StartWith(string.Empty).Replay(1).RefCount();
        var bridgeModes = _master
            .CombineLatest(
                behaviorControlOverrideState.WhenAnyValue(state => state.UseCapturedControl)
                    .StartWith(behaviorControlOverrideState.UseCapturedControl),
                aircraft,
                (isMaster, useCapturedControl, aircraftName) => new BridgeMode(
                    isMaster
                        ? (useCapturedControl ? BehaviorControl.CapturedMaster : BehaviorControl.Master)
                        : (useCapturedControl ? BehaviorControl.CapturedSlave : BehaviorControl.Slave),
                    !isMaster && aircraftName.Equals(InputOnlyAircraft, StringComparison.OrdinalIgnoreCase),
                    aircraftName,
                    useCapturedControl,
                    isMaster))
            .DistinctUntilChanged()
            .Replay(1).RefCount();

        _d.Add(net.Stream<SetMaster>()
            .Subscribe(setMaster =>
            {
                var newMaster = setMaster.Peer == PeerId;
                if (newMaster != IsMaster) _master.OnNext(newMaster);
            }));

        _d.Add(net.Peers
            .Select(peers => peers.Count == 0)
            .DistinctUntilChanged()
            .Where(noPeers => noPeers)
            .Subscribe(_ =>
            {
                if (IsMaster)
                    return;

                Log.Information("[MasterSwitch] No connected peers remain; restoring local master authority");
                _master.OnNext(true);
            }));
        
        _d.Add(bridgeModes
            .Subscribe(mode =>
            {
                sim.SetControl(mode.Control);
                sim.ExecuteHighPriority($"{(mode.InputOnly ? 1 : 0)} (>L:FSC_INPUT_ONLY)");
                Log.Information("[MasterSwitch] Bridge mode {Control} Captured={UseCapturedControl} Master={IsMaster} Aircraft={Aircraft}",
                    mode.Control, mode.UseCapturedControl, mode.IsMaster, mode.AircraftName);

                if (mode.InputOnly)
                    Log.Information("[MasterSwitch] Input-only slave bridge enabled for {Aircraft}", mode.AircraftName);
            }));

        _d.Add(Observable.Interval(TimeSpan.FromMilliseconds(250), DefaultScheduler.Instance)
            .WithLatestFrom(bridgeModes, (_, mode) => mode)
            .Where(mode => mode.UseCapturedControl)
            .Subscribe(mode => sim.ExecuteHighPriority($"{(int)mode.Control} (>L:FSC_CONTROL)")));
        
        _d.Add(_master.DistinctUntilChanged()
            .Where(isMaster => isMaster)
            .Subscribe(_ => net.SendAll(new SetMaster(PeerId))));
        
        _d.Add(sim.Stream("K:TOGGLE_LAUNCH_BAR_SWITCH", string.Empty)
            .Do(_ => Log.Information("Launch bar toggle detected"))
            .Subscribe(_ => TakeControl()));
        
        // _d.Add(sim.Config.Where(c => c.Undefined).Subscribe(_ => sim.Set(new SimConfig(false, _master.Value))));
        // _d.Add(_master.Subscribe(val => sim.Set(new SimConfig(false, val))));
        // _d.Add(sim.Config.Where(c => !c.Undefined).Subscribe(c =>
        // {
        //     if (c.Control) TakeControl();
        // }));
    }

    public void TakeControl() => _master.OnNext(true);

    //todo Temp solution. We should use ClientConnected event from Peer2Peer class 
    public void Join() => _master.OnNext(false);

    public void Dispose() => _d.Dispose();

    private record SetMaster(string Peer)
    {
        public class Codec : IPacketCodec<SetMaster>
        {
            public void Encode(SetMaster packet, BinaryWriter bw) => bw.Write(packet.Peer);

            public SetMaster Decode(BinaryReader br) => new(br.ReadString());
        }
    }

    private sealed record BridgeMode(BehaviorControl Control, bool InputOnly, string AircraftName, bool UseCapturedControl, bool IsMaster);
}
