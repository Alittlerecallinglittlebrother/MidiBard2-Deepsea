using System.Collections.Generic;

public enum PlayMode
{
    Single,
    SingleRepeat,
    ListOrdered,
    ListRepeat,
    Random
}

public enum GuitarToneMode
{
    Off,
    Standard,
    Simple,
    OverrideByTrack,
    ProgramElectricGuitarMode,
    //OverrideByChannel,
}

public class TrackStatus
{
    public bool Enabled = false;
    public int Tone = 0;
    public int Transpose = 0;
}

//public struct ChannelStatus
//{
//    public ChannelStatus(bool enabled = true, int tone = 0, int transpose = 0)
//    {
//        Enabled = enabled;
//        Tone = tone;
//        Transpose = transpose;
//    }

//    public bool Enabled = true;
//    public int Tone = 0;
//    public int Transpose = 0;
//}

public class EnsembleMember
{
    public ulong Cid;
    public string Name;
}

public class EnsembleMemberConfig
{
    public ulong Cid;
    public string Name;
    public string TrackAssignmentRegex;
    public List<EnsembleMember> LinkedEnsembleMembers { get; set; } = new();
}

public enum ChatType
{
    Current = 0,
    Say = 1,
    Party = 2,
}

public enum AntiStackType
{
    Off = 0,
    KeepFirstNote = 1,
    KeepShortestNote = 2,
    KeepLongestNote = 3,
}

public enum FilterPlayedSongOptions
{
    ShowAll = 0,
    ShowPlayed = 1,
    ShowUnPlayed = 2,
}

public enum CompensationModes
{
    None = 0,
    ByInstrument = 1,
    ByInstrumentNote = 2,
}
