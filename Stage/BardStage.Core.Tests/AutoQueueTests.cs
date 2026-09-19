using BardStage.Core;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class AutoQueueTests
{
    private static CatalogState State()
    {
        var show = new ShowSetlist { Name = "Queue" };
        return new CatalogState
        {
            Setlists = [show], SelectedSetlistId = show.Id,
            Songs = [new SongEntry { Title = "Alpha", Aliases = ["A"], FilePath = "a.mid" }, new SongEntry { Title = "Alpha Extra", FilePath = "b.mid" }],
            RequestSettings = new RequestSettings { AutoArrange = true, IsOpen = true, TargetSetlistId = show.Id, MaxOutstandingPerPerson = 10 },
        };
    }

    [Theory]
    [InlineData("Alpha", "Alpha")]
    [InlineData("a", "Alpha")]
    [InlineData(" Extra ", "Alpha Extra")]
    [InlineData("Alp", null)]
    [InlineData("Unknown", null)]
    public void MatchRequiresUniqueBestCandidate(string query, string? expected)
        => Assert.Equal(expected, AutoQueueOperations.Match(State(), query)?.Title);

    [Fact]
    public void DuplicateTitlesRequireVersionChoice()
    {
        var state = State(); state.Songs.Add(new SongEntry { Title = "Alpha", FilePath = "other.mid" });
        Assert.Null(AutoQueueOperations.Match(state, "Alpha"));
    }

    [Fact]
    public void PendingRequestsAreArrangedOnceInReceptionOrder()
    {
        var state = State(); var show = state.Setlists[0]; var now = DateTimeOffset.UtcNow;
        var second = RequestOperations.Submit(state, show.Id, "Two", "", "Alpha Extra", RequestChannel.Say, now.AddSeconds(1));
        var first = RequestOperations.Submit(state, show.Id, "One", "", "Alpha", RequestChannel.Say, now);
        AutoQueueOperations.ArrangePending(state); AutoQueueOperations.ArrangePending(state);
        Assert.Equal(new[] { first.SetlistEntryId, second.SetlistEntryId }, show.Entries.Select(e => (Guid?)e.Id));
        Assert.All(state.Requests, r => Assert.Equal(RequestStatus.Arranged, r.Status));
    }

    [Fact]
    public void OlderResolvedRequestGoesBeforeLaterWaitingSongsWithoutInterruptingCurrent()
    {
        var state = State(); var show = state.Setlists[0]; var now = DateTimeOffset.UtcNow;
        var older = RequestOperations.Submit(state, show.Id, "One", "", "Missing", RequestChannel.Say, now);
        RequestOperations.Submit(state, show.Id, "Two", "", "Alpha", RequestChannel.Say, now.AddSeconds(1));
        var later = RequestOperations.Submit(state, show.Id, "Three", "", "Alpha Extra", RequestChannel.Say, now.AddSeconds(2));
        AutoQueueOperations.ArrangePending(state);
        var current = show.Entries[0]; SetlistOperations.Start(show, current.Id, now.AddSeconds(3));
        AutoQueueOperations.ArrangeInOrder(state, older.Id, state.Songs[0].Id);
        Assert.Equal(current.Id, show.Entries[0].Id);
        Assert.Equal(EntryStatus.InProgress, current.Status);
        Assert.Equal(older.SetlistEntryId, show.Entries[1].Id);
        Assert.Equal(later.SetlistEntryId, show.Entries[2].Id);
    }
}
