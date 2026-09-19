namespace BardStage.Core;

public static class LibraryOperations
{
    public static void DeleteSongs(CatalogState state, IEnumerable<Guid> songIds, Guid? preparingEntryId = null)
    {
        var ids = songIds.ToHashSet();
        var entries = state.Setlists.SelectMany(s => s.Entries).Where(e => e.SongId.HasValue && ids.Contains(e.SongId.Value)).ToArray();
        if (entries.Any(e => e.Status == EntryStatus.InProgress || e.Id == preparingEntryId))
            throw new InvalidOperationException("不能删除正在准备或演奏的曲目，请先停止演奏。");
        foreach (var show in state.Setlists)
        {
            var removedEntries = show.Entries.Where(e => e.SongId.HasValue && ids.Contains(e.SongId.Value)).Select(e => e.Id).ToArray();
            CleanupOperations.DeleteEntries(state, show.Id, removedEntries, preparingEntryId);
        }
        foreach (var request in state.Requests.Where(r => r.SongId.HasValue && ids.Contains(r.SongId.Value)))
        {
            request.SongId = null;
            request.SetlistEntryId = null;
            request.Status = RequestStatus.Cancelled;
            request.ResolutionNote = "关联曲目已从曲库删除；原点歌内容和演出记录保留。";
        }
        state.Songs.RemoveAll(s => ids.Contains(s.Id));
    }
}
