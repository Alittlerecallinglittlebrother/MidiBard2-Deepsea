using System.Collections.Concurrent;
using System.Security.Cryptography;
using BardStage.Core;
using BardStage.Core.Rooms;

namespace BardStage;

public sealed record RoomPartyState(long PartyId, ulong SelfCid, ulong LeaderCid);

public interface IRoomEnsembleBackend : IStagePlaybackPort
{
    RoomPartyState Party { get; }
    string Fingerprint(string filePath);
    string? ResolveSong(string hash);
    void SendIdentityProof(Guid roomId, string challenge);
    bool Adopt(string hash);
    void Revoke();
}

public sealed class RoomPlaybackCoordinator : IStagePlaybackPort, IDisposable
{
    private readonly StageController controller;
    private readonly StageRoom room;
    private readonly IRoomEnsembleBackend backend;
    private readonly ConcurrentQueue<PlaybackSignal> signals = new();
    private readonly ConcurrentQueue<(Guid Room, string Challenge, ulong Cid, long Party)> proofs = new();
    private readonly Dictionary<RoomPeer, Proof> identities = [];
    private readonly Dictionary<Guid, Pending> pending = [];
    private readonly Dictionary<Guid, RoomExecutionResult> clientResults = [];
    private RoomPeer? executor;
    private RoomPartyState observedParty = new(0, 0, 0);
    private Guid observedRoom;
    private RoomExecutionLease? clientLease;
    private PendingClientLoad? clientLoad;
    private PlaybackSignal? lastLocalSignal;
    private string lastLocalHash = "", currentHash = "", currentPath = "";
    private Guid canonicalPlaybackId, sourcePlaybackId;
    private long epoch, clientEpoch, normalizedSequence, sourceSequence;
    private Guid lastClientCommand;
    private int clientGeneration;
    private bool ready, disposed, lastCoordinated;
    private string? issue;
    private DateTimeOffset adoptAfter;

    public RoomPlaybackCoordinator(StageController controller, StageRoom room, IRoomEnsembleBackend backend)
    { this.controller = controller; this.room = room; this.backend = backend; room.Coordinator = this; }

    public bool IsCoordinated => room.IsCaptain && controller.State.RequestSettings.PlaybackMode == QueuePlaybackMode.Ensemble;
    public long Epoch => epoch;
    public ulong ExecutorCid => ready ? observedParty.LeaderCid : 0;
    public string? ExecutionIssue => IsCoordinated ? issue ?? (!ready ? "正在确认当前队长的演奏端" : null) : null;
    public string? LocalControlIssue => IsCoordinated && (backend.Party.SelfCid == 0 || backend.Party.SelfCid != backend.Party.LeaderCid)
        ? "队长已转让，本机只读；演出房间继续同步" : null;
    public bool ClientHasAuthority => room.Connected && clientLease is { Granted: true } lease && ValidParty(lease);
    internal Task? LastTransportTask { get; private set; }
    public bool IsPlaying => IsCoordinated && executor != null
        ? controller.QueueShow?.Entries.Any(e => e.Status == EntryStatus.InProgress && !e.PausedAtUtc.HasValue) == true
        : backend.IsPlaying;

    public void Receive(PlaybackSignal signal)
    {
        if (!disposed && signals.Count < 512) signals.Enqueue(signal);
    }

    public void ReceiveProof(Guid roomId, string challenge, ulong senderCid, long partyId)
    {
        if (!disposed && challenge.Length == 32 && proofs.Count < 64) proofs.Enqueue((roomId, challenge, senderCid, partyId));
    }

    public void Tick()
    {
        if (disposed) return;
        DrainSignals();
        if (room.IsRemote) TickClient();
        else if (room.IsCaptain) TickServer();
        else if (observedRoom != Guid.Empty || lastCoordinated) ResetRoom();
        DrainSignals();
        if (room.IsCaptain && IsCoordinated) TickPending();
    }

    public string? BlockReason(QueuePlaybackMode mode)
    {
        if (!IsCoordinated || mode != QueuePlaybackMode.Ensemble) return backend.BlockReason(mode);
        if (ExecutionIssue is { } reason) return reason;
        if (pending.Count != 0) return "等待队长演奏端确认上一条操作";
        return executor == null ? backend.BlockReason(mode) : null;
    }

