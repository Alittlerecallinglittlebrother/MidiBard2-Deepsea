using System.Diagnostics;
using System.Runtime.Versioning;
using BardStage.Core.Rooms;

namespace BardStage;

/// <summary>Discovery and group membership for at most eight players; playback uses the shared state machine.</summary>
[SupportedOSPlatform("windows")]
public sealed class LocalEnsembleSession : IDisposable
{
    private readonly LocalEnsembleBus bus;
    private readonly Dictionary<Guid, ulong> selected = [];
    private LocalEnsembleSnapshot[] discovered = [];
    private LargeContext context = new(0, [], 0, 0);
    private bool enabled;
    private Guid hostNode;
    private double nextRead, nextWrite;
    public LocalEnsembleSession(string songDirectory, string? busName = null)
    { bus = new(busName); Songs = new(songDirectory); }
    public LocalSongStore Songs { get; }
    public Guid Node => bus.Node;
    public Guid Group { get; private set; }
    public bool IsHost { get; private set; }
    public bool Connected => Group != Guid.Empty;
    public LocalEnsembleSnapshot[] Discovered => discovered;
    public LocalEnsembleTarget[] Targets => IsHost ? selected.Select(p => new LocalEnsembleTarget(p.Key,p.Value)).ToArray()
        : HostSnapshot?.Targets ?? [];
    public LargeMember[] Participants => context.Members.Where(m => Targets.Any(t => t.Cid == m.Cid)).ToArray();
    private LocalEnsembleSnapshot? HostSnapshot => discovered.FirstOrDefault(p => p.Node == hostNode && p.Host && p.Group == Group);
    public LargeEnvelope? Incoming => !IsHost ? HostSnapshot?.Frame : null;
    public LocalEnsembleSnapshot[] MemberStates => discovered.Where(p => p.Node != Node && p.Enabled && p.Group == Group
        && selected.TryGetValue(p.Node, out var cid) && p.Context.SelfCid == cid && p.Report?.Cid == cid).ToArray();
    public string? CandidateIssue(LocalEnsembleSnapshot p) => !p.Enabled ? "未开启同机模式"
        : p.Context.Key != context.Key ? "不在同一团队"
        : p.Context.World != context.World || p.Context.Territory != context.Territory ? "不在同一世界或场景"
        : p.Context.Validate() is { } issue ? issue
        : p.Group != Guid.Empty && p.Group != Group ? "已被其他本机主控管理" : null;
    public void Refresh(LargeContext current, bool active)
    {
        if (context.SelfCid != current.SelfCid || context.Key != current.Key || context.World != current.World || context.Territory != current.Territory)
            Release();
        context = current; enabled = active;
        var now = LocalEnsembleBus.Now;
        if (now >= nextRead) { discovered = bus.Read() ?? discovered; nextRead = now + .05; }
        discovered = discovered.Where(p => now - p.At is >= -.1 and <= 1.5).ToArray();
        if (!enabled || context.Validate() != null) { Release(); return; }
        if (IsHost) return;
        bool Accept(LocalEnsembleSnapshot p) => p.Node != Node && p.Host && p.Enabled && p.Group != Guid.Empty
            && p.Context.Key == context.Key && p.Context.World == context.World && p.Context.Territory == context.Territory
            && p.Context.Validate() == null && p.Targets is { Length: >= 2 and <= LargePlan.MaxPlayers }
            && p.Targets.All(t => t != null && t.Node != Guid.Empty && t.Cid != 0)
            && p.Targets.Select(t => t.Node).Distinct().Count() == p.Targets.Length
            && (p.Frame == null || p.Frame.Reports is { Length: <= LargePlan.MaxPlayers }
                && !(p.Frame.LocalSessions?.Count > LargePlan.MaxPlayers)
                && p.Frame.Plan?.Song?.Members is not { Length: > LargePlan.MaxPlayers }
                && p.Frame.Plan?.LocalTeam is not { Length: > LargePlan.MaxPlayers })
            && p.Targets.Any(t => t.Node == p.Node && t.Cid == p.Context.SelfCid)
            && p.Targets.All(t => context.Members.Any(m => m.Cid == t.Cid))
            && p.Targets.Any(t => t.Node == Node && t.Cid == context.SelfCid)
            && p.Targets.Select(t => t.Cid).Distinct().Count() == p.Targets.Length;
        var host = discovered.FirstOrDefault(p => p.Node == hostNode && p.Group == Group && Accept(p))
            ?? discovered.Where(Accept).OrderBy(p => p.Group).FirstOrDefault();
        Group = host?.Group ?? Guid.Empty; hostNode = host?.Node ?? Guid.Empty;
    }
    public void BecomeHost()
    {
        if (!enabled) throw new InvalidOperationException("请先开启同机模式");
        if (Connected && !IsHost) throw new InvalidOperationException("已由本机主控管理，请先由原主控释放本角色");
        if (context.Validate() is { } issue) throw new InvalidOperationException(issue);
        if (IsHost) return;
        IsHost = true; Group = Guid.NewGuid(); hostNode = Node;
        selected.Clear(); selected[Node] = context.SelfCid;
    }
    public void Select(Guid node, bool value)
    {
        if (!IsHost) throw new InvalidOperationException("请先设为本机主控");
        if (node == Node) return;
        if (!value) { selected.Remove(node); return; }
        var peer = discovered.SingleOrDefault(p => p.Node == node) ?? throw new InvalidOperationException("此角色已离线");
        if (CandidateIssue(peer) is { } issue) throw new InvalidOperationException(issue);
        if (selected.Any(p => p.Key != node && p.Value == peer.Context.SelfCid)) throw new InvalidOperationException("同一个角色出现多个实例，请关闭重复实例");
        if (!selected.ContainsKey(node) && selected.Count >= LargePlan.MaxPlayers)
            throw new InvalidOperationException("同机独立合奏最多 8 人");
        selected[node] = peer.Context.SelfCid;
    }
    public void Publish(LargeReport report, LargeEnvelope frame)
    {
        new LocalEnsembleSnapshot(Node, Environment.ProcessId, 0, Stopwatch.Frequency,
            context, enabled, Group, IsHost, Targets, report, frame).ValidateCapacity();
        var now = LocalEnsembleBus.Now;
        if (now < nextWrite) return;
        if (bus.Write(new(Node, Environment.ProcessId, now, Stopwatch.Frequency,
            context, enabled, Group, IsHost, Targets, report, frame))) nextWrite = now + .05;
    }
    public void Release() { IsHost = false; Group = hostNode = Guid.Empty; selected.Clear(); nextWrite = 0; }
    public void Dispose() { Release(); bus.Dispose(); }
}
