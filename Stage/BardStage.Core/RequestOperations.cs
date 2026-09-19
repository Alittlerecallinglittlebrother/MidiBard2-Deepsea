using System.Text;

namespace BardStage.Core;

public static class RequestOperations
{
    public static SongRequest Submit(CatalogState state, Guid setlistId, string name, string world, string query, RequestChannel channel, DateTimeOffset now)
    {
        var show = GetShow(state, setlistId);
        var settings = state.RequestSettings;
        ValidateInput(name, 128, "观众姓名");
        ValidateInput(world, 128, "服务器名称", allowEmpty: true);
        ValidateInput(query, 256, "点歌曲名");
        if (!Enum.IsDefined(channel))
            throw new InvalidOperationException("点歌来源频道无效。");
        if (channel != RequestChannel.Manual)
        {
            if (!settings.IsOpen)
                throw new InvalidOperationException("当前未开放聊天点歌。");
            if (!settings.Channels.Contains(channel))
                throw new InvalidOperationException("此聊天频道未开启点歌接收。");
            if (settings.TargetSetlistId != setlistId)
                throw new InvalidOperationException("这条点歌所属的接收节目单已经改变，请手动登记。");
        }

        EnsureCapacity(state, show.Id, name, world);
        if (settings.DuplicateCooldownSeconds > 0 && state.Requests.Any(x =>
            x.SetlistId == show.Id && SamePerson(x, name, world) && Normalize(x.Query) == Normalize(query)
            && (now - x.ReceivedAtUtc).TotalSeconds < settings.DuplicateCooldownSeconds))
            throw new InvalidOperationException($"该观众刚刚点过相同曲目，请等待 {settings.DuplicateCooldownSeconds} 秒后重试。");

        var request = new SongRequest
        {
            SetlistId = show.Id,
            SetlistName = show.Name,
            RequesterName = name.Trim(),
            RequesterWorld = world.Trim(),
            Query = query.Trim(),
            Channel = channel,
            ReceivedAtUtc = now,
            Status = RequestStatus.Pending,
        };
        state.Requests.Add(request);
        return request;
    }

