namespace FsCopilot.Simulation;

using ReactiveUI;

public sealed class FenixSidestickProfileState : ReactiveObject
{
    private const int CurrentSettingsVersion = 4;
    private const int RollMigrationVersion = 2;
    private const string SettingsFileName = "fenix-sidestick.json";
    public const double DefaultDeadzone = 0.03;
    public const double DefaultSensitivity = 1.0;
    public const double DefaultExpo = 0.25;
    private bool _enabled;
    private bool _consentAccepted;
    private bool _setupChecklistDismissed;
    private double _pitchDeadzone;
    private double _rollDeadzone;
    private double _pitchSensitivity;
    private double _rollSensitivity;
    private double _pitchExpo;
    private double _rollExpo;
    private bool _invertPitch;
    private bool _invertRoll;
    private int _controllerIndex;
    private int _takeoverButtonIndex;
    private bool _dynamicAuthorityEnabled;

    public FenixSidestickProfileState()
    {
        var settings = LocalStateStorage.Load(SettingsFileName, PersistedSettings.Default);
        _enabled = settings.SettingsVersion < CurrentSettingsVersion
            ? false
            : settings.Enabled;
        _consentAccepted = settings.SettingsVersion < CurrentSettingsVersion
            ? false
            : settings.ConsentAccepted;
        _setupChecklistDismissed = settings.SettingsVersion < CurrentSettingsVersion
            ? false
            : settings.SetupChecklistDismissed;
        _pitchDeadzone = settings.SettingsVersion < CurrentSettingsVersion
            ? settings.Deadzone
            : settings.PitchDeadzone;
        _rollDeadzone = settings.SettingsVersion < CurrentSettingsVersion
            ? settings.Deadzone
            : settings.RollDeadzone;
        _pitchSensitivity = settings.PitchSensitivity;
        _rollSensitivity = settings.RollSensitivity;
        _pitchExpo = settings.PitchExpo;
        _rollExpo = settings.RollExpo;
        _invertPitch = settings.InvertPitch;
        _invertRoll = settings.SettingsVersion < RollMigrationVersion ? false : settings.InvertRoll;
        _controllerIndex = settings.ControllerIndex;
        _takeoverButtonIndex = settings.TakeoverButtonIndex;
        _dynamicAuthorityEnabled = settings.DynamicAuthorityEnabled;

        if (settings.SettingsVersion < CurrentSettingsVersion)
            Save();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            this.RaiseAndSetIfChanged(ref _enabled, value);
            Save();
        }
    }

    public bool ConsentAccepted
    {
        get => _consentAccepted;
        set
        {
            if (_consentAccepted == value)
                return;
            this.RaiseAndSetIfChanged(ref _consentAccepted, value);
            Save();
        }
    }

    public bool SetupChecklistDismissed
    {
        get => _setupChecklistDismissed;
        set
        {
            if (_setupChecklistDismissed == value)
                return;
            this.RaiseAndSetIfChanged(ref _setupChecklistDismissed, value);
            Save();
        }
    }

    public double PitchDeadzone
    {
        get => _pitchDeadzone;
        set
        {
            var next = Clamp01(value);
            if (_pitchDeadzone.Equals(next))
                return;
            this.RaiseAndSetIfChanged(ref _pitchDeadzone, next);
            Save();
        }
    }

    public double RollDeadzone
    {
        get => _rollDeadzone;
        set
        {
            var next = Clamp01(value);
            if (_rollDeadzone.Equals(next))
                return;
            this.RaiseAndSetIfChanged(ref _rollDeadzone, next);
            Save();
        }
    }

    public double PitchSensitivity
    {
        get => _pitchSensitivity;
        set
        {
            var next = Clamp(value, 0.25, 2.0);
            if (_pitchSensitivity.Equals(next))
                return;
            this.RaiseAndSetIfChanged(ref _pitchSensitivity, next);
            Save();
        }
    }

    public double RollSensitivity
    {
        get => _rollSensitivity;
        set
        {
            var next = Clamp(value, 0.25, 2.0);
            if (_rollSensitivity.Equals(next))
                return;
            this.RaiseAndSetIfChanged(ref _rollSensitivity, next);
            Save();
        }
    }

    public double PitchExpo
    {
        get => _pitchExpo;
        set
        {
            var next = Clamp01(value);
            if (_pitchExpo.Equals(next))
                return;
            this.RaiseAndSetIfChanged(ref _pitchExpo, next);
            Save();
        }
    }

    public double RollExpo
    {
        get => _rollExpo;
        set
        {
            var next = Clamp01(value);
            if (_rollExpo.Equals(next))
                return;
            this.RaiseAndSetIfChanged(ref _rollExpo, next);
            Save();
        }
    }

    public bool InvertPitch
    {
        get => _invertPitch;
        set
        {
            if (_invertPitch == value)
                return;
            this.RaiseAndSetIfChanged(ref _invertPitch, value);
            Save();
        }
    }

    public bool InvertRoll
    {
        get => _invertRoll;
        set
        {
            if (_invertRoll == value)
                return;
            this.RaiseAndSetIfChanged(ref _invertRoll, value);
            Save();
        }
    }

    public int ControllerIndex
    {
        get => _controllerIndex;
        set
        {
            var next = Clamp(value, 0, 7);
            if (_controllerIndex == next)
                return;
            this.RaiseAndSetIfChanged(ref _controllerIndex, next);
            Save();
        }
    }

    public int TakeoverButtonIndex
    {
        get => _takeoverButtonIndex;
        set
        {
            var next = Clamp(value, -1, 31);
            if (_takeoverButtonIndex == next)
                return;
            this.RaiseAndSetIfChanged(ref _takeoverButtonIndex, next);
            Save();
        }
    }

    public bool DynamicAuthorityEnabled
    {
        get => _dynamicAuthorityEnabled;
        set
        {
            if (_dynamicAuthorityEnabled == value)
                return;
            this.RaiseAndSetIfChanged(ref _dynamicAuthorityEnabled, value);
            Save();
        }
    }

    private void Save() => LocalStateStorage.Save(SettingsFileName, new PersistedSettings(
        SettingsVersion: CurrentSettingsVersion,
        Enabled: _enabled,
        ConsentAccepted: _consentAccepted,
        SetupChecklistDismissed: _setupChecklistDismissed,
        Deadzone: _pitchDeadzone,
        PitchDeadzone: _pitchDeadzone,
        RollDeadzone: _rollDeadzone,
        PitchSensitivity: _pitchSensitivity,
        RollSensitivity: _rollSensitivity,
        PitchExpo: _pitchExpo,
        RollExpo: _rollExpo,
        InvertPitch: _invertPitch,
        InvertRoll: _invertRoll,
        ControllerIndex: _controllerIndex,
        TakeoverButtonIndex: _takeoverButtonIndex,
        DynamicAuthorityEnabled: _dynamicAuthorityEnabled));

    private static double Clamp01(double value) => Clamp(value, 0.0, 1.0);

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    private readonly record struct PersistedSettings(
        int SettingsVersion,
        bool Enabled,
        bool ConsentAccepted,
        bool SetupChecklistDismissed,
        double Deadzone,
        double PitchDeadzone,
        double RollDeadzone,
        double PitchSensitivity,
        double RollSensitivity,
        double PitchExpo,
        double RollExpo,
        bool InvertPitch,
        bool InvertRoll,
        int ControllerIndex,
        int TakeoverButtonIndex,
        bool DynamicAuthorityEnabled)
    {
        public static PersistedSettings Default => new(
            SettingsVersion: CurrentSettingsVersion,
            Enabled: false,
            ConsentAccepted: false,
            SetupChecklistDismissed: false,
            Deadzone: DefaultDeadzone,
            PitchDeadzone: DefaultDeadzone,
            RollDeadzone: DefaultDeadzone,
            PitchSensitivity: DefaultSensitivity,
            RollSensitivity: DefaultSensitivity,
            PitchExpo: DefaultExpo,
            RollExpo: DefaultExpo,
            InvertPitch: false,
            InvertRoll: false,
            ControllerIndex: 0,
            TakeoverButtonIndex: -1,
            DynamicAuthorityEnabled: false);
    }
}
