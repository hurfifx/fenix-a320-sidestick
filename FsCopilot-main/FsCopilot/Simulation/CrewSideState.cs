namespace FsCopilot.Simulation;

using ReactiveUI;

public enum CrewSide : byte
{
    Captain = 0,
    FirstOfficer = 1
}

public sealed class CrewSideState : ReactiveObject
{
    private const string SettingsFileName = "crew-side.json";
    private CrewSide _selectedSide;

    public CrewSideState()
    {
        var settings = LocalStateStorage.Load(SettingsFileName, new CrewSideSettings(CrewSide.Captain));
        _selectedSide = settings.SelectedSide;
    }

    public CrewSide SelectedSide
    {
        get => _selectedSide;
        set
        {
            if (EqualityComparer<CrewSide>.Default.Equals(_selectedSide, value))
                return;

            this.RaiseAndSetIfChanged(ref _selectedSide, value);
            LocalStateStorage.Save(SettingsFileName, new CrewSideSettings(value));
        }
    }

    private readonly record struct CrewSideSettings(CrewSide SelectedSide);
}
