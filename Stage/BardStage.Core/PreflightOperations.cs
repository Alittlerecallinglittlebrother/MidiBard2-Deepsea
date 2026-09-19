using System.Security.Cryptography;
using System.Text.Json;

namespace BardStage.Core;

public enum PreflightSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record PreflightIssue(Guid? EntryId, string Title, string Code, PreflightSeverity Severity, string Message);

public sealed class PreflightReport
{
    public Guid ShowId { get; set; }
    public int AvailablePerformers { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public List<PreflightIssue> Issues { get; set; } = [];
    public int CheckedFiles { get; set; }
}

public static class PreflightOperations
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string Fingerprint(CatalogState state, Guid showId, int availablePerformers)
    {
        ValidateAttendance(availablePerformers);
        var show = GetShow(state, showId);
        var plan = StageOperations.RunOrder(show).Select(entry =>
        {
            var song = entry.Kind == EntryKind.Song ? state.Songs.FirstOrDefault(candidate => candidate.Id == entry.SongId) : null;
            return new
            {
                entry.Id,
                entry.Kind,
                entry.Status,
                entry.SongId,
                entry.Title,
                entry.DurationSeconds,
                entry.PlaybackSpeed,
                Source = song == null ? null : new { song.Id, song.FilePath, song.Sha256, song.PerformerCount },
            };
        }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            show.Id,
            show.Name,
            show.GapSeconds,
            show.LockedNextEntryId,
            AvailablePerformers = availablePerformers,
            Entries = plan,
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static PreflightReport Check(CatalogState state, Guid showId, int availablePerformers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = Fingerprint(state, showId, availablePerformers);
        var show = GetShow(state, showId);
        var plan = StageOperations.RunOrder(show);
        var report = new PreflightReport { ShowId = showId, AvailablePerformers = availablePerformers, Fingerprint = fingerprint };
        if (availablePerformers == 0)
            report.Issues.Add(new PreflightIssue(null, show.Name, "attendance_unset", PreflightSeverity.Warning,
                "尚未填写到场演奏人数，暂未比较曲目人数要求。"));
        if (plan.Count == 0)
            report.Issues.Add(new PreflightIssue(null, show.Name, "empty_plan", PreflightSeverity.Warning,
                "本场没有待演或进行中的节目。"));

        var files = new Dictionary<string, FileCheck>(PathComparer);
        foreach (var entry in plan.Where(candidate => candidate.Kind == EntryKind.Song))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var song = state.Songs.FirstOrDefault(candidate => candidate.Id == entry.SongId);
            if (song == null)
            {
                AddIssue(report, entry, "song_missing", PreflightSeverity.Error, "节目关联的曲库版本已经不存在，请重新选择曲目。");
                continue;
            }
            if (song.PerformerCount == 0)
                AddIssue(report, entry, "performer_count_unset", PreflightSeverity.Warning, "曲目尚未填写演奏人数，无法核对人数是否足够。");
            else if (availablePerformers > 0 && song.PerformerCount > availablePerformers)
                AddIssue(report, entry, "insufficient_performers", PreflightSeverity.Error,
                    $"此版本需要 {song.PerformerCount} 人，当前填写到场 {availablePerformers} 人；请调整曲目版本或补齐人员。");

            string path;
            try
            {
                path = Path.GetFullPath(song.FilePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                AddIssue(report, entry, "file_unreadable", PreflightSeverity.Error, $"MIDI 路径无效：{ex.Message}");
                continue;
            }

            if (!files.TryGetValue(path, out var result))
            {
                result = InspectFile(path, cancellationToken);
                files.Add(path, result);
            }
            if (result.Code != null)
                AddIssue(report, entry, result.Code, PreflightSeverity.Error, result.Message!);
            else if (!string.Equals(result.Hash, song.Sha256, StringComparison.OrdinalIgnoreCase))
                AddIssue(report, entry, "file_changed", PreflightSeverity.Error,
                    $"MIDI 内容与导入时的版本不一致：{path}。请将新版本另存并导入，或恢复原文件；本次检查不会更新曲库指纹。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        report.CheckedFiles = files.Count;
        report.CheckedAtUtc = DateTimeOffset.UtcNow;
        return report;
    }

    private static FileCheck InspectFile(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes)
                return new FileCheck(null, "file_too_large", $"MIDI 文件超过 64 MB，未读取内容：{path}");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                    break;
                total += count;
                if (total > MaximumFileBytes)
                    return new FileCheck(null, "file_too_large", $"MIDI 文件超过 64 MB，已停止读取：{path}");
                hash.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FileCheck(Convert.ToHexString(hash.GetHashAndReset()), null, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileCheck(null, "file_missing", $"MIDI 文件不存在：{path}。请恢复文件或修复曲库路径。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return new FileCheck(null, "file_unreadable", $"无法读取 MIDI 文件：{path}。{ex.Message}");
        }
    }

    private static void AddIssue(PreflightReport report, SetlistEntry entry, string code, PreflightSeverity severity, string message) =>
        report.Issues.Add(new PreflightIssue(entry.Id, entry.Title, code, severity, message));

    private static void ValidateAttendance(int availablePerformers)
    {
        if (availablePerformers is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(availablePerformers), "到场演奏人数应为 0（未填写）至 8。");
    }

    private static ShowSetlist GetShow(CatalogState state, Guid showId) => state.Setlists.FirstOrDefault(show => show.Id == showId)
        ?? throw new InvalidOperationException("要检查的演出节目单已经不存在。");

    private sealed record FileCheck(string? Hash, string? Code, string? Message);
}
