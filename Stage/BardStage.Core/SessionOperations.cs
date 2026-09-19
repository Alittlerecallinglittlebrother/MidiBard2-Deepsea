using System.Text.Json;

namespace BardStage.Core;

public enum RecordSource { Manual, MidiBard, Legacy }
public enum AttemptOutcome { InProgress, Completed, Interrupted, Skipped }

public sealed class ProgramSnapshot
{
    public Guid EntryId { get; set; }
    public Guid? SongId { get; set; }
    public EntryKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Arranger { get; set; } = "";
    public int Performers { get; set; }
    public double PlannedSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public string Notes { get; set; } = "";
    public List<string> Requesters { get; set; } = [];
}

public sealed class PerformanceAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ProgramSnapshot Program { get; set; } = new();
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public double PausedSeconds { get; set; }
    public DateTimeOffset? PausedAtUtc { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public RecordSource Source { get; set; }
}

public sealed class SessionEvent
{
    public DateTimeOffset AtUtc { get; set; }
    public Guid? EntryId { get; set; }
    public string Action { get; set; } = "";
    public RecordSource Source { get; set; }
}

public sealed class ShowSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SetlistId { get; set; }
    public string ShowName { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public bool ImportedLegacy { get; set; }
    public int GapSeconds { get; set; }
    public List<ProgramSnapshot> InitialPlan { get; set; } = [];
    public List<ProgramSnapshot> FinalPlan { get; set; } = [];
    public List<PerformanceAttempt> Attempts { get; set; } = [];
    public List<SessionEvent> Events { get; set; } = [];
    public List<SongRequest> UnresolvedRequests { get; set; } = [];
}

public static class SessionOperations
{
    public static ShowSession? Open(CatalogState state, Guid showId) => state.Sessions.FirstOrDefault(s => s.SetlistId == showId && s.EndedAtUtc == null);

    public static ProgramSnapshot Snapshot(CatalogState state, SetlistEntry entry)
    {
        var song = state.Songs.FirstOrDefault(s => s.Id == entry.SongId);
        return new ProgramSnapshot
        {
            EntryId = entry.Id, SongId = entry.SongId, Kind = entry.Kind, Title = entry.Title,
            FilePath = song?.FilePath ?? "", Sha256 = song?.Sha256 ?? "", Arranger = song?.Arranger ?? "", Performers = song?.PerformerCount ?? 0,
            PlannedSeconds = entry.DurationSeconds / (entry.Kind == EntryKind.Song ? entry.PlaybackSpeed : 1),
            DurationSeconds = entry.DurationSeconds, PlaybackSpeed = entry.PlaybackSpeed, Notes = entry.Notes,
            Requesters = state.Requests.Where(r => r.SetlistEntryId == entry.Id && r.Status == RequestStatus.Arranged)
                .DistinctBy(r => (RequestOperations.Normalize(r.RequesterName), RequestOperations.Normalize(r.RequesterWorld)))
                .Select(r => r.RequesterName + (r.RequesterWorld.Length > 0 ? "@" + r.RequesterWorld : "")).ToList(),
        };
    }

    public static ShowSession Begin(CatalogState state, Guid showId, DateTimeOffset now)
    {
        if (Open(state, showId) != null) throw new InvalidOperationException("本场已有未归档的演出记录。");
        var show = GetShow(state, showId);
        var session = new ShowSession { SetlistId = showId, ShowName = show.Name, StartedAtUtc = now, GapSeconds = show.GapSeconds, InitialPlan = show.Entries.Select(e => Snapshot(state, e)).ToList() };
        session.Events.Add(new SessionEvent { AtUtc = now, Action = "开始场次" });
        state.Sessions.Add(session);
        return session;
    }

