namespace BardStage.Core;

public static class AutoQueueOperations
{
    public static SongEntry? Match(CatalogState state, string query)
    {
        var matches = RequestOperations.FindMatches(state, query);
        var exact = matches.Where(s => s.Aliases.Prepend(s.Title).Any(n => RequestOperations.Normalize(n) == RequestOperations.Normalize(query))).ToArray();
        return exact.Length == 1 ? exact[0] : exact.Length == 0 && matches.Count == 1 ? matches[0] : null;
    }

    public static void ArrangePending(CatalogState state)
    {
        if (!state.RequestSettings.AutoArrange) return;
        foreach (var request in state.Requests.Where(r => r.SetlistId == state.RequestSettings.TargetSetlistId && r.Status == RequestStatus.Pending)
                     .OrderBy(r => r.ReceivedAtUtc).ToArray())
            if (Match(state, request.Query) is { } song) ArrangeInOrder(state, request.Id, song.Id);
    }

    public static SetlistEntry ArrangeInOrder(CatalogState state, Guid requestId, Guid songId)
    {
        var request = state.Requests.Single(r => r.Id == requestId);
        var show = state.Setlists.Single(s => s.Id == request.SetlistId);
        var insertion = show.Entries.FindIndex(e => e.Status == EntryStatus.Queued && state.Requests.Any(r => r.SetlistEntryId == e.Id && r.ReceivedAtUtc > request.ReceivedAtUtc));
        return RequestOperations.Arrange(state, [requestId], songId, insertion < 0 ? null : insertion);
    }
}
