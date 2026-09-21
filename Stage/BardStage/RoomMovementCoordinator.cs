using System.Diagnostics;
using BardStage.Core;
using BardStage.Core.Rooms;

namespace BardStage;

public interface IRoomMovementBackend : IMovementDriver, IDisposable
{
    MovementSnapshot Capture();
    string? Enable();
    void Disable();
}
/// <summary>Game-thread-only movement coordination. TLS carries plans and short renewable leases.</summary>
public sealed class RoomMovementCoordinator : IDisposable
{
    private readonly StageRoom room;
    private readonly IRoomMovementBackend backend;
    private readonly Func<RoomPeer, ulong> identity;
    private readonly Func<double> clock;
    private readonly MovementRunner runner;
    private readonly Dictionary<RoomPeer, (MovementReport Report, double At)> remote = [];
    private readonly HashSet<Guid> seenCommands = [];
    private readonly Queue<Guid> commandOrder = [];
    private MovementPlan? active;
    private MovementSnapshot snapshot = new(0, 0, 0, new(0, 0), []);
    private Guid observedRoom, poll, pendingCommand;
    private Guid receiverSession = Guid.NewGuid();
    private long observedParty;
    private ulong observedLeader, observedSelf;
    private MovementScene? observedScene;
    private string observedMembers = "";
    private int generation;
    private double nextPoll, pollAt, leaseUntil, submittedAt;
    private bool enabled, disposed;
    private MovementReport[] reports = [];
    public FormationStore Store { get; }
    public string Status { get; private set; } = "移动接收默认关闭";
    public MovementSnapshot Snapshot => snapshot;
    public IReadOnlyList<MovementReport> Reports => reports.Where(r => r.Cid != snapshot.SelfCid).Append(LocalReport()).ToArray();
    public bool Enabled => enabled;
    public bool IsLeader => snapshot.SelfCid != 0 && snapshot.SelfCid == snapshot.LeaderCid;
    public bool Running => runner.Running;
    public bool Busy => active != null || runner.Running || pendingCommand != Guid.Empty;
    public bool HasRoom => room.IsCaptain || room.IsViewer && room.Connected;
    public string? ControlIssue => !HasRoom ? "请创建或加入同一演出房间"
        : room.IsRemote && room.Client?.Snapshot?.MovementSupported != true ? "房主尚未支持移动，请全队升级新版"
        : !IsLeader ? "由当前游戏小队队长下发移动指令"
        : !enabled ? "请开启本机移动接收"
        : snapshot.BlockReason ?? (snapshot.Members.Length < 2 ? "需要至少两人的小队" : null);