    public static void Archive(CatalogState state, Guid showId, DateTimeOffset now)
    {
        var session = Open(state, showId) ?? throw new InvalidOperationException("本场尚无演出记录。");
        var show = GetShow(state, showId);
        if (show.Entries.Any(e => e.Status == EntryStatus.InProgress) || session.Attempts.Any(a => a.Outcome == AttemptOutcome.InProgress))
            throw new InvalidOperationException("请先完成或中断当前节目，再结束归档。");
        if (now < session.StartedAtUtc || session.Attempts.Any(a => a.EndedAtUtc > now) || session.Events.Any(e => e.AtUtc > now))
            throw new InvalidOperationException("归档时间不能早于本场已有记录。");
        var finalPlan = show.Entries.Select(e => Snapshot(state, e)).ToList();
        var unresolved = state.Requests.Where(r => r.SetlistId == showId &&
            (r.Status is RequestStatus.Pending or RequestStatus.Deferred || r.Status == RequestStatus.Arranged && show.Entries.Any(e => e.Id == r.SetlistEntryId && e.Status == EntryStatus.Queued)))
            .Select(Clone).ToList();
        session.EndedAtUtc = now;
        session.FinalPlan = finalPlan;
        session.GapSeconds = show.GapSeconds;
        session.UnresolvedRequests = unresolved;
        session.Events.Add(new SessionEvent { AtUtc = now, Action = "结束归档" });
    }

    // Capture mutations at the controller transaction boundary so every existing UI entry point shares the journal.
    public static void Capture(CatalogState previous, CatalogState state, DateTimeOffset now, RecordSource source = RecordSource.Manual)
    {
        foreach (var show in state.Setlists)
        {
            var oldShow = previous.Setlists.FirstOrDefault(s => s.Id == show.Id);
            foreach (var entry in show.Entries)
            {
                var old = oldShow?.Entries.FirstOrDefault(e => e.Id == entry.Id);
                if (old?.Status == entry.Status && old?.StartedAtUtc == entry.StartedAtUtc && old?.EndedAtUtc == entry.EndedAtUtc
                    && old?.PausedAtUtc == entry.PausedAtUtc && old?.PausedSeconds == entry.PausedSeconds) continue;
                if (entry.Status == EntryStatus.Queued && old == null) continue;
                if (entry.Status == EntryStatus.Queued && old?.Status == EntryStatus.Queued) continue;
                var session = Open(state, show.Id);
                if (session == null && entry.Status == EntryStatus.Queued) continue;
                if (session == null)
                {
                    session = Begin(state, show.Id, entry.StartedAtUtc ?? entry.EndedAtUtc ?? now);
                    session.Events[0].Source = source;
                }
                var attempt = session.Attempts.LastOrDefault(a => a.Program.EntryId == entry.Id && a.Outcome == AttemptOutcome.InProgress);
                if (entry.Status != EntryStatus.Queued)
                {
                    if (attempt == null)
                    {
                        attempt = new PerformanceAttempt { Program = Snapshot(state, entry), Source = source };
                        session.Attempts.Add(attempt);
                    }
                    attempt.StartedAtUtc = entry.StartedAtUtc; attempt.EndedAtUtc = entry.EndedAtUtc;
                    attempt.PausedAtUtc = entry.PausedAtUtc; attempt.PausedSeconds = entry.PausedSeconds;
                    attempt.Outcome = entry.Status switch
                    {
                        EntryStatus.InProgress => AttemptOutcome.InProgress,
                        EntryStatus.Completed => AttemptOutcome.Completed,
                        _ => entry.StartedAtUtc.HasValue ? AttemptOutcome.Interrupted : AttemptOutcome.Skipped,
                    };
                }
                var action = entry.Status switch
                {
                    EntryStatus.Queued => "安排补演",
                    EntryStatus.Completed => "完成",
                    EntryStatus.Skipped => entry.StartedAtUtc.HasValue ? "中断" : "跳过",
                    _ => entry.PausedAtUtc.HasValue ? "暂停" : old?.PausedAtUtc.HasValue == true ? "继续" : "开始",
                };
                session.Events.Add(new SessionEvent { AtUtc = entry.EndedAtUtc ?? (action == "开始" ? entry.StartedAtUtc : null) ?? now, EntryId = entry.Id, Action = action, Source = source });
            }
        }
    }

