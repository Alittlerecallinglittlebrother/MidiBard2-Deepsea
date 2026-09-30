using Xunit;

namespace BardStage.Core.Tests;

public sealed class PlaybackTailTests
{
    [Fact]
    public void AStoppedOutputTickerCannotBeMistakenForADrainedTail()
    {
        var time = TimeSpan.Zero;
        var tail = new PlaybackTailGuard(TimeSpan.FromSeconds(6), () => time);
        Assert.False(tail.IsReady(2, 3));
        time = TimeSpan.FromMinutes(1);
        Assert.False(tail.IsReady(1, 3));
        Assert.False(tail.IsReady(0, 3));
        time += TimeSpan.FromSeconds(5.999);
        Assert.False(tail.IsReady(0, 3));
        time += TimeSpan.FromMilliseconds(1);
        Assert.True(tail.IsReady(0, 3));
    }

    [Fact]
    public void OutputThatEnqueuesAndDrainsBetweenPollsRestartsProtection()
    {
        var time = TimeSpan.Zero;
        var tail = new PlaybackTailGuard(TimeSpan.FromSeconds(6), () => time);
        Assert.False(tail.IsReady(0, 8));
        time += TimeSpan.FromSeconds(5);
        Assert.False(tail.IsReady(0, 9));
        time += TimeSpan.FromSeconds(1);
        Assert.False(tail.IsReady(0, 9));
        time += TimeSpan.FromSeconds(5);
        Assert.True(tail.IsReady(0, 9));
    }

    [Fact]
    public void NewOutputCancelsAQuietInterval()
    {
        var time = TimeSpan.Zero;
        var tail = new PlaybackTailGuard(TimeSpan.FromSeconds(6), () => time);
        Assert.False(tail.IsReady(0, 0));
        time += TimeSpan.FromSeconds(7);
        Assert.False(tail.IsReady(1, 1));
        time += TimeSpan.FromSeconds(7);
        Assert.False(tail.IsReady(0, 1));
        time += TimeSpan.FromSeconds(6);
        Assert.True(tail.IsReady(0, 1));
    }
}
