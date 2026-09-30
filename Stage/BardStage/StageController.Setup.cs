namespace BardStage;

public sealed record EnsembleSetupState(int Members, bool Leader, bool LocalControl, bool RemoteControl,
    bool ReceiveSongs, bool FollowReady, bool AutoAssign, string? Issue);
public enum EnsembleSetupOption { LocalControl, RemoteControl, ReceiveSongs, FollowReady }

public sealed partial class StageController
{
    public Func<EnsembleSetupState>? EnsembleSetup { get; set; }
    public Action<EnsembleSetupOption,bool>? SetEnsembleSetup { get; set; }
    public Action? OpenEnsemblePanel { get; set; }
}
