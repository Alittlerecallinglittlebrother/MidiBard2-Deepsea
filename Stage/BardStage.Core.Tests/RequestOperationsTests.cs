using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class RequestOperationsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T10:00:00Z");

    [Fact]
    public void ManualRegistrationWorksClosedWhileChatRequiresOpenChannelAndTarget()
    {
        var state = CreateState();
        var show = state.Setlists[0];
        var manual = Submit(state, "Manual", "Song");
        Assert.Equal(RequestStatus.Pending, manual.Status);
        Assert.Empty(manual.RequesterWorld);
        Assert.Equal(show.Name, manual.SetlistName);
        var before = Snapshot(state);
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Submit(state, show.Id, "Chat", "World", "Song", RequestChannel.Say, Now));
        Assert.Equal(before, Snapshot(state));

        state.RequestSettings.IsOpen = true;
        state.RequestSettings.TargetSetlistId = state.Setlists[1].Id;
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Submit(state, show.Id, "Chat", "World", "Song", RequestChannel.Say, Now));
        state.RequestSettings.TargetSetlistId = show.Id;
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Submit(state, show.Id, "Chat", "World", "Song", RequestChannel.Party, Now));
        var chat = RequestOperations.Submit(state, show.Id, "Chat", "World", "Song", RequestChannel.Say, Now);
        Assert.Equal(show.Id, chat.SetlistId);
        Assert.Equal(2, state.Requests.Count);
    }

    [Fact]
    public void ExactNormalizedTitlesAndAliasesPrecedePartialMatchesWithoutChoosingAVersion()
    {
        var state = CreateState();
        state.Songs[0].Title = "Ｓｏｎｇ　Ｎａｍｅ";
        state.Songs[1].Title = "Song Name live";
        state.Songs.Add(new SongEntry { Title = "Other title", Aliases = ["song   name"], FilePath = Path.GetFullPath("alias.mid"), Sha256 = new string('C', 64) });

        var matches = RequestOperations.FindMatches(state, "  SONG   NAME  ");

        Assert.Equal(3, matches.Count);
        Assert.Contains(state.Songs[0], matches.Take(2));
        Assert.Contains(state.Songs[2], matches.Take(2));
        Assert.Same(state.Songs[1], matches[2]);
        Assert.Empty(RequestOperations.FindMatches(state, "  "));
        Assert.Empty(state.Setlists[0].Entries);
    }

    [Fact]
    public void ArrangeBatchInsertsOneEntryPreservingLockedAndActiveIdentities()
    {
        var state = CreateState();
        var show = state.Setlists[0];
        var active = SetlistOperations.AddSong(show, state.Songs[0]);
        var locked = SetlistOperations.AddSong(show, state.Songs[1]);
        SetlistOperations.Start(show, active.Id, Now);
        show.LockedNextEntryId = locked.Id;
        var first = Submit(state, "Alice", "Song");
        var second = Submit(state, "Bob", "Song alias");
        RequestOperations.Defer(state, second.Id);

        var entry = RequestOperations.Arrange(state, [first.Id, second.Id], state.Songs[0].Id, insertIndex: 1);

        Assert.Equal(3, show.Entries.Count);
        Assert.Same(entry, show.Entries[1]);
        Assert.Same(active, show.Entries.Single(x => x.Status == EntryStatus.InProgress));
        Assert.Equal(locked.Id, show.LockedNextEntryId);
        Assert.All(state.Requests, x =>
        {
            Assert.Equal(RequestStatus.Arranged, x.Status);
            Assert.Equal(entry.Id, x.SetlistEntryId);
            Assert.Equal(state.Songs[0].Id, x.SongId);
        });
        CatalogStore.Validate(state);
    }

    [Fact]
    public void MergeUsesExactSongVersionAndNeverCreatesAnotherEntry()
    {
        var state = CreateState();
        var show = state.Setlists[0];
        var entry = SetlistOperations.AddSong(show, state.Songs[0]);
        var request = Submit(state, "Alice", "Same title");
        var before = Snapshot(state);

        Assert.Throws<InvalidOperationException>(() => RequestOperations.Arrange(state, [request.Id], state.Songs[1].Id, mergeEntryId: entry.Id));
        Assert.Equal(before, Snapshot(state));
        var result = RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id, mergeEntryId: entry.Id);

        Assert.Same(entry, result);
        Assert.Single(show.Entries);
        Assert.Equal(entry.Id, request.SetlistEntryId);
    }

    [Fact]
    public void RepeatedConfirmationIsRejectedWithoutAddingDuplicateProgram()
    {
        var state = CreateState();
        var request = Submit(state, "Alice", "Song");
        RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        var before = Snapshot(state);

        Assert.Throws<InvalidOperationException>(() => RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id));

        Assert.Equal(before, Snapshot(state));
        Assert.Single(state.Setlists[0].Entries);
    }

    [Theory]
    [InlineData("unknown-request")]
    [InlineData("duplicate-request")]
    [InlineData("different-show")]
    [InlineData("unknown-song")]
    [InlineData("invalid-index")]
    [InlineData("unknown-merge")]
    [InlineData("active-merge")]
    [InlineData("merge-and-index")]
    [InlineData("rejected-request")]
    public void ArrangeValidatesTheEntireBatchBeforeMutating(string failure)
    {
        var state = CreateState();
        var first = Submit(state, "Alice", "Song");
        var second = Submit(state, "Bob", "Song");
        var existing = SetlistOperations.AddSong(state.Setlists[0], state.Songs[0]);
        IReadOnlyCollection<Guid> ids = [first.Id, second.Id];
        var songId = state.Songs[0].Id;
        int? index = null;
        Guid? merge = null;
        switch (failure)
        {
            case "unknown-request": ids = [first.Id, Guid.NewGuid()]; break;
            case "duplicate-request": ids = [first.Id, first.Id]; break;
            case "different-show": second.SetlistId = state.Setlists[1].Id; break;
            case "unknown-song": songId = Guid.NewGuid(); break;
            case "invalid-index": index = 2; break;
            case "unknown-merge": merge = Guid.NewGuid(); break;
            case "active-merge":
                SetlistOperations.Start(state.Setlists[0], existing.Id, Now);
                merge = existing.Id;
                break;
            case "merge-and-index": index = 0; merge = existing.Id; break;
            case "rejected-request": RequestOperations.Reject(state, second.Id, "Unavailable"); break;
        }
        var before = Snapshot(state);

        Assert.Throws<InvalidOperationException>(() => RequestOperations.Arrange(state, ids, songId, index, merge));

        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void OutstandingLimitsUseNormalizedNameAndWorldAndReleaseAfterCompletion()
    {
        var state = CreateState();
        state.RequestSettings.MaxOutstandingPerPerson = 1;
        var request = Submit(state, "Ａｌｉｃｅ", "First", world: "Ｗｏｒｌｄ");
        var entry = RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        Assert.Throws<InvalidOperationException>(() => Submit(state, " alice ", "Second", world: "world"));
        var otherWorld = Submit(state, "Alice", "Other world", world: "Other");
        Assert.Equal(RequestStatus.Pending, otherWorld.Status);
        SetlistOperations.Start(state.Setlists[0], entry.Id, Now);
        Assert.True(RequestOperations.IsOutstanding(state, request));
        Assert.Throws<InvalidOperationException>(() => Submit(state, "ALICE", "Second", world: "WORLD"));
        SetlistOperations.Finish(state.Setlists[0], entry.Id, Now.AddMinutes(1));

        var next = Submit(state, "alice", "Second", world: "world");

        Assert.False(RequestOperations.IsOutstanding(state, request));
        Assert.True(RequestOperations.IsOutstanding(state, next));
    }

    [Fact]
    public void MissingWorldStaysEmptyAndIsNotMergedWithAKnownWorld()
    {
        var state = CreateState();
        state.RequestSettings.MaxOutstandingPerPerson = 1;
        var unknown = Submit(state, "Alice", "First");
        var known = Submit(state, "Alice", "Second", world: "World");
        Assert.Empty(unknown.RequesterWorld);
        Assert.Equal("World", known.RequesterWorld);
        Assert.Throws<InvalidOperationException>(() => Submit(state, "ALICE", "Third"));
    }

    [Fact]
    public void QueueCapCountsOutstandingRequestsForOnlyTheSameShow()
    {
        var state = CreateState();
        state.RequestSettings.MaxQueueSize = 2;
        var first = Submit(state, "Alice", "Song");
        var second = Submit(state, "Bob", "Song");
        RequestOperations.Defer(state, second.Id);
        Assert.Throws<InvalidOperationException>(() => Submit(state, "Carol", "Song"));
        RequestOperations.Submit(state, state.Setlists[1].Id, "Carol", "", "Song", RequestChannel.Manual, Now);
        RequestOperations.Reject(state, first.Id, "Not available");
        Submit(state, "Carol", "Another");
        Assert.Equal(4, state.Requests.Count);
    }

    [Fact]
    public void DuplicateCooldownIsShortLivedAndScopedByPersonShowAndQuery()
    {
        var state = CreateState();
        var request = Submit(state, "Alice", "Ｓｏｎｇ　Ｎａｍｅ", world: "World");
        RequestOperations.Reject(state, request.Id, "Not now");
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Submit(state, state.Setlists[0].Id, "ALICE", "world", "song   name", RequestChannel.Manual, Now.AddSeconds(29)));
        RequestOperations.Submit(state, state.Setlists[1].Id, "Alice", "World", "song name", RequestChannel.Manual, Now.AddSeconds(1));
        var later = RequestOperations.Submit(state, state.Setlists[0].Id, "Alice", "World", "song name", RequestChannel.Manual, Now.AddSeconds(30));
        Assert.NotEqual(request.Id, later.Id);
        Assert.Equal(3, state.Requests.Count);
    }

    [Fact]
    public void ReopenHonorsBothCapacityLimitsAndPreservesOriginalReceiptTime()
    {
        var state = CreateState();
        state.RequestSettings.MaxOutstandingPerPerson = 1;
        var rejected = Submit(state, "Alice", "First");
        RequestOperations.Reject(state, rejected.Id, "Unavailable");
        var pending = Submit(state, "Alice", "Second");
        var before = Snapshot(state);
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Reopen(state, rejected.Id));
        Assert.Equal(before, Snapshot(state));
        RequestOperations.Reject(state, pending.Id, "Later");
        state.RequestSettings.MaxQueueSize = 1;
        var other = Submit(state, "Bob", "Third");
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Reopen(state, rejected.Id));
        RequestOperations.Reject(state, other.Id, "Later");

        RequestOperations.Reopen(state, rejected.Id);

        Assert.Equal(RequestStatus.Pending, rejected.Status);
        Assert.Equal(Now, rejected.ReceivedAtUtc);
        Assert.Empty(rejected.ResolutionNote);
    }

    [Fact]
    public void RemovingLinkedEntryDefersEveryLinkedRequestAndRetainsSelectedVersion()
    {
        var state = CreateState();
        var first = Submit(state, "Alice", "Song");
        var second = Submit(state, "Bob", "Song");
        var entry = RequestOperations.Arrange(state, [first.Id, second.Id], state.Songs[0].Id);

        SetlistOperations.Remove(state.Setlists[0], entry.Id);
        RequestOperations.Reconcile(state);

        Assert.All(state.Requests, request =>
        {
            Assert.Equal(RequestStatus.Deferred, request.Status);
            Assert.Null(request.SetlistEntryId);
            Assert.Equal(state.Songs[0].Id, request.SongId);
            Assert.NotEmpty(request.ResolutionNote);
        });
        CatalogStore.Validate(state);
        var replacement = RequestOperations.Arrange(state, [first.Id, second.Id], state.Songs[0].Id);
        Assert.NotEqual(entry.Id, replacement.Id);
        Assert.Single(state.Setlists[0].Entries);
    }

    [Fact]
    public void DeletingShowCancelsRequestsPreservesSnapshotAndClosesReception()
    {
        var state = CreateState();
        var show = state.Setlists[0];
        var request = Submit(state, "Alice", "Song");
        RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        state.RequestSettings.IsOpen = true;
        state.RequestSettings.TargetSetlistId = show.Id;

        state.Setlists.Remove(show);
        state.SelectedSetlistId = state.Setlists[0].Id;
        RequestOperations.Reconcile(state);

        Assert.Equal(RequestStatus.Cancelled, request.Status);
        Assert.Equal(show.Name, request.SetlistName);
        Assert.Null(request.SetlistId);
        Assert.Null(request.SetlistEntryId);
        Assert.False(state.RequestSettings.IsOpen);
        Assert.Null(state.RequestSettings.TargetSetlistId);
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Reopen(state, request.Id));
        CatalogStore.Validate(state);
        var before = Snapshot(state);
        RequestOperations.Reconcile(state);
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void RemovedCatalogSongClearsRememberedRequestVersion()
    {
        var state = CreateState();
        var request = Submit(state, "Alice", "Song");
        var entry = RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        SetlistOperations.Remove(state.Setlists[0], entry.Id);
        state.Songs.RemoveAt(0);

        RequestOperations.Reconcile(state);

        Assert.Equal(RequestStatus.Deferred, request.Status);
        Assert.Null(request.SongId);
        Assert.Null(request.SetlistEntryId);
        CatalogStore.Validate(state);
    }

    [Fact]
    public void DisplayStatusTracksManualProgramStateWithoutCopyingItIntoRequestStatus()
    {
        var state = CreateState();
        var request = Submit(state, "Alice", "Song");
        Assert.Equal("待处理", RequestOperations.DisplayStatus(state, request));
        var entry = RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        Assert.Equal("已安排", RequestOperations.DisplayStatus(state, request));
        SetlistOperations.Start(state.Setlists[0], entry.Id, Now);
        Assert.Equal("进行中", RequestOperations.DisplayStatus(state, request));
        SetlistOperations.Skip(state.Setlists[0], entry.Id, Now.AddSeconds(10));
        Assert.Equal("已跳过", RequestOperations.DisplayStatus(state, request));
        Assert.False(RequestOperations.IsOutstanding(state, request));
        SetlistOperations.ResetEntry(state.Setlists[0], entry.Id);
        Assert.Equal("已安排", RequestOperations.DisplayStatus(state, request));
        SetlistOperations.Start(state.Setlists[0], entry.Id, Now.AddSeconds(20));
        SetlistOperations.Finish(state.Setlists[0], entry.Id, Now.AddSeconds(60));
        Assert.Equal("已完成", RequestOperations.DisplayStatus(state, request));
        Assert.Equal(RequestStatus.Arranged, request.Status);
    }

    [Fact]
    public void StoreValidatorRejectsCrossVersionOrMissingProgramAssociations()
    {
        var state = CreateState();
        var request = Submit(state, "Alice", "Song");
        RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        request.SongId = state.Songs[1].Id;
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
        request.SongId = state.Songs[0].Id;
        request.SetlistEntryId = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
    }

    internal static CatalogState CreateState()
    {
        var first = new ShowSetlist { Name = "Evening show" };
        return new CatalogState
        {
            SelectedSetlistId = first.Id,
            Setlists = [first, new ShowSetlist { Name = "Next show" }],
            Songs =
            [
                new SongEntry { Title = "Song", Arranger = "Version A", DurationSeconds = 120, TrackCount = 4, FilePath = Path.GetFullPath("version-a.mid"), Sha256 = new string('A', 64) },
                new SongEntry { Title = "Song", Arranger = "Version B", DurationSeconds = 90, TrackCount = 6, FilePath = Path.GetFullPath("version-b.mid"), Sha256 = new string('B', 64) },
            ],
        };
    }

    private static SongRequest Submit(CatalogState state, string name, string query, string world = "") =>
        RequestOperations.Submit(state, state.Setlists[0].Id, name, world, query, RequestChannel.Manual, Now);

    private static string Snapshot(CatalogState state) => JsonSerializer.Serialize(state);
}