    public RoomMovementCoordinator(StageRoom room, IRoomMovementBackend backend, Func<RoomPeer, ulong> identity,
        string directory, Func<double>? clock = null)
    {
        this.room = room; this.backend = backend; this.identity = identity;
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        runner = new(backend); Store = new(directory);
    }
    public void SetEnabled(bool value)
    {
        if (disposed || enabled == value) return;
        if (value)
        {
            var issue = backend.Enable();
            if (issue != null) { Status = issue; return; }
            receiverSession = Guid.NewGuid(); enabled = true; Status = "已开启，等待队长指令";
        }
        else
        {
            StopLocal(); Status = "移动接收已关闭";
        }
        nextPoll = 0;
    }
    public void StopLocal()
    {
        runner.Stop("本机已停止");
        if (IsLeader) StopAll();
        else Status = "本机已停止并关闭移动接收";
        enabled = false; receiverSession = Guid.NewGuid(); backend.Disable();
        nextPoll = 0;
    }
    public void Follow()
    {
        Refresh();
        Submit(new(Guid.NewGuid(), snapshot.PartyId, snapshot.LeaderCid, snapshot.Scene,
            snapshot.Members.Select(m => m.Cid).ToArray(), MovementAction.Follow,
            snapshot.Members.Where(m => m.Cid != snapshot.LeaderCid)
                .Select(m => new MovementTarget(m.Cid, m.Position, m.Facing)).ToArray(), receiverSession));
    }
    public void Apply(FormationPreset preset)
    {
        Refresh(); preset.Validate();
        var anchor = snapshot.Leader ?? throw new InvalidOperationException("当前场景中看不到队长");
        var targets = preset.Slots.Where(s => snapshot.Members.Any(m => m.Cid == s.Cid))
            .Select(s => FormationPreset.ToWorld(s, anchor)).ToArray();
        if (preset.Slots.Any(s => !snapshot.Members.Any(m => m.Cid == s.Cid)))
            throw new InvalidOperationException("队形中有人不在当前小队，请重新绑定或从当前小队新建队形");
        Submit(new(Guid.NewGuid(), snapshot.PartyId, snapshot.LeaderCid, snapshot.Scene,
            snapshot.Members.Select(m => m.Cid).ToArray(), MovementAction.Formation, targets, receiverSession));
    }
    public void StopAll()
    {
        Refresh();
        receiverSession = Guid.NewGuid();
        runner.Stop("全员停止"); active = null;
        if (!IsLeader || !HasRoom || room.IsRemote && room.Client?.Snapshot?.MovementSupported != true)
        { Status = "本机已停止"; return; }
        var stop = new MovementPlan(Guid.NewGuid(), snapshot.PartyId, snapshot.LeaderCid, snapshot.Scene,
            snapshot.Members.Select(m => m.Cid).ToArray(), MovementAction.Stop, []);
        if (room.IsCaptain) { Status = "已停止全员移动"; return; }
        room.Client?.SendMovement("movementSubmit", new() { Plan = stop });
        pendingCommand = stop.Id; submittedAt = clock(); Status = "正在通知全员停止";
    }
    public FormationPreset CaptureFormation(string name)
    {
        Refresh();
        var anchor = snapshot.Leader ?? throw new InvalidOperationException("当前场景中看不到队长");
        if (snapshot.Members.Any(m => !m.Visible)) throw new InvalidOperationException("请让全队进入同一场景并靠近队长");
        var value = new FormationPreset { Name = name, Slots = snapshot.Members.Select(m => FormationPreset.Capture(m, anchor)).ToList() };
        value.Validate(); return value;
    }
    private void Submit(MovementPlan plan)
    {
        if (ControlIssue is { } reason) throw new InvalidOperationException(reason);
        plan.Validate();
        if (Busy) throw new InvalidOperationException("请先停止当前移动，再下发新指令");
        if (room.IsCaptain) StartOnHost(plan);
        else
        {
            if (room.Client?.SendMovement("movementSubmit", new() { Plan = plan }) != true)
                throw new InvalidOperationException("移动指令未发送");
            pendingCommand = plan.Id; submittedAt = clock(); Status = "正在下发移动指令";
        }
    }
    private void Refresh()
    {
        snapshot = backend.Capture();
        Store.Load(snapshot.SelfCid);
    }
    private MovementReport LocalReport() => new(snapshot.SelfCid, enabled, snapshot.BlockReason ?? "",
        snapshot.Scene, runner.CommandId, runner.State, runner.Detail, receiverSession);

