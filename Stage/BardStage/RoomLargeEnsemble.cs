using System.Diagnostics;
using System.Security.Cryptography;
using BardStage.Core;
using BardStage.Core.Rooms;

namespace BardStage;

public interface ILargeEnsembleBackend
{
    LargeContext Capture();
    string? EnableIssue { get; }
    string? PlaybackIssue { get; }
    bool Playing { get; }
    bool Finished { get; }
    bool Draining => false;
    int CompensationVersion => LargePlan.CurrentCompensationVersion;
    bool SupportsScheduledStart => false;
    PlaybackTimingDiagnostics OutputTiming => PlaybackTimingDiagnostics.Empty;
    double? ScheduledStartAt => null;
    void ScheduleStart(double at) => throw new NotSupportedException("本机预约开演需要新版插件");
    void ScheduleStart(double at, Func<bool>? canStart) => ScheduleStart(at);
    void PulseScheduledStart() { }
    double PositionSeconds { get; }
    IReadOnlyList<string> Instruments { get; }
    Task<LargeDraft> InspectAsync(string path, CancellationToken token);
    Task LoadAsync(string path, LargePlan plan, CancellationToken token);
    void Start();
    void Stop();
    void SendProof(Guid room, string challenge);
}

/// <summary>Framework-thread state machine. Native eight-person ready checks are never used here.</summary>
public sealed partial class RoomLargeEnsemble : IDisposable
{
    private readonly StageRoom room;
    private readonly ILargeEnsembleBackend backend;
    private readonly Func<double> clock;
    private readonly Dictionary<RoomPeer, PeerState> peers = [];
    private readonly Dictionary<ulong, Guid> planSessions = [];
    private TransportClockState Sync => room.Client?.Timing ?? TransportClockState.Unavailable;
    private readonly Dictionary<Guid, double> polls = [];
    private CancellationTokenSource operation = new();
    private Task? loading;
    private Task<LargeDraft>? inspecting;
    private LargePlan? plan;
    private Guid session = Guid.NewGuid(), observedRoom, schedule, activeSchedule, rejectedPlan, requestedStopPlan;
    private string contextKey = "", sentChallenge = "", localIssue = "";
    private ulong self, captain;
    private int generation;
    private long revision, acceptedRevision = -1;
    private double nextPoll, lastFrame, startAt, localStart, startedAt, loadDeadline, nextProof;
    private bool disposed, localReady, armed, committed, started, enabled, verified, stopRequested;
    private LargeReport[] reports = [];
    public RoomLargeEnsemble(StageRoom room, ILargeEnsembleBackend backend, Func<double>? clock = null)
    {
        this.room = room; this.backend = backend;
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        room.TimingClock = this.clock;
    }
    public bool Enabled => enabled;
    public bool IsCaptain => LocalMode ? Local?.IsHost == true : room.IsCaptain;
    public bool Busy => Phase is LargePhase.Loading or LargePhase.Arming or LargePhase.Committed or LargePhase.Playing || inspecting != null;
    public LargePhase Phase { get; private set; }
    public LargeContext Context { get; private set; } = new(0, [], 0, 0);
    public LargeDraft? Draft { get; private set; }
    public IReadOnlyList<string> Instruments => backend.Instruments;
    public IReadOnlyList<LargeReport> Reports => reports;
    public bool UseInstrumentCompensation { get; private set; } = true;
    public bool? PublishedInstrumentCompensation => plan == null ? null : plan.CompensationVersion != 0;
    public string Status { get; private set; } = "独立合奏（最多 8 人）默认关闭";
    public string? ControlIssue => !enabled ? "请先开启本机独立合奏模式"
        : LocalMode ? Context.Validate() ?? (!IsCaptain ? "请在一个角色上点击“设为本机主控”" : null)
        : !IsCaptain ? "请由创建房间的总指挥操作"
        : room.Server?.ViewerCapacity != RoomServer.MaxViewers ? "请重新创建最多 8 人的演奏房间"
        : Context.Validate();
    public ulong VerifiedMember(RoomPeer peer) => enabled && peer.IsAlive && peers.TryGetValue(peer, out var p)
        && p.Cid != 0 && Context.Members.Any(m => m.Cid == p.Cid) ? p.Cid : 0;
    public void SetEnabled(bool value)
    {
        if (disposed || value == enabled) return;
        if (value && backend.Capture().Members is { Length: > LargePlan.MaxPlayers })
            throw new InvalidOperationException("独立合奏最多 8 人，不支持超过 8 人的团队");
        if (value && backend.EnableIssue is { } issue) throw new InvalidOperationException(issue);
        Abort(value ? LocalMode ? "同机接收已开启，请在主控选择参与角色" : "独立合奏已开启，请连接同一房间" : "独立合奏已关闭", false);
        enabled = value; session = Guid.NewGuid(); nextPoll = 0; sentChallenge = ""; verified = false;
        stopRequested = false; requestedStopPlan = Guid.Empty;
        if (!value) Local?.Release();
    }
    public void ReceiveProof(Guid roomId, string challenge, ulong cid)
    {
        if (!enabled || !IsCaptain || roomId != room.Id || cid == 0 || !Context.Members.Any(m => m.Cid == cid)) return;
        var now = clock();
        var entry = peers.FirstOrDefault(p => p.Key.IsAlive && p.Value.Challenge == challenge && now < p.Value.ProofUntil);
        if (entry.Key == null || cid == Context.SelfCid || peers.Any(p => p.Key.IsAlive && p.Value.Cid == cid && p.Key != entry.Key)) return;
        entry.Value.Cid = cid;
    }
    public void Inspect(string path)
    {
        RequireControl();
        if (Busy) throw new InvalidOperationException("请先停止当前歌曲");
        Abort("正在读取 MIDI 轨道", false); Draft = null;
        inspecting = backend.InspectAsync(path, operation.Token);
    }
    public void AutoAssign()
    {
        RequireControl();
        if (Busy || Draft == null) throw new InvalidOperationException("请先读取歌曲并停止演奏");
        if (plan != null) Abort("分配已改变，请重新下发", false);
        var order = Participants.OrderBy(m => m.Group).ThenBy(m => m.Cid).ToArray();
        Draft = Draft with { Tracks = Draft.Tracks.Select((t,i) => t with
            { PerformerCid = i < order.Length ? order[i].Cid : 0, Enabled = i < order.Length }).ToArray() };
        Status = Draft.Tracks.Length > order.Length ? "已按团队分配；多出的轨道未启用，请手动检查" : "已按团队分配，请检查轨道与乐器后下发";
    }
    public void Edit(int index, LargeTrack track)
    {
        RequireControl();
        if (Busy || Draft == null || index < 0 || index >= Draft.Tracks.Length) return;
        // Editing invalidates a previously distributed plan, so Start cannot use stale assignments.
        if (plan != null) Abort("分配已改变，请重新下发", false);
        Draft.Tracks[index] = track with { Index = index };
    }
    public void SetInstrumentCompensation(bool value)
    {
        RequireControl();
        if (Busy) throw new InvalidOperationException("请先停止当前演奏，再修改起音补偿");
        if (UseInstrumentCompensation == value) return;
        if (plan != null) Abort("起音补偿已改变，请重新下发", false);
        UseInstrumentCompensation = value;
    }
    public void Distribute()
    {
        RequireControl();
        if (Busy || Draft == null) throw new InvalidOperationException("请先读取歌曲并停止当前演奏");
        if (ReadinessIssue(false) is { } issue) throw new InvalidOperationException(issue);
        var id = Guid.NewGuid();
        var value = new LargePlan(id, Context.Key, Context.Territory, Context.World,
            new(id, Draft.Hash, 0, Context.SelfCid, Participants.Select(m => m.Cid).ToArray(),
                Draft.Tracks.Select(t => new RoomTrackAssignment(t.Index,t.Enabled,t.Instrument,t.Transpose,t.PerformerCid)).ToArray(),
                1, true, 2))
            { TimingVersion = LargePlan.CurrentTimingVersion, CompensationVersion = UseInstrumentCompensation ? LargePlan.CurrentCompensationVersion : 0,
              LocalTeam = LocalMode ? Context.Members.Select(m => m.Cid).ToArray() : null };
        value.ValidateContext(Context, Context.SelfCid);
        Abort("正在下发歌曲与分配", false);
        captain = Context.SelfCid; plan = value; Phase = LargePhase.Loading; revision++;
        foreach (var r in AllReports()) planSessions[r.Cid] = r.Session;
        if (!LocalMode) room.Songs!.Offer(Draft.Path, Draft.Hash);
        BeginLoad(Draft.Path);
    }
    public void Start()
    {
        RequireControl();
        if (Phase != LargePhase.Ready || plan == null) throw new InvalidOperationException("请先下发歌曲并等待全员准备完成");
        plan.ValidateContext(Context, Context.SelfCid);
        if (ReadinessIssue(true) is { } issue) throw new InvalidOperationException(issue);
        schedule = Guid.NewGuid(); startAt = clock() + 5; localStart = startAt;
        activeSchedule = schedule; armed = true; committed = false;
        Phase = LargePhase.Arming; revision++; Status = "正在确认全员预约开演，倒计时 5 秒";
    }
    public void StopAll()
    {
        if (IsCaptain) Abort("总指挥已停止全员演奏", false);
        else { requestedStopPlan = plan?.Id ?? Guid.Empty; Abort("本机已停止，正在通知总指挥", false); stopRequested = true; }
    }
    private void RequireControl()
    {
        Context = backend.Capture();
        if (ControlIssue is { } issue) throw new InvalidOperationException(issue);
    }
    private void BeginLoad(string? path = null)
    {
        if (plan == null) return;
        localReady = false; localIssue = ""; armed = committed = started = false;
        loadDeadline = clock() + 120;
        loading = Load(plan, path, operation.Token);
    }
    private async Task Load(LargePlan value, string? path, CancellationToken token)
    {
        value.Validate();
        if (LocalMode)
            path = path != null ? await Local!.Songs.Publish(path, value.Song.SongHash, token)
                : await Local!.Songs.Receive(value.Song.SongHash, token);
        else path ??= await room.Songs!.ReceiveAsync(value.Song.SongHash, token);
        token.ThrowIfCancellationRequested();
        if (path == null) throw new InvalidOperationException("歌曲未能下载");
        await backend.LoadAsync(path, value, token);
    }
    public void Tick()
    {
        if (disposed) return;
        try { TickCore(); }
        catch (Exception ex) when (LocalMode && ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Abort("本机同步失败，请关闭后重新选择同机模式：" + ex.Message, true);
            enabled = false; Local!.Release();
        }
    }
    private void TickCore()
    {
        var now = clock(); Context = backend.Capture();
        if (Context.Members is { Length: > LargePlan.MaxPlayers })
        {
            Local?.Refresh(Context, false);
            if (plan != null || Busy || started) Abort("独立合奏最多 8 人，已停止超出人数限制的演奏", true);
            else Status = "独立合奏最多 8 人，请将团队总人数控制在 8 人以内";
            reports = [];
            return;
        }
        if (enabled && !LocalMode && room.Client?.Snapshot is { LargeEnsembleSupported: true, PerformerCapacity: > LargePlan.MaxPlayers })
        {
            if (plan != null || Busy || started) Abort("独立合奏最多 8 人，请退出旧的大容量演奏房间", true);
            else Status = "独立合奏最多 8 人，请让主控创建新版 8 人演奏房间";
            reports = [];
            return;
        }
        Local?.Refresh(Context, enabled);
        var key = Context.Key + "/" + Context.Territory + "/" + Context.World;
        var connectionId = LocalMode ? Local!.Group : room.Id;
        var connectionGeneration = LocalMode ? 0 : room.ConnectionGeneration;
        if (observedRoom != connectionId || generation != connectionGeneration || self != Context.SelfCid || contextKey != key)
        {
            Abort(LocalMode ? "本机参与组、角色或场景已更新，请由主控重新下发" : "房间、角色、团队名单或场景已改变，请重新下发", false);
            if (self != 0 && self != Context.SelfCid) enabled = false;
            session = Guid.NewGuid(); peers.Clear(); polls.Clear(); reports = []; verified = false;
            acceptedRevision = -1; sentChallenge = ""; nextPoll = 0;
            stopRequested = false; requestedStopPlan = Guid.Empty; Draft = null;
            observedRoom = connectionId; generation = connectionGeneration; self = Context.SelfCid; contextKey = key;
        }
        if (inspecting?.IsCompleted == true)
        {
            try { Draft = inspecting.GetAwaiter().GetResult(); inspecting = null; AutoAssign(); }
            catch (Exception ex) { inspecting = null; Status = "读取失败：" + ex.Message; }
        }
        if (loading?.IsCompleted == true)
        {
            try { loading.GetAwaiter().GetResult(); localReady = true; Status = "本机已准备，等待全员"; }
            catch (Exception ex) { localIssue = "载入失败：" + ex.Message; Status = localIssue; }
            loading = null;
        }
        if (plan != null && enabled)
        {
            try { plan.ValidateContext(Context, IsCaptain ? Context.SelfCid : captain); }
            catch (Exception ex) { Abort(ex.Message, true); }
            if (plan != null && (loading != null && now > loadDeadline)) Abort("歌曲准备超过 120 秒，请重新下发", true);
            if (plan != null && localReady && backend.PlaybackIssue is { } issue) Abort(issue, true);
            if (plan != null && localReady && !started && !localScheduled && backend.Playing) Abort("本机提前开始演奏，已取消本次独立合奏", true);
        }
        if (LocalMode) TickLocal(now);
        else if (IsCaptain) TickHost(now);
        else if (room.IsViewer && room.Connected && room.Client?.Snapshot?.LargeEnsembleSupported == true) TickClient(now);
        if (!LocalMode && !IsCaptain && plan != null && (!room.Connected || now - lastFrame > 1.5)) Abort("房间连接超时，已停止本机", true);
        if (enabled && committed && !started && plan != null) TickScheduledStart();
        if (started && backend.Playing && !backend.Draining && Math.Abs(backend.PositionSeconds - ExpectedElapsed(now)) > .300)
            Abort("本机播放偏差超过 300 毫秒，已停止；请重新下发", true);
        reports = IsCaptain ? AllReports() : reports.Where(r => r.Cid != Context.SelfCid).Append(LocalReport()).ToArray();
        if (LocalMode) PublishLocal();
    }
    private LargeReport LocalReport() => new(Context.SelfCid, session, enabled, Context.Key, Context.Territory, Context.World,
        plan?.Id ?? Guid.Empty, activeSchedule, localReady, armed, backend.Playing, started && backend.Finished,
        LocalMode || IsCaptain || Sync.Ready, LocalMode || IsCaptain ? 0 : Sync.Rtt,
        LocalMode || IsCaptain ? 0 : Sync.Jitter,
        started && backend.Playing && !backend.Draining ? backend.PositionSeconds - ExpectedElapsed(clock()) : 0,
        localIssue != "" ? localIssue : Context.Validate() ?? "", backend.CompensationVersion,
        backend.SupportsScheduledStart ? LargePlan.CurrentTimingVersion : 0,
        backend.ScheduledStartAt is { } fired ? (fired - localStart) * 1000 : null, backend.OutputTiming);
    // The committed mapping is frozen. Later calibration must not move a playing score.
    private double ExpectedElapsed(double now) => now - localStart;
    private LargeReport[] AllReports() => LocalMode ? Local!.MemberStates.Select(p => p.Report!).Append(LocalReport()).ToArray() : peers.Where(p => p.Key.IsAlive && p.Value.Cid != 0 && p.Value.Report != null
        && clock() - p.Value.At < 1.5).Select(p => p.Value.Report!).Append(LocalReport()).ToArray();
    private string? ReadinessIssue(bool loaded)
    {
        if (Context.Validate() is { } contextIssue) return contextIssue;
        if (Participants.Length > LargePlan.MaxPlayers || planSessions.Count > LargePlan.MaxPlayers)
            return "独立合奏最多 8 人，请重新选择参与角色并下发";
        var all = AllReports();
        if (all.Length > LargePlan.MaxPlayers) return "独立合奏最多 8 人，成员回执超过人数上限";
        if (LocalMode && Participants.Length < 2) return "请先勾选至少两位本机演奏人（包括主控）";
        foreach (var member in Participants)
        {
            var r = all.FirstOrDefault(r => r.Cid == member.Cid);
            var reason = r == null ? LocalMode ? "等待本机连接或已被其他主控管理" : "未连接新版房间或尚未验证团队身份"
                : !r.Enabled ? "未开启独立合奏模式"
                : (plan?.CompensationVersion ?? (UseInstrumentCompensation ? LargePlan.CurrentCompensationVersion : 0)) > r.CompensationVersion
                    ? "起音补偿需要全员使用 3.2.5.27 或更新版本"
                : r.Roster != Context.Key || r.Territory != Context.Territory || r.World != Context.World ? "团队名单或场景不同"
                : r.TimingVersion != LargePlan.CurrentTimingVersion ? "起播调度需要全员使用 3.2.5.29 或更新版本"
                : r.Issue != "" ? r.Issue : !r.ClockReady ? "时钟仍在校准，或网络延迟过大"
                : loaded && (r.PlanId != plan?.Id || !r.Ready) ? "歌曲或乐器尚未准备完成"
                : loaded && (!planSessions.TryGetValue(r.Cid, out var expected) || r.Session != expected) ? "接收会话已改变，请重新下发" : null;
            if (reason != null) return member.Name + "：" + reason;
        }
        return null;
    }
    private void TickHost(double now)
    {
        foreach (var peer in peers.Keys.Where(p => !p.IsAlive).ToArray()) peers.Remove(peer);
        while (room.Server!.TryLarge(out var value))
        {
            var e = value.Value;
            if (!value.Peer.IsAlive || e.Poll == Guid.Empty || !double.IsFinite(e.Sent) || e.Report is not { } r
                || r.Session == Guid.Empty || r.Issue is not { Length: <= 500 } || r.Roster is not { Length: 64 }
                || !double.IsFinite(r.Rtt) || !double.IsFinite(r.Jitter) || !double.IsFinite(r.Drift)) continue;
            if (!peers.TryGetValue(value.Peer, out var p)) peers[value.Peer] = p = new(now);
            if (p.Cid != 0 && (p.Cid != r.Cid || p.Report != null && p.Report.Session != r.Session))
            { p.Cid = 0; p.Report = null; p.Challenge = NewChallenge(); p.ProofUntil = now + 45; }
            if (p.Cid == 0 && now > p.ProofUntil) { p.Challenge = NewChallenge(); p.ProofUntil = now + 45; }
            if (p.Cid == r.Cid && p.Cid != 0)
            {
                p.Report = r; p.At = now;
                if (e.Stop && plan != null) Abort("队员请求停止，已停止全员", false);
            }
            var verifiedHere = enabled && p.Cid != 0 && p.Cid == r.Cid;
            value.Peer.TrySend(new() { Type = "largeFrame", RoomId = room.Id, Large = new()
            {
                Poll = e.Poll, Sent = e.Sent, HostReceive = now, HostSend = clock(), Captain = Context.SelfCid,
                Challenge = enabled && !verifiedHere ? p.Challenge : "", Verified = verifiedHere, Session = r.Session,
                Revision = revision, Phase = enabled ? Phase : LargePhase.Stopped,
                Plan = verifiedHere && planSessions.TryGetValue(p.Cid, out var s) && s == r.Session ? plan : null,
                Schedule = schedule, StartAt = startAt, Reports = AllReports(), Message = Status,
            } });
        }
        TickHostPhase(now);
    }
    private void TickHostPhase(double now)
    {
        if (plan == null || !enabled) return;
        var issue = ReadinessIssue(Phase != LargePhase.Loading);
        if (Phase == LargePhase.Loading)
        {
            if (now > loadDeadline) { Abort("等待全员准备超过 120 秒，请重新下发", true); return; }
            if (issue != null) { Abort(issue, true); return; }
            var loadedIssue = ReadinessIssue(true);
            if (loadedIssue == null) { Phase = LargePhase.Ready; revision++; Status = "全员已准备，可以预约开演"; }
            else Status = loadedIssue;
            return;
        }
        if (issue != null) { Abort(issue, true); return; }
        if (Phase == LargePhase.Arming)
        {
            if (AllReports().All(r => r.Armed && r.ScheduleId == schedule))
            {
                committed = true; Phase = LargePhase.Committed; revision++; Status = "全员已确认，等待统一开演时刻";
            }
            else if (now > startAt - 1.5) Abort("有人未确认预约，已取消本次开演", true);
        }
        if (Phase == LargePhase.Playing && AllReports().All(r => r.Finished))
        { Phase = LargePhase.Finished; revision++; Status = "全员演奏完成"; }
        else if (Phase == LargePhase.Playing && now > startAt + .75 && AllReports().Any(r => !r.Playing && !r.Finished))
            Abort("有人未能开始或中途停止演奏，已停止全员", true);
    }
    private void TickClient(double now)
    {
        while (room.Client!.TryLarge(out var e))
        {
            if (!polls.Remove(e.Poll, out var sent) || sent != e.Sent || now - sent > 2 || e.Session != session
                || !Enum.IsDefined(e.Phase) || e.Reports == null || e.Reports.Length > LargePlan.MaxPlayers
                || !Context.Members.Any(m => m.Cid == e.Captain)) continue;
            lastFrame = now; captain = e.Captain; verified = e.Verified;
            if (enabled && !e.Verified && e.Challenge.Length == 32 && e.Challenge.All(Uri.IsHexDigit)
                && (sentChallenge != e.Challenge || now >= nextProof))
            { backend.SendProof(room.Id, e.Challenge); sentChallenge = e.Challenge; nextProof = now + 10; }
            AcceptClientFrame(e, now);
        }
        foreach (var p in polls.Where(p => now - p.Value > 2).ToArray()) polls.Remove(p.Key);
        if (now < nextPoll) return;
        nextPoll = now + .1;
        var id = Guid.NewGuid(); polls[id] = now;
        if (!room.Client!.SendLarge(new() { Poll = id, Sent = now, Report = LocalReport(), Stop = stopRequested })) polls.Remove(id);
        stopRequested = false;
    }
    private void AcceptClientFrame(LargeEnvelope e, double now)
    {
        try { e.ValidateCapacity(); }
        catch (InvalidDataException ex) { Abort(ex.Message, true); return; }
        reports = e.Reports;
        if (plan != null && !e.Verified) { Abort("总指挥已关闭独立合奏或身份验证失效", false); return; }
        if (!enabled || !e.Verified || e.Revision < acceptedRevision) return;
        var changed = e.Revision > acceptedRevision;
        acceptedRevision = e.Revision;
        if (e.Plan == null || e.Phase is LargePhase.Stopped or LargePhase.Idle)
        { if (plan != null || changed) Abort(e.Message, false); return; }
        try
        {
            if (LocalMode != (e.Plan.LocalTeam != null)) throw new InvalidOperationException("合奏连接方式不匹配，请重新下发");
            e.Plan.ValidateContext(Context, captain);
            if (e.Plan.TimingVersion != LargePlan.CurrentTimingVersion)
                throw new InvalidOperationException("请让总指挥及全员更新到 3.2.5.29 后重新创建房间");
            if (plan?.Id != e.Plan.Id)
            {
                if (e.Phase != LargePhase.Loading || e.Plan.Id == rejectedPlan) return;
                Abort("正在接收 MIDI 和分配", false); plan = e.Plan; Phase = LargePhase.Loading; BeginLoad();
            }
            if (!localReady || localIssue != "") return;
            if (e.Phase == LargePhase.Ready) { Phase = LargePhase.Ready; Status = "全员已准备，等待总指挥开演"; }
            if (e.Phase is LargePhase.Arming or LargePhase.Committed)
            {
                if (e.Schedule == Guid.Empty || !double.IsFinite(e.StartAt) || (!LocalMode && !Sync.Ready))
                    throw new InvalidOperationException("预约开演或时钟校准无效");
                if (activeSchedule != e.Schedule)
                {
                    if (e.Phase != LargePhase.Arming) throw new InvalidOperationException("未收到预约，拒绝迟到开演");
                    localStart = e.StartAt - (LocalMode ? 0 : Sync.Offset);
                    startAt = e.StartAt;
                    if (localStart - now < 1.5 || localStart - now > 8) throw new InvalidOperationException("预约开演已过期");
                    activeSchedule = e.Schedule; armed = true;
                }
                else if (Math.Abs((e.StartAt - (LocalMode ? 0 : Sync.Offset)) - localStart) > .060)
                    throw new InvalidOperationException("预约后时钟变化过大，已取消开演");
                if (e.Phase == LargePhase.Committed)
                {
                    if (!committed && localStart - now < .250) throw new InvalidOperationException("开演确认迟到");
                    committed = true;
                }
                if (!started) { Phase = e.Phase; Status = "已预约统一开演时刻"; }
            }
            if (e.Phase == LargePhase.Playing && started) { Phase = LargePhase.Playing; Status = "独立合奏演奏中（最多 8 人）"; }
            if (e.Phase == LargePhase.Finished) { Phase = LargePhase.Finished; Status = "全员演奏完成"; }
        }
        catch (Exception ex) { Abort(ex.Message, true); }
    }
    private void Abort(string reason, bool fault)
    {
        if (plan != null) rejectedPlan = plan.Id;
        operation.Cancel(); operation.Dispose(); operation = new();
        if (loading != null) _ = loading.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (inspecting != null) _ = inspecting.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        loading = null; inspecting = null;
        if (plan != null || started) backend.Stop();
        plan = null; planSessions.Clear(); localReady = armed = committed = started = localScheduled = false;
        schedule = activeSchedule = Guid.Empty; Phase = LargePhase.Stopped; revision++; Status = reason;
        localIssue = fault ? reason : "";
    }
    public void Dispose() { if (disposed) return; Abort("独立合奏已卸载", false); disposed = true; operation.Dispose(); Local?.Dispose(); }
    private static string NewChallenge() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private sealed class PeerState(double now)
    {
        public ulong Cid;
        public string Challenge = NewChallenge();
        public double ProofUntil = now + 45, At;
        public LargeReport? Report;
    }
}
