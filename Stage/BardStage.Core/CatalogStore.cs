using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BardStage.Core;

public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
    };

    public CatalogStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        FilePath = Path.Combine(Path.GetFullPath(directory), "catalog.json");
    }

    public string FilePath { get; }

    public CatalogState Load() => File.Exists(FilePath) ? ReadValidated(FilePath) : new CatalogState();

    public static CatalogState Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var version = ReadVersion(document.RootElement);
            Require(version is 1 or 2 or 3, $"不支持的数据版本 {version}，当前支持版本 1、2 和 3。");
            if (version == 3) Require(document.RootElement.EnumerateObject().Count(p => p.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase)) == 1, "版本 3 数据缺少唯一的演出记录字段。");
            var state = document.RootElement.Deserialize<CatalogState>(JsonOptions) ?? throw new InvalidDataException("曲库数据为空。");
            if (version == 1)
            {
                state.Requests ??= [];
                state.RequestSettings ??= new RequestSettings();
            }
            state.SchemaVersion = 3;
            if (version < 3)
            {
                Require(state.Sessions == null || state.Sessions.Count == 0, "旧版数据中包含不匹配的演出历史，已停止迁移以避免丢失记录。");
                state.Sessions = [];
                ValidateCore(state, importingLegacy: true);
                SessionOperations.ImportLegacy(state);
            }
            Validate(state);
            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"数据格式无效：{ex.Message}", ex);
        }
    }

    public void Save(CatalogState state)
    {
        Validate(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        // A damaged on-disk catalog must never be replaced by a newly initialized state.
        if (File.Exists(FilePath))
        {
            _ = ReadValidated(FilePath);
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var oldVersion = ReadVersion(document.RootElement);
            if (oldVersion < 3) PreserveVersionBackup(directory, oldVersion);
        }
        var temporary = Path.Combine(directory, $".catalog.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath))
                File.Replace(temporary, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temporary, FilePath);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void Validate(CatalogState state) => ValidateCore(state, importingLegacy: false);

    private static void ValidateCore(CatalogState state, bool importingLegacy)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(state.SchemaVersion == 3, $"不支持的数据版本 {state.SchemaVersion}，当前保存格式为版本 3。");
        Require(state.Songs != null && state.Setlists != null && state.Requests != null && state.RequestSettings != null, "数据缺少曲库、节目单或点歌列表与设置。");
        var songIds = new HashSet<Guid>();
        foreach (var song in state.Songs!)
        {
            Require(song != null, "曲库包含空记录。");
            Require(song!.Id != Guid.Empty && songIds.Add(song.Id), "曲目 ID 不能为空或重复。");
            RequireText(song.Title, "曲目名称");
            Require(!string.IsNullOrWhiteSpace(song.FilePath) && Path.IsPathFullyQualified(song.FilePath), $"《{song.Title}》必须具有完整的文件路径。");
            Require(song.Sha256 != null && song.Sha256.Length == 64 && song.Sha256.All(Uri.IsHexDigit), $"《{song.Title}》的 SHA256 指纹无效。");
            RequireDuration(song.DurationSeconds, song.Title);
            Require(song.TrackCount is >= 0 and <= 65535, $"《{song.Title}》的音轨数无效。");
            Require(song.PerformerCount is >= 0 and <= 8, $"《{song.Title}》的演奏人数应为 0（未填写）至 8。");
            Require(song.Aliases != null && song.Aliases.All(x => x != null), $"《{song.Title}》的别名列表无效。");
            Require(song.Arranger != null && song.Notes != null, $"《{song.Title}》的曲目资料无效。");
        }

        var setlistIds = new HashSet<Guid>();
        var entryIds = new HashSet<Guid>();
        foreach (var setlist in state.Setlists!)
        {
            Require(setlist != null, "数据包含空节目单。");
            Require(setlist!.Id != Guid.Empty && setlistIds.Add(setlist.Id), "节目单 ID 不能为空或重复。");
            RequireText(setlist.Name, "节目单名称");
            Require(setlist.Entries != null, $"《{setlist.Name}》缺少节目列表。");
            Require(setlist.GapSeconds is >= 0 and <= 3600, $"《{setlist.Name}》的歌曲间隔应为 0 至 3600 秒。");
            Require(setlist.Entries!.Count(x => x?.Status == EntryStatus.InProgress) <= 1, $"《{setlist.Name}》同时存在多个进行中的节目。");
            foreach (var entry in setlist.Entries)
            {
                Require(entry != null, $"《{setlist.Name}》包含空节目记录。");
                Require(entry!.Id != Guid.Empty && entryIds.Add(entry.Id), "节目 ID 不能为空或重复。");
                RequireText(entry.Title, "节目名称");
                Require(Enum.IsDefined(entry.Kind) && Enum.IsDefined(entry.Status), $"《{entry.Title}》的节目类型或状态无效。");
                RequireDuration(entry.DurationSeconds, entry.Title);
                Require(double.IsFinite(entry.PlaybackSpeed) && entry.PlaybackSpeed is >= 0.1 and <= 4, $"《{entry.Title}》的速度应为 0.1 至 4 倍。");
                Require(entry.Notes != null, $"《{entry.Title}》的备注数据无效。");
                if (entry.Kind == EntryKind.Song)
                    Require(entry.SongId.HasValue && songIds.Contains(entry.SongId.Value), $"《{entry.Title}》引用的曲目不存在；请先从节目单移除，再删除曲目。");
                else
                    Require(entry.SongId == null, $"环节《{entry.Title}》不应引用曲目。");
                Require(entry.Status != EntryStatus.InProgress || (entry.StartedAtUtc.HasValue && !entry.EndedAtUtc.HasValue), $"Active entry '{entry.Title}' must have a start time and no end time.");
                var missingTerminalTime = entry.Status == EntryStatus.Completed && (!entry.StartedAtUtc.HasValue || !entry.EndedAtUtc.HasValue)
                    || entry.Status == EntryStatus.Skipped && !entry.EndedAtUtc.HasValue;
                var legacyTerminal = importingLegacy || missingTerminalTime && SessionOperations.HasLegacyTerminalRecord(state, setlist.Id, entry);
                Require(entry.Status != EntryStatus.Completed || (entry.StartedAtUtc.HasValue && entry.EndedAtUtc.HasValue) || legacyTerminal, $"Completed entry '{entry.Title}' must have start and end times.");
                Require(entry.Status != EntryStatus.Skipped || entry.EndedAtUtc.HasValue || legacyTerminal, $"Skipped entry '{entry.Title}' must have an end time.");
                Require(entry.Status != EntryStatus.Queued || (!entry.StartedAtUtc.HasValue && !entry.EndedAtUtc.HasValue), $"Queued entry '{entry.Title}' must not retain prior run timestamps.");
                Require(!entry.StartedAtUtc.HasValue || !entry.EndedAtUtc.HasValue || entry.EndedAtUtc >= entry.StartedAtUtc, $"Entry '{entry.Title}' ends before it starts.");
                Require(double.IsFinite(entry.PausedSeconds) && entry.PausedSeconds >= 0, "暂停时长无效。");
                Require(!entry.PausedAtUtc.HasValue || (entry.Status == EntryStatus.InProgress && entry.PausedAtUtc >= entry.StartedAtUtc), "暂停记录必须属于进行中的节目。");
                Require(entry.Status != EntryStatus.Queued || entry.PausedSeconds == 0, "待演节目不能保留旧暂停记录。");
                Require(entry.StartedAtUtc.HasValue || entry.PausedSeconds == 0, "没有开始时间的节目不能保留暂停时长。");
                Require(!entry.StartedAtUtc.HasValue || (entry.EndedAtUtc ?? entry.PausedAtUtc) is not { } elapsedEnd
                    || entry.PausedSeconds <= (elapsedEnd - entry.StartedAtUtc.Value).TotalSeconds, "累计暂停时长不能超过节目经过的时间。");
            }
            Require(!setlist.LockedNextEntryId.HasValue || setlist.Entries.Any(x => x.Id == setlist.LockedNextEntryId && x.Status == EntryStatus.Queued), $"Setlist '{setlist.Name}' locked next entry must exist and be queued.");
        }
        Require(!state.SelectedSetlistId.HasValue || setlistIds.Contains(state.SelectedSetlistId.Value), "选中的节目单已不存在。");
        ValidateRequests(state, songIds, setlistIds);
        SessionOperations.Validate(state);
    }

    private static void ValidateRequests(CatalogState state, HashSet<Guid> songIds, HashSet<Guid> setlistIds)
    {
        var settings = state.RequestSettings;
        Require(Enum.IsDefined(settings.PlaybackMode), "自动演奏模式无效。");
        Require(settings.Channels != null && settings.Channels.All(x => Enum.IsDefined(x) && x != RequestChannel.Manual)
            && settings.Channels.Distinct().Count() == settings.Channels.Count, "点歌接收频道无效或重复。");
        RequireText(settings.Prefix, "点歌前缀");
        Require(settings.Prefix.Length <= 32 && !settings.Prefix.Any(char.IsControl), "点歌前缀不能包含控制字符，长度最多为 32。");
        Require(settings.Prefix == settings.Prefix.Trim(), "点歌前缀两端不能包含空白。");
        Require(settings.MaxOutstandingPerPerson is >= 1 and <= 1000, "每位观众未完成点歌上限应为 1 至 1000。");
        Require(settings.DuplicateCooldownSeconds is >= 0 and <= 86400, "重复点歌间隔应为 0 至 86400 秒。");
        Require(settings.MaxQueueSize is >= 1 and <= 10000, "每场演出的点歌队列上限应为 1 至 10000。");
        Require(!settings.TargetSetlistId.HasValue || setlistIds.Contains(settings.TargetSetlistId.Value), "点歌接收的目标节目单已经不存在。");
        Require(!settings.IsOpen || settings.TargetSetlistId.HasValue, "开放点歌前必须选择接收的目标节目单。");
        Require(!settings.IsOpen || settings.Channels.Count > 0, "开放点歌前必须至少选择一个接收频道。");
        var requestIds = new HashSet<Guid>();
        foreach (var request in state.Requests)
        {
            Require(request != null, "点歌列表包含空记录。");
            Require(request.Id != Guid.Empty && requestIds.Add(request.Id), "点歌 ID 不能为空或重复。");
            Require(Enum.IsDefined(request.Channel) && Enum.IsDefined(request.Status), "点歌频道或处理状态无效。");
            RequireText(request.SetlistName, "点歌所属演出名称");
            RequireText(request.RequesterName, "点歌观众姓名");
            RequireText(request.Query, "点歌曲名");
            Require(request.RequesterName.Length <= 128 && !request.RequesterName.Any(char.IsControl), "点歌观众姓名无效。");
            Require(request.RequesterWorld != null && request.RequesterWorld.Length <= 128 && !request.RequesterWorld.Any(char.IsControl), "点歌观众服务器名称无效。");
            Require(request.Query.Length <= 256 && !request.Query.Any(char.IsControl), "点歌曲名不能包含控制字符，长度最多为 256。");
            Require(request.ResolutionNote != null && request.ResolutionNote.Length <= 2048, "点歌处理备注无效。");
            Require(!request.SongId.HasValue || songIds.Contains(request.SongId.Value), "点歌关联的曲目已经不存在，请先修复关联。");
            if (!request.SetlistId.HasValue)
            {
                Require(request.Status == RequestStatus.Cancelled && !request.SetlistEntryId.HasValue, "没有所属演出的点歌必须标记为已取消。");
                continue;
            }
            Require(setlistIds.Contains(request.SetlistId.Value), "点歌所属节目单已经不存在，请先修复关联。");
            if (request.Status == RequestStatus.Arranged)
            {
                var entry = state.Setlists.First(x => x.Id == request.SetlistId.Value).Entries.FirstOrDefault(x => x.Id == request.SetlistEntryId);
                Require(request.SongId.HasValue && entry?.Kind == EntryKind.Song && entry.SongId == request.SongId, "已安排的点歌必须关联到同一节目单中的相同曲目版本。");
            }
            else
            {
                Require(!request.SetlistEntryId.HasValue, "尚未安排的点歌不应关联演出节目。");
            }
        }
    }

    private void PreserveVersionBackup(string directory, int version)
    {
        var backup = Path.Combine(directory, $"catalog.v{version}.bak");
        if (File.Exists(backup))
        {
            _ = ReadValidated(backup);
            using var existing = JsonDocument.Parse(File.ReadAllText(backup));
            Require(ReadVersion(existing.RootElement) == version, $"永久旧版备份不是版本 {version}，已停止保存。请检查 catalog.v{version}.bak。");
            return;
        }
        var temporary = Path.Combine(directory, $".catalog.v{version}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporary, backup);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static CatalogState ReadValidated(string path)
    {
        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"无法打开曲库 '{path}'：{ex.Message} 原文件已保留。请检查内容，或从 '{path}.bak' 恢复后重新载入插件。", ex);
        }
    }

    private static int ReadVersion(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object, "数据根节点必须为对象。");
        var versions = root.EnumerateObject().Where(x => x.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(versions.Length == 1, "数据必须包含唯一的版本标识。");
        Require(versions[0].Value.ValueKind == JsonValueKind.Number, "数据版本标识必须为整数。");
        Require(versions[0].Value.TryGetInt32(out var version), "数据版本标识必须为整数。");
        return version;
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void RequireText(string? text, string name) => Require(!string.IsNullOrWhiteSpace(text), $"{name}不能为空。");

    private static void RequireDuration(double seconds, string title) =>
        Require(double.IsFinite(seconds) && seconds is >= 0 and <= 604800, $"《{title}》的时长应为 0 秒至 7 天。");
}
