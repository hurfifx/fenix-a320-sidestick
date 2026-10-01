namespace FsCopilot.Simulation;

using ReactiveUI;

public sealed class FenixSidestickSessionState : ReactiveObject
{
    private bool _localReady;
    private bool _remoteReady;
    private bool _localDynamicAuthorityEnabled;
    private bool _remoteDynamicAuthorityEnabled;

    public bool LocalReady
    {
        get => _localReady;
        set => SetField(ref _localReady, value);
    }

    public bool RemoteReady
    {
        get => _remoteReady;
        set => SetField(ref _remoteReady, value);
    }

    public bool LocalDynamicAuthorityEnabled
    {
        get => _localDynamicAuthorityEnabled;
        set => SetField(ref _localDynamicAuthorityEnabled, value);
    }

    public bool RemoteDynamicAuthorityEnabled
    {
        get => _remoteDynamicAuthorityEnabled;
        set => SetField(ref _remoteDynamicAuthorityEnabled, value);
    }

    public bool SessionActive => _localReady && _remoteReady;

    public bool DynamicAuthorityActive =>
        SessionActive && _localDynamicAuthorityEnabled && _remoteDynamicAuthorityEnabled;

    public void ResetRemote()
    {
        RemoteReady = false;
        RemoteDynamicAuthorityEnabled = false;
    }

    private void SetField(ref bool field, bool value)
    {
        if (field == value)
            return;

        this.RaiseAndSetIfChanged(ref field, value);
        this.RaisePropertyChanged(nameof(SessionActive));
        this.RaisePropertyChanged(nameof(DynamicAuthorityActive));
    }
}