    public static void ImportLegacy(CatalogState state)
    {
        state.Sessions ??= [];
        foreach (var show in state.Setlists.Where(s => s.Entries.Any(e => e.Status != EntryStatus.Queued)))
        {
            var records = show.Entries.Where(e => e.Status != EntryStatus.Queued).ToArray();
            var start = records.Select(e => e.StartedAtUtc ?? e.EndedAtUtc).Where(t => t.HasValue).Min() ?? DateTimeOffset.UtcNow;
            var session = Begin(state, show.Id, start); session.ImportedLegacy = true;
            session.Events.Clear();
            foreach (var entry in records)
                session.Attempts.Add(new PerformanceAttempt
                {
                    Program = Snapshot(state, entry), StartedAtUtc = entry.StartedAtUtc, EndedAtUtc = entry.EndedAtUtc, Source = RecordSource.Legacy,
                    PausedAtUtc = entry.PausedAtUtc, PausedSeconds = entry.PausedSeconds,
                    Outcome = entry.Status == EntryStatus.InProgress ? AttemptOutcome.InProgress : entry.Status == EntryStatus.Completed ? AttemptOutcome.Completed : entry.StartedAtUtc.HasValue ? AttemptOutcome.Interrupted : AttemptOutcome.Skipped,
                });
            if (records.All(e => e.Status != EntryStatus.InProgress))
            {
                var lastKnown = records.SelectMany(e => new[] { e.StartedAtUtc, e.EndedAtUtc }).Where(t => t.HasValue).Max() ?? start;
                Archive(state, show.Id, lastKnown);
                session.Events[^1].Source = RecordSource.Legacy;
            }
        }
    }

    public static ShowSetlist CopyForNext(CatalogState state, Guid sessionId)
    {
        var session = state.Sessions.FirstOrDefault(s => s.Id == sessionId) ?? throw new InvalidOperationException("所选演出归档已经不存在。");
        if (!session.EndedAtUtc.HasValue) throw new InvalidOperationException("请先将本场结束归档。");
        var show = new ShowSetlist { Name = session.ShowName + " 下一场", GapSeconds = session.GapSeconds };
        foreach (var program in session.FinalPlan)
        {
            if (program.Kind == EntryKind.Song)
            {
                var song = state.Songs.FirstOrDefault(s => s.Id == program.SongId && s.Sha256.Equals(program.Sha256, StringComparison.OrdinalIgnoreCase))
                    ?? state.Songs.FirstOrDefault(s => s.Sha256.Equals(program.Sha256, StringComparison.OrdinalIgnoreCase));
                if (song == null) throw new InvalidOperationException($"曲库中找不到《{program.Title}》原版本，请先重新导入。");
                var entry = SetlistOperations.AddSong(show, song); entry.Title = program.Title; entry.DurationSeconds = program.DurationSeconds;
                entry.PlaybackSpeed = program.PlaybackSpeed; entry.Notes = program.Notes;
            }
            else SetlistOperations.AddSegment(show, program.Kind, program.Title, program.PlannedSeconds).Notes = program.Notes;
        }
        state.Setlists.Add(show); state.SelectedSetlistId = show.Id;
        return show;
    }

    public static double ActualSeconds(PerformanceAttempt attempt, DateTimeOffset now)
    {
        if (attempt.StartedAtUtc is not { } start) return 0;
        var end = attempt.EndedAtUtc ?? (attempt.Outcome == AttemptOutcome.InProgress ? attempt.PausedAtUtc ?? now : (DateTimeOffset?)null);
        return end.HasValue ? Math.Max(0, (end.Value - start).TotalSeconds - attempt.PausedSeconds) : 0;
    }

