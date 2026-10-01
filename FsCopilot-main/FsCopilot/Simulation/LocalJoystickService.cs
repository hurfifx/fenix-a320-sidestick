namespace FsCopilot.Simulation;

using Microsoft.Win32;
using System.Runtime.InteropServices;

public sealed class LocalJoystickService : IDisposable
{
    public const int MaxSupportedJoysticks = 8;
    private const int JoyReturnX = 0x00000001;
    private const int JoyReturnY = 0x00000002;
    private const int JoyReturnButtons = 0x00000080;
    private const int JoyErrorNoError = 0;

    private readonly BehaviorSubject<IReadOnlyList<JoystickDeviceInfo>> _devices;
    private readonly IDisposable _refreshSubscription;
    private readonly bool _timerPrecisionRequested;
    private DeviceSnapshot[] _deviceSnapshots;

    public IObservable<IReadOnlyList<JoystickDeviceInfo>> Devices => _devices;

    public LocalJoystickService()
    {
        _timerPrecisionRequested = timeBeginPeriod(1) == 0;
        _deviceSnapshots = EnumerateDeviceSnapshots();
        _devices = new BehaviorSubject<IReadOnlyList<JoystickDeviceInfo>>(ToDeviceInfos(_deviceSnapshots));
        _refreshSubscription = Observable.Interval(TimeSpan.FromSeconds(2), DefaultScheduler.Instance)
            .Subscribe(_ => RefreshDevices());
    }

    public void Dispose()
    {
        _refreshSubscription.Dispose();
        if (_timerPrecisionRequested)
            timeEndPeriod(1);
        _devices.Dispose();
    }

    public IReadOnlyList<JoystickDeviceInfo> GetSnapshot() => _devices.Value;

    public bool TryReadSample(int index, out LocalJoystickSample sample)
    {
        sample = default;
        if (index < 0 || index >= MaxSupportedJoysticks)
            return false;

        if (!TryGetDeviceSnapshot(index, out var device)
            && !TryCreateDeviceSnapshot(index, out device))
            return false;

        var info = new JoyInfoEx
        {
            dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(),
            dwFlags = JoyReturnX | JoyReturnY | JoyReturnButtons
        };

        var posResult = joyGetPosEx((uint)index, ref info);
        if (posResult != JoyErrorNoError)
            return false;

        sample = new LocalJoystickSample(
            Index: index,
            Name: device.Info.Name,
            RawX: unchecked((int)info.dwXpos),
            RawY: unchecked((int)info.dwYpos),
            XAxis: NormalizeAxis(info.dwXpos, device.Caps.wXmin, device.Caps.wXmax),
            YAxis: NormalizeAxis(info.dwYpos, device.Caps.wYmin, device.Caps.wYmax),
            Buttons: unchecked((int)info.dwButtons),
            XAxisAtHardRail: IsHardRail(info.dwXpos, device.Caps.wXmin, device.Caps.wXmax),
            YAxisAtHardRail: IsHardRail(info.dwYpos, device.Caps.wYmin, device.Caps.wYmax));
        return true;
    }

    private void RefreshDevices()
    {
        var snapshots = EnumerateDeviceSnapshots();
        var devices = ToDeviceInfos(snapshots);
        _deviceSnapshots = snapshots;
        var current = _devices.Value;
        if (DevicesEqual(current, devices))
            return;

        _devices.OnNext(devices);
    }

    private static DeviceSnapshot[] EnumerateDeviceSnapshots()
    {
        var devices = new List<DeviceSnapshot>();
        var count = Math.Min((int)joyGetNumDevs(), MaxSupportedJoysticks);
        for (var index = 0; index < count; index++)
        {
            if (TryCreateDeviceSnapshot(index, out var snapshot))
                devices.Add(snapshot);
        }

        return devices.ToArray();
    }

    private static IReadOnlyList<JoystickDeviceInfo> ToDeviceInfos(IEnumerable<DeviceSnapshot> snapshots) =>
        snapshots.Select(snapshot => snapshot.Info).ToArray();

    private bool TryGetDeviceSnapshot(int index, out DeviceSnapshot snapshot)
    {
        foreach (var candidate in _deviceSnapshots)
        {
            if (candidate.Info.Index != index)
                continue;

            snapshot = candidate;
            return true;
        }

        snapshot = default;
        return false;
    }

