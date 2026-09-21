using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BardStage.Core.Rooms;

public sealed class RoomServer : IDisposable
{
    public const int MaxViewers = 7;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly X509Certificate2 certificate;
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] viewerSecret = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim handshakes = new(MaxViewers + 1 + 4);
    private readonly SemaphoreSlim viewerSlots = new(MaxViewers);
    private readonly ConcurrentDictionary<RoomPeer, byte> viewers = new();
    private readonly ConcurrentDictionary<TcpClient, Task> connections = new();
    private readonly ConcurrentQueue<(RoomPeer Peer, RoomCommand Command)> commands = new();
    private readonly ConcurrentQueue<(RoomPeer Peer, RoomPacket Packet)> executionPackets = new();
    private readonly ConcurrentQueue<(RoomPeer Peer, RoomSongRequest Request)> songRequests = new();
    private readonly ConcurrentQueue<(RoomPeer Peer, RoomPacket Packet)> planPackets = new();
    private readonly ConcurrentQueue<(RoomPeer Peer, RoomPacket Packet)> movementPackets = new();
    private readonly ConcurrentDictionary<RoomPeer, RoomRole> participants = new();
    private RoomPeer? executor;
    private RoomPeer? presenter;
    private RoomSnapshot? snapshot;
    private RoomSnapshot? viewerSnapshot;
    private int queued, disposed;
    private string? lastConnectionError;
    public Guid RoomId { get; } = Guid.NewGuid();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool HasPresenter => Volatile.Read(ref presenter)?.IsAlive == true;
    public int ViewerCount => viewers.Keys.Count(p => p.IsAlive);
    public Task Completion { get; }
    public string? LastConnectionError => Volatile.Read(ref lastConnectionError);
    public IReadOnlyList<(RoomPeer Peer, RoomRole Role)> Participants => participants.Where(p => p.Key.IsAlive).Select(p => (p.Key, p.Value)).ToArray();
    public bool CanControl(RoomPeer peer) => peer.IsAlive && (ReferenceEquals(peer, Volatile.Read(ref presenter)) || ReferenceEquals(peer, Volatile.Read(ref executor)));
    public bool IsPresenter(RoomPeer peer) => peer.IsAlive && ReferenceEquals(peer, Volatile.Read(ref presenter));
    public bool IsExecutor(RoomPeer peer) => peer.IsAlive && ReferenceEquals(peer, Volatile.Read(ref executor));
    public void SetExecutor(RoomPeer? peer) => Volatile.Write(ref executor, peer);
    public bool TryExecution(out (RoomPeer Peer, RoomPacket Packet) value) => executionPackets.TryDequeue(out value);
    public bool TrySongRequest(out (RoomPeer Peer, RoomSongRequest Request) value) => songRequests.TryDequeue(out value);
    public bool TryPlanPacket(out (RoomPeer Peer, RoomPacket Packet) value) => planPackets.TryDequeue(out value);
    public bool TryMovement(out (RoomPeer Peer, RoomPacket Packet) value) => movementPackets.TryDequeue(out value);
    public RoomSnapshot SnapshotFor(RoomPeer peer, RoomSnapshot value) => CanControl(peer) ? value.ForController() : value.ForViewer();

    public RoomServer(int port = 28765)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MidiBard-Room", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));
        // Windows Schannel needs an imported user key; the temporary key container is deleted on disposal.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(MaxViewers + 4); }
        catch { certificate.Dispose(); throw; }
        Completion = AcceptLoop();
    }

    public RoomInvite Invite(string publicHost, int publicPort, RoomRole role = RoomRole.Presenter)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        return new(publicHost.Trim(), publicPort, certificate.GetCertHashString(HashAlgorithmName.SHA256),
            Convert.ToHexString(role == RoomRole.Viewer ? viewerSecret : secret), RoomId, role);
    }

    public bool TryRead(out (RoomPeer Peer, RoomCommand Command) value)
    {
        if (!commands.TryDequeue(out value)) return false;
        Interlocked.Decrement(ref queued);
        return true;
    }

    public void Publish(RoomSnapshot value)
    {
        Volatile.Write(ref snapshot, value);
        var view = value.ForViewer();
        Volatile.Write(ref viewerSnapshot, view);
        Volatile.Read(ref presenter)?.Send(new RoomPacket { Type = "snapshot", Snapshot = value.ForController() });
        foreach (var viewer in viewers.Keys) viewer.Send(new RoomPacket { Type = "snapshot", Snapshot = SnapshotFor(viewer, value), Role = RoomRole.Viewer });
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(lifetime.Token);
                socket.NoDelay = true;
                if (!handshakes.Wait(0)) { socket.Dispose(); continue; }
                var task = Serve(socket);
                connections[socket] = task;
                _ = task.ContinueWith(_ => { connections.TryRemove(socket, out var ignored); }, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally
        {
            foreach (var socket in connections.Keys) socket.Dispose();
            await Task.WhenAll(connections.Values);
            certificate.Dispose();
        }
    }

    private async Task Serve(TcpClient socket)
    {
        RoomPeer? peer = null;
        var viewerSlot = false;
        try
        {
            using var stream = new SslStream(socket.GetStream(), false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            }, timeout.Token);
            var hello = await RoomWire.Read(stream, timeout.Token, 4096);
            if (hello.Type != "hello" || hello.RoomId != RoomId || hello.Key?.Length != 64 || !Enum.IsDefined(hello.Role)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hello.Key), hello.Role == RoomRole.Viewer ? viewerSecret : secret)) return;
            peer = new RoomPeer(socket, stream, lifetime.Token, 16 * 1024);
            if (hello.Role == RoomRole.Viewer)
            {
                if (!viewerSlots.Wait(0)) return;
                viewerSlot = true; viewers.TryAdd(peer, 0);
            }
            else if (Interlocked.CompareExchange(ref presenter, peer, null) != null) return;
            participants.TryAdd(peer, hello.Role);
            var current = hello.Role == RoomRole.Viewer ? Volatile.Read(ref viewerSnapshot) : Volatile.Read(ref snapshot);
            if (current != null) peer.Send(new RoomPacket { Type = "snapshot", Snapshot = current, Role = hello.Role });
            await peer.Run(packet =>
            {
                if (packet.Type is "movementPoll" or "movementSubmit" && hello.Role == RoomRole.Viewer && packet.RoomId == RoomId && packet.Movement != null)
                {
                    if (movementPackets.Count >= 64) { peer.Dispose(); return; }
                    movementPackets.Enqueue((peer, packet)); return;
                }
                if (packet.Type is "planPublish" or "planRequest" && hello.Role == RoomRole.Viewer && packet.RoomId == RoomId)
                {
                    if (planPackets.Count >= 32) { peer.Dispose(); return; }
                    planPackets.Enqueue((peer, packet)); return;
                }
                if (packet.Type == "songRequest" && hello.Role == RoomRole.Viewer && packet.RoomId == RoomId
                    && packet.SongRequest is { } request && request.Id != Guid.Empty
                    && request.Hash is { Length: 64 } && request.Hash.All(Uri.IsHexDigit)
                    && request.Index is >= 0 and < 256)
                {
                    if (songRequests.Count >= 64) { peer.Dispose(); return; }
                    songRequests.Enqueue((peer, request)); return;
                }
                if (packet.Type is "executionResult" or "executionSignal")
                {
                    if (hello.Role != RoomRole.Viewer || executionPackets.Count >= 256) { peer.Dispose(); return; }
                    executionPackets.Enqueue((peer, packet)); return;
                }
                if (!CanControl(peer)) { peer.Dispose(); return; }
                if (packet.Type != "command" || packet.Command == null) { peer.Dispose(); return; }
                if (Interlocked.Increment(ref queued) > 128)
                { Interlocked.Decrement(ref queued); peer.Dispose(); return; }
                commands.Enqueue((peer, packet.Command));
            });
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException
            or SocketException or System.Text.Json.JsonException or FormatException or CryptographicException or ArgumentException)
        { Volatile.Write(ref lastConnectionError, ex.ToString()); }
        finally
        {
            if (peer != null) { Interlocked.CompareExchange(ref presenter, null, peer); Interlocked.CompareExchange(ref executor, null, peer); viewers.TryRemove(peer, out _); participants.TryRemove(peer, out _); peer.Dispose(); }
            if (viewerSlot) viewerSlots.Release();
            socket.Dispose();
            handshakes.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); listener.Stop();
        Volatile.Read(ref presenter)?.Dispose();
        foreach (var socket in connections.Keys) socket.Dispose();
    }
}
