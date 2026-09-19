using System.Security.Cryptography;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace BardStage.Core;

public sealed class MidiLibraryImporter
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public ImportReport Import(IEnumerable<string> paths, CatalogState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);
        var report = new ImportReport();
        var seenPaths = new HashSet<string>(PathComparer);
        foreach (var path in ExpandPaths(paths, report, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenPaths.Add(path))
            {
                report.Duplicates++;
                continue;
            }
            try
            {
                // Hash and parse the same bytes so an external file edit cannot produce mismatched metadata.
                if (new FileInfo(path).Length > 64 * 1024 * 1024)
                    throw new InvalidDataException("MIDI 文件超过 64 MB，请先拆分文件。");
                var bytes = File.ReadAllBytes(path);
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var samePath = state.Songs.FirstOrDefault(x => PathComparer.Equals(x.FilePath, path));
                if (samePath != null)
                {
                    if (string.Equals(samePath.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                        report.Duplicates++;
                    else
                        report.Errors.Add($"{path}: 原路径的文件内容已改变。请将新版本另存为独立文件后导入，原曲库记录会保留。");
                    continue;
                }

                var sameContent = state.Songs.FirstOrDefault(x => string.Equals(x.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                if (sameContent != null)
                {
                    if (!File.Exists(sameContent.FilePath))
                    {
                        sameContent.FilePath = path;
                        report.Relocated++;
                    }
                    else
                    {
                        report.Duplicates++;
                    }
                    continue;
                }

                using var stream = new MemoryStream(bytes, writable: false);
                var midi = MidiFile.Read(stream);
                var duration = (TimeSpan)midi.GetDuration<MetricTimeSpan>();
                var noteTracks = midi.GetTrackChunks().Count(x => x.GetNotes().Any());
                cancellationToken.ThrowIfCancellationRequested();
                if (duration.TotalSeconds > 604800)
                    throw new InvalidDataException("MIDI 时长超过 7 天。");
                state.Songs.Add(new SongEntry
                {
                    Title = Path.GetFileNameWithoutExtension(path),
                    FilePath = path,
                    Sha256 = hash,
                    DurationSeconds = duration.TotalSeconds,
                    TrackCount = noteTracks,
                    PerformerCount = 0,
                });
                report.Added++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report.Errors.Add($"{path}: {ex.Message}");
            }
        }
        return report;
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths, ImportReport report, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, bool IsExplicit)>(paths.Select(x => (x, true)).Reverse());
        var visitedDirectories = new HashSet<string>(PathComparer);
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = item.Path;
            string path;
            try
            {
                path = Path.GetFullPath(input);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                report.Errors.Add($"{input}: {ex.Message}");
                continue;
            }

            if (Directory.Exists(path))
            {
                if (!visitedDirectories.Add(path))
                    continue;
                try
                {
                    foreach (var child in Directory.GetFileSystemEntries(path).OrderByDescending(x => x, PathComparer))
                    {
                        if (Directory.Exists(child) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            report.Errors.Add($"{child}: 已跳过链接目录；可直接选择目标目录导入。");
                            continue;
                        }
                        pending.Push((child, false));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Errors.Add($"{path}: {ex.Message}");
                }
                continue;
            }

            if (!File.Exists(path))
            {
                report.Errors.Add($"{path}: 文件或目录不存在，或无法访问。");
                continue;
            }
            if (IsMidi(path))
                yield return path;
            else if (item.IsExplicit)
                report.Errors.Add($"{path}: 仅支持 .mid 和 .midi 文件。");
        }
    }

    private static bool IsMidi(string path) =>
        Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase);
}