    private static bool TryCreateDeviceSnapshot(int index, out DeviceSnapshot snapshot)
    {
        snapshot = default;

        var caps = new JoyCaps();
        var capsResult = joyGetDevCapsW((uint)index, ref caps, (uint)Marshal.SizeOf<JoyCaps>());
        if (capsResult != JoyErrorNoError)
            return false;

        var info = new JoyInfoEx
        {
            dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(),
            dwFlags = JoyReturnX | JoyReturnY | JoyReturnButtons
        };

        var posResult = joyGetPosEx((uint)index, ref info);
        if (posResult != JoyErrorNoError)
            return false;

        snapshot = new DeviceSnapshot(new JoystickDeviceInfo(index, GetDisplayName(index, caps)), caps);
        return true;
    }

    private static bool DevicesEqual(IReadOnlyList<JoystickDeviceInfo> left, IReadOnlyList<JoystickDeviceInfo> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Index != right[index].Index)
                return false;
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string GetDisplayName(int index, JoyCaps caps)
    {
        var displayName = GetRegistryDisplayName(caps) ?? GetFallbackDisplayName(caps.szPname);
        return string.IsNullOrEmpty(displayName)
            ? $"Joystick {index}"
            : $"Joystick {index}: {displayName}";
    }

    private static string? GetRegistryDisplayName(JoyCaps caps)
    {
        foreach (var candidate in GetRegistryCandidates(caps))
        {
            var fromCurrentUser = ReadRegistryOemName(
                Registry.CurrentUser,
                $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\{candidate}");
            if (!string.IsNullOrWhiteSpace(fromCurrentUser))
                return fromCurrentUser;

            var fromLocalMachine = ReadRegistryOemName(
                Registry.LocalMachine,
                $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\{candidate}");
            if (!string.IsNullOrWhiteSpace(fromLocalMachine))
                return fromLocalMachine;
        }

        return null;
    }

    private static IEnumerable<string> GetRegistryCandidates(JoyCaps caps)
    {
        var regKey = caps.szRegKey?.Trim();
        if (!string.IsNullOrWhiteSpace(regKey))
            yield return regKey;

        yield return $"VID_{caps.wMid:X4}&PID_{caps.wPid:X4}";
    }

    private static string? ReadRegistryOemName(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key == null)
            return null;

        return GetFallbackDisplayName(key.GetValue("OEMName") as string);
    }

    private static string? GetFallbackDisplayName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (trimmed.Equals("Microsoft PC-joystick driver", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Microsoft joystick driver", StringComparison.OrdinalIgnoreCase))
            return null;

        return trimmed;
    }

    private static double NormalizeAxis(uint rawValue, uint minValue, uint maxValue)
    {
        if (maxValue <= minValue)
            return 0;

        var normalized = ((rawValue - minValue) * 2.0 / (maxValue - minValue)) - 1.0;
        return Math.Clamp(normalized, -1.0, 1.0);
    }

    private static bool IsHardRail(uint rawValue, uint minValue, uint maxValue) =>
        maxValue > minValue && (rawValue <= minValue || rawValue >= maxValue);

    public readonly record struct JoystickDeviceInfo(int Index, string Name);

    private readonly record struct DeviceSnapshot(JoystickDeviceInfo Info, JoyCaps Caps);

    public readonly record struct LocalJoystickSample(
        int Index,
        string Name,
        int RawX,
        int RawY,
        double XAxis,
        double YAxis,
        int Buttons,
        bool XAxisAtHardRail,
        bool YAxisAtHardRail);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint wXmin;
        public uint wXmax;
        public uint wYmin;
        public uint wYmax;
        public uint wZmin;
        public uint wZmax;
        public uint wNumButtons;
        public uint wPeriodMin;
        public uint wPeriodMax;
        public uint wRmin;
        public uint wRmax;
        public uint wUmin;
        public uint wUmax;
        public uint wVmin;
        public uint wVmax;
        public uint wCaps;
        public uint wMaxAxes;
        public uint wNumAxes;
        public uint wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint dwSize;
        public uint dwFlags;
        public uint dwXpos;
        public uint dwYpos;
        public uint dwZpos;
        public uint dwRpos;
        public uint dwUpos;
        public uint dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1;
        public uint dwReserved2;
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCapsW(uint uJoyID, ref JoyCaps pjc, uint cbjc);

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(uint uJoyID, ref JoyInfoEx pji);
}
