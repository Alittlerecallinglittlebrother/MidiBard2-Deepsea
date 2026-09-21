using BardStage.Core;
using BardStage.Core.Rooms;
using Xunit;

public sealed class MovementTests
{
    private static MovementSnapshot Snapshot() => new(7, 2, 1, new(100, 10),
        [new(1, "队长", new(0, 0, 10), 0, true), new(2, "队员", new(0, 0, 0), 0, true)]);
    private static MovementPlan Plan(MovementAction action = MovementAction.Formation) => new(Guid.NewGuid(), 7, 1, new(100, 10),
        [1, 2], action, [new(2, new(4, 0, 5), 1.5f)]);
    private sealed class Driver : IMovementDriver
    {
        public MovementPosition? Target;
        public float? Facing;
        public void Drive(MovementPosition p, float d) => Target = p;
        public void Face(float f) => Facing = f;
        public void Stop() => Target = null;
    }
    [Fact] public void RelativeCoordinatesRoundTripForEveryFacing()
    {
        foreach (var angle in new[] { 0f, MathF.PI / 2, -MathF.PI / 2, MathF.PI })
        {
            var anchor = new MovementMember(1, "队长", new(10, 2, 30), angle, true);
            var slot = new FormationSlot(2, "队员", 4, -3, 30);
            var world = FormationPreset.ToWorld(slot, anchor);
            var roundTrip = FormationPreset.Capture(new(2, "队员", world.Position, world.Facing, true), anchor);
            Assert.Equal(slot.Right, roundTrip.Right, 4);
            Assert.Equal(slot.Forward, roundTrip.Forward, 4);
            Assert.Equal(slot.FacingDegrees, roundTrip.FacingDegrees, 3);
        }
    }
    [Fact] public void InvalidTargetsAndRostersAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => (Plan() with { Targets = [new(2, new(float.NaN, 0, 0), 0)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Plan() with { Targets = [new(9, new(0, 0, 0), 0)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Plan() with { Members = [1, 1] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Plan() with { Action = MovementAction.Follow, Targets = [new(1, new(0, 0, 0), 0)] }).Validate());
    }
    [Fact] public void FormationStopsAtDestinationAndFacesOnlyAfterArrival()
    {
        var driver = new Driver(); var runner = new MovementRunner(driver); var plan = Plan(); var s = Snapshot();
        runner.Accept(plan, s, 0); runner.Tick(s, 0, true);
        Assert.Equal(plan.Targets[0].Position, driver.Target); Assert.Null(driver.Facing);
        s.Members[1] = s.Members[1] with { Position = plan.Targets[0].Position };
        runner.Tick(s, 1, true);
        Assert.Null(driver.Target); Assert.Equal(1.5f, driver.Facing); Assert.False(runner.Running);
        runner.Accept(plan, s, 2); Assert.False(runner.Running);
    }
    [Fact] public void FollowUsesLocallyObservedLeaderAndStopsWithinDistance()
    {
        var driver = new Driver(); var runner = new MovementRunner(driver); var s = Snapshot();
        runner.Accept(Plan(MovementAction.Follow), s, 0); runner.Tick(s, 0, true);
        Assert.Equal(s.Leader!.Position, driver.Target);
        s.Members[0] = s.Members[0] with { Position = new(0, 0, 1) };
        runner.Tick(s, 1, true); Assert.Null(driver.Target); Assert.True(runner.Running);
        s.Members[0] = s.Members[0] with { Position = new(5, 0, 1) };
        runner.Tick(s, 2, true); Assert.Equal(s.Leader!.Position, driver.Target);
    }
    [Theory]
    [InlineData("lease")] [InlineData("manual")] [InlineData("performing")] [InlineData("scene")]
    [InlineData("leader")] [InlineData("roster")] [InlineData("invisible")] [InlineData("height")] [InlineData("far")]
    public void ContextChangesStopAndDuplicatePlansNeverRestart(string kind)
    {
        var d = new Driver(); var r = new MovementRunner(d); var p = Plan(MovementAction.Follow); var s = Snapshot();
        r.Accept(p, s, 0); r.Tick(s, 0, true); Assert.NotNull(d.Target);
        var changed = kind switch {
            "manual" => s with { ManualInput = true },
            "performing" => s with { BlockReason = "正在演奏" },
            "scene" => s with { Scene = new(200, 10) },
            "leader" => s with { LeaderCid = 2 },
            "roster" => s with { Members = [s.Members[0]] },
            "invisible" => s with { Members = [s.Members[0] with { Visible = false }, s.Members[1]] },
            "height" => s with { Members = [s.Members[0] with { Position = new(0, 5, 1) }, s.Members[1]] },
            "far" => s with { Members = [s.Members[0] with { Position = new(0, 0, 100) }, s.Members[1]] },
            _ => s };
        r.Tick(changed, 1, kind != "lease"); Assert.Null(d.Target); Assert.False(r.Running);
        r.Accept(p, s, 2); r.Tick(s, 2, true); Assert.False(r.Running); Assert.Null(d.Target);
    }
    [Fact] public void ObstacleStopsAfterNoProgress()
    {
        var d = new Driver(); var r = new MovementRunner(d); var s = Snapshot();
        r.Accept(Plan(), s, 0); r.Tick(s, 0, true); r.Tick(s, 3.1, true);
        Assert.False(r.Running); Assert.Contains("受阻", r.Detail);
    }
    [Fact] public void LocalStopRequiresANewCommand()
    {
        var d = new Driver(); var r = new MovementRunner(d); var s = Snapshot(); var p = Plan();
        r.Accept(p, s, 0); r.Stop("手动停止"); r.Accept(p, s, 1); Assert.False(r.Running);
        r.Accept(p with { Id = Guid.NewGuid() }, s, 2); Assert.True(r.Running);
    }
    [Fact] public void InvalidSavedFormationDoesNotAcceptNanOrDuplicateMembers()
    {
        var p = new FormationPreset { Slots = [new(1, "队长", 0, 0, 0), new(1, "队员", 0, 0, 0)] };
        Assert.Throws<InvalidDataException>(p.Validate);
        p.Slots = [new(1, "队长", 21, 0, 0)]; Assert.Throws<InvalidDataException>(p.Validate);
        p.Slots = [new(1, "队长", 0, 0, float.NaN)]; Assert.Throws<InvalidDataException>(p.Validate);
    }
}
