namespace BardStage.Core;

public sealed record StageTimeline(
    double CurrentPlannedSeconds,
    double CurrentElapsedSeconds,
    double CurrentRemainingSeconds,
    double CurrentOverrunSeconds,
    double RemainingSeconds,
    DateTimeOffset EstimatedEndUtc,
    double? TargetVarianceSeconds,
    int CompletedCount,
    int TotalCount);

public static class StageOperations
{
    public static SetlistEntry? Current(ShowSetlist show) =>
        show.Entries.FirstOrDefault(entry => entry.Status == EntryStatus.InProgress);

    public static SetlistEntry? Next(ShowSetlist show) =>
        show.Entries.FirstOrDefault(entry => entry.Id == show.LockedNextEntryId && entry.Status == EntryStatus.Queued)
        ?? show.Entries.FirstOrDefault(entry => entry.Status == EntryStatus.Queued);

    public static IReadOnlyList<SetlistEntry> RunOrder(ShowSetlist show)
    {
        var order = new List<SetlistEntry>();
        var current = Current(show);
        if (current != null)
            order.Add(current);
        var next = Next(show);
        if (next != null)
            order.Add(next);
        order.AddRange(show.Entries.Where(entry => entry.Status == EntryStatus.Queued && entry.Id != next?.Id));
        return order;
    }

    public static StageTimeline Timeline(ShowSetlist show, DateTimeOffset now)
    {
        var current = Current(show);
        var ordered = new ShowSetlist { Entries = RunOrder(show).ToList(), GapSeconds = show.GapSeconds };
        var planned = current == null ? 0 : SetlistOperations.TotalSeconds(new ShowSetlist { Entries = [current] });
        var elapsed = current == null ? 0 : SetlistOperations.ElapsedSeconds(current, now);
        var currentRemaining = Math.Max(0, planned - elapsed);
        var overrun = Math.Max(0, elapsed - planned);
        var remaining = SetlistOperations.RemainingSeconds(ordered, now);
        var estimatedEnd = now.AddSeconds(remaining);
        var variance = show.TargetEndUtc.HasValue ? (double?)(estimatedEnd - show.TargetEndUtc.Value).TotalSeconds : null;
        return new StageTimeline(planned, elapsed, currentRemaining, overrun, remaining, estimatedEnd, variance,
            show.Entries.Count(entry => entry.Status == EntryStatus.Completed), show.Entries.Count);
    }

    public static void StartNext(CatalogState state, Guid showId, Guid expectedEntryId, DateTimeOffset now)
    {
        var show = GetShow(state, showId);
        if (Next(show)?.Id != expectedEntryId)
            throw new InvalidOperationException("下一项节目已经改变，请核对当前下一项后再开始。");
        if (state.Setlists.Any(candidate => candidate.Entries.Any(entry => entry.Status == EntryStatus.InProgress)))
            throw new InvalidOperationException("仍有节目正在进行，请先完成或中断它，再开始下一项。");
        SetlistOperations.Start(show, expectedEntryId, now);
    }

    public static Guid? CompleteAndAdvance(CatalogState state, Guid showId, Guid activeEntryId, DateTimeOffset now)
    {
        var show = GetShow(state, showId);
        if (Current(show)?.Id != activeEntryId)
            throw new InvalidOperationException("进行中的节目已经改变，请核对当前节目后再标记完成。");
        SetlistOperations.Finish(show, activeEntryId, now);
        return Next(show)?.Id;
    }

    public static Guid? SkipAndAdvance(CatalogState state, Guid showId, Guid entryId, DateTimeOffset now)
    {
        var show = GetShow(state, showId);
        SetlistOperations.Skip(show, entryId, now);
        return Next(show)?.Id;
    }

    public static void PrepareReplay(CatalogState state, Guid showId, Guid entryId)
    {
        var show = GetShow(state, showId);
        var entry = GetEntry(show, entryId);
        if (entry.Status is not (EntryStatus.Completed or EntryStatus.Skipped))
            throw new InvalidOperationException("只有已完成或已跳过的节目可以安排补演。");
        SetlistOperations.ResetEntry(show, entryId);
        show.LockedNextEntryId = entryId;
    }

    public static SetlistEntry InsertNextSegment(CatalogState state, Guid showId, EntryKind kind, string title, double seconds)
    {
        var show = GetShow(state, showId);
        var segment = SetlistOperations.AddSegment(new ShowSetlist(), kind, title, seconds);
        var queuedOrder = RunOrder(show).Where(entry => entry.Status == EntryStatus.Queued).ToArray();
        var queuedSlots = show.Entries.Select((entry, index) => (entry, index))
            .Where(item => item.entry.Status == EntryStatus.Queued).Select(item => item.index).ToArray();

        // Materialize the previous effective queue before replacing its one available next-item lock.
        for (var index = 0; index < queuedSlots.Length; index++)
            show.Entries[queuedSlots[index]] = queuedOrder[index];
        var insertionIndex = queuedSlots.Length == 0 ? show.Entries.Count : queuedSlots[0];
        show.Entries.Insert(insertionIndex, segment);
        show.LockedNextEntryId = segment.Id;
        return segment;
    }

    private static ShowSetlist GetShow(CatalogState state, Guid showId) =>
        state.Setlists.FirstOrDefault(show => show.Id == showId)
        ?? throw new InvalidOperationException("所选演出节目单已经不存在。");

    private static SetlistEntry GetEntry(ShowSetlist show, Guid entryId) =>
        show.Entries.FirstOrDefault(entry => entry.Id == entryId)
        ?? throw new InvalidOperationException("所选演出节目已经不存在。");
}
