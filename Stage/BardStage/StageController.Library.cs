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

    public bool SynchronizePlayerLibrary(IEnumerable<string> paths)
    {
        if (disposed || IsReadOnly || IsBusy || Room?.IsRemote == true) return false;
        var inputs = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var keep = inputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = Clone(State);
        try
        {
            LibraryOperations.DeleteSongs(snapshot, snapshot.Songs.Where(s => !keep.Contains(s.FilePath)).Select(s => s.Id).ToArray(), QueuePlayer?.ActiveEntryId);
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, true); return false; }
        importingPlayerLibrary = true;
        importTask = Task.Run(() =>
        {
            var report = new MidiLibraryImporter().Import(inputs.Where(p => !snapshot.Songs.Any(s => s.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase))), snapshot, lifetime.Token);
            snapshot.SharedLibraryInitialized = true;
            return (snapshot, report);
        }, lifetime.Token);
        SetStatus("正在同步 MidiBard 曲库...");
        return true;
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
