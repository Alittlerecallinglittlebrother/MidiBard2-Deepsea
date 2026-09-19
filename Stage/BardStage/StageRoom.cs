using System.Text.Json;
using BardStage.Core;
using BardStage.Core.Rooms;

namespace BardStage;

public sealed class StageRoom(StageController controller) : IDisposable
{
    private RoomServer? server;
    private RoomClient? client;
    private readonly CatalogState empty = new();
    private readonly Dictionary<Guid, RoomResult> completed = [];
    private readonly Dictionary<Guid, (RoomCommand Command, DateTimeOffset Sent)> pendingChat = [];
    private Guid? pendingOperation;
    private DateTimeOffset pendingSince, publishAfter;
    private long publishedRevision = -1;
    private RoomPlayback? publishedPlayback;
    private string? publishedHostIssue;
    private bool publishedOwner, wasConnected;
    private int generation;
    public Func<string?>? CanHost { get; set; }
    public RoomPlaybackCoordinator? Coordinator { get; internal set; }
    internal RoomServer? Server => server;
    internal RoomClient? Client => client;
    public bool HasRemoteControl => client?.CanControl == true && (IsPresenter || Coordinator?.ClientHasAuthority == true);
    public bool IsCaptain => server != null;
    public string? HostingBlockReason => IsCaptain ? Coordinator?.IsCoordinated == true ? Coordinator.ExecutionIssue : CanHost?.Invoke() : null;
    public bool IsRemote => client != null;
    public bool IsPresenter => client?.Role == RoomRole.Presenter;
    public bool IsViewer => client?.Role == RoomRole.Viewer;
    public bool Connected => client?.Connected == true;
    public bool HasPresenter => server?.HasPresenter == true;
    public int ViewerCount => server?.ViewerCount ?? 0;
    public bool OperationPending => pendingOperation != null;
    public bool PresenterReceivesChat { get; set; } = true;
    public bool ViewPresenterReceivesChat => IsRemote ? client?.Snapshot?.PresenterReceivesChat == true : PresenterReceivesChat;
    public Guid Id => server?.RoomId ?? client?.RoomId ?? Guid.Empty;
    public int LocalPort => server?.Port ?? 28765;
    public string ConnectionStatus => IsCaptain
        ? HostingBlockReason is { } issue ? "房间控制已暂停 · " + issue
            : $"演出房间 · {(HasPresenter ? "主持人已连接" : "等待主持人")} · 队员 {ViewerCount}/{RoomServer.MaxViewers}"
        : IsViewer ? HasRemoteControl ? "当前队长 · 已接管演出控制" : "队员查看 · " + client!.Status : client?.Status ?? "本地模式";
    public CatalogState RemoteState => client?.Snapshot?.Catalog ?? empty;
    public RoomPlayback RemotePlayback => client?.Snapshot?.Playback ?? new(false, false, false, false, null, "等待队长同步节目单");
    public Task? TransportCompletion => client?.Completion ?? server?.Completion;

    public bool Create(int port = 28765)
    {
        try
        {
            if (IsCaptain || IsRemote) throw new InvalidOperationException("请先关闭或离开当前房间");
            if (controller.IsReadOnly || controller.IsBusy) throw new InvalidOperationException("曲库只读或正在导入，暂时不能创建房间");
            if (CanHost?.Invoke() is { } reason) throw new InvalidOperationException(reason);
            if (!controller.EnsureAutomaticQueue()) return false;
            server = new RoomServer(port); completed.Clear(); publishedRevision = -1; publishedHostIssue = null;
            Tick(true);
            controller.SetStatus("演出房间已创建");
            return true;
        }
        catch (Exception ex) { controller.SetStatus("房间未创建：" + ex.Message, true); return false; }
    }

    public string Invite(string host, int port, RoomRole role = RoomRole.Presenter) => server?.Invite(host, port, role).Encode() ?? throw new InvalidOperationException("请先创建房间");

