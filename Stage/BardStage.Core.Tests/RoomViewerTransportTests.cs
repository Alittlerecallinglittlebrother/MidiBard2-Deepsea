using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using BardStage.Core.Rooms;
using Xunit;

namespace BardStage.Core.Tests;

public sealed partial class RoomTransportTests
{
    [Fact]
    public async Task SevenViewersShareUpdatesWithoutOccupyingPresenterAndFreedSlotsCanRejoin()
    {
        using var server = new RoomServer(0);
        var state = ViewerFixture(server.RoomId);
        server.Publish(state);
        var invite = server.Invite("127.0.0.1", server.Port, RoomRole.Viewer);
        Assert.Equal(invite, RoomInvite.Decode(invite.Encode()));
        Assert.NotEqual(invite.Key, server.Invite("127.0.0.1", server.Port).Key);
        var viewers = new List<RoomClient>();
        try
        {
            for (var i = 0; i < RoomServer.MaxViewers; i++)
            {
                var viewer = new RoomClient(invite); viewers.Add(viewer);
                await Until(() => viewer.Connected);
            }
            Assert.Equal(7, server.ViewerCount); Assert.False(server.HasPresenter);
            using var presenter = new RoomClient(server.Invite("127.0.0.1", server.Port));
            await Until(() => presenter.Connected);
            Assert.Single(presenter.Snapshot!.Catalog.Songs);
            using var overflow = new RoomClient(invite);
            await Task.Delay(350); Assert.False(overflow.Connected);
            overflow.Dispose(); await overflow.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            state.Revision++;
            state.Catalog.Setlists[0].LockedNextEntryId = state.Catalog.Setlists[0].Entries[1].Id;
            server.Publish(state);
            await Until(() => viewers.All(v => v.Snapshot?.Revision == state.Revision));
            Assert.All(viewers, v =>
            {
                var snapshot = v.Snapshot!;
                Assert.Equal(RoomRole.Viewer, v.Role);
                Assert.Empty(snapshot.Catalog.Songs); Assert.Empty(snapshot.Catalog.Requests); Assert.Empty(snapshot.Catalog.Sessions);
                Assert.Equal(2, snapshot.Catalog.Setlists.Single().Entries.Count);
                Assert.All(snapshot.Catalog.Setlists[0].Entries, e => Assert.Equal("", e.Notes));
                Assert.Equal("Second", StageOperations.Next(snapshot.Catalog.Setlists[0])!.Title);
                Assert.False(v.Send(new() { Action = RoomAction.Skip }));
            });
            Assert.False(server.TryRead(out _));
            viewers[0].Dispose(); await viewers[0].Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => server.ViewerCount == 6);
            using var replacement = new RoomClient(invite); await Until(() => replacement.Connected);
            Assert.Equal(7, server.ViewerCount); Assert.True(presenter.Connected);
            replacement.Dispose(); presenter.Dispose();
            await Task.WhenAll(replacement.Completion, presenter.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var viewer in viewers) viewer.Dispose();
            server.Dispose();
            await Task.WhenAll(viewers.Select(v => v.Completion).Append(server.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ViewerKeyCannotAuthenticateAsPresenterEvenWithForgedRole()
    {
        using var server = new RoomServer(0); server.Publish(ViewerFixture(server.RoomId));
        var invite = server.Invite("127.0.0.1", server.Port, RoomRole.Viewer);
        using var socket = new TcpClient(); await socket.ConnectAsync(invite.Host, invite.Port);
        using var stream = await Authenticate(socket, invite);
        await Write(stream, new() { Type = "hello", RoomId = invite.RoomId, Key = invite.Key, Role = RoomRole.Presenter });
        await AssertClosed(stream);
        Assert.False(server.HasPresenter); Assert.Equal(0, server.ViewerCount); Assert.False(server.TryRead(out _));
        server.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerRejectsEveryViewerCommandEvenWhenClientGuardIsBypassed()
    {
        using var server = new RoomServer(0); server.Publish(ViewerFixture(server.RoomId));
        var invite = server.Invite("127.0.0.1", server.Port, RoomRole.Viewer);
        using var presenter = new RoomClient(server.Invite("127.0.0.1", server.Port));
        await Until(() => presenter.Connected);
        foreach (var action in Enum.GetValues<RoomAction>())
        {
            using var socket = new TcpClient(); await socket.ConnectAsync(invite.Host, invite.Port);
            using var stream = await Authenticate(socket, invite);
            await Write(stream, new() { Type = "hello", RoomId = invite.RoomId, Key = invite.Key, Role = RoomRole.Viewer });
            var packet = await ReadPacket(stream);
            Assert.Equal(RoomRole.Viewer, packet.Role);
            await Write(stream, new() { Type = "command", Role = RoomRole.Presenter,
                Command = new() { RoomId = server.RoomId, Revision = 1, Action = action } });
            await AssertClosed(stream);
            Assert.False(server.TryRead(out _));
            Assert.True(presenter.Connected);
        }
        presenter.Dispose(); server.Dispose();
        await Task.WhenAll(presenter.Completion, server.Completion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconnectingViewerKeepsReadOnlyRoleAndReceivesLatestSnapshot()
    {
        using var server = new RoomServer(0); var state = ViewerFixture(server.RoomId); server.Publish(state);
        using var viewer = new RoomClient(server.Invite("127.0.0.1", server.Port, RoomRole.Viewer));
        await Until(() => viewer.Connected);
        var generation = viewer.Generation;
        var peer = (RoomPeer)typeof(RoomClient).GetField("peer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        peer.Dispose();
        state.Revision = 20; state.Catalog.Setlists[0].Entries.RemoveAt(0); server.Publish(state);
        await Until(() => viewer.Connected && viewer.Generation > generation);
        Assert.Equal(20, viewer.Snapshot!.Revision); Assert.Single(viewer.Snapshot.Catalog.Setlists[0].Entries);
        Assert.False(viewer.Send(new() { Action = RoomAction.Start })); Assert.False(server.HasPresenter);
        viewer.Dispose(); server.Dispose();
        await Task.WhenAll(viewer.Completion, server.Completion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ExistingPresenterInvitationWithoutRoleStillDecodesAsPresenter()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { host = "127.0.0.1", port = 28765,
            fingerprint = new string('1', 64), key = new string('2', 64), roomId = Guid.NewGuid() }, RoomJson.Options);
        Assert.Equal(RoomRole.Presenter, RoomInvite.Decode("mbroom1." + Convert.ToBase64String(json)).Role);
    }

    private static RoomSnapshot ViewerFixture(Guid roomId)
    {
        var show = new ShowSetlist { Name = "Shared show", Entries =
        [new() { Title = "First", Notes = "Private note" }, new() { Title = "Second" }, new() { Title = "Finished", Status = EntryStatus.Completed }] };
        return new() { RoomId = roomId, Revision = 1, Catalog = new()
        {
            Setlists = [show], RequestSettings = new() { TargetSetlistId = show.Id },
            Songs = [new() { Title = "Private library", FilePath = "C:/private/song.mid" }],
            Requests = [new() { RequesterName = "Private audience" }], Sessions = [new() { ShowName = "Private archive" }],
        } };
    }

    private static async Task<RoomPacket> ReadPacket(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await stream.ReadExactlyAsync(payload, timeout.Token);
        return JsonSerializer.Deserialize<RoomPacket>(payload, RoomJson.Options)!;
    }
}
