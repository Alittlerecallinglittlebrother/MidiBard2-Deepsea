using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace BardStage.Core.Rooms;

internal static class RoomWire
{
    public const int MaxPacket = 8 * 1024 * 1024;

    public static async Task<RoomPacket> Read(Stream stream, CancellationToken token, int limit = MaxPacket, Func<double>? clock = null)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 || length > limit) throw new IOException("房间消息长度超出限制");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token);
        var received = clock?.Invoke() ?? TransportClock.Now;
        var packet = JsonSerializer.Deserialize<RoomPacket>(buffer, RoomJson.Options);
        if (packet == null || packet.Version != 1) throw new IOException("房间协议版本不兼容");
        packet.Large?.ValidateCapacity();
        packet.ReceivedAt = received;
        return packet;
    }

    public static async Task Write(Stream stream, RoomPacket packet, CancellationToken token)
    {
        packet.Large?.ValidateCapacity();
        var buffer = JsonSerializer.SerializeToUtf8Bytes(packet, RoomJson.Options);
        if (buffer.Length > MaxPacket) throw new IOException("共享曲库过大，房间消息超过 8 MB");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, buffer.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(buffer, token);
        await stream.FlushAsync(token);
    }
}

public sealed class RoomPeer : IDisposable
{
    private readonly TcpClient socket;
    private readonly SslStream stream;
    private readonly CancellationTokenSource lifetime;
    private readonly Channel<RoomPacket> outgoing = Channel.CreateBounded<RoomPacket>(new BoundedChannelOptions(16) { SingleReader = true });
    private int disposed;
    private readonly int incomingLimit;
    private readonly Func<double> clock;
    private volatile bool clockEnabled;
    private RoomPacket? latestLarge;
    public RoomPacket? LatestLarge => Volatile.Read(ref latestLarge);
    public TransportClock Clock { get; }
    public void EnableClock() => clockEnabled = true;
    public bool IsAlive => Volatile.Read(ref disposed) == 0;
    public bool SupportsAuthorityRefresh { get; internal set; }

    internal RoomPeer(TcpClient socket, SslStream stream, CancellationToken token, int incomingLimit = RoomWire.MaxPacket, Func<double>? clock = null)
    {
        this.socket = socket; this.stream = stream; this.incomingLimit = incomingLimit;
        this.clock = clock ?? (() => TransportClock.Now); Clock = new(this.clock);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public bool Send(RoomPacket packet)
    {
        packet.Large?.ValidateCapacity();
        if (!IsAlive) return false;
        if (outgoing.Writer.TryWrite(packet)) return true;
        Dispose();
        return false;
    }

    // File transfers retry on the framework pump instead of disconnecting a
    // slow peer when the control-message queue is temporarily full.
    public bool TrySend(RoomPacket packet)
    {
        packet.Large?.ValidateCapacity();
        return IsAlive && outgoing.Writer.TryWrite(packet);
    }

    internal async Task Run(Action<RoomPacket> receive)
    {
        var write = SendLoop();
        var heartbeat = Heartbeat();
        var calibration = Calibrate();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var packet = await RoomWire.Read(stream, timeout.Token, incomingLimit, clock);
                if (packet.Type is "largePoll" or "largeFrame") Volatile.Write(ref latestLarge, packet);
                if (packet.Type == "clockProbe")
                {
                    if (packet.Clock is { } c && c.Id != Guid.Empty && double.IsFinite(c.Sent))
                        TrySend(new RoomPacket { Type = "clockReply", Clock = new() { Id = c.Id, Sent = c.Sent, Received = packet.ReceivedAt } });
                }
                else if (packet.Type == "clockReply")
                { if (clockEnabled && packet.Clock is { } c) Clock.Accept(c, packet.ReceivedAt); }
                else if (packet.Type == "ping") Send(new RoomPacket { Type = "pong" });
                else if (packet.Type != "pong") receive(packet);
            }
        }
        finally
        {
            Dispose();
            try { await Task.WhenAll(write, heartbeat, calibration); } catch (Exception) { }
        }
    }

    private async Task SendLoop()
    {
        try
        {
            await foreach (var packet in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                // Stamp after the outgoing queue, immediately before serialization/write.
                if (packet.Type == "clockProbe" && packet.Clock is { } probe) Clock.Sending(probe);
                else if (packet.Type == "clockReply" && packet.Clock is { } reply) reply.Replied = clock();
                await RoomWire.Write(stream, packet, timeout.Token);
            }
        }
        finally { Dispose(); }
    }

    private async Task Heartbeat()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(lifetime.Token)) Send(new RoomPacket { Type = "ping" });
    }

    private async Task Calibrate()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(lifetime.Token))
            if (clockEnabled) TrySend(new RoomPacket { Type = "clockProbe", Clock = new() { Id = Guid.NewGuid() } });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        outgoing.Writer.TryComplete();
        socket.Dispose();
        stream.Dispose();
    }
}
