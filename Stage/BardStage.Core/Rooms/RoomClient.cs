using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BardStage.Core.Rooms;

public sealed class RoomClient : IDisposable
{
    private readonly RoomInvite invite;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<RoomResult> results = new();
    private readonly ConcurrentQueue<RoomPacket> executionPackets = new();
    private readonly ConcurrentQueue<RoomPacket> songPackets = new();
    private readonly ConcurrentQueue<RoomPlanResult> planResults = new();
    private readonly ConcurrentQueue<MovementEnvelope> movementFrames = new();
    private RoomPeer? peer;
    private TcpClient? connecting;
    private RoomSnapshot? snapshot;
    private string status = "正在连接队长";
    private int generation;
    public RoomSnapshot? Snapshot => Volatile.Read(ref snapshot);
    public bool Connected => Volatile.Read(ref peer)?.IsAlive == true && Snapshot != null;
    public int Generation => Volatile.Read(ref generation);
    public string Status => Volatile.Read(ref status);
    public Task Completion { get; }
    public RoomRole Role => invite.Role;
    public Guid RoomId => invite.RoomId;
    public bool CanControl => Role == RoomRole.Presenter || Snapshot?.CanControl == true;

    public RoomClient(RoomInvite invite)
    { invite.Validate(); this.invite = invite; Completion = ConnectLoop(); }

    public bool Send(RoomCommand command) => CanControl && Connected
        && Volatile.Read(ref peer)?.Send(new RoomPacket { Type = "command", Command = command }) == true;
    public bool TryResult(out RoomResult result) => results.TryDequeue(out result!);
    public bool SendExecution(RoomPacket packet) => Connected && Volatile.Read(ref peer)?.Send(packet) == true;
    public bool TryExecution(out RoomPacket packet) => executionPackets.TryDequeue(out packet!);
    public bool SendSongRequest(RoomSongRequest request) => Connected && Role == RoomRole.Viewer
        && Volatile.Read(ref peer)?.Send(new RoomPacket { Type = "songRequest", RoomId = RoomId, Role = Role, SongRequest = request }) == true;
    public bool TrySongPacket(out RoomPacket packet) => songPackets.TryDequeue(out packet!);
    public bool SendPlanPacket(RoomPacket packet) => Connected && Role == RoomRole.Viewer
        && Volatile.Read(ref peer)?.Send(packet) == true;
    public bool TryPlanResult(out RoomPlanResult result) => planResults.TryDequeue(out result!);
    public bool SendMovement(string type, MovementEnvelope value) => Connected && Role == RoomRole.Viewer
        && Volatile.Read(ref peer)?.Send(new() { Type = type, RoomId = RoomId, Movement = value }) == true;
    public bool TryMovement(out MovementEnvelope value) => movementFrames.TryDequeue(out value!);

    private async Task ConnectLoop()
    {
        var attempt = 0;
        while (!lifetime.IsCancellationRequested)
        {
            RoomPeer? session = null;
            try
            {
                using var socket = new TcpClient { NoDelay = true };
                Volatile.Write(ref connecting, socket);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(invite.Host, invite.Port, timeout.Token);
                using var stream = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
                    certificate != null && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(invite.Fingerprint)));
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "MidiBard-Room", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);
                await RoomWire.Write(stream, new RoomPacket { Type = "hello", Key = invite.Key, RoomId = invite.RoomId, Role = invite.Role }, timeout.Token);
                session = new RoomPeer(socket, stream, lifetime.Token);
                Volatile.Write(ref snapshot, null);
                executionPackets.Clear();
                songPackets.Clear();
                planResults.Clear();
                movementFrames.Clear();
                Volatile.Write(ref peer, session);
                Interlocked.Increment(ref generation);
                await session.Run(packet =>
                {
                    if (packet.Type == "snapshot" && packet.Role == Role && packet.Snapshot is { } value && value.RoomId == invite.RoomId)
                    { AcceptSnapshot(value); Volatile.Write(ref status, Role == RoomRole.Viewer ? "已连接队长 · 只读" : "已连接队长"); attempt = 0; }
                    else if (packet.Type == "result" && packet.Result is { } result && packet.Snapshot is { } confirmed
                        && confirmed.RoomId == invite.RoomId && results.Count < 128)
                    { AcceptSnapshot(confirmed); results.Enqueue(result); }
                    else if (Role == RoomRole.Viewer && packet.Type is "songChunk" or "songResult"
                        && packet.RoomId == invite.RoomId && songPackets.Count < 4
                        && (packet.Type == "songResult" || packet.SongChunk?.Data is { Length: <= 65536 }))
                        songPackets.Enqueue(packet);
                    else if (Role == RoomRole.Viewer && packet.Type == "movementFrame" && packet.RoomId == invite.RoomId
                        && packet.Movement is { } movement && movementFrames.Count < 16)
                        movementFrames.Enqueue(movement);
                    else if (Role == RoomRole.Viewer && packet.Type == "planResult" && packet.RoomId == invite.RoomId
                        && packet.PlanResult is { } planResult && planResults.Count < 16)
                        planResults.Enqueue(planResult);
                    else if (Role == RoomRole.Viewer && packet.Type is "proofChallenge" or "executionLease" or "execute" && executionPackets.Count < 256)
                        executionPackets.Enqueue(packet);
                    else session.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException
                or SocketException or System.Text.Json.JsonException or FormatException)
            { Volatile.Write(ref status, ex is AuthenticationException ? "房间身份校验失败，请核对邀请口令与隧道认证" : "连接中断，正在重连队长"); }
            finally
            {
                session?.Dispose();
                Volatile.Write(ref peer, null); Volatile.Write(ref connecting, null);
                if (!lifetime.IsCancellationRequested && Status is "已连接队长" or "已连接队长 · 只读" or "正在连接队长")
                    Volatile.Write(ref status, "连接中断，正在重连队长");
            }
            if (lifetime.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << Math.Min(attempt++, 3))), lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
        Volatile.Write(ref status, "已离开房间");
    }

    public void Dispose()
    {
        lifetime.Cancel();
        Volatile.Read(ref peer)?.Dispose();
        Volatile.Read(ref connecting)?.Dispose();
    }

    private void AcceptSnapshot(RoomSnapshot value)
    {
        if (Snapshot is not { } previous || value.Revision >= previous.Revision) Volatile.Write(ref snapshot, value);
    }
}
