using System.Numerics;
using System.Text.Json.Serialization;

namespace BardStage.Core.Rooms;

public enum MovementAction { Follow, Formation, Stop }
public sealed record MovementPosition(float X, float Y, float Z)
{
    [JsonIgnore] public Vector3 Vector => new(X, Y, Z);
    public static MovementPosition From(Vector3 value) => new(value.X, value.Y, value.Z);
    [JsonIgnore] public bool Valid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z)
        && Math.Abs(X) < 100000 && Math.Abs(Y) < 100000 && Math.Abs(Z) < 100000;
    public float DistanceXZ(MovementPosition other) => Vector2.Distance(new(X, Z), new(other.X, other.Z));
}
public sealed record MovementScene(uint Territory, uint World);
public sealed record MovementMember(ulong Cid, string Name, MovementPosition Position, float Facing, bool Visible);
public sealed record MovementTarget(ulong Cid, MovementPosition Position, float Facing, Guid Session = default);
public sealed record MovementPlan(Guid Id, long PartyId, ulong LeaderCid, MovementScene Scene, ulong[] Members,
    MovementAction Action, MovementTarget[] Targets, Guid LeaderSession = default)
{
    public void Validate()
    {
        if (Id == Guid.Empty || PartyId == 0 || LeaderCid == 0 || Scene is not { Territory: > 0, World: > 0 }
            || !Enum.IsDefined(Action)) throw new InvalidDataException("移动指令无效");
        if (Members == null || Members.Length is < 2 or > 8 || Members.Any(x => x == 0)
            || Members.Distinct().Count() != Members.Length || !Members.Contains(LeaderCid))
            throw new InvalidDataException("小队名单无效");
        if (Targets == null || Targets.Length > 8 || Targets.Any(t => t == null) || Targets.Select(t => t.Cid).Distinct().Count() != Targets.Length
            || Targets.Any(t => !Members.Contains(t.Cid) || t.Position is not { Valid: true } || !float.IsFinite(t.Facing)))
            throw new InvalidDataException("队形位置无效");
        if (Action != MovementAction.Stop && Targets.Length == 0) throw new InvalidDataException("未选择移动人员");
        if (Action == MovementAction.Follow && Targets.Any(t => t.Cid == LeaderCid))
            throw new InvalidDataException("队长不需要跟随自己");
    }
    public bool Matches(long party, ulong leader, MovementScene scene, IEnumerable<ulong> members)
        => PartyId == party && LeaderCid == leader && Scene == scene && Members.ToHashSet().SetEquals(members);
}
public sealed record MovementReport(ulong Cid, bool Enabled, string Issue, MovementScene Scene, Guid CommandId,
    string State, string Detail, Guid Session = default);
public sealed record MovementEnvelope
{
    public Guid PollId { get; init; }
    public MovementReport? Report { get; init; }
    public MovementPlan? Plan { get; init; }
    public MovementReport[]? Reports { get; init; }
    public Guid ResultId { get; init; }
    public string? Error { get; init; }
}

public sealed record FormationSlot(ulong Cid, string Name, float Right, float Forward, float FacingDegrees);
public sealed class FormationPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新队形";
    public List<FormationSlot> Slots { get; set; } = [];
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Slots == null || Slots.Count > 8 || Slots.Any(s => s == null)
            || Slots.Select(s => s.Cid).Distinct().Count() != Slots.Count
            || Slots.Any(s => s.Cid == 0 || s.Name == null || s.Name.Length > 100
                || !float.IsFinite(s.Right) || !float.IsFinite(s.Forward) || !float.IsFinite(s.FacingDegrees)
                || Math.Abs(s.Right) > 20 || Math.Abs(s.Forward) > 20 || Math.Abs(s.FacingDegrees) > 180))
            throw new InvalidDataException("队形数据无效（位置范围为正负 20 米）");
    }
    // Right/forward are relative to the leader's facing at the moment of dispatch.
    public static MovementTarget ToWorld(FormationSlot slot, MovementMember anchor)
    {
        var c = MathF.Cos(anchor.Facing); var s = MathF.Sin(anchor.Facing);
        return new(slot.Cid, new(anchor.Position.X + slot.Right * c + slot.Forward * s,
            anchor.Position.Y, anchor.Position.Z - slot.Right * s + slot.Forward * c),
            anchor.Facing + slot.FacingDegrees * MathF.PI / 180);
    }
    public static FormationSlot Capture(MovementMember member, MovementMember anchor)
    {
        var x = member.Position.X - anchor.Position.X; var z = member.Position.Z - anchor.Position.Z;
        var c = MathF.Cos(anchor.Facing); var s = MathF.Sin(anchor.Facing);
        var degrees = MathF.IEEERemainder((member.Facing - anchor.Facing) * 180 / MathF.PI, 360);
        return new(member.Cid, member.Name, x * c - z * s, x * s + z * c, degrees);
    }
}