    public bool Join(string invitation, RoomRole? expectedRole = null)
    {
        try
        {
            if (IsCaptain || IsRemote) throw new InvalidOperationException("请先关闭或离开当前房间");
            var invite = RoomInvite.Decode(invitation);
            if (expectedRole.HasValue && expectedRole.Value != invite.Role)
                throw new InvalidOperationException("邀请口令与所选角色不符，请使用对应角色的口令");
            controller.Poll();
            if (controller.IsBusy || controller.QueuePlayer?.IsRunning == true || controller.QueuePlayer?.IsLoading == true
                || controller.QueuePlayer?.ActiveEntryId != null
                || (invite.Role == RoomRole.Presenter && controller.QueuePlayer?.IsEnginePlaying == true)
                || controller.PendingChatCount > 0 || controller.SyncBlocked || controller.ChatWriteBlocked
                || controller.State.Setlists.Any(s => StageOperations.Current(s) != null))
                throw new InvalidOperationException(invite.Role == RoomRole.Viewer
                    ? "请先停止本机自动点歌连播，完成导入并保存待处理记录，再进入队员查看"
                    : "请先停止本机演奏，完成导入并保存待处理记录，再进入主持人模式");
            client = new RoomClient(invite);
            generation = 0; wasConnected = false;
            controller.SetStatus("正在连接演出房间");
            return true;
        }
        catch (Exception ex) { controller.SetStatus(ex.Message, true); return false; }
    }

    public void Leave()
    {
        server?.Dispose(); client?.Dispose(); server = null; client = null;
        pendingOperation = null; pendingChat.Clear(); completed.Clear(); wasConnected = false;
        controller.SetStatus("已退出协作，队长本机演奏状态保持不变");
    }

    public bool Send(RoomCommand command)
    {
        if (IsViewer && !HasRemoteControl) { controller.SetStatus("队员查看模式只有查看权限", true); return false; }
        if (client?.Connected != true || (pendingOperation != null && command.Action != RoomAction.Chat))
        { controller.SetStatus("连接未就绪或上一条操作尚未确认", true); return false; }
        command.RoomId = client.Snapshot!.RoomId;
        command.Revision = client.Snapshot.Revision;
        command.AuthorityEpoch = client.Snapshot.AuthorityEpoch;
        if (!client.Send(command)) { controller.SetStatus("指令未发送，请等待重新连接", true); return false; }
        if (command.Action == RoomAction.Chat) pendingChat[command.Id] = (command, DateTimeOffset.UtcNow);
        else { pendingOperation = command.Id; pendingSince = DateTimeOffset.UtcNow; }
        controller.SetStatus("等待队长端确认");
        return true;
    }

    public RequestSettings ReceptionSettings
    {
        get
        {
            var settings = StageController.Clone(IsRemote ? RemoteState.RequestSettings : controller.ReceptionSettings);
            if (IsViewer) settings.IsOpen &= HasRemoteControl && !ViewPresenterReceivesChat;
            else if (IsPresenter) settings.IsOpen &= Connected && ViewPresenterReceivesChat;
            else if (IsCaptain && PresenterReceivesChat) settings.IsOpen = false;
            if (!IsRemote && controller.LocalQueueControlIssue != null) settings.IsOpen = false;
            return settings;
        }
    }

    public void ReceiveChat(IncomingChatRequest request)
    {
        if (IsViewer && (!HasRemoteControl || ViewPresenterReceivesChat)) return;
        if (!IsRemote && controller.LocalQueueControlIssue != null) return;
        if (!IsRemote) { if (!IsCaptain || !PresenterReceivesChat) controller.ReceiveChat(request); return; }
        if (IsPresenter && !ViewPresenterReceivesChat) return;
        if (request.SetlistId != RemoteState.RequestSettings.TargetSetlistId)
        { controller.SetStatus("点歌接收的房间队列已改变，请重新点歌", true); return; }
        if (pendingChat.Count >= 64) { controller.SetStatus("主持人点歌缓冲已满，请等待连接恢复", true); return; }
        Send(new RoomCommand { Action = RoomAction.Chat, Chat = new RoomChat(request.Name, request.World, request.Query, request.Channel) });
    }

