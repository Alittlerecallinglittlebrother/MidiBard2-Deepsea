using System.Text.Json;
using BardStage.Core.Rooms;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class TransportClockTests
{
    [Fact] public void FourTimestampsExcludeServerQueueDelayAndRejectReplay()
    {
        double now = 0;
        var clock = new TransportClock(() => now);
        for (int i = 0; i < 8; i++)
        {
            now = i;
            var p = new ClockExchange { Id = Guid.NewGuid() };
            now += .123; // Time in the client outgoing queue is not send time.
            clock.Sending(p);
            Assert.Equal(now, p.Sent);
            var reply = new ClockExchange { Id = p.Id, Sent = p.Sent,
                Received = now + 900 + .010, Replied = now + 900 + .210 };
            now += .220;
            Assert.True(clock.Accept(reply, now));
            Assert.False(clock.Accept(reply, now));
        }
        Assert.True(clock.State.Ready);
        Assert.InRange(clock.State.Offset, 899.99999, 900.00001);
        Assert.InRange(clock.State.Rtt, .01999, .02001);
        now += 3;
        Assert.False(clock.State.Ready);
    }
    [Fact] public void UnsolicitedMismatchedAndNonFiniteRepliesCannotCalibrate()
    {
        double now = 1;
        var clock = new TransportClock(() => now);
        Assert.False(clock.Accept(new() { Id = Guid.NewGuid() }, 2));
        var probe = new ClockExchange { Id = Guid.NewGuid() }; clock.Sending(probe);
        Assert.False(clock.Accept(new() { Id = probe.Id, Sent = 0, Received = 1, Replied = 1 }, 2));
        probe = new() { Id = Guid.NewGuid() }; clock.Sending(probe);
        Assert.False(clock.Accept(new() { Id = probe.Id, Sent = probe.Sent, Received = double.NaN }, 2));
        Assert.False(clock.State.Ready);
    }
    [Fact] public void ReceiveTimestampIsNeverTakenFromTheWire()
    {
        var packet = JsonSerializer.Deserialize<RoomPacket>("{\"ReceivedAt\":99999,\"Type\":\"clockReply\"}");
        Assert.Equal(0, packet!.ReceivedAt);
        Assert.DoesNotContain("ReceivedAt", JsonSerializer.Serialize(new RoomPacket { ReceivedAt = 99 }));
    }
    [Fact] public async Task LoopbackCalibratesDifferentClockOriginsWithoutAnyFrameworkPolling()
    {
        using var server = new RoomServer(0, 7, () => TransportClock.Now + 1200);
        server.Publish(new RoomSnapshot { RoomId = server.RoomId, TimingVersion = 1 });
        using var client = new RoomClient(server.Invite("127.0.0.1", server.Port, RoomRole.Viewer), () => TransportClock.Now + 17);
        var end = TransportClock.Now + 10;
        while (!client.Timing.Ready && TransportClock.Now < end) await Task.Delay(20);
        Assert.True(client.Timing.Ready);
        Assert.InRange(client.Timing.Offset, 1182.98, 1183.02);
        // No room or game tick occurs during this wait, including after samples age out.
        await Task.Delay(3000);
        Assert.True(client.Timing.Ready);
        Assert.True(client.Timing.Age < .5);
    }
    [Fact] public async Task LegacyHostIsNotSentUnknownCalibrationMessages()
    {
        using var server = new RoomServer(0);
        server.Publish(new RoomSnapshot { RoomId = server.RoomId });
        using var client = new RoomClient(server.Invite("127.0.0.1", server.Port, RoomRole.Viewer));
        await Task.Delay(1500);
        Assert.True(client.Connected);
        Assert.False(client.Timing.Ready);
    }
}
