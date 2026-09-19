using BardStage.Core;
using BardStage.Core.Rooms;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private void ConfirmDeleteEntry(ShowSetlist show, SetlistEntry entry, bool roomQueue = false)
    {
        var showId = show.Id; var entryId = entry.Id;
        Confirm($"删除节目《{entry.Title}》？关联点歌将标为已取消，曲库和演出记录保留。", () =>
        {
            var success = roomQueue ? controller.QueueCommand(RoomAction.DeleteFinished, entry: entryId)
                : controller.DeleteEntries(showId, [entryId]);
            if (success && selectedEntryId == entryId) selectedEntryId = null;
        });
    }

    private void ConfirmClearFinished(ShowSetlist show, bool roomQueue = false)
    {
        var showId = show.Id;
        var ids = show.Entries.Where(e => e.Status is EntryStatus.Completed or EntryStatus.Skipped).Select(e => e.Id).ToArray();
        Confirm($"清理 {ids.Length} 条已结束节目？待演节目、曲库和演出记录保留。", () =>
        {
            if (roomQueue) controller.QueueCommand(RoomAction.ClearFinished);
            else controller.DeleteEntries(showId, ids, true);
        });
    }

    private void ConfirmDeleteRequest(SongRequest request)
    {
        var id = request.Id;
        Confirm($"删除《{request.Query}》的点歌记录？已排队的节目和演出记录保留。", () =>
        {
            if (!controller.DeleteRequests([id])) return;
            checkedRequests.Remove(id);
            if (focusedRequestId == id) focusedRequestId = null;
        });
    }
}
