using System.Security.Cryptography;
using System.Text;

namespace BardStage.Core.Rooms;

public sealed record LargeMember(ulong Cid, string Name, uint World, int Group);
public sealed record LargeContext(ulong SelfCid, LargeMember[] Members, uint Territory, uint World, string? Issue = null)
{
    public string Key => RosterKey(Members);
    public static string RosterKey(IEnumerable<LargeMember> members) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(",", members.Select(m => m.Cid).Order()))));
    public string? Validate() => Members is { Length: > LargePlan.MaxPlayers } ? "独立合奏最多 8 人，不支持超过 8 人的团队"
        : Issue ?? (SelfCid == 0 || Territory == 0 || World == 0 ? "等待角色进入场景"
        : Members is not { Length: >= 2 and <= LargePlan.MaxPlayers } ? "独立合奏需要 2～8 人；请将团队总人数控制在 8 人以内"
        : Members.Any(m => m == null || m.Cid == 0) || Members.Select(m => m.Cid).Distinct().Count() != Members.Length
            || !Members.Any(m => m.Cid == SelfCid) ? "团队名单不完整，请等待角色信息同步" : null);
}

public sealed record LargeTrack(int Index, string Name, uint Instrument, int Transpose, ulong PerformerCid, bool Enabled);
public sealed record LargeDraft(string Path, string Hash, LargeTrack[] Tracks);
public sealed record LargePlan(Guid Id, string Roster, uint Territory, uint World, RoomSongPlan Song)
{
    public const int MaxPlayers = 8;
    public const int CurrentCompensationVersion = 1;
    public const int CurrentTimingVersion = 1;
    public int TimingVersion { get; init; }
    // Zero is also the wire default for plans from older hosts.
    public int CompensationVersion { get; init; }
    // Set only by local IPC: the whole team is retained even when a subset plays.
    public ulong[]? LocalTeam { get; init; }
    public void Validate()
    {
        if (Song?.Members is { Length: > MaxPlayers } || LocalTeam is { Length: > MaxPlayers })
            throw new InvalidDataException("独立合奏最多 8 人，拒绝超过 8 人的旧演奏计划");
        if (Id == Guid.Empty || Song == null || Id != Song.Id || Roster is not { Length: 64 }
            || !Roster.All(Uri.IsHexDigit) || Territory == 0 || World == 0)
            throw new InvalidDataException("独立合奏歌曲或场景信息无效");
        Song.Validate(MaxPlayers);
        if (TimingVersion is < 0 or > CurrentTimingVersion) throw new InvalidDataException("不支持此起播调度版本，请全员更新插件");
        if (CompensationVersion is < 0 or > CurrentCompensationVersion)
            throw new InvalidDataException("不支持此起音补偿版本，请全员更新插件");
        if (LocalTeam != null && (LocalTeam.Length is < 2 or > MaxPlayers || LocalTeam.Any(c => c == 0)
            || LocalTeam.Distinct().Count() != LocalTeam.Length || Song.Members.Any(c => !LocalTeam.Contains(c))))
            throw new InvalidDataException("本机合奏成员不属于当前团队");
        if (Song.PartyId != 0 || Roster != LargeContext.RosterKey((LocalTeam ?? Song.Members).Select(c => new LargeMember(c, "", 0, 0))))
            throw new InvalidDataException("独立合奏名单指纹不一致");
    }
    public void ValidateContext(LargeContext context, ulong captain)
    {
        Validate();
        if (context.Validate() is { } issue) throw new InvalidOperationException(issue);
        if (Song.LeaderCid != captain || context.Key != Roster || context.Territory != Territory || context.World != World
            || !(LocalTeam ?? Song.Members).ToHashSet().SetEquals(context.Members.Select(m => m.Cid))
            || !Song.Members.Contains(context.SelfCid))
            throw new InvalidOperationException("团队、总指挥或场景已改变，请重新下发");
    }
}

public enum LargePhase { Idle, Loading, Ready, Arming, Committed, Playing, Finished, Stopped }
public sealed record LargeReport(ulong Cid, Guid Session, bool Enabled, string Roster, uint Territory, uint World,
    Guid PlanId, Guid ScheduleId, bool Ready, bool Armed, bool Playing, bool Finished, bool ClockReady,
    double Rtt, double Jitter, double Drift, string Issue, int CompensationVersion = 0,
    int TimingVersion = 0, double? StartLateMs = null, PlaybackTimingDiagnostics? Output = null);

// Each frame is a response to a recent, unique poll on the pinned TLS connection.
public sealed class LargeEnvelope
{
    public Guid Poll { get; set; }
    public double Sent { get; set; }
    public double HostReceive { get; set; }
    public double HostSend { get; set; }
    public LargeReport? Report { get; set; }
    public LargeReport[] Reports { get; set; } = [];
    public ulong Captain { get; set; }
    public string Challenge { get; set; } = "";
    public bool Verified { get; set; }
    public Guid Session { get; set; }
    public long Revision { get; set; }
    public LargePhase Phase { get; set; }
    public LargePlan? Plan { get; set; }
    public Guid Schedule { get; set; }
    public double StartAt { get; set; }
    public string Message { get; set; } = "";
    public bool Stop { get; set; }
    public Guid StopPlan { get; set; }
    public Dictionary<ulong, Guid>? LocalSessions { get; set; }

    // Enforce the public build's capacity before transport caches or the playback
    // state machine can observe a frame from an older, larger ensemble.
    public void ValidateCapacity()
    {
        if (Reports is not { Length: <= LargePlan.MaxPlayers } || LocalSessions?.Count > LargePlan.MaxPlayers
            || Plan?.Song?.Members is { Length: > LargePlan.MaxPlayers }
            || Plan?.LocalTeam is { Length: > LargePlan.MaxPlayers })
            throw new InvalidDataException("独立合奏最多 8 人，拒绝超过 8 人的演奏消息");
    }
}

/// <summary>NTP-style estimate using monotonic seconds, independent of Windows wall-clock/time-zone changes.</summary>
public sealed class EnsembleClock
{
    private readonly Queue<(double Offset, double Rtt)> samples = new();
    public double Offset { get; private set; }
    public double Rtt { get; private set; } = double.PositiveInfinity;
    public double Jitter { get; private set; } = double.PositiveInfinity;
    public bool Ready => samples.Count >= 5 && Rtt <= .250 && Jitter <= .040;
    public bool Add(double sent, double receive, double send, double now)
    {
        if (!double.IsFinite(sent) || !double.IsFinite(receive) || !double.IsFinite(send) || !double.IsFinite(now)
            || now < sent || send < receive || now - sent > 2) return false;
        var rtt = now - sent - (send - receive);
        if (rtt < 0 || rtt > 1) return false;
        samples.Enqueue(((receive - sent + send - now) / 2, rtt));
        while (samples.Count > 16) samples.Dequeue();
        var best = samples.OrderBy(s => s.Rtt).Take(5).ToArray();
        Offset = best.Select(s => s.Offset).Order().ElementAt(best.Length / 2);
        Rtt = best.Max(s => s.Rtt);
        Jitter = best.Max(s => s.Offset) - best.Min(s => s.Offset);
        return true;
    }
    public void Reset() { samples.Clear(); Offset = 0; Rtt = Jitter = double.PositiveInfinity; }
}
