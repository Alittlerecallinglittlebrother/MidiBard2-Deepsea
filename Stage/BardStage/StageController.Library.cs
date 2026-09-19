using BardStage.Core;

namespace BardStage;

public interface ILibraryEdit : IDisposable
{
    void Commit();
}

public sealed partial class StageController
{
    public Func<IReadOnlyList<SongEntry>, IReadOnlyList<SongEntry>, ILibraryEdit>? PreparePlayerLibraryEdit { get; set; }
    public Func<IReadOnlyList<string>, string?>? PlayerLibraryRemovalIssue { get; set; }
    private bool importingPlayerLibrary;
    private HashSet<string>? lastPlayerLibraryInputs;
    private Dictionary<string, PlayerLibraryFileStamp> retryPlayerLibraryFiles = new(StringComparer.OrdinalIgnoreCase);

    // Import warnings are not a failed transaction. Keep the committed result separate from
    // StatusIsError, which can also be changed by playback, chat, or an unrelated UI operation.
    public bool LastPlayerLibrarySyncSaved { get; private set; }

    public bool CanDeleteSong(Guid songId) => !IsBusy && !IsReadOnly && Room?.IsRemote != true
        && !State.Setlists.SelectMany(s => s.Entries).Any(e => e.SongId == songId && (e.Status == EntryStatus.InProgress || e.Id == QueuePlayer?.ActiveEntryId))
        && PlayerLibraryRemovalIssue?.Invoke(State.Songs.Where(s => s.Id == songId).Select(s => s.FilePath).ToArray()) == null;

    public bool DeleteSong(Guid songId) => DeleteSongs([songId]);

    public bool ClearLibrary() => DeleteSongs(State.Songs.Select(s => s.Id).ToArray());

    private bool DeleteSongs(IReadOnlyCollection<Guid> ids) => Change(s =>
    {
        var paths = s.Songs.Where(song => ids.Contains(song.Id)).Select(song => song.FilePath).ToArray();
        if (PlayerLibraryRemovalIssue?.Invoke(paths) is { } issue) throw new InvalidOperationException(issue);
        LibraryOperations.DeleteSongs(s, ids, QueuePlayer?.ActiveEntryId);
    }, "已从曲库及节目单移除；MIDI 原文件和演出记录保留");

    public bool SynchronizePlayerLibrary(IEnumerable<string> paths, bool force = false)
    {
        if (disposed || IsReadOnly || IsBusy || Room?.IsRemote == true) return false;
        var inputs = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var keep = inputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Remember attempts, not only fully error-free imports. Otherwise one permanently bad
        // file starts another save/import every second and repeatedly disables the entire UI.
        // Only unresolved files need a cheap metadata check; never rehash a whole library here.
        if (!force && lastPlayerLibraryInputs?.SetEquals(keep) == true
            && retryPlayerLibraryFiles.All(pair => PlayerLibraryFileStamp.Read(pair.Key) == pair.Value)) return false;
        var snapshot = Clone(State);
        try
        {
            LibraryOperations.DeleteSongs(snapshot, snapshot.Songs.Where(s => !keep.Contains(s.FilePath)).Select(s => s.Id).ToArray(), QueuePlayer?.ActiveEntryId);
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, true); return false; }
        var knownPaths = snapshot.Songs.Select(s => s.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = inputs.Where(p => !knownPaths.Contains(p)).ToArray();
        // Capture before reading so a failed file repaired during the scan is retried next poll.
        retryPlayerLibraryFiles = newPaths.ToDictionary(p => p, PlayerLibraryFileStamp.Read, StringComparer.OrdinalIgnoreCase);
        lastPlayerLibraryInputs = keep;
        LastPlayerLibrarySyncSaved = false;
        importingPlayerLibrary = true;
        importTask = Task.Run(() =>
        {
            var report = new MidiLibraryImporter().Import(newPaths, snapshot, lifetime.Token);
            snapshot.SharedLibraryInitialized = true;
            return (snapshot, report);
        }, lifetime.Token);
        SetStatus("正在同步 MidiBard 曲库...");
        return true;
    }

    private void CompletePlayerLibrarySynchronization(CatalogState state)
    {
        LastPlayerLibrarySyncSaved = true;
        foreach (var song in state.Songs) retryPlayerLibraryFiles.Remove(song.FilePath);
    }

    private readonly record struct PlayerLibraryFileStamp(bool Exists, long Length, long LastWriteTicks)
    {
        public static PlayerLibraryFileStamp Read(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? new(true, file.Length, file.LastWriteTimeUtc.Ticks) : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
        }
    }

    private void SaveWithPlayerLibrary(CatalogState state, CatalogState previous, bool fromPlayer = false)
    {
        var changed = !state.Songs.Select(s => s.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(previous.Songs.Select(s => s.FilePath));
        if (fromPlayer || !changed || PreparePlayerLibraryEdit == null) { store.Save(state); return; }
        CatalogStore.Validate(state);
        using var edit = PreparePlayerLibraryEdit(previous.Songs, state.Songs);
        store.Save(state);
        edit.Commit();
    }
}
