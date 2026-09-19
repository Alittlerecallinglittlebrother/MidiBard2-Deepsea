using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class SessionOperationsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T12:00:00Z");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-session-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReplayPreservesTheInterruptedAttemptAndCreatesANewAttempt()
    {
        var (state, show, entry) = CreatePlan();
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now, RecordSource.MidiBard);
        var session = Assert.Single(state.Sessions);
        Assert.All(session.Events, e => Assert.Equal(RecordSource.MidiBard, e.Source));
        Change(state, s => SetlistOperations.Skip(show, entry.Id, Now.AddSeconds(20)), Now.AddSeconds(20));
        var interrupted = Snapshot(Assert.Single(session.Attempts));

        Change(state, s => SetlistOperations.ResetEntry(show, entry.Id), Now.AddSeconds(21));
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now.AddSeconds(30)), Now.AddSeconds(30));
        Change(state, s => SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(100)), Now.AddSeconds(100));

        Assert.Equal(2, session.Attempts.Count);
        Assert.Equal(interrupted, Snapshot(session.Attempts[0]));
        Assert.Equal(AttemptOutcome.Interrupted, session.Attempts[0].Outcome);
        Assert.Equal(AttemptOutcome.Completed, session.Attempts[1].Outcome);
        Assert.NotEqual(session.Attempts[0].Id, session.Attempts[1].Id);
        Assert.Equal(entry.Id, session.Attempts[0].Program.EntryId);
        Assert.Equal(entry.Id, session.Attempts[1].Program.EntryId);
        Assert.Equal(20, SessionOperations.ActualSeconds(session.Attempts[0], Now.AddDays(1)));
        Assert.Equal(70, SessionOperations.ActualSeconds(session.Attempts[1], Now.AddDays(1)));
        Assert.Contains(session.Events, e => e.Action == "安排补演");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndingWhilePausedSettlesAllPauseTimeWithoutInflatingActualDuration(bool interrupt)
    {
        var (state, show, entry) = CreatePlan();
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        Change(state, s => SetlistOperations.Pause(show, entry.Id, Now.AddSeconds(10)), Now.AddSeconds(10));
        Change(state, s => SetlistOperations.Resume(show, entry.Id, Now.AddSeconds(30)), Now.AddSeconds(30));
        Change(state, s => SetlistOperations.Pause(show, entry.Id, Now.AddSeconds(40)), Now.AddSeconds(40));
        var attempt = Assert.Single(state.Sessions[0].Attempts);
        Assert.Equal(20, SetlistOperations.ElapsedSeconds(entry, Now.AddSeconds(99)));
        Assert.Equal(20, SessionOperations.ActualSeconds(attempt, Now.AddSeconds(99)));
        Assert.Equal(100, StageOperations.Timeline(show, Now.AddSeconds(99)).CurrentRemainingSeconds);

        Change(state, s =>
        {
            if (interrupt) SetlistOperations.Skip(show, entry.Id, Now.AddSeconds(100));
            else SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(100));
        }, Now.AddSeconds(100));

        Assert.Equal(80, entry.PausedSeconds);
        Assert.Null(entry.PausedAtUtc);
        Assert.Equal(20, SetlistOperations.ElapsedSeconds(entry, Now.AddDays(1)));
        Assert.Equal(80, attempt.PausedSeconds);
        Assert.Null(attempt.PausedAtUtc);
        Assert.Equal(20, SessionOperations.ActualSeconds(attempt, Now.AddDays(1)));
        Assert.Equal(interrupt ? AttemptOutcome.Interrupted : AttemptOutcome.Completed, attempt.Outcome);
    }

    [Fact]
    public void DuplicatePauseAndResumeDoNotCreateNewAttemptsOrJournalEvents()
    {
        var (state, show, entry) = CreatePlan();
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        Change(state, s => SetlistOperations.Pause(show, entry.Id, Now.AddSeconds(10)), Now.AddSeconds(10));
        var count = state.Sessions[0].Events.Count;
        Change(state, s => SetlistOperations.Pause(show, entry.Id, Now.AddSeconds(20)), Now.AddSeconds(20));
        Assert.Equal(count, state.Sessions[0].Events.Count);
        Change(state, s => SetlistOperations.Resume(show, entry.Id, Now.AddSeconds(30)), Now.AddSeconds(30));
        count = state.Sessions[0].Events.Count;
        Change(state, s => SetlistOperations.Resume(show, entry.Id, Now.AddSeconds(40)), Now.AddSeconds(40));
        Assert.Equal(count, state.Sessions[0].Events.Count);
        Assert.Single(state.Sessions[0].Attempts);
        Assert.Equal(20, entry.PausedSeconds);
    }

    [Theory]
    [InlineData("pause-before-start")]
    [InlineData("resume-before-start")]
    [InlineData("resume-before-pause")]
    [InlineData("finish-before-pause")]
    [InlineData("skip-before-pause")]
    public void InvalidPauseTimelineDoesNotPartiallyMutateEntry(string operation)
    {
        var (_, show, entry) = CreatePlan();
        SetlistOperations.Start(show, entry.Id, Now);
        if (operation is "resume-before-pause" or "finish-before-pause" or "skip-before-pause")
            SetlistOperations.Pause(show, entry.Id, Now.AddSeconds(20));
        var before = Snapshot(show);
        Action action = operation switch
        {
            "pause-before-start" => () => SetlistOperations.Pause(show, entry.Id, Now.AddSeconds(-1)),
            "resume-before-start" => () => SetlistOperations.Resume(show, entry.Id, Now.AddSeconds(-1)),
            "resume-before-pause" => () => SetlistOperations.Resume(show, entry.Id, Now.AddSeconds(10)),
            "finish-before-pause" => () => SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(10)),
            _ => () => SetlistOperations.Skip(show, entry.Id, Now.AddSeconds(10)),
        };

        Assert.Throws<InvalidOperationException>(action);

        Assert.Equal(before, Snapshot(show));
    }

    [Fact]
    public void ArchivedSnapshotsStayIndependentOfCatalogProgramAndRequestEdits()
    {
        var (state, show, entry) = CreatePlan();
        var request = RequestOperations.Submit(state, show.Id, "Alice", "World", "Song", RequestChannel.Manual, Now.AddMinutes(-1));
        RequestOperations.Arrange(state, [request.Id], entry.SongId!.Value, mergeEntryId: entry.Id);
        RequestOperations.Submit(state, show.Id, "Bob", "World", "Pending song", RequestChannel.Manual, Now.AddMinutes(-1));
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        Change(state, s => SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(60)), Now.AddSeconds(60));
        SessionOperations.Archive(state, show.Id, Now.AddSeconds(70));
        var archived = Assert.Single(state.Sessions);
        var before = Snapshot(archived);

        state.Songs[0].Title = "Renamed";
        state.Songs[0].Arranger = "Different arranger";
        state.Songs[0].Sha256 = new string('C', 64);
        entry.Title = "Different program title";
        entry.Notes = "Changed later";
        request.RequesterName = "Changed person";
        state.Requests[1].Query = "Changed pending request";
        show.Entries.Clear();
        state.Songs.Clear();
        RequestOperations.Reconcile(state);

        Assert.Equal(before, Snapshot(archived));
        Assert.Equal("Bob", Assert.Single(archived.UnresolvedRequests).RequesterName);
        Assert.Equal("Alice@World", Assert.Single(archived.Attempts[0].Program.Requesters));
        CatalogStore.Validate(state);
    }

    [Fact]
    public void CopyForNextPrefersOriginalSongIdentityWhenSeveralEntriesShareAHash()
    {
        var (state, show, entry) = CreatePlan();
        var original = state.Songs[0];
        SessionOperations.Begin(state, show.Id, Now);
        SessionOperations.Archive(state, show.Id, Now.AddSeconds(10));
        var session = state.Sessions[0];
        var duplicate = Clone(original);
        duplicate.Id = Guid.NewGuid();
        duplicate.FilePath = Path.GetFullPath("different-copy.mid");
        duplicate.Arranger = "Different metadata";
        state.Songs.Insert(0, duplicate);
        var before = Snapshot(session);

        var next = SessionOperations.CopyForNext(state, session.Id);

        Assert.Equal(original.Id, Assert.Single(next.Entries).SongId);
        Assert.NotEqual(show.Id, next.Id);
        Assert.NotEqual(entry.Id, next.Entries[0].Id);
        Assert.Equal(EntryStatus.Queued, next.Entries[0].Status);
        Assert.Null(next.Entries[0].StartedAtUtc);
        Assert.Null(next.LockedNextEntryId);
        Assert.Equal(before, Snapshot(session));
        CatalogStore.Validate(state);
    }

    [Fact]
    public void CopyForNextFallsBackToOriginalHashAndNeverAcceptsChangedOriginalId()
    {
        var (state, show, _) = CreatePlan();
        var original = state.Songs[0];
        SessionOperations.Begin(state, show.Id, Now);
        SessionOperations.Archive(state, show.Id, Now.AddSeconds(10));
        var session = state.Sessions[0];
        var restored = Clone(original);
        restored.Id = Guid.NewGuid();
        original.Sha256 = new string('D', 64);
        state.Songs.Add(restored);

        var copied = SessionOperations.CopyForNext(state, session.Id);

        Assert.Equal(restored.Id, copied.Entries[0].SongId);
        state.Songs.Remove(restored);
        var before = Snapshot(state);
        Assert.Throws<InvalidOperationException>(() => SessionOperations.CopyForNext(state, session.Id));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void CopyFailureAfterEarlierValidProgramsDoesNotAddPartialShow()
    {
        var (state, show, _) = CreatePlan();
        SetlistOperations.AddSegment(show, EntryKind.Talk, "Introduction", 30);
        var second = state.Songs[1];
        SetlistOperations.AddSong(show, second);
        SessionOperations.Begin(state, show.Id, Now);
        SessionOperations.Archive(state, show.Id, Now.AddSeconds(10));
        state.Songs.Remove(second);
        var before = Snapshot(state);

        Assert.Throws<InvalidOperationException>(() => SessionOperations.CopyForNext(state, state.Sessions[0].Id));

        Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("before-start")]
    [InlineData("before-final-attempt")]
    public void InvalidArchiveLeavesSessionUnchanged(string scenario)
    {
        var (state, show, entry) = CreatePlan();
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        if (scenario != "active") Change(state, s => SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(20)), Now.AddSeconds(20));
        var before = Snapshot(state);
        var archiveTime = scenario == "before-start" ? Now.AddSeconds(-1) : scenario == "before-final-attempt" ? Now.AddSeconds(10) : Now.AddSeconds(30);

        Assert.Throws<InvalidOperationException>(() => SessionOperations.Archive(state, show.Id, archiveTime));

        Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData("null-event")]
    [InlineData("invalid-event-source")]
    [InlineData("unknown-event-entry")]
    [InlineData("event-after-archive")]
    [InlineData("null-request")]
    [InlineData("wrong-request-show")]
    [InlineData("invalid-request-status")]
    [InlineData("bad-performers")]
    [InlineData("bad-hash")]
    [InlineData("missing-original-id")]
    [InlineData("relative-original-path")]
    [InlineData("inconsistent-duration")]
    [InlineData("null-requester")]
    [InlineData("duplicate-program-id")]
    [InlineData("unfinished-archived-attempt")]
    [InlineData("completed-missing-start")]
    [InlineData("paused-terminal-attempt")]
    [InlineData("pause-exceeds-elapsed")]
    [InlineData("attempt-after-archive")]
    [InlineData("forged-legacy-source")]
    public void InvalidHistoricalStructureIsRejected(string corruption)
    {
        var (state, show, entry) = CreatePlan();
        RequestOperations.Submit(state, show.Id, "Guest", "World", "Pending", RequestChannel.Manual, Now);
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        Change(state, s => SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(20)), Now.AddSeconds(20));
        SessionOperations.Archive(state, show.Id, Now.AddSeconds(30));
        var session = state.Sessions[0];
        var attempt = session.Attempts[0];
        var program = session.FinalPlan[0];
        switch (corruption)
        {
            case "null-event": session.Events.Add(null!); break;
            case "invalid-event-source": session.Events[0].Source = (RecordSource)99; break;
            case "unknown-event-entry": session.Events[0].EntryId = Guid.NewGuid(); break;
            case "event-after-archive": session.Events[0].AtUtc = Now.AddSeconds(31); break;
            case "null-request": session.UnresolvedRequests.Add(null!); break;
            case "wrong-request-show": session.UnresolvedRequests[0].SetlistId = Guid.NewGuid(); break;
            case "invalid-request-status": session.UnresolvedRequests[0].Status = RequestStatus.Rejected; break;
            case "bad-performers": program.Performers = 9; break;
            case "bad-hash": program.Sha256 = "invalid"; break;
            case "missing-original-id": program.SongId = null; break;
            case "relative-original-path": program.FilePath = "relative.mid"; break;
            case "inconsistent-duration": program.PlannedSeconds += 1; break;
            case "null-requester": program.Requesters.Add(null!); break;
            case "duplicate-program-id": session.FinalPlan.Add(Clone(program)); break;
            case "unfinished-archived-attempt": attempt.Outcome = AttemptOutcome.InProgress; attempt.EndedAtUtc = null; break;
            case "completed-missing-start": attempt.StartedAtUtc = null; break;
            case "paused-terminal-attempt": attempt.PausedAtUtc = Now.AddSeconds(10); break;
            case "pause-exceeds-elapsed": attempt.PausedSeconds = 21; break;
            case "attempt-after-archive": attempt.EndedAtUtc = Now.AddSeconds(31); break;
            case "forged-legacy-source": attempt.Source = RecordSource.Legacy; attempt.StartedAtUtc = null; break;
        }

        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
    }

    [Fact]
    public void OpenAttemptMustMatchCurrentProgramAndCannotBeDuplicated()
    {
        var (state, show, entry) = CreatePlan();
        Change(state, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        var attempt = state.Sessions[0].Attempts[0];
        attempt.PausedAtUtc = Now.AddSeconds(10);
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
        attempt.PausedAtUtc = null;
        var duplicate = Clone(attempt);
        duplicate.Id = Guid.NewGuid();
        state.Sessions[0].Attempts.Add(duplicate);
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
    }

    [Fact]
    public void FailedAtomicSaveKeepsThePreviouslyPersistedCatalogAndHistory()
    {
        var (state, _, entry) = CreatePlan();
        var store = new CatalogStore(directory);
        store.Save(state);
        var bytes = File.ReadAllBytes(store.FilePath);
        var proposed = Clone(state);
        var show = proposed.Setlists[0];
        Change(proposed, s => SetlistOperations.Start(show, entry.Id, Now), Now);
        Directory.CreateDirectory(store.FilePath + ".bak");

        var error = Record.Exception(() => store.Save(proposed));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        Assert.Empty(store.Load().Sessions);
        Assert.Empty(state.Sessions);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    private static (CatalogState State, ShowSetlist Show, SetlistEntry Entry) CreatePlan()
    {
        var state = RequestOperationsTests.CreateState();
        var show = state.Setlists[0];
        var entry = SetlistOperations.AddSong(show, state.Songs[0]);
        return (state, show, entry);
    }

    private static void Change(CatalogState state, Action<CatalogState> action, DateTimeOffset now, RecordSource source = RecordSource.Manual)
    {
        var before = Clone(state);
        action(state);
        SessionOperations.Capture(before, state, now, source);
        CatalogStore.Validate(state);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
