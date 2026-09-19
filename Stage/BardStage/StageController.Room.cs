using BardStage.Core;
using BardStage.Core.Rooms;

namespace BardStage;

public sealed partial class StageController
{
    public StageRoom? Room { get; set; }
    public Func<string?>? LocalEnsembleControlIssue { get; set; }
    public string? LocalQueueControlIssue => Room?.IsRemote == true ? null
        : Room?.Coordinator?.IsCoordinated == true ? Room.Coordinator.LocalControlIssue
        : Room?.HostingBlockReason ?? (State.RequestSettings.PlaybackMode == QueuePlaybackMode.Ensemble ? LocalEnsembleControlIssue?.Invoke() : null);
    public CatalogState QueueState => Room?.IsRemote == true ? Room.RemoteState : State;
    public ShowSetlist? QueueViewShow => QueueState.Setlists.FirstOrDefault(x => x.Id == QueueState.RequestSettings.TargetSetlistId);
    public RoomPlayback QueueViewPlayback => Room?.IsRemote == true ? Room.RemotePlayback : LocalPlayback;
    public bool CanEditQueue => Room?.IsRemote == true ? Room.Connected && Room.HasRemoteControl && !Room.OperationPending
        : !IsReadOnly && !IsBusy && LocalQueueControlIssue == null;
    internal void InvalidateQueueAuthority() => QueueRevision++;
    internal RoomPlayback LocalPlayback => new(QueuePlayer?.Enabled == true, QueuePlayer?.IsLoading == true,
        QueuePlayer?.IsPaused == true, QueuePlayer?.IsEnginePlaying == true, QueuePlayer?.ActiveEntryId, QueuePlayer?.Status ?? "播放控制未连接");

    public bool QueueCommand(RoomAction action, Guid? entry = null, Guid? song = null, Guid? request = null,
        bool value = false, int number = 0, RequestSettings? settings = null, Guid? targetEntry = null)
    {
        var command = new RoomCommand { Action = action, EntryId = entry, SongId = song, RequestId = request,
            Value = value, Number = number, Settings = settings, TargetEntryId = targetEntry };
        if (action == RoomAction.Skip) command.EntryId = QueueViewPlayback.ActiveEntryId ?? (QueueViewShow is { } show ? StageOperations.Next(show)?.Id : null);
        if (Room?.IsRemote == true) return Room.Send(command);
        var result = ApplyQueueCommand(command, false);
        SetStatus(result.Message, !result.Success);
        return result.Success;
    }