    public Task LoadAsync(string filePath, QueuePlaybackMode mode, CancellationToken cancellationToken)
    {
        if (IsCoordinated && mode == QueuePlaybackMode.Ensemble) RequireReady();
        currentHash = backend.Fingerprint(filePath); currentPath = filePath;
        canonicalPlaybackId = Guid.NewGuid(); sourcePlaybackId = Guid.Empty; sourceSequence = 0;
        if (!IsCoordinated || executor == null || mode != QueuePlaybackMode.Ensemble)
            return backend.LoadAsync(filePath, mode, cancellationToken);
        return Send(RoomExecutionAction.Load, currentHash, mode, cancellationToken: cancellationToken).Task;
    }

    public void Start(QueuePlaybackMode mode) => Transport(RoomExecutionAction.Start, mode, () => backend.Start(mode));
    public void Pause() => Transport(RoomExecutionAction.Pause, QueuePlaybackMode.Ensemble, backend.Pause);
    public void Resume() => Transport(RoomExecutionAction.Resume, QueuePlaybackMode.Ensemble, backend.Resume);
    public void Finish(QueuePlaybackMode mode) => Transport(RoomExecutionAction.Finish, mode, () => backend.Finish(mode));
    public void Stop(QueuePlaybackMode mode, bool keepInstruments = false)
        => Transport(RoomExecutionAction.Stop, mode, () => backend.Stop(mode, keepInstruments), keepInstruments);

    private void Transport(RoomExecutionAction action, QueuePlaybackMode mode, Action local, bool keep = false)
    {
        LastTransportTask = null;
        if (!IsCoordinated || mode != QueuePlaybackMode.Ensemble) { local(); return; }
        RequireReady();
        if (executor == null) { local(); return; }
        LastTransportTask = Send(action, currentHash, mode, keep).Task;
    }

    private void RequireReady()
    {
        if (ExecutionIssue is { } reason) throw new InvalidOperationException(reason);
        if (backend.Party != observedParty || (executor != null && !executor.IsAlive))
            throw new InvalidOperationException("队长或连接已改变，正在重新接管");
    }

    private Pending Send(RoomExecutionAction action, string hash, QueuePlaybackMode mode, bool keep = false,
        CancellationToken cancellationToken = default)
    {
        var command = new RoomExecutionCommand { Epoch = epoch, Action = action, Hash = hash, Mode = mode, KeepInstruments = keep };
        var item = new Pending(command, cancellationToken, DateTimeOffset.UtcNow.AddSeconds(action == RoomExecutionAction.Load ? 90 : 12));
        if (executor?.Send(new RoomPacket { Type = "execute", RoomId = room.Id, Execution = command }) != true)
            throw new InvalidOperationException("当前队长演奏端未连接");
        pending.Add(command.Id, item);
        if (action != RoomExecutionAction.Load)
            _ = item.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return item;
    }

