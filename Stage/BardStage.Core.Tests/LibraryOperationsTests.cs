using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class LibraryOperationsTests
{
    [Fact]
    public void DeleteRemovesAllReferencesWithoutLosingPerformanceHistoryOrRequeueingRequests()
    {
        var state = Fixture(); var song = state.Songs[0]; var show = state.Setlists[0];
        var request = RequestOperations.Submit(state, show.Id, "Audience", "World", "Song", RequestChannel.Manual, DateTimeOffset.UtcNow.AddMinutes(-3));
        RequestOperations.Arrange(state, [request.Id], song.Id);
        var entry = show.Entries[0]; var before = Clone(state);
        SetlistOperations.Start(show, entry.Id, DateTimeOffset.UtcNow.AddMinutes(-2)); SessionOperations.Capture(before, state, DateTimeOffset.UtcNow.AddMinutes(-2));
        before = Clone(state);
        SetlistOperations.Finish(show, entry.Id, DateTimeOffset.UtcNow.AddMinutes(-1)); SessionOperations.Capture(before, state, DateTimeOffset.UtcNow.AddMinutes(-1));
        SetlistOperations.AddSong(show, song);
        var history = JsonSerializer.Serialize(state.Sessions);
        show.LockedNextEntryId = show.Entries[1].Id;
        LibraryOperations.DeleteSongs(state, [song.Id]); RequestOperations.Reconcile(state); AutoQueueOperations.ArrangePending(state);
        Assert.Empty(state.Songs); Assert.Empty(show.Entries); Assert.Null(show.LockedNextEntryId);
        Assert.Equal(RequestStatus.Cancelled, request.Status); Assert.Null(request.SongId); Assert.Null(request.SetlistEntryId);
        Assert.Equal(history, JsonSerializer.Serialize(state.Sessions));
        CatalogStore.Validate(state);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveOrPreparingSongMakesBulkRemovalAtomic(bool preparing)
    {
        var state = Fixture(); var show = state.Setlists[0];
        var other = new SongEntry { Title = "Other", FilePath = Path.GetFullPath("other.mid"), Sha256 = new string('B', 64) };
        state.Songs.Add(other);
        var first = SetlistOperations.AddSong(show, state.Songs[0]);
        var second = SetlistOperations.AddSong(show, other);
        if (!preparing) SetlistOperations.Start(show, second.Id, DateTimeOffset.UtcNow);
        var before = JsonSerializer.Serialize(state);
        Assert.Throws<InvalidOperationException>(() => LibraryOperations.DeleteSongs(state, state.Songs.Select(s => s.Id), preparing ? second.Id : null));
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }

    [Fact]
    public void SingleDeleteKeepsUnrelatedQueueOrderAndCancelsUnarrangedMatches()
    {
        var state = Fixture(); var song = state.Songs[0]; var show = state.Setlists[0];
        var first = SetlistOperations.AddSegment(show, EntryKind.Talk, "Opening", 10);
        SetlistOperations.AddSong(show, song);
        var last = SetlistOperations.AddSegment(show, EntryKind.Break, "Break", 10);
        var request = RequestOperations.Submit(state, show.Id, "Audience", "World", "Song", RequestChannel.Manual, DateTimeOffset.UtcNow);
        request.SongId = song.Id;
        LibraryOperations.DeleteSongs(state, [song.Id]); RequestOperations.Reconcile(state); AutoQueueOperations.ArrangePending(state);
        Assert.Equal(new[] { first.Id, last.Id }, show.Entries.Select(e => e.Id));
        Assert.Equal(RequestStatus.Cancelled, request.Status);
        CatalogStore.Validate(state);
    }

    private static CatalogState Fixture()
    {
        var song = new SongEntry { Title = "Song", FilePath = Path.GetFullPath("library.mid"), Sha256 = new string('A', 64), DurationSeconds = 90 };
        var show = new ShowSetlist { Name = "Show" };
        return new CatalogState { Songs = [song], Setlists = [show], SelectedSetlistId = show.Id, RequestSettings = new RequestSettings { TargetSetlistId = show.Id, AutoArrange = true } };
    }

    private static CatalogState Clone(CatalogState state) => JsonSerializer.Deserialize<CatalogState>(JsonSerializer.Serialize(state))!;
}
