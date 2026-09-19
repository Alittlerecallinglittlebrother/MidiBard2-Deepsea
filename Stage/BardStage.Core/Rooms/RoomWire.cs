using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace BardStage.Core.Rooms;

internal static class RoomWire
{
    public const int MaxPacket = 8 * 1024 * 1024;

    public static async Task<RoomPacket> Read(Stream stream, CancellationToken token, int limit = MaxPacket)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 || length > limit) throw new IOException("房间消息长度超出限制");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token);
        var packet = JsonSerializer.Deserialize<RoomPacket>(buffer, RoomJson.Options);
        if (packet == null || packet.Version != 1) throw new IOException("房间协议版本不兼容");
        return packet;
    }

    public static async Task Write(Stream stream, RoomPacket packet, CancellationToken token)
    {
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
    public bool IsAlive => Volatile.Read(ref disposed) == 0;

    internal RoomPeer(TcpClient socket, SslStream stream, CancellationToken token, int incomingLimit = RoomWire.MaxPacket)
    { this.socket = socket; this.stream = stream; this.incomingLimit = incomingLimit; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); }

    public bool Send(RoomPacket packet)
    {
        if (!IsAlive) return false;
        if (outgoing.Writer.TryWrite(packet)) return true;
        Dispose();
        return false;
    }

    internal async Task Run(Action<RoomPacket> receive)
    {
        var write = SendLoop();
        var heartbeat = Heartbeat();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var packet = await RoomWire.Read(stream, timeout.Token, incomingLimit);
                if (packet.Type == "ping") Send(new RoomPacket { Type = "pong" });
                else if (packet.Type != "pong") receive(packet);
            }
        }
        finally
        {
            Dispose();
            try { await Task.WhenAll(write, heartbeat); } catch (Exception) { }
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        outgoing.Writer.TryComplete();
        socket.Dispose();
        stream.Dispose();
    }
}
