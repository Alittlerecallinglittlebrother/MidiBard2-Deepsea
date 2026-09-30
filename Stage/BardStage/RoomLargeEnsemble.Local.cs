using BardStage.Core.Rooms;

namespace BardStage;

public sealed partial class RoomLargeEnsemble
{
    private bool localScheduled;
    public Func<LocalEnsembleSession>? LocalFactory { get; set; }
    public LocalEnsembleSession? Local { get; private set; }
    public bool LocalMode => Local != null;
    public LargeMember[] Participants => LocalMode ? Local!.Participants : Context.Members;
    public void SetLocalMode(bool value)
    {
        if (value == LocalMode) return;
        if (enabled || Busy) throw new InvalidOperationException("请先关闭本机独立合奏，再切换连接方式");
        if (room.IsCaptain || room.IsRemote) throw new InvalidOperationException("请先退出跨电脑房间，再切换连接方式");
        Abort("连接方式已改变，请重新选择角色和歌曲", false); Draft = null;
        if (value) Local = (LocalFactory ?? (() => new LocalEnsembleSession(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiBard2", "Stage", "LocalSongs"))))();
        else { Local?.Dispose(); Local = null; }
    }
    public void BecomeLocalHost()
    {
        if (!LocalMode || !enabled) throw new InvalidOperationException("请先开启同机独立合奏");
        Local!.Refresh(backend.Capture(),enabled); Local.BecomeHost();
        Status = "已设为本机主控，请勾选参与角色";
    }
    public void ReleaseLocalHost()
    {
        if (Local?.IsHost != true) return;
        StopAll(); Local.Release(); Draft = null;
        Status = "已释放本组角色";
    }
    public void SelectLocalParticipant(Guid node, bool value)
    {
        RequireControl();
        if (!LocalMode || Busy) throw new InvalidOperationException("请先停止演奏，再选择本机角色");
        if (plan != null) Abort("参与角色已改变，请重新读取歌曲并下发",false);
        Local!.Select(node,value); Draft = null;
    }
    private void TickLocal(double now)
    {
        if (!enabled) return;
        if (IsCaptain)
        {
            if (plan != null && Local!.MemberStates.Any(p => p.Frame?.Stop == true && p.Frame.StopPlan == plan.Id))
                Abort("队员请求停止，已停止全员",false);
            TickHostPhase(now);
        }
        else if (Local!.Incoming is { } frame && frame.Captain != 0 && Context.Members.Any(m => m.Cid == frame.Captain))
        {
            captain = frame.Captain;
            lastFrame = now;
            // A newly enabled/reloaded receiver must never resume a previous plan.
            if (frame.Plan == null || frame.LocalSessions?.GetValueOrDefault(Context.SelfCid) == session)
                AcceptClientFrame(frame,now);
            if (frame.Plan == null) stopRequested = false;
        }
    }
    private void TickScheduledStart()
    {
        try
        {
            if (plan == null) throw new InvalidOperationException("演奏计划已取消，请重新下发");
            plan.ValidateContext(Context, IsCaptain ? Context.SelfCid : captain);
            if (Participants.Length > LargePlan.MaxPlayers || planSessions.Count > LargePlan.MaxPlayers)
                throw new InvalidOperationException("独立合奏最多 8 人，已取消超出人数限制的预约");
            if (!backend.SupportsScheduledStart) throw new InvalidOperationException("当前演奏端不支持独立预约，请更新全员插件");
            if (backend.ScheduledStartAt == null && !LocalMode && !IsCaptain && (!Sync.Ready || clock() - lastFrame > .5))
                throw new InvalidOperationException("开演前同步已失效");
            if (!localScheduled) { backend.ScheduleStart(localStart, StartValidation()); localScheduled = true; }
            backend.PulseScheduledStart();
            if (backend.ScheduledStartAt != null)
            {
                started = true; startedAt = localStart; Phase = LargePhase.Playing;
                Status = LocalMode ? "同机独立合奏演奏中" : "跨电脑独立合奏演奏中";
            }
        }
        catch (Exception ex) { Abort(ex.Message,true); }
    }
    private Func<bool>? StartValidation()
    {
        plan!.Validate();
        if (LocalMode) return null;
        // Capture immutable identities on the framework; this callback reads only
        // transport snapshots, never game objects or the framework's dictionaries.
        var expectedPlan = plan!.Id; var expectedSchedule = activeSchedule; var expectedSession = session;
        if (!IsCaptain)
        {
            var client = room.Client!;
            return () => client.Connected && client.Timing.Ready
                && client.LatestLarge is { Large: { } frame } packet && clock() - packet.ReceivedAt < 1.5
                && frame.Session == expectedSession && frame.Verified && frame.Plan?.Id == expectedPlan
                && frame.Schedule == expectedSchedule && frame.Phase is LargePhase.Committed or LargePhase.Playing;
        }
        var endpoints = peers.Where(p => p.Value.Cid != 0 && planSessions.ContainsKey(p.Value.Cid))
            .Select(p => (p.Key, Cid: p.Value.Cid, Session: planSessions[p.Value.Cid])).ToArray();
        return () => endpoints.All(p => p.Key.IsAlive
            && p.Key.LatestLarge is { Large: { Report: { } r } e } packet && clock() - packet.ReceivedAt < 1.5
            && !e.Stop && r.Cid == p.Cid && r.Session == p.Session && r.Enabled && r.Armed
            && r.PlanId == expectedPlan && r.ScheduleId == expectedSchedule);
    }
    private void PublishLocal()
    {
        var frame = IsCaptain ? new LargeEnvelope
        {
            Captain = Context.SelfCid, Verified = true, Revision = revision, Phase = Phase, Plan = plan,
            Schedule = schedule, StartAt = startAt, Reports = AllReports(), Message = Status,
            LocalSessions = new(planSessions),
        } : new LargeEnvelope { Stop = stopRequested, StopPlan = requestedStopPlan };
        Local!.Publish(LocalReport(),frame);
    }
}