    internal RoomResult ApplyQueueCommand(RoomCommand command, bool remote)
    {
        try
        {
            if (!Enum.IsDefined(command.Action) || command.Id == Guid.Empty) throw new InvalidOperationException("房间操作无效");
            if (IsReadOnly || IsBusy) throw new InvalidOperationException("队长正在导入曲库或数据只读，请稍后重试");
            if (remote && (Room?.IsCaptain != true || command.RoomId != Room.Id)) throw new InvalidOperationException("演出房间已改变，请重新加入");
            var switchToSolo = !remote && Room?.IsCaptain != true && command.Action == RoomAction.PlaybackMode
                && command.Number == (int)QueuePlaybackMode.Solo;
            if (!switchToSolo && !(remote && Room?.Coordinator?.IsCoordinated == true) && LocalQueueControlIssue is { } controlIssue) throw new InvalidOperationException(controlIssue);
            if (remote && Room?.Coordinator?.IsCoordinated == true && command.AuthorityEpoch != Room.Coordinator.Epoch)
                throw new InvalidOperationException("小队队长已变更，请等待接管后重试");
            if (remote && command.Action != RoomAction.Chat && command.Revision != QueueRevision)
                throw new InvalidOperationException("节目单已更新，本次操作未执行，请核对最新列表后重试");
            if (QueueShow == null && !EnsureAutomaticQueue()) throw new InvalidOperationException(StatusMessage);
            var show = QueueShow!;
            var player = QueuePlayer;
            SetlistEntry FindEntry() => show.Entries.FirstOrDefault(e => e.Id == command.EntryId) ?? throw new InvalidOperationException("歌曲条目已经不存在");
            SetlistEntry MutableEntry()
            {
                var entry = FindEntry();
                if (entry.Status != EntryStatus.Queued || player?.ActiveEntryId == entry.Id) throw new InvalidOperationException("这首歌正在准备或演奏，请使用跳过当前曲目");
                return entry;
            }
            void Save(Action<CatalogState> update)
            { if (!Change(update)) throw new InvalidOperationException(remote && LastChangeStorageError ? "队长端保存失败，请队长检查数据目录后重试" : StatusMessage); }
            switch (command.Action)
            {
                case RoomAction.Add:
                    if (show.Entries.Count(e => e.Status is EntryStatus.Queued or EntryStatus.InProgress) >= State.RequestSettings.MaxQueueSize)
                        throw new InvalidOperationException("待演队列已满");
                    Save(s => SetlistOperations.AddSong(s.Setlists.Single(x => x.Id == show.Id), s.Songs.Single(x => x.Id == command.SongId)));
                    break;
                case RoomAction.MoveUp:
                case RoomAction.MoveDown:
                    var mutable = MutableEntry();
                    var pending = show.Entries.Where(e => e.Status == EntryStatus.Queued && e.Id != player?.ActiveEntryId).ToList();
                    var offset = pending.FindIndex(e => e.Id == mutable.Id) + (command.Action == RoomAction.MoveUp ? -1 : 1);
                    if (offset < 0 || offset >= pending.Count) throw new InvalidOperationException("歌曲已经位于队列边界");
                    var index = show.Entries.IndexOf(pending[offset]);
                    Save(s => SetlistOperations.Move(s.Setlists.Single(x => x.Id == show.Id), mutable.Id, index));
                    break;
                case RoomAction.PlayNext:
                    var next = MutableEntry();
                    Save(s => s.Setlists.Single(x => x.Id == show.Id).LockedNextEntryId = next.Id);
                    break;
                case RoomAction.MoveTo:
                    var source = MutableEntry();
                    var target = show.Entries.FirstOrDefault(e => e.Id == command.TargetEntryId);
                    if (target == null || target.Id == source.Id || target.Status != EntryStatus.Queued || target.Id == player?.ActiveEntryId)
                        throw new InvalidOperationException("拖动目标已改变，请重试");
                    Save(s =>
                    {
                        var updated = s.Setlists.Single(x => x.Id == show.Id);
                        var moving = updated.Entries.Single(e => e.Id == source.Id);
                        updated.Entries.Remove(moving);
                        var destination = updated.Entries.FindIndex(e => e.Id == target.Id) + (command.Value ? 1 : 0);
                        updated.Entries.Insert(destination, moving);
                        updated.LockedNextEntryId = null;
                    });
                    break;
                case RoomAction.Remove:
                    var removed = MutableEntry();
                    Save(s => SetlistOperations.Skip(s.Setlists.Single(x => x.Id == show.Id), removed.Id, DateTimeOffset.UtcNow));
                    break;
                case RoomAction.DeleteFinished:
                    var finishedId = FindEntry().Id;
                    Save(s => CleanupOperations.DeleteEntries(s, show.Id, [finishedId], player?.ActiveEntryId, true));
                    break;
                case RoomAction.ClearFinished:
                    var finishedIds = show.Entries.Where(e => e.Status is EntryStatus.Completed or EntryStatus.Skipped).Select(e => e.Id).ToArray();
                    Save(s => CleanupOperations.DeleteEntries(s, show.Id, finishedIds, player?.ActiveEntryId, true));
                    break;
                case RoomAction.Requeue:
                    var old = FindEntry();
                    Requeue(old.Id);
                    if (StatusIsError) throw new InvalidOperationException(StatusMessage);
                    break;
                case RoomAction.Resolve:
                    Save(s => AutoQueueOperations.ArrangeInOrder(s, command.RequestId ?? Guid.Empty, command.SongId ?? Guid.Empty));
                    break;
                case RoomAction.Reject:
                    Save(s => RequestOperations.Reject(s, command.RequestId ?? Guid.Empty, "本次不演奏"));
                    break;
                case RoomAction.Reception:
                    Save(s => s.RequestSettings.IsOpen = command.Value);
                    break;
                case RoomAction.ReceptionOwner:
                    if (Room?.IsCaptain != true) throw new InvalidOperationException("请先创建演出房间");
                    Room.PresenterReceivesChat = command.Value;
                    QueueRevision++;
                    break;
                case RoomAction.Settings:
                    var draft = command.Settings ?? throw new InvalidOperationException("缺少点歌设置");
                    if (string.IsNullOrWhiteSpace(draft.Prefix) || draft.Prefix.Length > 32 || draft.Channels == null
                        || draft.Channels.Count > 5 || draft.Channels.Any(c => !Enum.IsDefined(c) || c == RequestChannel.Manual))
                        throw new InvalidOperationException("点歌前缀或接收频道无效");
                    Save(s =>
                    {
                        var updated = Clone(draft);
                        updated.IsOpen = s.RequestSettings.IsOpen; updated.TargetSetlistId = show.Id;
                        updated.PlaybackMode = s.RequestSettings.PlaybackMode; updated.AutoArrange = true;
                        updated.Prefix = updated.Prefix.Trim(); s.RequestSettings = updated;
                        s.Setlists.Single(x => x.Id == show.Id).GapSeconds = command.Number;
                    });
                    break;
                case RoomAction.PlaybackMode:
                    if (command.Number == (int)QueuePlaybackMode.Solo && Room?.Coordinator?.IsCoordinated == true
                        && Room.Coordinator.LocalControlIssue != null)
                        throw new InvalidOperationException("请先将队长转回原房主，再切换房间的单人演奏模式");
                    if (player?.ActiveEntryId != null || player?.IsLoading == true || player?.IsEnginePlaying == true)
                        throw new InvalidOperationException("请先停止演奏再切换演奏模式");
                    if (!Enum.IsDefined((QueuePlaybackMode)command.Number)) throw new InvalidOperationException("演奏模式无效");
                    Save(s => s.RequestSettings.PlaybackMode = (QueuePlaybackMode)command.Number);
                    break;
                case RoomAction.Start:
                case RoomAction.Pause:
                case RoomAction.Stop:
                case RoomAction.Skip:
                case RoomAction.Continuous:
                    if (player == null) throw new InvalidOperationException("队长播放控制未连接");
                    if (command.Action == RoomAction.Skip)
                    {
                        var actual = player.ActiveEntryId ?? StageOperations.Next(show)?.Id;
                        if (actual == null || actual != command.EntryId) throw new InvalidOperationException("当前曲目已改变，本次跳过未执行");
                        player.Skip();
                    }
                    else if (command.Action == RoomAction.Continuous) player.SetContinuous(command.Value);
                    else if (command.Action == RoomAction.Start) player.Start();
                    else if (command.Action == RoomAction.Pause) player.Pause();
                    else player.Stop();
                    // Apply synchronous playback notifications before the scheduler's next tick.
                    Poll();
                    QueueRevision++;
                    var accepted = !player.StatusIsError;
                    return new RoomResult(command.Id, accepted, remote && player.StatusIsError
                        ? "演奏操作未完成，请队长检查本机提示" : player.Status);
                case RoomAction.Chat:
                    if (!remote) throw new InvalidOperationException("聊天点歌来源无效");
                    var chat = command.Chat ?? throw new InvalidOperationException("缺少观众点歌内容");
                    if (chat.Channel == RequestChannel.Manual) throw new InvalidOperationException("聊天来源无效");
                    Save(s => RequestOperations.Submit(s, show.Id, chat.Name, chat.World, chat.Query, chat.Channel, DateTimeOffset.UtcNow));
                    break;
                default: throw new InvalidOperationException("不支持的房间操作");
            }
            return new RoomResult(command.Id, true, command.Action == RoomAction.Chat ? "观众点歌已接收" : "队列已更新");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return new RoomResult(command.Id, false, ex.Message); }
        catch (Exception) when (remote)
        { return new RoomResult(command.Id, false, "操作未完成，请队长检查本机数据后重试"); }
    }
}
