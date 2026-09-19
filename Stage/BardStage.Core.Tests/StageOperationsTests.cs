using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class StageOperationsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T12:00:00Z");

    [Fact]
    public void EffectiveRunOrderPutsActiveThenLockedThenStoredQueueAndExcludesHistory()
    {
        var show = new ShowSetlist { Name = "Show" };
        var first = Song("First");
        var completed = Song("Completed", EntryStatus.Completed);
        var locked = Song("Locked");
        var active = Song("Active", EntryStatus.InProgress);
        var skipped = Song("Skipped", EntryStatus.Skipped);
        var last = Song("Last");
        show.Entries = [first, completed, locked, active, skipped, last];
        show.LockedNextEntryId = locked.Id;
        var before = Snapshot(show);

        Assert.Same(active, StageOperations.Current(show));
        Assert.Same(locked, StageOperations.Next(show));
        Assert.Equal([active.Id, locked.Id, first.Id, last.Id], StageOperations.RunOrder(show).Select(entry => entry.Id));
        Assert.Equal(before, Snapshot(show));
    }

    [Fact]
    public void MissingOrNoLongerQueuedLockFallsBackWithoutChangingStoredLock()
    {
        var first = Song("First");
        var completed = Song("Completed", EntryStatus.Completed);
        var show = new ShowSetlist { Name = "Show", Entries = [completed, first], LockedNextEntryId = completed.Id };
        Assert.Same(first, StageOperations.Next(show));
        Assert.Equal(completed.Id, show.LockedNextEntryId);
        show.LockedNextEntryId = Guid.NewGuid();
        Assert.Same(first, StageOperations.Next(show));
        show.Entries.Remove(first);
        Assert.Null(StageOperations.Next(show));
        Assert.Empty(StageOperations.RunOrder(show));
    }

    [Fact]
    public void CompleteReturnsNextWithoutStartingItOrClearingItsLock()
    {
        var (state, show) = CreateState();
        var first = Song("First");
        var locked = Song("Locked");
        var active = Song("Active", EntryStatus.InProgress);
        show.Entries = [first, locked, active];
        show.LockedNextEntryId = locked.Id;

        var nextId = StageOperations.CompleteAndAdvance(state, show.Id, active.Id, Now.AddSeconds(10));

        Assert.Equal(locked.Id, nextId);
        Assert.Equal(EntryStatus.Completed, active.Status);
        Assert.Equal(Now.AddSeconds(10), active.EndedAtUtc);
        Assert.Equal(EntryStatus.Queued, locked.Status);
        Assert.Null(locked.StartedAtUtc);
        Assert.Equal(locked.Id, show.LockedNextEntryId);
        Assert.Null(StageOperations.Current(show));
    }

    [Fact]
    public void ExplicitStartUsesExpectedNextAndClearsOnlyItsOwnLock()
    {
        var (state, show) = CreateState();
        var first = Song("First");
        var locked = Song("Locked");
        show.Entries = [first, locked];
        show.LockedNextEntryId = locked.Id;

        StageOperations.StartNext(state, show.Id, locked.Id, Now);

        Assert.Same(locked, StageOperations.Current(show));
        Assert.Equal(Now, locked.StartedAtUtc);
        Assert.Null(show.LockedNextEntryId);
        Assert.Same(first, StageOperations.Next(show));
    }

    [Fact]
    public void StaleExpectedNextIsRejectedWithoutMutatingAnything()
    {
        var (state, show) = CreateState();
        var first = Song("First");
        var newlyLocked = Song("New next");
        show.Entries = [first, newlyLocked];
        show.LockedNextEntryId = newlyLocked.Id;
        var before = Snapshot(state);

        Assert.Throws<InvalidOperationException>(() => StageOperations.StartNext(state, show.Id, first.Id, Now));

        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void ActiveProgramInAnotherShowPreventsStartingNext()
    {
        var (state, show) = CreateState();
        var next = Song("Next");
        show.Entries.Add(next);
        var other = new ShowSetlist { Name = "Other show", Entries = [Song("Active", EntryStatus.InProgress)] };
        state.Setlists.Add(other);
        var before = Snapshot(state);

        Assert.Throws<InvalidOperationException>(() => StageOperations.StartNext(state, show.Id, next.Id, Now));

        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void TimelineUsesEffectiveOrderForSongSpeedAndAdjacentGaps()
    {
        var show = new ShowSetlist { Name = "Show", GapSeconds = 4, TargetEndUtc = Now.AddSeconds(165) };
        var first = Song("First");
        first.DurationSeconds = 80;
        first.PlaybackSpeed = 2;
        var talk = new SetlistEntry { Kind = EntryKind.Talk, Title = "Talk", DurationSeconds = 20, PlaybackSpeed = 4 };
        var locked = Song("Locked");
        locked.DurationSeconds = 100;
        locked.PlaybackSpeed = 2;
        var active = Song("Active", EntryStatus.InProgress);
        active.DurationSeconds = 120;
        active.PlaybackSpeed = 2;
        active.StartedAtUtc = Now.AddSeconds(-10);
        show.Entries = [first, talk, locked, active, Song("Done", EntryStatus.Completed), Song("Skip", EntryStatus.Skipped)];
        show.LockedNextEntryId = locked.Id;
        var before = Snapshot(show);

        var timeline = StageOperations.Timeline(show, Now);

        Assert.Equal(60, timeline.CurrentPlannedSeconds);
        Assert.Equal(10, timeline.CurrentElapsedSeconds);
        Assert.Equal(50, timeline.CurrentRemainingSeconds);
        Assert.Equal(0, timeline.CurrentOverrunSeconds);
        Assert.Equal(168, timeline.RemainingSeconds);
        Assert.Equal(Now.AddSeconds(168), timeline.EstimatedEndUtc);
        Assert.Equal(3, timeline.TargetVarianceSeconds);
        Assert.Equal(1, timeline.CompletedCount);
        Assert.Equal(6, timeline.TotalCount);
        Assert.Equal(before, Snapshot(show));
    }

    [Fact]
    public void TimelineKeepsOverrunVisibleUntilManuallyFinishedAndClampsRemaining()
    {
        var (state, show) = CreateState();
        var active = Song("Active", EntryStatus.InProgress);
        active.DurationSeconds = 60;
        active.StartedAtUtc = Now.AddSeconds(-90);
        show.Entries = [active, Song("Next")];

        var timeline = StageOperations.Timeline(show, Now);

        Assert.Equal(90, timeline.CurrentElapsedSeconds);
        Assert.Equal(30, timeline.CurrentOverrunSeconds);
        Assert.Equal(0, timeline.CurrentRemainingSeconds);
        Assert.Equal(123, timeline.RemainingSeconds);
        Assert.Equal(EntryStatus.InProgress, active.Status);
        StageOperations.CompleteAndAdvance(state, show.Id, active.Id, Now);
        var finished = StageOperations.Timeline(show, Now);
        Assert.Equal(0, finished.CurrentOverrunSeconds);
        Assert.Equal(0, finished.CurrentElapsedSeconds);
        Assert.Equal(120, finished.RemainingSeconds);
    }

    [Fact]
    public void TimelineClampsNegativeElapsedAndHandlesNoCurrentOrTarget()
    {
        var active = Song("Active", EntryStatus.InProgress);
        active.StartedAtUtc = Now.AddSeconds(30);
        var show = new ShowSetlist { Name = "Show", Entries = [active] };
        var timeline = StageOperations.Timeline(show, Now);
        Assert.Equal(0, timeline.CurrentElapsedSeconds);
        Assert.Equal(120, timeline.CurrentRemainingSeconds);
        Assert.Null(timeline.TargetVarianceSeconds);
        show.Entries.Clear();
        var empty = StageOperations.Timeline(show, Now);
        Assert.Equal(0, empty.CurrentPlannedSeconds);
        Assert.Equal(0, empty.RemainingSeconds);
        Assert.Equal(Now, empty.EstimatedEndUtc);
        Assert.Equal(0, empty.TotalCount);
    }

    [Fact]
    public void InsertedTemporarySegmentResumesPreviouslyLockedQueueAfterCompletion()
    {
        var (state, show) = CreateState();
        var first = Song("First");
        var history = Song("History", EntryStatus.Completed);
        var locked = Song("Locked");
        var active = Song("Active", EntryStatus.InProgress);
        var last = Song("Last");
        var skipped = Song("Skipped", EntryStatus.Skipped);
        show.Entries = [first, history, locked, active, last, skipped];
        show.LockedNextEntryId = locked.Id;
        var request = new SongRequest
        {
            SetlistId = show.Id, SetlistName = show.Name, RequesterName = "Alice", Query = "Locked",
            SongId = locked.SongId, SetlistEntryId = locked.Id, Status = RequestStatus.Arranged,
        };
        state.Requests.Add(request);
        var requestBefore = Snapshot(request);
        var historyBefore = Snapshot(history);
        var activeBefore = Snapshot(active);

        var temporary = StageOperations.InsertNextSegment(state, show.Id, EntryKind.Break, "Rest", 30);

        Assert.Equal([active.Id, temporary.Id, locked.Id, first.Id, last.Id], StageOperations.RunOrder(show).Select(entry => entry.Id));
        Assert.Equal([history.Id, active.Id, skipped.Id], show.Entries.Where(entry => entry.Status != EntryStatus.Queued).Select(entry => entry.Id));
        Assert.Equal(historyBefore, Snapshot(history));
        Assert.Equal(activeBefore, Snapshot(active));
        Assert.Equal(requestBefore, Snapshot(request));
        StageOperations.CompleteAndAdvance(state, show.Id, active.Id, Now.AddSeconds(1));
        StageOperations.StartNext(state, show.Id, temporary.Id, Now.AddSeconds(2));
        var following = StageOperations.CompleteAndAdvance(state, show.Id, temporary.Id, Now.AddSeconds(32));
        Assert.Equal(locked.Id, following);
        Assert.Equal(EntryStatus.Queued, locked.Status);
        Assert.Equal([locked.Id, first.Id, last.Id], StageOperations.RunOrder(show).Select(entry => entry.Id));
    }

    [Fact]
    public void NestedTemporaryInsertionsKeepTheirEntirePreviousEffectiveQueue()
    {
        var (state, show) = CreateState();
        var first = Song("First");
        var locked = Song("Locked");
        show.Entries = [first, locked];
        show.LockedNextEntryId = locked.Id;
        var talk = StageOperations.InsertNextSegment(state, show.Id, EntryKind.Talk, "Talk", 20);
        var rest = StageOperations.InsertNextSegment(state, show.Id, EntryKind.Break, "Rest", 30);
        Assert.Equal([rest.Id, talk.Id, locked.Id, first.Id], StageOperations.RunOrder(show).Select(entry => entry.Id));
        StageOperations.SkipAndAdvance(state, show.Id, rest.Id, Now);
        Assert.Same(talk, StageOperations.Next(show));
        StageOperations.SkipAndAdvance(state, show.Id, talk.Id, Now);
        Assert.Same(locked, StageOperations.Next(show));
    }

    [Fact]
    public void InsertSegmentIntoFinishedShowAppendsAndLocksIt()
    {
        var (state, show) = CreateState();
        var history = Song("History", EntryStatus.Completed);
        show.Entries = [history];
        var segment = StageOperations.InsertNextSegment(state, show.Id, EntryKind.Talk, "  Closing remarks  ", 15);
        Assert.Equal("Closing remarks", segment.Title);
        Assert.Equal([history.Id, segment.Id], show.Entries.Select(entry => entry.Id));
        Assert.Equal(segment.Id, show.LockedNextEntryId);
        Assert.Same(segment, StageOperations.Next(show));
    }

    [Theory]
    [InlineData(EntryStatus.Completed)]
    [InlineData(EntryStatus.Skipped)]
    public void ReplayResetsTimesAndLocksExistingIdentityWithoutStartingIt(EntryStatus originalStatus)
    {
        var (state, show) = CreateState();
        var entry = Song("Replay", originalStatus);
        entry.Notes = "Keep this note";
        var other = Song("Other");
        show.Entries = [other, entry];
        show.LockedNextEntryId = other.Id;
        var originalId = entry.Id;
        var originalSongId = entry.SongId;

        StageOperations.PrepareReplay(state, show.Id, entry.Id);

        Assert.Equal(originalId, entry.Id);
        Assert.Equal(originalSongId, entry.SongId);
        Assert.Equal("Keep this note", entry.Notes);
        Assert.Equal(EntryStatus.Queued, entry.Status);
        Assert.Null(entry.StartedAtUtc);
        Assert.Null(entry.EndedAtUtc);
        Assert.Equal(entry.Id, show.LockedNextEntryId);
        Assert.Same(entry, StageOperations.Next(show));
        Assert.Null(StageOperations.Current(show));
    }

    [Fact]
    public void SkipLockedNextClearsItWhileInterruptingCurrentPreservesAnotherLock()
    {
        var (state, show) = CreateState();
        var active = Song("Active", EntryStatus.InProgress);
        var first = Song("First");
        var locked = Song("Locked");
        show.Entries = [active, first, locked];
        show.LockedNextEntryId = locked.Id;
        var afterInterrupt = StageOperations.SkipAndAdvance(state, show.Id, active.Id, Now.AddSeconds(10));
        Assert.Equal(locked.Id, afterInterrupt);
        Assert.Equal(locked.Id, show.LockedNextEntryId);
        Assert.Equal(EntryStatus.Skipped, active.Status);
        Assert.Null(StageOperations.Current(show));
        var afterSkip = StageOperations.SkipAndAdvance(state, show.Id, locked.Id, Now.AddSeconds(11));
        Assert.Equal(first.Id, afterSkip);
        Assert.Null(show.LockedNextEntryId);
        Assert.Equal(EntryStatus.Queued, first.Status);
    }

    [Theory]
    [InlineData("missing-show")]
    [InlineData("missing-entry")]
    [InlineData("wrong-active")]
    [InlineData("end-before-start")]
    [InlineData("replay-queued")]
    [InlineData("replay-active")]
    [InlineData("skip-history")]
    [InlineData("song-segment")]
    [InlineData("invalid-kind")]
    [InlineData("empty-title")]
    [InlineData("negative-duration")]
    [InlineData("nan-duration")]
    public void InvalidFlowOperationsNeverMutateTheCatalog(string failure)
    {
        var (state, show) = CreateState();
        var active = Song("Active", EntryStatus.InProgress);
        var queued = Song("Queued");
        var history = Song("History", EntryStatus.Completed);
        show.Entries = [active, queued, history];
        show.LockedNextEntryId = queued.Id;
        var before = Snapshot(state);
        Action action = failure switch
        {
            "missing-show" => () => StageOperations.InsertNextSegment(state, Guid.NewGuid(), EntryKind.Talk, "Talk", 10),
            "missing-entry" => () => StageOperations.PrepareReplay(state, show.Id, Guid.NewGuid()),
            "wrong-active" => () => StageOperations.CompleteAndAdvance(state, show.Id, queued.Id, Now),
            "end-before-start" => () => StageOperations.CompleteAndAdvance(state, show.Id, active.Id, Now.AddSeconds(-1)),
            "replay-queued" => () => StageOperations.PrepareReplay(state, show.Id, queued.Id),
            "replay-active" => () => StageOperations.PrepareReplay(state, show.Id, active.Id),
            "skip-history" => () => StageOperations.SkipAndAdvance(state, show.Id, history.Id, Now),
            "song-segment" => () => StageOperations.InsertNextSegment(state, show.Id, EntryKind.Song, "Song", 10),
            "invalid-kind" => () => StageOperations.InsertNextSegment(state, show.Id, (EntryKind)999, "Other", 10),
            "empty-title" => () => StageOperations.InsertNextSegment(state, show.Id, EntryKind.Talk, "  ", 10),
            "negative-duration" => () => StageOperations.InsertNextSegment(state, show.Id, EntryKind.Break, "Rest", -1),
            "nan-duration" => () => StageOperations.InsertNextSegment(state, show.Id, EntryKind.Talk, "Talk", double.NaN),
            _ => throw new InvalidOperationException(),
        };

        Assert.ThrowsAny<Exception>(action);

        Assert.Equal(before, Snapshot(state));
    }

    private static SetlistEntry Song(string title, EntryStatus status = EntryStatus.Queued) => new()
    {
        Title = title,
        Kind = EntryKind.Song,
        SongId = Guid.NewGuid(),
        DurationSeconds = 120,
        Status = status,
        StartedAtUtc = status is EntryStatus.InProgress or EntryStatus.Completed ? Now : null,
        EndedAtUtc = status is EntryStatus.Completed or EntryStatus.Skipped ? Now.AddSeconds(120) : null,
    };

    private static (CatalogState State, ShowSetlist Show) CreateState()
    {
        var show = new ShowSetlist { Name = "Show" };
        return (new CatalogState { Setlists = [show], SelectedSetlistId = show.Id }, show);
    }

    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value);
}
