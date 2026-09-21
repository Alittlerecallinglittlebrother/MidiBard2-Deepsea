using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using BardStage.Core.Rooms;
using Xunit;

namespace BardStage.Core.Tests;

public sealed partial class RoomTransportTests
{
    [Fact]
    public async Task ViewerCanRequestAuthenticatedSongChunksWithoutCatalogAccess()
    {
        using var server = new RoomServer(0);
        server.Publish(new RoomSnapshot { RoomId = server.RoomId, Revision = 1 });
        using var viewer = new RoomClient(server.Invite("127.0.0.1", server.Port, RoomRole.Viewer));
        await Until(() => viewer.Connected);
        var hash = new string('A', 64);
        var request = new RoomSongRequest(Guid.NewGuid(), hash);
        Assert.True(viewer.SendSongRequest(request));
        (RoomPeer Peer, RoomSongRequest Request) received = default;
        await Until(() => server.TrySongRequest(out received));
        Assert.Equal(request, received.Request);
        using var presenter = new RoomClient(server.Invite("127.0.0.1", server.Port));
        await Until(() => presenter.Connected);
        Assert.False(presenter.SendSongRequest(request));
        received.Peer!.Send(new RoomPacket { Type = "songChunk", RoomId = server.RoomId,
            SongChunk = new(request.Id, hash, 0, 3, "song.mid", [1, 2, 3]) });
        received.Peer.Send(new RoomPacket { Type = "songResult", RoomId = server.RoomId,
            SongResult = new(request.Id, hash, true, "ok") });
        RoomPacket packet = null!;
        await Until(() => viewer.TrySongPacket(out packet));
        Assert.Equal("songChunk", packet.Type);
        await Until(() => viewer.TrySongPacket(out packet));
        Assert.True(packet.SongResult!.Success);
        viewer.Dispose(); presenter.Dispose(); server.Dispose();
        await Task.WhenAll(viewer.Completion, presenter.Completion, server.Completion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PinnedTlsAuthenticatesAndExchangesStateAndCommands()
    {
        using var server = new RoomServer(0);
        server.Publish(new RoomSnapshot { RoomId = server.RoomId, Revision = 12 });
        var invite = server.Invite("127.0.0.1", server.Port);
        Assert.Equal(invite, RoomInvite.Decode(invite.Encode()));
        using var client = new RoomClient(invite);
        try { await Until(() => client.Connected); }
        catch { throw new InvalidOperationException(server.LastConnectionError ?? client.Status); }
        Assert.Equal(12, client.Snapshot!.Revision);
        Assert.True(server.HasPresenter);
        var command = new RoomCommand { RoomId = server.RoomId, Action = RoomAction.Add };
        Assert.True(client.Send(command));
        (RoomPeer Peer, RoomCommand Command) received = default;
        await Until(() => server.TryRead(out received));
        Assert.Equal(command.Id, received.Command!.Id);
        received.Peer!.Send(new RoomPacket { Type = "result", Result = new(command.Id, true, "ok"),
            Snapshot = new() { RoomId = server.RoomId, Revision = 13 } });
        RoomResult result = null!;
        await Until(() => client.TryResult(out result));
        Assert.True(result.Success);
        Assert.Equal(13, client.Snapshot!.Revision);
        received.Peer.Send(new RoomPacket { Type = "snapshot", Snapshot = new() { RoomId = server.RoomId, Revision = 12 } });
        received.Peer.Send(new RoomPacket { Type = "result", Result = new(command.Id, true, "barrier"),
            Snapshot = new() { RoomId = server.RoomId, Revision = 12 } });
        await Until(() => client.TryResult(out result));
        Assert.Equal(13, client.Snapshot!.Revision);
        client.Dispose(); server.Dispose();
        await Task.WhenAll(client.Completion, server.Completion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongSecretOrCertificateNeverReceivesCatalog(bool wrongKey)
    {
        using var server = new RoomServer(0);
        server.Publish(new RoomSnapshot { RoomId = server.RoomId });
        var invite = server.Invite("127.0.0.1", server.Port);
        invite = wrongKey ? invite with { Key = new string('0', 64) } : invite with { Fingerprint = new string('0', 64) };
        using var client = new RoomClient(invite);
        await Task.Delay(1200);
        Assert.False(client.Connected); Assert.Null(client.Snapshot); Assert.False(server.HasPresenter);
        client.Dispose(); server.Dispose();
        await Task.WhenAll(client.Completion, server.Completion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SecondPresenterCannotReplaceFirstAndDisconnectCanReconnect()
    {
        using var server = new RoomServer(0);
        server.Publish(new RoomSnapshot { RoomId = server.RoomId });
        var invite = server.Invite("127.0.0.1", server.Port);
        using var first = new RoomClient(invite);
        await Until(() => first.Connected);
        using var second = new RoomClient(invite);
        await Task.Delay(350);
        Assert.False(second.Connected); Assert.True(first.Connected);
        second.Dispose(); await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var generation = first.Generation;
        Assert.True(first.Send(new() { Action = RoomAction.Stop }));
        (RoomPeer Peer, RoomCommand Command) received = default;
        await Until(() => server.TryRead(out received));
        received.Peer!.Dispose();
        await Until(() => first.Connected && first.Generation > generation);
        first.Dispose(); server.Dispose();
        await Task.WhenAll(first.Completion, server.Completion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8388609)]
    public async Task InvalidFrameLengthIsRejectedBeforeAllocation(int length)
    {
        using var server = new RoomServer(0);
        var invite = server.Invite("127.0.0.1", server.Port);
        using var socket = new TcpClient();
        await socket.ConnectAsync(invite.Host, invite.Port);
        using var stream = await Authenticate(socket, invite);
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, length);
        await stream.WriteAsync(header);
        await AssertClosed(stream);
        Assert.False(server.HasPresenter);
        server.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuthenticatedCommandFloodIsBoundedAndReleasesPort()
    {
        using var server = new RoomServer(0);
        var port = server.Port;
        var invite = server.Invite("127.0.0.1", port);
        using var socket = new TcpClient();
        await socket.ConnectAsync(invite.Host, port);
        using var stream = await Authenticate(socket, invite);
        await Write(stream, new RoomPacket { Type = "hello", Key = invite.Key, RoomId = invite.RoomId });
        await Until(() => server.HasPresenter);
        try
        {
            for (var i = 0; i < 140; i++)
                await Write(stream, new RoomPacket { Type = "command", Command = new() { Action = RoomAction.Add } });
        }
        catch (IOException) { }
        await Until(() => !server.HasPresenter);
        var count = 0;
        while (server.TryRead(out _)) count++;
        Assert.InRange(count, 1, 128);
        server.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var replacement = new RoomServer(port);
        Assert.NotEqual(invite.RoomId, replacement.RoomId);
        Assert.NotEqual(invite.Fingerprint, replacement.Invite("127.0.0.1", port).Fingerprint);
        replacement.Dispose(); await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnknownProtocolVersionCannotAuthenticate()
    {
        using var server = new RoomServer(0);
        var invite = server.Invite("127.0.0.1", server.Port);
        using var socket = new TcpClient(); await socket.ConnectAsync(invite.Host, invite.Port);
        using var stream = await Authenticate(socket, invite);
        await Write(stream, new RoomPacket { Version = 999, Type = "hello", Key = invite.Key, RoomId = invite.RoomId });
        await AssertClosed(stream); Assert.False(server.HasPresenter);
        server.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<SslStream> Authenticate(TcpClient socket, RoomInvite invite)
    {
        var stream = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert != null
            && Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)) == invite.Fingerprint);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "MidiBard-Room", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
        return stream;
    }

    private static async Task Write(Stream stream, RoomPacket packet)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, RoomJson.Options);
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header); await stream.WriteAsync(payload); await stream.FlushAsync();
    }

    private static async Task AssertClosed(Stream stream)
    {
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5))); }
        catch (IOException) { }
    }

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!done()) await Task.Delay(10, timeout.Token);
    }
}