    private void TickServer()
    {
        var server = room.Server!;
        var party = backend.Party;
        if (observedRoom != room.Id || observedParty.PartyId != party.PartyId)
        {
            identities.Clear(); observedRoom = room.Id;
        }
        foreach (var departed in identities.Keys.Where(p => !p.IsAlive).ToArray()) identities.Remove(departed);
        if (party.PartyId != 0 && party.SelfCid != 0)
        {
            foreach (var participant in server.Participants.Where(p => p.Role == RoomRole.Viewer))
            {
                if (identities.TryGetValue(participant.Peer, out var old) && (old.Cid != 0 || old.Expires > DateTimeOffset.UtcNow)) continue;
                var proof = new Proof(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), party.PartyId,
                    DateTimeOffset.UtcNow.AddSeconds(45));
                identities[participant.Peer] = proof;
                participant.Peer.Send(new RoomPacket { Type = "proofChallenge", RoomId = room.Id, Proof = new(proof.Challenge, party.PartyId) });
            }
        }
        while (proofs.TryDequeue(out var received))
        {
            if (received.Room != room.Id || received.Party != party.PartyId || received.Cid == 0) continue;
            var match = identities.FirstOrDefault(p => p.Key.IsAlive && p.Value.Challenge == received.Challenge
                && p.Value.Party == received.Party && p.Value.Expires > DateTimeOffset.UtcNow && p.Value.Cid == 0);
            if (match.Key == null || identities.Any(p => !ReferenceEquals(p.Key, match.Key) && p.Key.IsAlive && p.Value.Cid == received.Cid)) continue;
            identities[match.Key] = match.Value with { Cid = received.Cid };
        }
        var selected = party.LeaderCid == party.SelfCid ? null
            : identities.FirstOrDefault(p => p.Key.IsAlive && p.Value.Party == party.PartyId && p.Value.Cid == party.LeaderCid).Key;
        if (party != observedParty || !ReferenceEquals(selected, executor) || lastCoordinated != IsCoordinated)
            ChangeExecutor(party, selected);
        observedParty = party; lastCoordinated = IsCoordinated;
        while (server.TryExecution(out var incoming))
        {
            if (!ReferenceEquals(incoming.Peer, executor) || !incoming.Peer.IsAlive || incoming.Packet.RoomId != room.Id) continue;
            if (incoming.Packet.ExecutionResult is { } result && result.Epoch == epoch) HandleResult(result);
            else if (incoming.Packet.Signal is { } signal && signal.Epoch == epoch && ready) HandleRemoteSignal(signal);
        }
        if (!IsCoordinated) return;
        if (party.PartyId == 0 || party.SelfCid == 0 || party.LeaderCid == 0)
        { SetIssue("原房主需要留在小队中，才能确认当前队长"); return; }
        if (party.SelfCid != party.LeaderCid && executor == null)
        { SetIssue("等待新队长加入原演出房间并完成小队身份验证"); return; }
        if (!ready && !pending.Values.Any(p => p.Command.Action == RoomExecutionAction.Adopt) && DateTimeOffset.UtcNow >= adoptAfter)
            AdoptCurrent();
    }

    private void ChangeExecutor(RoomPartyState party, RoomPeer? selected)
    {
        epoch++; ready = false; executor = selected; observedParty = party;
        backend.Revoke();
        foreach (var item in pending.Values) item.Completion.TrySetCanceled();
        pending.Clear();
        room.Server!.SetExecutor(IsCoordinated ? selected : null);
        foreach (var peer in room.Server.Participants.Where(p => p.Role == RoomRole.Viewer))
            peer.Peer.Send(new RoomPacket { Type = "executionLease", RoomId = room.Id,
                Lease = new(epoch, party.PartyId, party.LeaderCid, IsCoordinated && ReferenceEquals(peer.Peer, selected)) });
        if (IsCoordinated) controller.QueuePlayer?.SuspendForHandoff("正在接管新队长的演奏端");
        adoptAfter = DateTimeOffset.MinValue;
        SetIssue(IsCoordinated ? "正在接管新队长的演奏端" : null);
        controller.InvalidateQueueAuthority();
    }

    private void AdoptCurrent()
    {
        var active = controller.QueueShow?.Entries.FirstOrDefault(e => e.Status == EntryStatus.InProgress);
        if (active == null) { ready = true; SetIssue(null); return; }
        var song = controller.State.Songs.FirstOrDefault(s => s.Id == active.SongId);
        if (song == null) { SetIssue("当前演奏曲目不在曲库中，接管已暂停"); return; }
        try
        {
            currentHash = backend.Fingerprint(song.FilePath); currentPath = song.FilePath;
            if (canonicalPlaybackId == Guid.Empty) canonicalPlaybackId = controller.LastPlaybackSignal?.PlaybackId ?? Guid.NewGuid();
            if (executor != null) { Send(RoomExecutionAction.Adopt, currentHash, QueuePlaybackMode.Ensemble); return; }
            if (!backend.Adopt(currentHash) || lastLocalSignal == null || lastLocalHash != currentHash || lastLocalSignal.Kind == PlaybackSignalKind.Loaded)
                throw new InvalidOperationException("新队长尚未载入同一首正在演奏的 MIDI，接管等待中");
            AcceptAdoption(ToWire(lastLocalSignal, currentHash));
        }
        catch (Exception ex)
        {
            controller.SetStatus("接管未完成：" + ex.Message, true);
            SetIssue("当前歌曲尚未就绪，请当前队长检查本机提示");
            adoptAfter = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    private void AcceptAdoption(RoomExecutionSignal state)
    {
        if (state.Hash != currentHash || !Enum.TryParse<PlaybackSignalKind>(state.Kind, out var kind) || kind == PlaybackSignalKind.Loaded)
        { SetIssue("新队长当前歌曲状态尚未确认，接管等待中"); adoptAfter = DateTimeOffset.UtcNow.AddSeconds(1); return; }
        sourcePlaybackId = state.PlaybackId; sourceSequence = state.Sequence; ready = true; SetIssue(null);
        var current = controller.QueueShow?.Entries.FirstOrDefault(e => e.Status == EntryStatus.InProgress);
        if (kind is PlaybackSignalKind.Finished or PlaybackSignalKind.Stopped)
            EmitCanonical(kind, state.AtUtc);
        else if (current?.PausedAtUtc.HasValue == true && kind is PlaybackSignalKind.Started or PlaybackSignalKind.Resumed)
            EmitCanonical(PlaybackSignalKind.Resumed, state.AtUtc);
        else if (current?.PausedAtUtc.HasValue == false && kind == PlaybackSignalKind.Paused)
            EmitCanonical(PlaybackSignalKind.Paused, state.AtUtc);
    }

    private void HandleResult(RoomExecutionResult result)
    {
        if (!pending.Remove(result.Id, out var item)) return;
        if (!result.Success)
        {
            var message = "队长演奏端：" + result.Message;
            item.Completion.TrySetException(new InvalidOperationException(message));
            if (item.Command.Action == RoomExecutionAction.Adopt)
            {
                controller.SetStatus(message, true);
                SetIssue("当前歌曲尚未就绪，请当前队长检查本机提示"); adoptAfter = DateTimeOffset.UtcNow.AddSeconds(1);
            }
            else if (item.Command.Action != RoomExecutionAction.Load) controller.QueuePlayer?.TransportFault(message);
            return;
        }
        item.Completion.TrySetResult();
        if (item.Command.Action == RoomExecutionAction.Adopt)
        {
            if (result.State is { } state) AcceptAdoption(state);
            else { SetIssue("新队长未回报当前演奏状态"); adoptAfter = DateTimeOffset.UtcNow.AddSeconds(1); }
        }
    }

    private void TickPending()
    {
        foreach (var item in pending.Values.ToArray())
        {
            if (!item.Cancellation.IsCancellationRequested && DateTimeOffset.UtcNow < item.Deadline) continue;
            pending.Remove(item.Command.Id);
            executor?.Send(new RoomPacket { Type = "execute", RoomId = room.Id,
                Execution = new() { Id = item.Command.Id, Epoch = epoch, Action = RoomExecutionAction.Cancel } });
            if (item.Cancellation.IsCancellationRequested) item.Completion.TrySetCanceled(item.Cancellation);
            else
            {
                const string reason = "队长演奏端确认超时，队列已保留，请检查连接";
                item.Completion.TrySetException(new TimeoutException(reason));
                if (item.Command.Action == RoomExecutionAction.Adopt)
                { SetIssue(reason); adoptAfter = DateTimeOffset.UtcNow.AddSeconds(1); }
                else if (item.Command.Action != RoomExecutionAction.Load) controller.QueuePlayer?.TransportFault(reason);
            }
        }
    }

    private void TickClient()
    {
        var client = room.Client!;
        if (clientGeneration != client.Generation || observedRoom != client.RoomId)
        {
            RevokeClient(); clientGeneration = client.Generation; observedRoom = client.RoomId; clientEpoch = 0;
            clientResults.Clear();
        }
        if (clientLease != null && (!room.Connected || !ValidParty(clientLease))) RevokeClient();
        while (client.TryExecution(out var packet))
        {
            if (packet.RoomId != room.Id) continue;
            if (packet.Proof is { } proof && proof.PartyId != 0 && proof.PartyId == backend.Party.PartyId && proof.Challenge.Length == 32)
            {
                try { backend.SendIdentityProof(room.Id, proof.Challenge); }
                catch (Exception ex) { controller.SetStatus("小队身份验证未发送：" + ex.Message, true); }
            }
            else if (packet.Lease is { } lease && lease.Epoch >= clientEpoch)
            {
                RevokeClient(); clientResults.Clear(); clientEpoch = lease.Epoch;
                if (lease.Granted && ValidParty(lease)) clientLease = lease;
            }
            else if (packet.Execution is { } command) ExecuteClient(command);
        }
        if (clientLoad is { } load && load.Task.IsCompleted)
        {
            clientLoad = null;
            try
            {
                load.Task.GetAwaiter().GetResult();
                if (load.Cancellation.IsCancellationRequested || !ClientHasAuthority || load.Command.Epoch != clientLease!.Epoch)
                    throw new OperationCanceledException("载入期间队长发生变化");
                Reply(new(load.Command.Id, load.Command.Epoch, true, "歌曲已载入"));
            }
            catch (Exception ex) { Reply(new(load.Command.Id, load.Command.Epoch, false, ex.Message)); }
            finally { load.Cancellation.Dispose(); }
        }
    }

    private bool ValidParty(RoomExecutionLease lease)
    {
        var party = backend.Party;
        return lease.PartyId != 0 && party.PartyId == lease.PartyId && party.SelfCid != 0
            && party.SelfCid == party.LeaderCid && party.LeaderCid == lease.LeaderCid;
    }

    private void ExecuteClient(RoomExecutionCommand command)
    {
        if (!ClientHasAuthority || command.Epoch != clientLease!.Epoch)
        { Reply(new(command.Id, command.Epoch, false, "队长权限已改变，指令已拒绝")); return; }
        if (command.Action == RoomExecutionAction.Cancel)
        {
            if (clientLoad?.Command.Id == command.Id) { clientLoad.Cancellation.Cancel(); backend.Revoke(); }
            else if (lastClientCommand == command.Id) backend.Revoke();
            return;
        }
        if (clientResults.TryGetValue(command.Id, out var previous)) { Reply(previous); return; }
        if (clientLoad?.Command.Id == command.Id) return;
        try
        {
            if (clientLoad != null) throw new InvalidOperationException("上一首歌曲仍在载入，请稍后重试");
            if (command.Mode != QueuePlaybackMode.Ensemble || !Enum.IsDefined(command.Action)) throw new InvalidOperationException("房间演奏指令无效");
            if (command.Hash == null || command.Hash.Length != 64 || !command.Hash.All(Uri.IsHexDigit))
                throw new InvalidOperationException("歌曲指纹无效");
            if (command.Action is not (RoomExecutionAction.Load or RoomExecutionAction.Adopt) && command.Hash != lastLocalHash)
                throw new InvalidOperationException("当前歌曲已改变，旧曲演奏指令已拒绝");
            lastClientCommand = command.Id;
            switch (command.Action)
            {
                case RoomExecutionAction.Adopt:
                    if (!backend.Adopt(command.Hash) || lastLocalSignal == null || lastLocalHash != command.Hash || lastLocalSignal.Kind == PlaybackSignalKind.Loaded)
                        throw new InvalidOperationException("本机尚未载入同一首正在演奏的 MIDI");
                    Reply(new(command.Id, command.Epoch, true, "当前演奏已接管", ToWire(lastLocalSignal, lastLocalHash)));
                    return;
                case RoomExecutionAction.Load:
                    var path = backend.ResolveSong(command.Hash) ?? throw new InvalidOperationException("本机曲库缺少同一份 MIDI 文件");
                    var cancellation = new CancellationTokenSource();
                    try { clientLoad = new(command, cancellation, backend.LoadAsync(path, command.Mode, cancellation.Token)); }
                    catch { cancellation.Dispose(); throw; }
                    return;
                case RoomExecutionAction.Start:
                    if (backend.BlockReason(command.Mode) is { } reason) throw new InvalidOperationException(reason);
                    backend.Start(command.Mode); break;
                case RoomExecutionAction.Pause: backend.Pause(); break;
                case RoomExecutionAction.Resume: backend.Resume(); break;
                case RoomExecutionAction.Finish: backend.Finish(command.Mode); break;
                case RoomExecutionAction.Stop: backend.Stop(command.Mode, command.KeepInstruments); break;
                default: throw new InvalidOperationException("不支持的演奏指令");
            }
            Reply(new(command.Id, command.Epoch, true, "演奏指令已执行"));
        }
        catch (Exception ex) { Reply(new(command.Id, command.Epoch, false, ex.Message)); }
    }

    private void Reply(RoomExecutionResult result)
    {
        if (!result.Success && clientLease?.Epoch == result.Epoch)
            controller.SetStatus("演奏执行未完成：" + result.Message, true);
        if (clientResults.Count >= 256) clientResults.Remove(clientResults.Keys.First());
        clientResults[result.Id] = result;
        room.Client?.SendExecution(new RoomPacket { Type = "executionResult", RoomId = room.Id, ExecutionResult = result });
    }

    private void RevokeClient()
    {
        clientLease = null; clientLoad?.Cancellation.Cancel(); backend.Revoke();
    }

    private void DrainSignals()
    {
        while (signals.TryDequeue(out var signal))
        {
            string hash;
            try { hash = string.IsNullOrWhiteSpace(signal.FilePath) ? "" : backend.Fingerprint(signal.FilePath); }
            catch { hash = ""; }
            lastLocalSignal = signal; lastLocalHash = hash;
            if (room.IsRemote)
            {
                if (ClientHasAuthority && hash.Length == 64)
                    room.Client!.SendExecution(new RoomPacket { Type = "executionSignal", RoomId = room.Id, Signal = ToWire(signal, hash) });
                continue;
            }
            if (!IsCoordinated)
            {
                normalizedSequence = Math.Max(normalizedSequence, controller.LastPlaybackSignal?.Sequence ?? 0) + 1;
                controller.ReceivePlayback(signal with { Sequence = normalizedSequence });
                canonicalPlaybackId = signal.PlaybackId; currentHash = hash; currentPath = signal.FilePath;
                sourcePlaybackId = signal.PlaybackId; sourceSequence = signal.Sequence;
            }
            else if (executor == null && ready && backend.Party == observedParty && backend.Party.SelfCid == backend.Party.LeaderCid)
                HandleRemoteSignal(ToWire(signal, hash));
        }
    }

    private RoomExecutionSignal ToWire(PlaybackSignal signal, string hash)
        => new(room.IsRemote ? clientLease?.Epoch ?? 0 : epoch, signal.PlaybackId, signal.Sequence, hash, signal.Kind.ToString(), signal.AtUtc);

    private void HandleRemoteSignal(RoomExecutionSignal signal)
    {
        if (signal.Hash != currentHash || !Enum.TryParse<PlaybackSignalKind>(signal.Kind, out var kind)) return;
        if (signal.PlaybackId != sourcePlaybackId)
        {
            if (kind is not (PlaybackSignalKind.Loaded or PlaybackSignalKind.Started)) return;
            sourcePlaybackId = signal.PlaybackId; sourceSequence = 0;
            if (kind == PlaybackSignalKind.Started && controller.QueueShow?.Entries.Any(e => e.Status == EntryStatus.InProgress) != true)
                canonicalPlaybackId = Guid.NewGuid();
        }
        if (signal.Sequence <= sourceSequence) return;
        sourceSequence = signal.Sequence;
        EmitCanonical(kind, signal.AtUtc);
    }

    private void EmitCanonical(PlaybackSignalKind kind, DateTimeOffset at)
    {
        if (canonicalPlaybackId == Guid.Empty) canonicalPlaybackId = Guid.NewGuid();
        normalizedSequence = Math.Max(normalizedSequence, controller.LastPlaybackSignal?.Sequence ?? 0) + 1;
        controller.ReceivePlayback(new(canonicalPlaybackId, normalizedSequence, currentPath, kind, at));
    }

    private void SetIssue(string? value)
    {
        if (issue == value) return;
        issue = value; controller.InvalidateQueueAuthority();
    }

    private void ResetRoom()
    {
        backend.Revoke(); ready = false; executor = null; observedRoom = Guid.Empty; lastCoordinated = false;
        identities.Clear(); RevokeClient();
        foreach (var item in pending.Values) item.Completion.TrySetCanceled();
        pending.Clear(); issue = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ResetRoom();
        if (clientLoad is { } load) _ = load.Task.ContinueWith(t => { _ = t.Exception; load.Cancellation.Dispose(); }, TaskScheduler.Default);
        room.Coordinator = null;
    }

    private sealed record Proof(string Challenge, long Party, DateTimeOffset Expires, ulong Cid = 0);
    private sealed record PendingClientLoad(RoomExecutionCommand Command, CancellationTokenSource Cancellation, Task Task);
    private sealed class Pending(RoomExecutionCommand command, CancellationToken cancellation, DateTimeOffset deadline)
    {
        public RoomExecutionCommand Command { get; } = command;
        public CancellationToken Cancellation { get; } = cancellation;
        public DateTimeOffset Deadline { get; } = deadline;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => Completion.Task;
    }
}
