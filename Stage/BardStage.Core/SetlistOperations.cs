namespace BardStage.Core;

public static class SetlistOperations
{
    public static SetlistEntry AddSong(ShowSetlist setlist, SongEntry song)
    {
        ArgumentNullException.ThrowIfNull(song);
        RequireDuration(song.DurationSeconds);
        var entry = new SetlistEntry
        {
            Kind = EntryKind.Song,
            SongId = song.Id,
            Title = song.Title,
            DurationSeconds = song.DurationSeconds,
            Notes = song.Notes,
        };
        setlist.Entries.Add(entry);
        return entry;
    }

    public static SetlistEntry AddSegment(ShowSetlist setlist, EntryKind kind, string title, double seconds)
    {
        if (kind is not (EntryKind.Talk or EntryKind.Break))
            throw new ArgumentException("环节类型必须为串场或休息。", nameof(kind));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("请填写环节名称。", nameof(title));
        RequireDuration(seconds);
        var entry = new SetlistEntry { Kind = kind, Title = title.Trim(), DurationSeconds = seconds };
        setlist.Entries.Add(entry);
        return entry;
    }

    public static void Move(ShowSetlist setlist, Guid entryId, int newIndex)
    {
        var entry = Find(setlist, entryId);
        if (newIndex < 0 || newIndex >= setlist.Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(newIndex), "目标位置超出节目单范围。");
        setlist.Entries.Remove(entry);
        setlist.Entries.Insert(newIndex, entry);
    }

    public static void Start(ShowSetlist setlist, Guid entryId, DateTimeOffset now)
    {
        var entry = Find(setlist, entryId);
        if (entry.Status != EntryStatus.Queued)
            throw new InvalidOperationException("只有待演节目可以开始；补演前请先重置记录。");
        if (setlist.Entries.Any(x => x.Status == EntryStatus.InProgress))
            throw new InvalidOperationException("请先结束或中断当前节目。");
        entry.Status = EntryStatus.InProgress;
        entry.StartedAtUtc = now;
        entry.EndedAtUtc = null;
        entry.PausedAtUtc = null; entry.PausedSeconds = 0;
        ClearLock(setlist, entryId);
    }

    public static void Finish(ShowSetlist setlist, Guid entryId, DateTimeOffset now)
    {
        var entry = Find(setlist, entryId);
        if (entry.Status != EntryStatus.InProgress)
            throw new InvalidOperationException("只有进行中的节目可以标记完成。");
        RequireEndTime(entry, now);
        SettlePause(entry, now);
        entry.Status = EntryStatus.Completed;
        entry.EndedAtUtc = now;
    }

    public static void Skip(ShowSetlist setlist, Guid entryId, DateTimeOffset now)
    {
        var entry = Find(setlist, entryId);
        if (entry.Status is not (EntryStatus.Queued or EntryStatus.InProgress))
            throw new InvalidOperationException("只能跳过待演节目或中断进行中的节目。");
        RequireEndTime(entry, now);
        SettlePause(entry, now);
        entry.Status = EntryStatus.Skipped;
        entry.EndedAtUtc = now;
        ClearLock(setlist, entryId);
    }

    public static void ResetEntry(ShowSetlist setlist, Guid entryId)
    {
        var entry = Find(setlist, entryId);
        if (entry.Status == EntryStatus.InProgress)
            throw new InvalidOperationException("请先中断当前节目，再重置记录。");
        entry.Status = EntryStatus.Queued;
        entry.StartedAtUtc = null;
        entry.EndedAtUtc = null;
        entry.PausedAtUtc = null; entry.PausedSeconds = 0;
    }

    public static void Pause(ShowSetlist show, Guid entryId, DateTimeOffset now)
    {
        var entry = Find(show, entryId);
        if (entry.Status != EntryStatus.InProgress) throw new InvalidOperationException("只有进行中的节目可以暂停。");
        RequireEndTime(entry, now);
        entry.PausedAtUtc ??= now;
    }

