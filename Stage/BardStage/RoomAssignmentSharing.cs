using BardStage.Core.Rooms;

namespace BardStage;

/// <summary>Transient assignment snapshots bound to the verified game leader and selection.</summary>
public sealed class RoomAssignmentSharing(StageRoom room, Func<bool> enabled, Func<RoomPartyState> party,
    Func<ulong[]> members, Func<RoomPeer, ulong> identity) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid, bool), Pending> pending = [];
    private readonly Dictionary<RoomPeer, Incoming> incoming = [];
    private RoomSongPlan? offered;
    private Guid offeredRoom;
    private bool disposed;

    public async Task PublishAsync(RoomSongPlan plan, CancellationToken token)
    {
        plan = plan.Copy();
        var context = party();
        plan.ValidateContext(context.PartyId, context.LeaderCid, members());
        if (context.SelfCid != context.LeaderCid) throw new InvalidOperationException("请由当前小队队长下发手动分配");
        await Exchange(new() { Type = "planPublish", RoomId = room.Id, SongPlan = plan }, plan.Id, true, token);
    }

    public async Task<RoomSongPlan> ReceiveAsync(Guid id, string hash, CancellationToken token)
    {
        var result = await Exchange(new() { Type = "planRequest", RoomId = room.Id, PlanRequest = new(id, hash) }, id, false, token);
        var plan = result.Plan ?? throw new InvalidDataException("队长没有下发手动分配");
        var context = party();
        plan.ValidateContext(context.PartyId, context.LeaderCid, members());
        if (plan.Id != id || !string.Equals(plan.SongHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("手动分配与当前歌曲不一致，请重新下发");
        return plan.Copy();
    }

    private async Task<RoomPlanResult> Exchange(RoomPacket packet, Guid id, bool publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Pending item;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(RoomAssignmentSharing));
            if (!enabled()) throw new InvalidOperationException("请开启跨电脑当前歌曲同步");
            if (room.IsCaptain)
            {
                if (publish) { offered = packet.SongPlan!.Copy(); offeredRoom = room.Id; return new(id, true, true, "手动分配已发布"); }
                return ReadOffered(id, packet.PlanRequest!.SongHash);
            }
            if (!room.IsViewer || !room.Connected) throw new InvalidOperationException("请先加入队长的演出房间");
            var key = (id, publish);
            if (pending.ContainsKey(key)) throw new InvalidOperationException("这次手动分配正在处理中");
            item = new(room.Id, room.ConnectionGeneration, party(), members(), token);
            pending.Add(key, item);
            if (!room.Client!.SendPlanPacket(packet))
            { pending.Remove(key); throw new InvalidOperationException("手动分配请求未发送"); }
        }
        try
        {
            var result = await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(50), token);
            token.ThrowIfCancellationRequested();
            if (!result.Success) throw new InvalidOperationException(result.Message);
            return result;
        }
        finally { lock (gate) pending.Remove((id, publish)); }
    }

    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            var context = party();
            var roster = members();
            if (offered != null && (!enabled() || !room.IsCaptain || offeredRoom != room.Id
                || offered.PartyId != context.PartyId || offered.LeaderCid != context.LeaderCid
                || !offered.Members.ToHashSet().SetEquals(roster))) offered = null;
            foreach (var item in pending.Values)
                if (item.Token.IsCancellationRequested || !enabled() || !room.Connected || item.Room != room.Id
                    || item.Generation != room.ConnectionGeneration || item.Party != context || !item.Members.ToHashSet().SetEquals(roster))
                    item.Completion.TrySetCanceled();
            while (room.Server?.TryPlanPacket(out var value) == true)
                incoming[value.Peer] = new(value.Packet, DateTimeOffset.UtcNow.AddSeconds(40));
            foreach (var (peer, entry) in incoming.ToArray())
            {
                if (!peer.IsAlive) { incoming.Remove(peer); continue; }
                var packet = entry.Packet;
                var publish = packet.Type == "planPublish";
                var id = publish ? packet.SongPlan?.Id ?? Guid.Empty : packet.PlanRequest?.Id ?? Guid.Empty;
                RoomPlanResult result;
                try
                {
                    if (!enabled()) throw new InvalidOperationException("房主未开启跨电脑歌曲同步");
                    var cid = identity(peer);
                    if (cid == 0 && DateTimeOffset.UtcNow < entry.Deadline) continue;
                    if (cid == 0 || !roster.Contains(cid)) throw new InvalidOperationException("未能确认演奏队员的小队身份");
                    if (publish)
                    {
                        var plan = packet.SongPlan ?? throw new InvalidDataException("手动分配为空");
                        plan.ValidateContext(context.PartyId, context.LeaderCid, roster);
                        if (cid != context.LeaderCid) throw new InvalidOperationException("只有当前游戏队长可以下发分配");
                        offered = plan.Copy(); offeredRoom = room.Id;
                        result = new(id, true, true, "手动分配已发布");
                    }
                    else result = ReadOffered(id, packet.PlanRequest?.SongHash ?? "");
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                { result = new(id, publish, false, ex.Message); }
                if (peer.TrySend(new() { Type = "planResult", RoomId = room.Id, PlanResult = result })) incoming.Remove(peer);
            }
            while (room.Client?.TryPlanResult(out var result) == true)
                if (pending.TryGetValue((result.Id, result.Published), out var item) && !item.Completion.Task.IsCompleted)
                    item.Completion.TrySetResult(result);
        }
    }

    private RoomPlanResult ReadOffered(Guid id, string hash)
    {
        var context = party();
        if (offered == null || offeredRoom != room.Id || offered.Id != id
            || !string.Equals(offered.SongHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("这次手动分配已失效，请队长重新下发");
        offered.ValidateContext(context.PartyId, context.LeaderCid, members());
        return new(id, false, true, "", offered.Copy());
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; offered = null; incoming.Clear();
            foreach (var item in pending.Values) item.Completion.TrySetCanceled();
            pending.Clear();
        }
    }

    private sealed record Incoming(RoomPacket Packet, DateTimeOffset Deadline);
    private sealed record Pending(Guid Room, int Generation, RoomPartyState Party, ulong[] Members, CancellationToken Token)
    {
        public TaskCompletionSource<RoomPlanResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
