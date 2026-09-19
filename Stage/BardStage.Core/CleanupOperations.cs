namespace BardStage.Core;

public static class CleanupOperations
{
    public static void DeleteEntries(CatalogState state, Guid showId, IEnumerable<Guid> entryIds, Guid? preparingEntryId = null, bool finishedOnly = false)
    {
        var show = state.Setlists.Single(s => s.Id == showId);
        var ids = entryIds.ToHashSet();
        var entries = ids.Select(id => show.Entries.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException("节目已经不存在，请刷新列表。")).ToArray();
        if (entries.Any(e => e.Status == EntryStatus.InProgress || e.Id == preparingEntryId))
            throw new InvalidOperationException("不能删除正在准备或演奏的节目，请先停止演奏。");
        if (finishedOnly && entries.Any(e => e.Status is not (EntryStatus.Completed or EntryStatus.Skipped)))
            throw new InvalidOperationException("只能清理已结束的节目。");
        // Detach explicitly so reconciliation cannot turn a deleted request back into pending work.
        foreach (var request in state.Requests.Where(r => r.SetlistId == showId && r.SetlistEntryId is { } id && ids.Contains(id)))
        {
            var previousStatus = RequestOperations.DisplayStatus(state, request);
            request.Status = RequestStatus.Cancelled;
            request.SetlistEntryId = null;
            request.ResolutionNote = $"关联节目已删除，原状态：{previousStatus}。";
        }
        foreach (var entry in entries) SetlistOperations.Remove(show, entry.Id);
    }

    public static void DeleteRequests(CatalogState state, IEnumerable<Guid> requestIds, Guid? preparingEntryId = null)
    {
        var ids = requestIds.ToHashSet();
        var selected = state.Requests.Where(r => ids.Contains(r.Id)).ToArray();
        if (selected.Any(r => r.SetlistEntryId.HasValue && (r.SetlistEntryId == preparingEntryId
            || state.Setlists.SelectMany(s => s.Entries).Any(e => e.Id == r.SetlistEntryId && e.Status == EntryStatus.InProgress))))
            throw new InvalidOperationException("不能删除正在准备或演奏曲目的点歌记录。");
        state.Requests.RemoveAll(r => ids.Contains(r.Id));
    }

    public static bool IsFinishedRequest(CatalogState state, SongRequest request) => request.Status is RequestStatus.Rejected or RequestStatus.Cancelled
        || request.Status == RequestStatus.Arranged && state.Setlists.SelectMany(s => s.Entries)
            .Any(e => e.Id == request.SetlistEntryId && e.Status is EntryStatus.Completed or EntryStatus.Skipped);

    public static void DeleteAttempts(CatalogState state, Guid sessionId, IEnumerable<Guid> attemptIds)
    {
        var session = state.Sessions.Single(s => s.Id == sessionId);
        var ids = attemptIds.ToHashSet();
        var selected = session.Attempts.Where(a => ids.Contains(a.Id)).ToArray();
        if (selected.Any(a => a.Outcome == AttemptOutcome.InProgress))
            throw new InvalidOperationException("不能删除正在演奏的明细，请先结束当前曲目。");
        RequireLegacyBacking(state, ids);
        session.Attempts.RemoveAll(a => ids.Contains(a.Id));
        var removedEntries = selected.Select(a => a.Program.EntryId).Except(session.Attempts.Select(a => a.Program.EntryId)).ToHashSet();
        session.Events.RemoveAll(e => e.EntryId.HasValue && removedEntries.Contains(e.EntryId.Value));
    }

    public static void DeleteSession(CatalogState state, Guid sessionId)
    {
        var session = state.Sessions.Single(s => s.Id == sessionId);
        if (session.Attempts.Any(a => a.Outcome == AttemptOutcome.InProgress)
            || !session.EndedAtUtc.HasValue && state.Setlists.Any(s => s.Id == session.SetlistId && s.Entries.Any(e => e.Status == EntryStatus.InProgress)))
            throw new InvalidOperationException("不能删除正在演奏的场次，请先结束当前曲目。");
        RequireLegacyBacking(state, session.Attempts.Select(a => a.Id).ToHashSet());
        state.Sessions.Remove(session);
    }

    public static void DeleteShow(CatalogState state, Guid showId, DateTimeOffset now)
    {
        var show = state.Setlists.Single(s => s.Id == showId);
        if (show.Entries.Any(e => e.Status == EntryStatus.InProgress))
            throw new InvalidOperationException("请先结束正在演奏的节目。");
        if (SessionOperations.Open(state, showId) != null) SessionOperations.Archive(state, showId, now);
        state.Setlists.Remove(show);
        if (state.Setlists.Count == 0) state.Setlists.Add(new ShowSetlist { Name = "新演出" });
        if (state.SelectedSetlistId == showId) state.SelectedSetlistId = state.Setlists[0].Id;
    }

    private static void RequireLegacyBacking(CatalogState state, IReadOnlySet<Guid> removedAttempts)
    {
        foreach (var show in state.Setlists)
        foreach (var entry in show.Entries)
        {
            var missingTime = entry.Status == EntryStatus.Completed && (!entry.StartedAtUtc.HasValue || !entry.EndedAtUtc.HasValue)
                || entry.Status == EntryStatus.Skipped && !entry.EndedAtUtc.HasValue;
            if (missingTime && !SessionOperations.HasLegacyTerminalRecord(state, show.Id, entry, removedAttempts))
                throw new InvalidOperationException($"请先从节目单删除缺少时间的旧版条目《{entry.Title}》，再删除对应演出记录。");
        }
    }
}