    public static void Validate(CatalogState state)
    {
        if (state.Sessions == null) throw new InvalidDataException("缺少演出场次记录。");
        var ids = new HashSet<Guid>();
        var attempts = new HashSet<Guid>();
        var openShows = new HashSet<Guid>();
        foreach (var session in state.Sessions)
        {
            if (session == null || session.Id == Guid.Empty || !ids.Add(session.Id) || session.SetlistId == Guid.Empty || string.IsNullOrWhiteSpace(session.ShowName)
                || session.InitialPlan == null || session.FinalPlan == null || session.Attempts == null || session.Events == null || session.UnresolvedRequests == null
                || session.EndedAtUtc < session.StartedAtUtc || session.GapSeconds is < 0 or > 3600 || session.Attempts.Any(a => a == null)) throw new InvalidDataException("演出场次记录无效。");
            if (session.EndedAtUtc == null && (!openShows.Add(session.SetlistId) || !state.Setlists.Any(s => s.Id == session.SetlistId)))
                throw new InvalidDataException("请先结束归档，再删除节目单；同一节目单只能有一场未归档演出。");
            ValidatePlan(session.InitialPlan);
            ValidatePlan(session.FinalPlan);
            var recordedEntryIds = session.InitialPlan.Concat(session.FinalPlan).Select(p => p.EntryId).ToHashSet();
            var activeAttempts = 0;
            foreach (var attempt in session.Attempts)
            {
                ValidateSnapshot(attempt.Program);
                recordedEntryIds.Add(attempt.Program.EntryId);
                var legacy = session.ImportedLegacy && attempt.Source == RecordSource.Legacy;
                if (attempt.Id == Guid.Empty || !attempts.Add(attempt.Id) || !Enum.IsDefined(attempt.Source) || !Enum.IsDefined(attempt.Outcome)
                    || !double.IsFinite(attempt.PausedSeconds) || attempt.PausedSeconds < 0 || attempt.EndedAtUtc < attempt.StartedAtUtc
                    || (attempt.Source == RecordSource.Legacy && !session.ImportedLegacy)
                    || (attempt.Outcome == AttemptOutcome.InProgress && (!attempt.StartedAtUtc.HasValue || attempt.EndedAtUtc.HasValue || session.EndedAtUtc.HasValue))
                    || (!legacy && attempt.Outcome != AttemptOutcome.InProgress && !attempt.EndedAtUtc.HasValue)
                    || (!legacy && attempt.Outcome is AttemptOutcome.Completed or AttemptOutcome.Interrupted && !attempt.StartedAtUtc.HasValue)
                    || (attempt.Outcome == AttemptOutcome.Skipped && (attempt.StartedAtUtc.HasValue || attempt.PausedSeconds != 0))
                    || (attempt.PausedAtUtc.HasValue && (attempt.Outcome != AttemptOutcome.InProgress || !attempt.StartedAtUtc.HasValue || attempt.PausedAtUtc < attempt.StartedAtUtc))
                    || (attempt.StartedAtUtc.HasValue && (attempt.StartedAtUtc < session.StartedAtUtc || attempt.StartedAtUtc > session.EndedAtUtc))
                    || (attempt.EndedAtUtc.HasValue && (attempt.EndedAtUtc < session.StartedAtUtc || attempt.EndedAtUtc > session.EndedAtUtc))
                    || (attempt.StartedAtUtc.HasValue && (attempt.EndedAtUtc ?? attempt.PausedAtUtc) is { } end && attempt.PausedSeconds > (end - attempt.StartedAtUtc.Value).TotalSeconds))
                    throw new InvalidDataException("历史演奏记录无效。");
                if (attempt.Outcome == AttemptOutcome.InProgress)
                {
                    activeAttempts++;
                    var entry = state.Setlists.First(s => s.Id == session.SetlistId).Entries.FirstOrDefault(e => e.Id == attempt.Program.EntryId);
                    if (entry == null || entry.Status != EntryStatus.InProgress || entry.StartedAtUtc != attempt.StartedAtUtc
                        || entry.PausedAtUtc != attempt.PausedAtUtc || entry.PausedSeconds != attempt.PausedSeconds)
                        throw new InvalidDataException("进行中的历史记录与现场节目状态不一致。");
                }
            }
            if (activeAttempts > 1) throw new InvalidDataException("同一场次不能同时记录多个进行中的节目。");
            foreach (var item in session.Events)
                if (item == null || string.IsNullOrWhiteSpace(item.Action) || item.Action.Length > 128 || item.Action.Any(char.IsControl)
                    || !Enum.IsDefined(item.Source) || item.AtUtc < session.StartedAtUtc || item.AtUtc > session.EndedAtUtc
                    || (item.Source == RecordSource.Legacy && !session.ImportedLegacy)
                    || (item.EntryId.HasValue && !recordedEntryIds.Contains(item.EntryId.Value)))
                    throw new InvalidDataException("演出事件记录无效。");
            ValidateUnresolvedRequests(session);
        }
    }

    internal static bool HasLegacyTerminalRecord(CatalogState state, Guid showId, SetlistEntry entry, IReadOnlySet<Guid>? excludedAttempts = null) =>
        state.Sessions?.Any(session => session != null && session.ImportedLegacy && session.SetlistId == showId
            && session.Attempts != null && session.Attempts.Any(attempt => attempt != null && excludedAttempts?.Contains(attempt.Id) != true && attempt.Source == RecordSource.Legacy
                && attempt.Program?.EntryId == entry.Id && attempt.StartedAtUtc == entry.StartedAtUtc && attempt.EndedAtUtc == entry.EndedAtUtc
                && (entry.Status == EntryStatus.Completed && attempt.Outcome == AttemptOutcome.Completed
                    || entry.Status == EntryStatus.Skipped && attempt.Outcome is AttemptOutcome.Interrupted or AttemptOutcome.Skipped))) == true;

