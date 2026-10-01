namespace FsCopilot.Simulation;

using System.Globalization;
using System.Security.Cryptography;
using Connection;
using Network;
using ReactiveUI;

public class Coordinator : IDisposable
{
    private const string CaptDisconnectVar = "L:S_FC_CAPT_INST_DISCONNECT";
    private const string FoDisconnectVar = "L:S_FC_FO_INST_DISCONNECT";
    private static readonly HashSet<string> ForcedMasterAuthorityVars = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fenix throttle levers must never be published by the non-control side.
        // Some older/downloaded profiles list these as shared, which lets the slave move the master throttles.
        "L:A_FC_THROTTLE_LEFT_INPUT",
        "L:A_FC_THROTTLE_RIGHT_INPUT"
    };
    private static readonly TimeSpan ForcedMasterPublishInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ForcedMasterHoldInterval = TimeSpan.FromMilliseconds(20);

    private readonly INetwork _net;
    private readonly MasterSwitch _masterSwitch;
    private readonly SimClient _sim;
    private readonly CrewSideState _crewSideState;
    private readonly CompositeDisposable _d = new();
    private CompositeDisposable _cSubs = new();
    private HashSet<string> _ignore = [];
    private volatile bool _disableSurfaceSync;

    public Coordinator(
        SimClient sim,
        INetwork net,
        MasterSwitch masterSwitch,
        CrewSideState crewSideState,
        BehaviorControlOverrideState behaviorControlOverrideState)
    {
        _net = net;
        _masterSwitch = masterSwitch;
        _sim = sim;
        _crewSideState = crewSideState;
        var sw = Stopwatch.StartNew();

        Span<byte> sessionBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(sessionBytes);
        var sessionId = BitConverter.ToUInt64(sessionBytes);

        net.RegisterPacket<Update, Update.Codec>();
        net.RegisterPacket<Interact, InteractCodec>();

        _sim.Register<Physics>();
        _sim.Register<Surfaces>();
        _net.RegisterPacket<Physics, Physics.Codec>();
        _net.RegisterPacket<Surfaces, Surfaces.Codec>();

        _d.Add(behaviorControlOverrideState.WhenAnyValue(state => state.UseCapturedControl)
            .StartWith(behaviorControlOverrideState.UseCapturedControl)
            .DistinctUntilChanged()
            .Subscribe(disableSurfaceSync => _disableSurfaceSync = disableSurfaceSync));

        _d.Add(sim.Aircraft.Take(1).Subscribe(_ => AddLink((ref Physics physics) =>
        {
            physics.SessionId = sessionId;
            physics.TimeMs = (uint)sw.ElapsedMilliseconds;
        })));

        _d.Add(sim.Aircraft.Take(1).Subscribe(_ => AddLink((ref Surfaces surfaces) =>
        {
            surfaces.SessionId = sessionId;
            surfaces.TimeMs = (uint)sw.ElapsedMilliseconds;
        })));

        _d.Add(_sim.Interactions
            .Where(i => !_ignore.Contains(i.Instrument))
            .Subscribe(interact => _net.SendAll(interact)));
        _d.Add(_net.Stream<Interact>()
            .Subscribe(update => _sim.Set(update)));
    }

    public void Dispose()
    {
        _d.Dispose();
        _cSubs.Dispose();
    }

    public void Load(Definitions definitions)
    {
        if (!_cSubs.IsDisposed) _cSubs.Dispose();
        _ignore.Clear();
        _cSubs = new();
        foreach (var def in definitions) AddLink(def);
        foreach (var i in definitions.Ignore) _ignore.Add(i);
    }

    private void AddLink<TPacket>(RefAction<TPacket> modify)
        where TPacket : unmanaged
    {
        var isSurfacesPacket = typeof(TPacket) == typeof(Surfaces);

        _d.Add(_sim.Stream<TPacket>()
            .Sample(TimeSpan.FromMilliseconds(50), new EventLoopScheduler()) // 20 fps
            .Where(_ => !isSurfacesPacket || !_disableSurfaceSync)
            .Where(_ => _masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                modify(ref update);
                try { _net.SendAll(update, true); }
                catch (Exception e) { Log.Error(e, "[Coordinator] Error while sending packet {Packet}", typeof(TPacket).Name); }
            }, ex => { Log.Fatal(ex, "[Coordinator] Error while processing a message from sim"); }));

        _d.Add(_net.Stream<TPacket>()
            .Where(_ => !isSurfacesPacket || !_disableSurfaceSync)
            .Where(_ => !_masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                try { _sim.Set(update); }
                catch (Exception e) { Log.Error(e, "[Coordinator] Error processing packet {Packet}", typeof(TPacket).Name); }
            }, ex => { Log.Fatal(ex, "[Coordinator] Error while processing a message from client"); }));
    }

    private void AddLink(Definition def)
    {
        var forcedMasterAuthority = ForcedMasterAuthorityVars.Contains(def.Get);
        var master = !def.Shared || forcedMasterAuthority;
        object? currentValue = null;
        object? authoritativeValue = null;
        var loggedForcedPublish = false;
        var loggedForcedReceive = false;
        var loggedForcedHold = false;
        var getVar = def.Get;

        var rawSimRx = _sim.Stream(getVar, def.Units);
        var simRx = rawSimRx;
        if (forcedMasterAuthority)
        {
            _cSubs.Add(Observable.Interval(ForcedMasterPublishInterval, DefaultScheduler.Instance)
                .WithLatestFrom(rawSimRx, (_, value) => value)
                .Where(_ => _masterSwitch.IsMaster)
                .Subscribe(value =>
                {
                    currentValue = value;
                    try
                    {
                        _net.SendAll(new Update(getVar, value), unreliable: true);
                        if (!loggedForcedPublish)
                        {
                            loggedForcedPublish = true;
                            Log.Information("[Coordinator] Publishing master authority for {Name}: {Value}", getVar, value);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "[Coordinator] Error while publishing master-authority var {Name}", getVar);
                    }
                }));

            _cSubs.Add(Observable.Interval(ForcedMasterHoldInterval, DefaultScheduler.Instance)
                .Where(_ => !_masterSwitch.IsMaster)
                .Where(_ => authoritativeValue != null)
                .Subscribe(_ =>
                {
                    try
                    {
                        ApplyDefinitionValue(def, authoritativeValue!, currentValue ?? authoritativeValue!, highPriority: true);
                        if (!loggedForcedHold)
                        {
                            loggedForcedHold = true;
                            Log.Information("[Coordinator] Holding local master-authority var {Name}: {Value}", getVar, authoritativeValue);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "[Coordinator] Error while holding local master-authority var {Name}", getVar);
                    }
                }));

            _cSubs.Add(rawSimRx
                .Sample(ForcedMasterHoldInterval, DefaultScheduler.Instance)
                .Where(_ => !_masterSwitch.IsMaster)
                .Where(_ => authoritativeValue != null)
                .Where(localValue => !ValuesEquivalent(localValue, authoritativeValue))
                .Subscribe(localValue =>
                {
                    try
                    {
                        ApplyDefinitionValue(def, authoritativeValue!, localValue, highPriority: true);
                        Log.Verbose("[Coordinator] Guarded local {Name} {LocalValue} -> {MasterValue}",
                            getVar, localValue, authoritativeValue);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "[Coordinator] Error while guarding local master-authority var {Name}", getVar);
                    }
                }));
        }

        if (master) simRx = simRx.Sample(TimeSpan.FromMilliseconds(30), DefaultScheduler.Instance); // 33 fps
        simRx = simRx
            .Do(value => currentValue = value)
            .Where(_ => !master || _masterSwitch.IsMaster);
        if (getVar[0] == 'H') simRx = simRx.Delay(TimeSpan.FromMilliseconds(500));
        if (!master) simRx = simRx.Where(_ => !Skip.Should(getVar));

        _cSubs.Add(simRx
            .Subscribe(value =>
            {
                if (!master && def.Skip != null) Skip.Next(def.Skip);
                _net.SendAll(new Update(getVar, value), unreliable: master);
                Log.Verbose("[PACKET] SENT {Name} {Value}", getVar, value);
            }));

        _cSubs.Add(_net.Stream<Update>()
            .Where(update => update.Name == getVar)
            .Do(update => Log.Verbose("[PACKET] RECV {Name} {Value}", getVar, update.Value))
            .Where(_ => !master || !_masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                if (ShouldIgnoreLocalDisconnectReplay(getVar))
                    return;

                if (master)
                {
                    if (forcedMasterAuthority)
                    {
                        authoritativeValue = update.Value;
                        if (!loggedForcedReceive)
                        {
                            loggedForcedReceive = true;
                            Log.Information("[Coordinator] Received master authority for {Name}: {Value}", getVar, update.Value);
                        }
                    }

                    ApplyDefinitionValue(def, update.Value, currentValue ?? update.Value, highPriority: forcedMasterAuthority);
                }
                else
                {
                    var expression = def.Set(update.Value, currentValue ?? update.Value);
                    if (expression.Contains(">K:#"))
                    {
                        var set = def.ParseSet(update.Value, currentValue ?? update.Value, out var units, out var values);
                        if (values.Length == 0) return;
                        Skip.Next(getVar);
                        _sim.Set(set, units, values);
                    }
                    else
                    {
                        if ((expression.Contains(">K:") || expression.Contains(">B:"))
                            && expression.Contains("TOGGLE", StringComparison.OrdinalIgnoreCase)
                            && update.Value.Equals(currentValue)) return;
                        Skip.Next(getVar);
                        _sim.Execute(expression);
                    }
                }
            }));
    }

    private void ApplyDefinitionValue(Definition def, object value, object current, bool highPriority = false)
    {
        if (highPriority)
        {
            var expression = def.Set(value, current);
            if (!string.IsNullOrWhiteSpace(expression))
            {
                _sim.ExecuteHighPriority(expression);
                return;
            }
        }

        var set = def.ParseSet(value, current, out var units, out var values);
        if (values.Length == 0) return;
        _sim.Set(set, units, values);
    }

    private static bool ValuesEquivalent(object? left, object? right)
    {
        if (left == null || right == null)
            return left == right;

        if (TryDouble(left, out var leftNumber) && TryDouble(right, out var rightNumber))
            return Math.Abs(leftNumber - rightNumber) < 0.001;

        return left.Equals(right);

        static bool TryDouble(object value, out double number)
        {
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                number = 0;
                return false;
            }
        }
    }

    private bool ShouldIgnoreLocalDisconnectReplay(string getVar) =>
        _crewSideState.SelectedSide == CrewSide.Captain && getVar.Equals(CaptDisconnectVar, StringComparison.OrdinalIgnoreCase)
        || _crewSideState.SelectedSide == CrewSide.FirstOfficer && getVar.Equals(FoDisconnectVar, StringComparison.OrdinalIgnoreCase);

    private record Update(string Name, object Value)
    {
        public class Codec : IPacketCodec<Update>
        {
            public void Encode(Update packet, BinaryWriter bw)
            {
                bw.Write(packet.Name);
                var v = packet.Value;
                var type = Type.GetTypeCode(v.GetType());
                bw.Write((byte)type);
                switch (type)
                {
                    case TypeCode.String: bw.Write((string)v); break;
                    case TypeCode.Int32: bw.Write((int)v); break;
                    case TypeCode.Int64: bw.Write((long)v); break;
                    case TypeCode.Double: bw.Write((double)v); break;
                    case TypeCode.Single: bw.Write((float)v); break;
                    case TypeCode.Boolean: bw.Write((bool)v); break;
                    case TypeCode.Int16: bw.Write((short)v); break;
                    case TypeCode.UInt16: bw.Write((ushort)v); break;
                    case TypeCode.UInt32: bw.Write((uint)v); break;
                    case TypeCode.UInt64: bw.Write((ulong)v); break;
                    case TypeCode.Decimal: bw.Write((decimal)v); break;
                    case TypeCode.SByte: bw.Write((sbyte)v); break;
                    case TypeCode.Byte: bw.Write((byte)v); break;
                    default: throw new NotSupportedException( $"Data.Value type '{v.GetType().FullName}' is not supported by codec");
                }
            }

            public Update Decode(BinaryReader br)
            {
                var name = br.ReadString();
                var t = (TypeCode)br.ReadByte();
                object value = t switch
                {
                    TypeCode.String => br.ReadString(),
                    TypeCode.Int32 => br.ReadInt32(),
                    TypeCode.Int64 => br.ReadInt64(),
                    TypeCode.Double => br.ReadDouble(),
                    TypeCode.Single => br.ReadSingle(),
                    TypeCode.Boolean => br.ReadBoolean(),
                    TypeCode.Int16 => br.ReadInt16(),
                    TypeCode.UInt16 => br.ReadUInt16(),
                    TypeCode.UInt32 => br.ReadUInt32(),
                    TypeCode.UInt64 => br.ReadUInt64(),
                    TypeCode.Decimal => br.ReadDecimal(),
                    TypeCode.SByte => br.ReadSByte(),
                    TypeCode.Byte => br.ReadByte(),
                    TypeCode.DateTime => DateTime.FromBinary(br.ReadInt64()),
                    _ => throw new NotSupportedException($"Неизвестный TypeCode={t}")
                };

                return new(name, value);
            }
        }
    }

    private class InteractCodec : IPacketCodec<Interact>
    {
        public void Encode(Interact packet, BinaryWriter bw)
        {
            bw.Write(packet.Instrument);
            bw.Write(packet.Event);
            bw.Write(packet.Id);
            bw.Write(packet.Value != null);
            if (packet.Value != null) bw.Write(packet.Value);
        }

        public Interact Decode(BinaryReader br)
        {
            var instrument = br.ReadString();
            var @event = br.ReadString();
            var id = br.ReadString();
            var hasValue = br.ReadBoolean();
            var value = hasValue ? br.ReadString() : null;
            return new(instrument, @event, id, value);
        }
    }

    delegate void RefAction<T>(ref T value);
}
