using BardStage.Core;

namespace BardStage;

public sealed partial class StageController
{
    public bool DeleteEntries(Guid showId, IEnumerable<Guid> entryIds, bool finishedOnly = false) =>
        Change(s => CleanupOperations.DeleteEntries(s, showId, entryIds, QueuePlayer?.ActiveEntryId, finishedOnly), "节目已删除，曲库与演出记录保留");

    public bool DeleteRequests(IEnumerable<Guid> requestIds) =>
        Change(s => CleanupOperations.DeleteRequests(s, requestIds, QueuePlayer?.ActiveEntryId), "点歌记录已删除，已排队节目保留");

    public bool DeleteAttempts(Guid sessionId, IEnumerable<Guid> attemptIds) =>
        Change(s => CleanupOperations.DeleteAttempts(s, sessionId, attemptIds), "演出明细已删除");

    public bool DeleteSession(Guid sessionId) =>
        Change(s => CleanupOperations.DeleteSession(s, sessionId), "演出记录已删除，节目单和曲库保留");

    public bool DeleteShow(Guid showId) => Change(s =>
    {
        if (QueueShow?.Id == showId && (QueuePlayer?.IsRunning == true || QueuePlayer?.IsLoading == true || QueuePlayer?.ActiveEntryId != null))
            throw new InvalidOperationException("请先停止此节目单的自动连播，再删除节目单。");
        CleanupOperations.DeleteShow(s, showId, DateTimeOffset.UtcNow);
    }, "节目单已删除，曲库和演出记录保留");
}