    private static void ValidatePlan(List<ProgramSnapshot> plan)
    {
        var ids = new HashSet<Guid>();
        foreach (var snapshot in plan)
        {
            ValidateSnapshot(snapshot);
            if (!ids.Add(snapshot.EntryId)) throw new InvalidDataException("历史节目计划包含重复的节目 ID。");
        }
    }

    private static void ValidateSnapshot(ProgramSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.EntryId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Title) || !Enum.IsDefined(snapshot.Kind)
            || !double.IsFinite(snapshot.PlannedSeconds) || snapshot.PlannedSeconds < 0 || snapshot.Requesters == null || snapshot.Requesters.Any(string.IsNullOrWhiteSpace)
            || snapshot.FilePath == null || snapshot.Sha256 == null || snapshot.Arranger == null || snapshot.Notes == null || snapshot.Performers is < 0 or > 8
            || !double.IsFinite(snapshot.DurationSeconds) || snapshot.DurationSeconds is < 0 or > 604800
            || !double.IsFinite(snapshot.PlaybackSpeed) || snapshot.PlaybackSpeed is < 0.1 or > 4
            || Math.Abs(snapshot.PlannedSeconds - snapshot.DurationSeconds / (snapshot.Kind == EntryKind.Song ? snapshot.PlaybackSpeed : 1)) > 0.000001)
            throw new InvalidDataException("历史节目快照无效。");
        if (snapshot.Kind == EntryKind.Song)
        {
            if (!snapshot.SongId.HasValue || snapshot.SongId == Guid.Empty || !Path.IsPathFullyQualified(snapshot.FilePath)
                || snapshot.Sha256.Length != 64 || !snapshot.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("历史歌曲快照缺少原曲目、完整路径或有效指纹。");
        }
        else if (snapshot.SongId.HasValue || snapshot.FilePath.Length > 0 || snapshot.Sha256.Length > 0)
            throw new InvalidDataException("历史串场或休息不应关联曲目文件。");
    }

    private static void ValidateUnresolvedRequests(ShowSession session)
    {
        if (!session.EndedAtUtc.HasValue && session.UnresolvedRequests.Count > 0)
            throw new InvalidDataException("尚未归档的场次不应包含冻结的待处理点歌。");
        var ids = new HashSet<Guid>();
        foreach (var request in session.UnresolvedRequests)
        {
            if (request == null || request.Id == Guid.Empty || !ids.Add(request.Id) || request.SetlistId != session.SetlistId
                || string.IsNullOrWhiteSpace(request.SetlistName) || string.IsNullOrWhiteSpace(request.RequesterName) || request.RequesterName.Length > 128 || request.RequesterName.Any(char.IsControl)
                || request.RequesterWorld == null || request.RequesterWorld.Length > 128 || request.RequesterWorld.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 256 || request.Query.Any(char.IsControl)
                || !Enum.IsDefined(request.Channel) || request.Status is not (RequestStatus.Pending or RequestStatus.Deferred or RequestStatus.Arranged)
                || request.ResolutionNote == null || request.ResolutionNote.Length > 2048 || request.SongId == Guid.Empty)
                throw new InvalidDataException("归档待处理点歌记录无效。");
            if (request.Status == RequestStatus.Arranged)
            {
                if (!request.SongId.HasValue || !session.FinalPlan.Any(p => p.EntryId == request.SetlistEntryId && p.SongId == request.SongId && p.Kind == EntryKind.Song))
                    throw new InvalidDataException("归档点歌未关联到最终节目计划中的原曲目。");
            }
            else if (request.SetlistEntryId.HasValue)
                throw new InvalidDataException("归档的待处理或暂缓点歌不应关联节目。");
        }
    }

    private static ShowSetlist GetShow(CatalogState state, Guid showId) => state.Setlists.FirstOrDefault(s => s.Id == showId)
        ?? throw new InvalidOperationException("所选演出节目单已经不存在。");

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