    public void Tick()
    {
        if (disposed) return;
        var now = clock();
        Refresh();
        var members = string.Join(",", snapshot.Members.Select(m => m.Cid).Order());
        if (observedRoom != room.Id || generation != room.ConnectionGeneration || observedParty != snapshot.PartyId
            || observedLeader != snapshot.LeaderCid || observedScene != snapshot.Scene
            || observedMembers != members || observedSelf != snapshot.SelfCid)
        {
            if (Busy) { runner.Stop("房间、小队或场景已改变"); Status = runner.Detail; }
            active = null; pendingCommand = poll = Guid.Empty; remote.Clear(); reports = []; leaseUntil = 0; nextPoll = 0;
            receiverSession = Guid.NewGuid();
            if (observedSelf != 0 && observedSelf != snapshot.SelfCid) { enabled = false; backend.Disable(); }
            observedRoom = room.Id; generation = room.ConnectionGeneration; observedParty = snapshot.PartyId;
            observedLeader = snapshot.LeaderCid; observedScene = snapshot.Scene; observedMembers = members; observedSelf = snapshot.SelfCid;
        }
        if (!enabled || !HasRoom)
        {
            if (runner.Running) { runner.Stop(!enabled ? "移动接收已关闭" : "房间连接已断开"); Status = runner.Detail; }
            if (!HasRoom) { active = null; pendingCommand = Guid.Empty; }
        }
        if (room.IsCaptain) TickHost(now);
        else if (room.IsViewer && room.Connected && room.Client?.Snapshot?.MovementSupported == true) TickClient(now);
        if (active != null && enabled)
        {
            if (active.Targets.FirstOrDefault(t => t.Cid == snapshot.SelfCid) is { } own && own.Session != receiverSession)
            {
                runner.Stop("移动接收会话已改变，请重新下发"); active = null; Status = runner.Detail; return;
            }
            runner.Accept(active, snapshot, now);
            runner.Tick(snapshot, now, room.IsCaptain || now < leaseUntil);
            if (runner.State == "已停止") Status = runner.Detail;
        }
        if (pendingCommand != Guid.Empty && now - submittedAt > 3)
        {
            pendingCommand = Guid.Empty; receiverSession = Guid.NewGuid(); active = null;
            runner.Stop("指令回执超时"); nextPoll = 0;
            if (IsLeader && room.IsViewer && room.Connected)
                room.Client?.SendMovement("movementSubmit", new() { Plan = new(Guid.NewGuid(), snapshot.PartyId,
                    snapshot.LeaderCid, snapshot.Scene, snapshot.Members.Select(m => m.Cid).ToArray(), MovementAction.Stop, []) });
            Status = "指令回执超时，已取消，请重新操作";
        }
    }
    private void TickHost(double now)
    {
        var server = room.Server!;
        foreach (var (peer, value) in remote.ToArray())
            if (!peer.IsAlive || identity(peer) != value.Report.Cid || now - value.At > 3) remote.Remove(peer);
        while (server.TryMovement(out var entry))
        {
            var cid = identity(entry.Peer);
            if (cid == 0 || !snapshot.Members.Any(m => m.Cid == cid)) continue;
            var envelope = entry.Packet.Movement!;
            if (entry.Packet.Type == "movementPoll" && envelope.PollId != Guid.Empty && envelope.Report is { } report
                && report.Session != Guid.Empty
                && report.Cid == cid && report.Issue is { Length: <= 300 } && report.Detail is { Length: <= 300 }
                && report.State is { Length: <= 30 })
            {
                remote[entry.Peer] = (report, now);
                // Reply only to clients which advertised movement support; old versions are never sent new packets.
                entry.Peer.TrySend(new() { Type = "movementFrame", RoomId = room.Id,
                    Movement = new() { PollId = envelope.PollId, Plan = active,
                        Reports = AllReports() } });
            }
            else if (entry.Packet.Type == "movementSubmit" && envelope.Plan is { } plan)
            {
                string? error = null;
                try
                {
                    if (cid != snapshot.LeaderCid) throw new InvalidOperationException("只有当前游戏队长可以下发移动指令");
                    StartOnHost(plan);
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { error = ex.Message; }
                entry.Peer.TrySend(new() { Type = "movementFrame", RoomId = room.Id,
                    Movement = new() { ResultId = plan.Id, Error = error } });
            }
        }
        reports = AllReports();
        if (active is { } current)
        {
            var leader = reports.FirstOrDefault(p => p.Cid == snapshot.LeaderCid);
            var participants = current.Targets.Select(t => reports.FirstOrDefault(p => p.Cid == t.Cid)).ToArray();
            var failure = leader == null || !leader.Enabled || leader.Issue != "" || leader.Scene != current.Scene
                || leader.Session != current.LeaderSession
                || participants.Any(p => p == null || !p.Enabled || p.Issue != "" || p.Scene != current.Scene
                    || p.CommandId == current.Id && p.State == "已停止")
                || current.Targets.Any(t => reports.FirstOrDefault(p => p.Cid == t.Cid)?.Session != t.Session)
                || !current.Matches(snapshot.PartyId, snapshot.LeaderCid, snapshot.Scene, snapshot.Members.Select(m => m.Cid))
                || snapshot.Leader == null;
            if (failure) { active = null; runner.Stop("有成员停止、失联或状态不符"); Status = runner.Detail; }
            else if (current.Action == MovementAction.Formation && participants.All(p => p!.CommandId == current.Id && p.State == "已到位"))
            { active = null; Status = "全员已到位"; }
        }
    }
    private MovementReport[] AllReports() => remote.Where(v => v.Key.IsAlive && identity(v.Key) == v.Value.Report.Cid
        && clock() - v.Value.At < 3).Select(v => v.Value.Report).Append(LocalReport())
        .Where(r => snapshot.Members.Any(m => m.Cid == r.Cid)).GroupBy(r => r.Cid).Select(g => g.Last()).ToArray();
    private void StartOnHost(MovementPlan plan)
    {
        plan.Validate();
        if (!seenCommands.Add(plan.Id)) throw new InvalidOperationException("这条移动指令已处理，请重新下发");
        commandOrder.Enqueue(plan.Id);
        while (commandOrder.Count > 128) seenCommands.Remove(commandOrder.Dequeue());
        if (!plan.Matches(snapshot.PartyId, snapshot.LeaderCid, snapshot.Scene, snapshot.Members.Select(m => m.Cid)))
            throw new InvalidOperationException("小队或场景已改变，请重新下发");
        if (plan.Action == MovementAction.Stop) { active = null; runner.Stop("队长已停止全员移动"); Status = runner.Detail; return; }
        if (active != null) throw new InvalidOperationException("请先停止当前移动");
        if (snapshot.Leader == null) throw new InvalidOperationException("当前场景中看不到队长");
        var required = plan.Targets.Select(t => t.Cid).Append(plan.LeaderCid).Distinct().ToArray();
        var all = AllReports();
        if (plan.LeaderSession == Guid.Empty || all.FirstOrDefault(p => p.Cid == plan.LeaderCid)?.Session != plan.LeaderSession)
            throw new InvalidOperationException("队长移动会话已改变，请重新操作");
        foreach (var cid in required)
        {
            var member = snapshot.Members.FirstOrDefault(m => m.Cid == cid && m.Visible);
            var status = all.FirstOrDefault(p => p.Cid == cid);
            if (member == null || status == null || !status.Enabled || status.Issue != "" || status.Scene != plan.Scene)
                throw new InvalidOperationException($"{snapshot.Members.FirstOrDefault(m => m.Cid == cid)?.Name ?? cid.ToString()}：请升级新版、连接房间、开启移动接收并进入同一场景");
            if (member.Position.DistanceXZ(snapshot.Leader!.Position) > 40)
                throw new InvalidOperationException("有成员离队长过远，请先靠近");
        }
        if (plan.Action == MovementAction.Formation && plan.Targets.Any(t => t.Position.DistanceXZ(snapshot.Leader!.Position) > 30
            || Math.Abs(t.Position.Y - snapshot.Leader.Position.Y) > 3))
            throw new InvalidOperationException("队形目标过远或高度不符");
        active = plan with { Targets = plan.Targets.Select(t => t with { Session = all.Single(p => p.Cid == t.Cid).Session }).ToArray() };
        Status = plan.Action == MovementAction.Follow ? "跟随指令已下发" : "队形已下发，等待全员到位";
    }
    private void TickClient(double now)
    {
        var client = room.Client!;
        while (client.TryMovement(out var value))
        {
            if (value.ResultId != Guid.Empty && value.ResultId == pendingCommand)
            { pendingCommand = Guid.Empty; Status = value.Error ?? "指令已接收"; }
            if (value.PollId == Guid.Empty || value.PollId != poll || now - pollAt > 2) continue;
            poll = Guid.Empty; leaseUntil = now + 2;
            reports = value.Reports ?? [];
            if (value.Plan == null)
            {
                active = null;
                if (runner.Running) { runner.Stop("队长已停止移动"); Status = runner.Detail; }
            }
            else
            {
                try
                {
                    value.Plan.Validate();
                    if (!value.Plan.Matches(snapshot.PartyId, snapshot.LeaderCid, snapshot.Scene, snapshot.Members.Select(m => m.Cid)))
                        throw new InvalidDataException("指令属于旧小队或其他场景");
                    active = value.Plan;
                }
                catch (InvalidDataException ex) { active = null; runner.Stop(ex.Message); Status = ex.Message; }
            }
        }
        if (now >= nextPoll && (poll == Guid.Empty || now - pollAt > 2))
        {
            poll = Guid.NewGuid(); pollAt = now; nextPoll = now + 0.5;
            client.SendMovement("movementPoll", new() { PollId = poll, Report = LocalReport() });
        }
        if (runner.Running && now >= leaseUntil) { runner.Stop("房间响应超时"); Status = runner.Detail; }
    }
    public void Dispose()
    {
        if (disposed) return;
        runner.Stop("插件已卸载"); backend.Disable(); backend.Dispose(); disposed = true;
    }
}
