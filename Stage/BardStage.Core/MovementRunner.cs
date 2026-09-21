using BardStage.Core.Rooms;

namespace BardStage.Core;

public sealed record MovementSnapshot(long PartyId, ulong SelfCid, ulong LeaderCid, MovementScene Scene,
    MovementMember[] Members, string? BlockReason = null, bool ManualInput = false)
{
    public MovementMember? Self => Members.FirstOrDefault(m => m.Cid == SelfCid && m.Visible);
    public MovementMember? Leader => Members.FirstOrDefault(m => m.Cid == LeaderCid && m.Visible);
}
public interface IMovementDriver
{
    void Drive(MovementPosition target, float stopDistance);
    void Face(float radians);
    void Stop();
}
public sealed class MovementRunner(IMovementDriver driver)
{
    private MovementPlan? active;
    private MovementPosition? progressPosition;
    private double progressAt, startedAt;
    public Guid CommandId { get; private set; }
    public string State { get; private set; } = "待命";
    public string Detail { get; private set; } = "";
    public bool Running => active != null;
    public void Accept(MovementPlan plan, MovementSnapshot snapshot, double now)
    {
        if (CommandId == plan.Id) return; // A repeated lease can never resurrect a stopped/completed move.
        Stop("新指令");
        CommandId = plan.Id;
        try
        {
            plan.Validate();
            var issue = ContextIssue(plan, snapshot);
            if (issue != null) throw new InvalidOperationException(issue);
            if (plan.Action == MovementAction.Stop) { State = "已停止"; return; }
            var target = plan.Targets.FirstOrDefault(t => t.Cid == snapshot.SelfCid);
            if (target == null) { State = "未参与"; return; }
            if (plan.Action == MovementAction.Formation && (snapshot.Self!.Position.DistanceXZ(target.Position) > 40
                || Math.Abs(snapshot.Self.Position.Y - target.Position.Y) > 3))
                throw new InvalidOperationException("目标过远或不在同一地面");
            active = plan; startedAt = progressAt = now; progressPosition = snapshot.Self!.Position;
            State = plan.Action == MovementAction.Follow ? "跟随中" : "列队中"; Detail = "";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        { State = "已停止"; Detail = ex.Message; }
    }
    public void Tick(MovementSnapshot snapshot, double now, bool leaseAlive)
    {
        if (active == null) return;
        var issue = !leaseAlive ? "移动指令已失联" : ContextIssue(active, snapshot);
        if (issue != null) { Stop(issue); return; }
        var self = snapshot.Self!;
        var target = active.Action == MovementAction.Follow ? snapshot.Leader!.Position
            : active.Targets.First(t => t.Cid == snapshot.SelfCid).Position;
        var distance = self.Position.DistanceXZ(target);
        if (distance > 40 || Math.Abs(self.Position.Y - target.Y) > 3) { Stop("目标过远或高度不符"); return; }
        var tolerance = active.Action == MovementAction.Follow ? 2.5f : 0.2f;
        if (distance <= tolerance)
        {
            driver.Stop(); progressAt = now; progressPosition = self.Position;
            if (active.Action == MovementAction.Formation)
            {
                driver.Face(active.Targets.First(t => t.Cid == snapshot.SelfCid).Facing);
                active = null; State = "已到位"; Detail = "";
            }
            return;
        }
        if (now - startedAt > 30 && active.Action == MovementAction.Formation) { Stop("列队超时，请检查障碍物"); return; }
        if (progressPosition == null || progressPosition.DistanceXZ(self.Position) > 0.15f)
        { progressPosition = self.Position; progressAt = now; }
        if (now - progressAt > 3) { Stop("移动受阻，请手动调整位置"); return; }
        driver.Drive(target, tolerance);
    }
    public void Stop(string reason)
    {
        driver.Stop(); active = null;
        State = "已停止"; Detail = reason;
    }
    private static string? ContextIssue(MovementPlan plan, MovementSnapshot s)
    {
        if (!plan.Matches(s.PartyId, s.LeaderCid, s.Scene, s.Members.Select(m => m.Cid))) return "小队或场景已改变";
        if (s.ManualInput) return "本机手动操作已接管";
        if (s.BlockReason != null) return s.BlockReason;
        if (s.Self == null || s.Leader == null) return "当前场景中看不到队长";
        return null;
    }
}
