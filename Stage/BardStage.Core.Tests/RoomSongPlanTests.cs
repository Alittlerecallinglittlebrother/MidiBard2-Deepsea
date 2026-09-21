using BardStage.Core.Rooms;
using Xunit;

public class RoomSongPlanTests
{
    private static RoomSongPlan Plan() => new(Guid.NewGuid(), new string('A', 64), 10, 1,
        [1, 2, 3], [new(0, true, 2, 12, 2), new(1, true, 20, -12, 1), new(2, false, 0, 0, 0)], 1.1f, true, 0);

    [Fact] public void MembersCanHaveDifferentLocalOrderAndAnIdlePlayer()
        => Plan().ValidateContext(10, 1, [3, 2, 1]);

    [Fact] public void ChangedLeaderOrRosterCannotReuseAPlan()
    {
        Assert.Throws<InvalidDataException>(() => Plan().ValidateContext(10, 2, [1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => Plan().ValidateContext(10, 1, [1, 2, 4]));
        Assert.Throws<InvalidDataException>(() => Plan().ValidateContext(11, 1, [1, 2, 3]));
    }

    [Fact] public void RejectsMissingPerformerAndForeignPerformer()
    {
        foreach (var cid in new ulong[] { 0, 4 })
        {
            var plan = Plan(); plan.Tracks[0] = plan.Tracks[0] with { PerformerCid = cid };
            Assert.Throws<InvalidDataException>(plan.Validate);
        }
    }

    [Fact] public void RejectsWrongTrackIndexInvalidInstrumentAndConflictingInstruments()
    {
        foreach (var track in new RoomTrackAssignment[]
            { new(1, true, 2, 0, 2), new(0, true, 29, 0, 2), new(0, true, 2, 121, 2), new(0, true, 2, 0, 1) })
        {
            var plan = Plan(); plan.Tracks[0] = track;
            Assert.Throws<InvalidDataException>(plan.Validate);
        }
    }

    [Fact] public void SamePerformerMayPlayMultipleTracksOnTheSameInstrument()
    {
        var plan = Plan(); plan.Tracks[1] = new(1, true, 2, -12, 2); plan.Validate();
    }

    [Fact] public void RejectsInvalidGlobalSettings()
    {
        Assert.Throws<InvalidDataException>((Plan() with { Speed = float.NaN }).Validate);
        Assert.Throws<InvalidDataException>((Plan() with { ToneMode = 99 }).Validate);
        Assert.Throws<InvalidDataException>((Plan() with { Tracks = [] }).Validate);
        Assert.Throws<InvalidDataException>((Plan() with { SongHash = "../../escape" }).Validate);
    }
}
