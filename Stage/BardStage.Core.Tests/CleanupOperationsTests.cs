using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class CleanupOperationsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T10:00:00Z");

    [Fact]
    public void RemovingFinishedEntriesPreservesArchiveAndDoesNotReopenRequests()
    {
        var (state, show, first, next) = Fixture();
        var request = RequestOperations.Submit(state, show.Id, "Audience", "", "Song", RequestChannel.Manual, Now);
        RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id, mergeEntryId: first.Id);
        Complete(state, show, first);
        var archive = JsonSerializer.Serialize(state.Sessions);
        state.RequestSettings.AutoArrange = true;
        CleanupOperations.DeleteEntries(state, show.Id, [first.Id], finishedOnly: true);
        RequestOperations.Reconcile(state); AutoQueueOperations.ArrangePending(state);
        Assert.Equal(next.Id, Assert.Single(show.Entries).Id);
        Assert.Equal(RequestStatus.Cancelled, request.Status);
        Assert.Null(request.SetlistEntryId);
        Assert.Contains("已完成", request.ResolutionNote);
        Assert.Equal(archive, JsonSerializer.Serialize(state.Sessions));
        CatalogStore.Validate(state);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APreparingOrPlayingEntryMakesBatchDeletionFailBeforeMutation(bool preparing)
    {
        var (state, show, first, next) = Fixture();
        Complete(state, show, first);
        if (!preparing) SetlistOperations.Start(show, next.Id, Now.AddMinutes(1));
        var before = JsonSerializer.Serialize(state);
        Assert.Throws<InvalidOperationException>(() => CleanupOperations.DeleteEntries(state, show.Id, [first.Id, next.Id], preparing ? next.Id : null));
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }

    [Fact]
    public void DeletingQueuedEntryClearsNextLockWithoutDeletingMidiOrOtherEntries()
    {
        var (state, show, first, next) = Fixture(); show.LockedNextEntryId = first.Id;
        CleanupOperations.DeleteEntries(state, show.Id, [first.Id]);
        Assert.Null(show.LockedNextEntryId); Assert.Single(state.Songs); Assert.Equal(next.Id, Assert.Single(show.Entries).Id);
        Assert.Throws<InvalidOperationException>(() => CleanupOperations.DeleteEntries(state, show.Id, [next.Id], finishedOnly: true));
        CatalogStore.Validate(state);
    }

    [Fact]
    public void RemovingRequestLeavesItsQueuedProgramButProtectsActiveRequests()
    {
        var (state, show, first, _) = Fixture();
        var request = RequestOperations.Submit(state, show.Id, "Audience", "", "Song", RequestChannel.Manual, Now);
        RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id, mergeEntryId: first.Id);
        Assert.Throws<InvalidOperationException>(() => CleanupOperations.DeleteRequests(state, [request.Id], first.Id));
        CleanupOperations.DeleteRequests(state, [request.Id]);
        Assert.Empty(state.Requests); Assert.Contains(first, show.Entries);
        CatalogStore.Validate(state);
    }

    [Fact]
    public void RemovingAnAttemptForALateAddedProgramAlsoRemovesOrphanEvents()
    {
        var (state, show, first, _) = Fixture();
        var session = SessionOperations.Begin(state, show.Id, Now);
        var late = SetlistOperations.AddSong(show, state.Songs[0]);
        Complete(state, show, late);
        var attempt = Assert.Single(session.Attempts);
        CleanupOperations.DeleteAttempts(state, session.Id, [attempt.Id]);
        Assert.Empty(session.Attempts);
        Assert.DoesNotContain(session.Events, e => e.EntryId == late.Id);
        Assert.Contains(first, show.Entries);
        CatalogStore.Validate(state);
    }

    [Fact]
    public void RemovingSessionsAndAttemptsProtectsInProgressRecords()
    {
        var (state, show, first, _) = Fixture();
        var previous = Clone(state); SetlistOperations.Start(show, first.Id, Now); SessionOperations.Capture(previous, state, Now);
        var session = Assert.Single(state.Sessions); var attempt = Assert.Single(session.Attempts);
        Assert.Throws<InvalidOperationException>(() => CleanupOperations.DeleteAttempts(state, session.Id, [attempt.Id]));
        Assert.Throws<InvalidOperationException>(() => CleanupOperations.DeleteSession(state, session.Id));
        CatalogStore.Validate(state);
    }

    [Fact]
    public void RemovingIdleUnarchivedSessionDoesNotRecreateItOnUnrelatedChanges()
    {
        var (state, show, first, _) = Fixture(); Complete(state, show, first);
        var previous = Clone(state);
        CleanupOperations.DeleteSession(state, state.Sessions.Single().Id);
        SessionOperations.Capture(previous, state, Now.AddMinutes(2));
        Assert.Empty(state.Sessions); Assert.Equal(2, show.Entries.Count);
        CatalogStore.Validate(state);
    }

    [Fact]
    public void DeletingShowArchivesItsIdleSessionAndKeepsHistoryAndLibrary()
    {
        var (state, show, first, _) = Fixture(); Complete(state, show, first);
        CleanupOperations.DeleteShow(state, show.Id, Now.AddMinutes(2)); RequestOperations.Reconcile(state);
        Assert.DoesNotContain(state.Setlists, s => s.Id == show.Id);
        Assert.NotNull(Assert.Single(state.Sessions).EndedAtUtc); Assert.Single(state.Songs);
        CatalogStore.Validate(state);
    }

    [Fact]
    public void LegacyHistoryCannotLoseItsTimestampBackingUntilTheProgramIsRemoved()
    {
        var (state, show, first, _) = Fixture(); first.Status = EntryStatus.Completed;
        SessionOperations.ImportLegacy(state);
        var session = Assert.Single(state.Sessions);
        Assert.Throws<InvalidOperationException>(() => CleanupOperations.DeleteSession(state, session.Id));
        CatalogStore.Validate(state);
        CleanupOperations.DeleteEntries(state, show.Id, [first.Id]);
        CleanupOperations.DeleteSession(state, session.Id);
        CatalogStore.Validate(state);
    }

    private static (CatalogState State, ShowSetlist Show, SetlistEntry First, SetlistEntry Next) Fixture()
    {
        var state = new CatalogState();
        var song = new SongEntry { Title = "Song", FilePath = Path.GetFullPath("cleanup.mid"), Sha256 = new string('A', 64), DurationSeconds = 90 };
        var show = new ShowSetlist { Name = "Show" };
        state.Songs.Add(song); state.Setlists.Add(show); state.SelectedSetlistId = show.Id; state.RequestSettings.TargetSetlistId = show.Id;
        return (state, show, SetlistOperations.AddSong(show, song), SetlistOperations.AddSong(show, song));
    }

    private static void Complete(CatalogState state, ShowSetlist show, SetlistEntry entry)
    {
        var previous = Clone(state); SetlistOperations.Start(show, entry.Id, Now); SessionOperations.Capture(previous, state, Now);
        previous = Clone(state); SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(30)); SessionOperations.Capture(previous, state, Now.AddSeconds(30));
    }

    private static CatalogState Clone(CatalogState state) => JsonSerializer.Deserialize<CatalogState>(JsonSerializer.Serialize(state))!;
}