    public static IReadOnlyList<SongEntry> FindMatches(CatalogState state, string query)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0)
            return [];
        return state.Songs
            .Select(song => new { Song = song, Names = song.Aliases.Prepend(song.Title).Select(Normalize).ToArray() })
            .Select(x => new { x.Song, Exact = x.Names.Any(n => n == normalized), Partial = x.Names.Any(n => n.Contains(normalized, StringComparison.Ordinal)) })
            .Where(x => x.Partial)
            .OrderByDescending(x => x.Exact)
            .ThenBy(x => x.Song.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Song.Arranger, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Song.Id)
            .Select(x => x.Song)
            .ToArray();
    }

    public static SetlistEntry Arrange(CatalogState state, IReadOnlyCollection<Guid> requestIds, Guid songId, int? insertIndex = null, Guid? mergeEntryId = null)
    {
        if (requestIds == null || requestIds.Count == 0)
            throw new InvalidOperationException("请至少选择一条待处理点歌。");
        if (requestIds.Distinct().Count() != requestIds.Count)
            throw new InvalidOperationException("选中的点歌记录存在重复。");
        var requests = requestIds.Select(id => GetRequest(state, id)).ToArray();
        if (requests.Any(x => x.Status is not (RequestStatus.Pending or RequestStatus.Deferred)))
            throw new InvalidOperationException("只能安排待处理或暂缓的点歌；已经安排的记录不能重复加入节目单。");
        if (requests.Any(x => !x.SetlistId.HasValue) || requests.Select(x => x.SetlistId).Distinct().Count() != 1)
            throw new InvalidOperationException("合并安排的点歌必须属于同一份现有节目单。");
        var show = GetShow(state, requests[0].SetlistId!.Value);
        var song = state.Songs.FirstOrDefault(x => x.Id == songId)
            ?? throw new InvalidOperationException("所选曲目版本已经不存在，请重新选择。");
        if (insertIndex.HasValue && (insertIndex < 0 || insertIndex > show.Entries.Count))
            throw new InvalidOperationException("插入位置超出节目单范围。");
        if (insertIndex.HasValue && mergeEntryId.HasValue)
            throw new InvalidOperationException("插入位置与合并已有节目不能同时指定。");

        SetlistEntry? entry = null;
        if (mergeEntryId.HasValue)
        {
            entry = show.Entries.FirstOrDefault(x => x.Id == mergeEntryId.Value)
                ?? throw new InvalidOperationException("要合并的节目已经不存在。");
            if (entry.Kind != EntryKind.Song || entry.SongId != songId || entry.Status != EntryStatus.Queued)
                throw new InvalidOperationException("只能合并到仍在排队且曲目版本完全相同的歌曲节目。");
        }

        // Mutations start only after the entire batch and destination have passed validation.
        if (entry == null)
        {
            entry = SetlistOperations.AddSong(show, song);
            if (insertIndex.HasValue && insertIndex.Value < show.Entries.Count - 1)
                SetlistOperations.Move(show, entry.Id, insertIndex.Value);
        }
        foreach (var request in requests)
        {
            request.SongId = song.Id;
            request.SetlistEntryId = entry.Id;
            request.Status = RequestStatus.Arranged;
            request.ResolutionNote = string.Empty;
        }
        return entry;
    }

    public static void Defer(CatalogState state, Guid requestId)
    {
        var request = GetRequest(state, requestId);
        RequireUnarranged(request);
        request.Status = RequestStatus.Deferred;
        request.ResolutionNote = string.Empty;
    }

    public static void Reject(CatalogState state, Guid requestId, string reason)
    {
        var request = GetRequest(state, requestId);
        RequireUnarranged(request);
        ValidateInput(reason, 2048, "处理备注", allowEmpty: true, allowNewlines: true);
        request.Status = RequestStatus.Rejected;
        request.SetlistEntryId = null;
        request.ResolutionNote = reason.Trim();
    }

    public static void Reopen(CatalogState state, Guid requestId)
    {
        var request = GetRequest(state, requestId);
        if (request.Status is not (RequestStatus.Rejected or RequestStatus.Cancelled))
            throw new InvalidOperationException("只有已拒绝或已取消的点歌可以重新开放处理。");
        if (!request.SetlistId.HasValue)
            throw new InvalidOperationException("原节目单已经删除，请在新的节目单中重新登记点歌。");
        var show = GetShow(state, request.SetlistId.Value);
        EnsureCapacity(state, show.Id, request.RequesterName, request.RequesterWorld);
        request.Status = RequestStatus.Pending;
        request.SetlistEntryId = null;
        request.ResolutionNote = string.Empty;
    }

    public static void Reconcile(CatalogState state)
    {
        var shows = state.Setlists.ToDictionary(x => x.Id);
        var songs = state.Songs.Select(x => x.Id).ToHashSet();
        foreach (var request in state.Requests)
        {
            if (!request.SetlistId.HasValue || !shows.TryGetValue(request.SetlistId.Value, out var show))
            {
                if (request.Status != RequestStatus.Cancelled || request.SetlistId.HasValue || request.SetlistEntryId.HasValue)
                    request.ResolutionNote = "关联节目单已删除，点歌已取消。";
                request.Status = RequestStatus.Cancelled;
                request.SetlistId = null;
                request.SetlistEntryId = null;
            }
            else if (request.SetlistEntryId.HasValue || request.Status == RequestStatus.Arranged)
            {
                var entry = show.Entries.FirstOrDefault(x => x.Id == request.SetlistEntryId);
                if (entry == null || entry.Kind != EntryKind.Song || entry.SongId != request.SongId)
                {
                    request.Status = RequestStatus.Deferred;
                    request.SetlistEntryId = null;
                    request.ResolutionNote = "关联节目已移除或更换曲目，请重新安排。";
                }
            }
            if (request.SongId.HasValue && !songs.Contains(request.SongId.Value))
                request.SongId = null;
        }
        if (!state.RequestSettings.TargetSetlistId.HasValue || !shows.ContainsKey(state.RequestSettings.TargetSetlistId.Value))
        {
            state.RequestSettings.IsOpen = false;
            state.RequestSettings.TargetSetlistId = null;
        }
    }

    public static string DisplayStatus(CatalogState state, SongRequest request) => request.Status switch
    {
        RequestStatus.Pending => "待处理",
        RequestStatus.Deferred => "暂缓",
        RequestStatus.Rejected => "已拒绝",
        RequestStatus.Cancelled => "已取消",
        RequestStatus.Arranged => LinkedEntry(state, request)?.Status switch
        {
            EntryStatus.Queued => "已安排",
            EntryStatus.InProgress => "进行中",
            EntryStatus.Completed => "已完成",
            EntryStatus.Skipped => "已跳过",
            _ => "暂缓",
        },
        _ => "待处理",
    };

    public static bool IsOutstanding(CatalogState state, SongRequest request)
    {
        if (!request.SetlistId.HasValue || !state.Setlists.Any(x => x.Id == request.SetlistId))
            return false;
        return request.Status is RequestStatus.Pending or RequestStatus.Deferred
            || request.Status == RequestStatus.Arranged && LinkedEntry(state, request)?.Status is EntryStatus.Queued or EntryStatus.InProgress;
    }

    internal static string Normalize(string? value) => string.Join(' ', (value ?? string.Empty).Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool SamePerson(SongRequest request, string name, string world) =>
        Normalize(request.RequesterName) == Normalize(name) && Normalize(request.RequesterWorld) == Normalize(world);

    private static void EnsureCapacity(CatalogState state, Guid showId, string name, string world)
    {
        var outstanding = state.Requests.Where(x => x.SetlistId == showId && IsOutstanding(state, x)).ToArray();
        if (outstanding.Length >= state.RequestSettings.MaxQueueSize)
            throw new InvalidOperationException($"该演出的待处理点歌已达到 {state.RequestSettings.MaxQueueSize} 条上限。");
        if (outstanding.Count(x => SamePerson(x, name, world)) >= state.RequestSettings.MaxOutstandingPerPerson)
            throw new InvalidOperationException($"该观众尚未完成的点歌已达到 {state.RequestSettings.MaxOutstandingPerPerson} 条上限。");
    }

    private static SongRequest GetRequest(CatalogState state, Guid id) => state.Requests.FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("所选点歌记录已经不存在。");

    private static ShowSetlist GetShow(CatalogState state, Guid id) => state.Setlists.FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("点歌所属节目单已经不存在。");

    private static SetlistEntry? LinkedEntry(CatalogState state, SongRequest request) =>
        state.Setlists.FirstOrDefault(x => x.Id == request.SetlistId)?.Entries.FirstOrDefault(x => x.Id == request.SetlistEntryId);

    private static void RequireUnarranged(SongRequest request)
    {
        if (request.Status is not (RequestStatus.Pending or RequestStatus.Deferred))
            throw new InvalidOperationException("只能处理待处理或暂缓的点歌；已安排的点歌请先从节目单移除。");
    }

    private static void ValidateInput(string? value, int maximumLength, string label, bool allowEmpty = false, bool allowNewlines = false)
    {
        if (value == null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength
            || value.Any(x => char.IsControl(x) && !(allowNewlines && x is '\r' or '\n')))
            throw new InvalidOperationException($"{label}无效，最多允许 {maximumLength} 个字符。" );
    }
}
