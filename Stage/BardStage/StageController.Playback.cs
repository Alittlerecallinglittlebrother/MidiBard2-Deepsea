using BardStage.Core;
using System.Collections.Concurrent;

namespace BardStage;

public sealed partial class StageController
{
    private readonly ConcurrentQueue<PlaybackSignal> playbackSignals = new();
    private Guid? syncShowId, syncEntryId, syncPlaybackId;
    private long handledSequence;
    public bool SyncAvailable { get; set; }
    public bool SyncEnabled { get; private set; }
    public bool SyncBlocked { get; private set; }
    public string SyncStatus { get; private set; } = "自动同步未启用";
    public PlaybackSignal? LastPlaybackSignal { get; private set; }
    public Guid? SyncShowId => syncShowId;

    public void ConfigureSync(Guid showId, bool enabled)
    {
        if (enabled && (!SyncAvailable || IsReadOnly || !State.Setlists.Any(s => s.Id == showId))) return;
        SyncEnabled = enabled; syncShowId = enabled ? showId : null;
        syncEntryId = syncPlaybackId = null; SyncBlocked = false;
        playbackSignals.Clear();
        SyncStatus = enabled ? "已启用，等待下一次播放开始" : "自动同步已关闭";
    }

    public void ReceivePlayback(PlaybackSignal signal)
    {
        if (disposed) return;
        if (playbackSignals.Count >= 256) { SyncEnabled = false; SyncBlocked = true; SyncStatus = "播放事件积压过多，请核对节目状态后重新启用同步"; return; }
        playbackSignals.Enqueue(signal);
    }

    public void RetryPlayback() => SyncBlocked = false;

    private void DetachManualPlayback(CatalogState previous, RecordSource source)
    {
        if (source != RecordSource.Manual || !syncEntryId.HasValue) return;
        var old = previous.Setlists.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == syncEntryId);
        var current = State.Setlists.SelectMany(s => s.Entries).FirstOrDefault(e => e.Id == syncEntryId);
        if (old?.Status == current?.Status && old?.StartedAtUtc == current?.StartedAtUtc && old?.EndedAtUtc == current?.EndedAtUtc
            && old?.PausedAtUtc == current?.PausedAtUtc && old?.PausedSeconds == current?.PausedSeconds) return;
        syncEntryId = syncPlaybackId = null;
        SyncStatus = "节目已被手动处理，等待下一次播放开始";
    }

    private void DrainPlayback()
    {
        if (IsBusy || SyncBlocked) return;
        while (playbackSignals.TryPeek(out var signal))
        {
            LastPlaybackSignal = signal;
            if (signal.Sequence <= handledSequence) { playbackSignals.TryDequeue(out _); continue; }
            if (SyncEnabled && !ApplyPlayback(signal)) { SyncBlocked = true; return; }
            handledSequence = signal.Sequence; playbackSignals.TryDequeue(out _);
        }
    }

    private bool ApplyPlayback(PlaybackSignal signal)
    {
        var show = State.Setlists.FirstOrDefault(s => s.Id == syncShowId);
        if (show == null) { SyncEnabled = false; SyncStatus = "接收演出已不存在，同步已关闭"; return true; }
        if (signal.Kind == PlaybackSignalKind.Loaded) { SyncStatus = "已载入：" + Path.GetFileName(signal.FilePath); return true; }
        if (signal.Kind == PlaybackSignalKind.Started)
        {
            if (syncPlaybackId == signal.PlaybackId) return true;
            var next = StageOperations.Next(show);
            var song = State.Songs.FirstOrDefault(s => s.Id == next?.SongId);
            if (song == null || next?.Kind != EntryKind.Song || string.IsNullOrWhiteSpace(signal.FilePath)
                || !Path.GetFullPath(song.FilePath).Equals(Path.GetFullPath(signal.FilePath), StringComparison.OrdinalIgnoreCase))
            { SyncStatus = "正在播放的文件与下一项不同，未改变节目记录"; return true; }
            if (State.Setlists.Any(s => StageOperations.Current(s) != null)) { SyncStatus = "已有进行中的节目，请先核对并完成或中断"; return true; }
            var entryId = next.Id;
            if (!Change(s => StageOperations.StartNext(s, show.Id, entryId, signal.AtUtc), "已同步播放开始", RecordSource.MidiBard, signal.AtUtc))
            { SyncStatus = StatusMessage; return false; }
            syncEntryId = entryId; syncPlaybackId = signal.PlaybackId; SyncStatus = "演奏中：" + next.Title; return true;
        }
        if (syncPlaybackId != signal.PlaybackId || !syncEntryId.HasValue) return true;
        if (StageOperations.Current(show)?.Id != syncEntryId)
        { syncEntryId = syncPlaybackId = null; SyncStatus = "节目已被手动处理，等待下一次播放开始"; return true; }
        var id = syncEntryId.Value;
        var saved = signal.Kind switch
        {
            PlaybackSignalKind.Paused => Change(s => SetlistOperations.Pause(s.Setlists.Single(x => x.Id == show.Id), id, signal.AtUtc), "已同步暂停", RecordSource.MidiBard, signal.AtUtc),
            PlaybackSignalKind.Resumed => Change(s => SetlistOperations.Resume(s.Setlists.Single(x => x.Id == show.Id), id, signal.AtUtc), "已同步继续", RecordSource.MidiBard, signal.AtUtc),
            PlaybackSignalKind.Finished => Change(s => StageOperations.CompleteAndAdvance(s, show.Id, id, signal.AtUtc), "已同步自然结束，下一项待开始", RecordSource.MidiBard, signal.AtUtc),
            PlaybackSignalKind.Stopped => Change(s => StageOperations.SkipAndAdvance(s, show.Id, id, signal.AtUtc), "已同步停止，记录为中断", RecordSource.MidiBard, signal.AtUtc),
            _ => true,
        };
        SyncStatus = StatusMessage;
        if (saved && signal.Kind is PlaybackSignalKind.Finished or PlaybackSignalKind.Stopped) syncEntryId = syncPlaybackId = null;
        return saved;
    }
}