    // All catalog mutations and game commands run on the framework thread through this pump.
    public void Tick(bool force = false)
    {
        if (server != null)
        {
            var hostIssue = HostingBlockReason;
            if (hostIssue != publishedHostIssue)
            {
                publishedHostIssue = hostIssue;
                controller.SetStatus(hostIssue == null ? "房间控制权限已恢复" : "房间控制已暂停：" + hostIssue, hostIssue != null);
                force = true;
            }
            for (var i = 0; i < 16 && server.TryRead(out var incoming); i++)
            {
                if (!incoming.Peer.IsAlive || !server.CanControl(incoming.Peer)) continue;
                if (!completed.TryGetValue(incoming.Command.Id, out var result))
                {
                    if (completed.Count >= 4096) { incoming.Peer.Dispose(); controller.SetStatus("房间操作记录已满，请重新创建房间", true); break; }
                    var chatReceiver = PresenterReceivesChat ? server.IsPresenter(incoming.Peer) : server.IsExecutor(incoming.Peer);
                    result = incoming.Command.Action == RoomAction.Chat && !chatReceiver
                        ? new RoomResult(incoming.Command.Id, false, "点歌接收端已改变，请由当前接收端重新点歌")
                        : controller.ApplyQueueCommand(incoming.Command, true);
                    completed[incoming.Command.Id] = result;
                }
                // The acknowledgement and its authoritative state travel in the same frame.
                incoming.Peer.Send(new RoomPacket { Type = "result", Result = result, Snapshot = server.SnapshotFor(incoming.Peer, Capture()) });
                force = true;
            }
            if (!force && DateTimeOffset.UtcNow < publishAfter) return;
            publishAfter = DateTimeOffset.UtcNow.AddMilliseconds(200);
            var playback = controller.LocalPlayback;
            if (force || publishedRevision != controller.QueueRevision || publishedPlayback != playback || publishedOwner != PresenterReceivesChat)
            {
                var snapshot = Capture();
                if (JsonSerializer.SerializeToUtf8Bytes(new RoomPacket { Type = "snapshot", Snapshot = snapshot }, RoomJson.Options).Length > 8 * 1024 * 1024)
                { controller.SetStatus("共享曲库超过 8 MB，房间已关闭，请减少共享曲库规模", true); server.Dispose(); server = null; return; }
                server.Publish(snapshot);
                publishedRevision = controller.QueueRevision; publishedPlayback = playback; publishedOwner = PresenterReceivesChat;
            }
        }
        if (client == null) return;
        var receivedResult = false;
        while (client.TryResult(out var result))
        {
            receivedResult = true;
            if (pendingOperation == result.Id) pendingOperation = null;
            pendingChat.Remove(result.Id);
            controller.SetStatus(result.Message, !result.Success);
        }
        if (wasConnected && !Connected)
        {
            if (pendingOperation != null) controller.SetStatus("连接中断，上一条操作结果待核对；重连后请查看最新节目单", true);
            else controller.SetStatus("正在重连队长，列表为上次同步结果", true);
            pendingOperation = null;
        }
        if (pendingOperation != null && DateTimeOffset.UtcNow - pendingSince > TimeSpan.FromSeconds(15))
        { pendingOperation = null; controller.SetStatus("操作确认超时，请核对节目单；不会自动重发切歌指令", true); }
        if (Connected && generation != client.Generation)
        {
            generation = client.Generation;
            if (!receivedResult && pendingOperation == null)
                controller.SetStatus(IsViewer ? "已连接队长，节目单只读同步中" : "已连接队长，节目单已同步");
            foreach (var item in pendingChat.Values.ToArray())
            {
                if (DateTimeOffset.UtcNow - item.Sent < TimeSpan.FromMinutes(2)) client.Send(item.Command);
                else { pendingChat.Remove(item.Command.Id); controller.SetStatus("有点歌超过重连保留时限，请手动核对", true); }
            }
        }
        wasConnected = Connected;
    }

    public RoomSnapshot Capture()
    {
        var show = controller.QueueShow;
        var catalog = new CatalogState
        {
            Songs = controller.State.Songs.Select(s => new SongEntry { Id = s.Id, Title = s.Title, Aliases = [.. s.Aliases],
                Arranger = s.Arranger, DurationSeconds = s.DurationSeconds, PerformerCount = s.PerformerCount, TrackCount = s.TrackCount }).ToList(),
            Setlists = show == null ? [] : [StageController.Clone(show)],
            SelectedSetlistId = show?.Id,
            RequestSettings = StageController.Clone(controller.State.RequestSettings),
            Requests = controller.State.Requests.Where(r => r.SetlistId == show?.Id).Select(StageController.Clone).ToList(),
        };
        return new RoomSnapshot { RoomId = server?.RoomId ?? Guid.Empty, Revision = controller.QueueRevision,
            Catalog = catalog, Playback = HostingBlockReason is { } issue
                ? controller.LocalPlayback with { Status = "房间控制已暂停：" + issue }
                : controller.QueuePlayer?.StatusIsError == true
                    ? controller.LocalPlayback with { Status = "演奏未就绪，请队长检查本机提示" } : controller.LocalPlayback,
            PresenterReceivesChat = PresenterReceivesChat, AuthorityEpoch = Coordinator?.Epoch ?? 0,
            ExecutorCid = Coordinator?.ExecutorCid ?? 0, ExecutionStatus = Coordinator?.ExecutionIssue ?? "" };
    }

    public void Dispose() { server?.Dispose(); client?.Dispose(); }
}