    public static void Resume(ShowSetlist show, Guid entryId, DateTimeOffset now)
    {
        var entry = Find(show, entryId);
        if (entry.Status != EntryStatus.InProgress) throw new InvalidOperationException("只有进行中的节目可以继续。");
        RequireEndTime(entry, now);
        SettlePause(entry, now);
    }

    private static void SettlePause(SetlistEntry entry, DateTimeOffset now)
    {
        if (entry.PausedAtUtc is not { } paused) return;
        if (now < paused) throw new InvalidOperationException("恢复时间不能早于暂停时间。");
        entry.PausedSeconds += (now - paused).TotalSeconds; entry.PausedAtUtc = null;
    }

    public static double ElapsedSeconds(SetlistEntry entry, DateTimeOffset now)
    {
        if (entry.StartedAtUtc is not { } started) return 0;
        var end = entry.EndedAtUtc ?? (entry.Status == EntryStatus.InProgress ? entry.PausedAtUtc ?? now : (DateTimeOffset?)null);
        return end.HasValue ? Math.Max(0, (end.Value - started).TotalSeconds - entry.PausedSeconds) : 0;
    }

    public static void Remove(ShowSetlist setlist, Guid entryId)
    {
        var entry = Find(setlist, entryId);
        if (entry.Status == EntryStatus.InProgress)
            throw new InvalidOperationException("不能移除进行中的节目，请先结束或中断。");
        setlist.Entries.Remove(entry);
        ClearLock(setlist, entryId);
    }

    public static double TotalSeconds(ShowSetlist setlist) => SumTimeline(setlist.Entries, setlist.GapSeconds, null);

    public static double RemainingSeconds(ShowSetlist setlist, DateTimeOffset now) =>
        SumTimeline(setlist.Entries.Where(x => x.Status is EntryStatus.Queued or EntryStatus.InProgress), setlist.GapSeconds, now);

    private static double SumTimeline(IEnumerable<SetlistEntry> entries, int gapSeconds, DateTimeOffset? now)
    {
        if (gapSeconds < 0 || gapSeconds > 3600)
            throw new InvalidOperationException("歌曲间隔应为 0 至 3600 秒。");
        double total = 0;
        SetlistEntry? previous = null;
        foreach (var entry in entries)
        {
            RequireDuration(entry.DurationSeconds);
            if (!double.IsFinite(entry.PlaybackSpeed) || entry.PlaybackSpeed < 0.1 || entry.PlaybackSpeed > 4)
                throw new InvalidOperationException("播放速度应为 0.1 至 4 倍。");
            var seconds = entry.Kind == EntryKind.Song ? entry.DurationSeconds / entry.PlaybackSpeed : entry.DurationSeconds;
            if (now.HasValue && entry.Status == EntryStatus.InProgress && entry.StartedAtUtc.HasValue)
                seconds = Math.Max(0, seconds - ElapsedSeconds(entry, now.Value));
            if (previous?.Kind == EntryKind.Song && entry.Kind == EntryKind.Song)
                total += gapSeconds;
            total += seconds;
            previous = entry;
        }
        return total;
    }

    private static SetlistEntry Find(ShowSetlist setlist, Guid entryId) =>
        setlist.Entries.FirstOrDefault(x => x.Id == entryId)
        ?? throw new InvalidOperationException("选中的节目已不存在。");

    private static void ClearLock(ShowSetlist setlist, Guid entryId)
    {
        if (setlist.LockedNextEntryId == entryId)
            setlist.LockedNextEntryId = null;
    }

    private static void RequireDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 604800)
            throw new ArgumentOutOfRangeException(nameof(seconds), "时长应为 0 秒至 7 天。");
    }

    private static void RequireEndTime(SetlistEntry entry, DateTimeOffset now)
    {
        if (entry.StartedAtUtc.HasValue && now < entry.StartedAtUtc.Value)
            throw new InvalidOperationException("结束时间不能早于开始时间。");
    }
}
