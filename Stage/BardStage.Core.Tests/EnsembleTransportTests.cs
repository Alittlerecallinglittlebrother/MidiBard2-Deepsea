using MidiBard.StageIntegration;
using Xunit;

public sealed class EnsembleTransportTests
{
    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("stop")]
    public void CommandsRoundTrip(string action)
    {
        var original = new EnsembleTransportCommand(action, "HASH", 11000, 500000, 1234567);
        Assert.True(EnsembleTransportCommand.TryParse(original.Encode(), "HASH", 10000, 1000000, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("pause", "OTHER", "10000", "0", "1")]
    [InlineData("bad", "HASH", "10000", "0", "1")]
    [InlineData("resume", "HASH", "4999", "0", "1")]
    [InlineData("resume", "HASH", "15001", "0", "1")]
    [InlineData("pause", "HASH", "10000", "-1", "1")]
    [InlineData("pause", "HASH", "10000", "1000001", "1")]
    [InlineData("stop", "HASH", "10000", "0", "0")]
    [InlineData("stop", "HASH", "9223372036854775807", "0", "1")]
    public void InvalidOrExpiredCommandsAreIgnored(params string[] args) =>
        Assert.False(EnsembleTransportCommand.TryParse(args, "HASH", 10000, 1000000, out _));

    [Fact]
    public void RapidPauseResumeStopRemainOrderedAndNewPlaybackOrLeaderResetsOrder()
    {
        var order = new EnsembleTransportOrder();
        var playback = new object();
        Assert.True(order.Accept(playback, 5, 10, 100));
        Assert.True(order.Accept(playback, 5, 10, 101));
        Assert.True(order.Accept(playback, 5, 10, 102));
        Assert.False(order.Accept(playback, 5, 10, 101));
        Assert.False(order.Accept(playback, 5, 10, 102));
        Assert.True(order.Accept(playback, 5, 11, 1));
        Assert.True(order.Accept(playback, 6, 11, 1));
        Assert.True(order.Accept(new object(), 6, 11, 1));
    }
}
