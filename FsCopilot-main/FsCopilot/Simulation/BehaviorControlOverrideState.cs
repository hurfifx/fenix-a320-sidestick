namespace FsCopilot.Simulation;

using ReactiveUI;

public sealed class BehaviorControlOverrideState : ReactiveObject
{
    private bool _useCapturedControl;

    public bool UseCapturedControl
    {
        get => _useCapturedControl;
        set => this.RaiseAndSetIfChanged(ref _useCapturedControl, value);
    }
}
