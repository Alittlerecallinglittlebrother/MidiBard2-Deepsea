using Xunit;

namespace BardStage.Core.Tests;

public sealed class ChatRequestParserTests
{
    [Theory]
    [InlineData("点歌 月下华尔兹", "月下华尔兹")]
    [InlineData("  点歌　 Moon River  ", "Moon River")]
    public void ParsesOnlyExplicitRequests(string input, string expected)
    {
        Assert.True(ChatRequestParser.TryParse(input, "点歌", out var query));
        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData("我想点歌 月下华尔兹")]
    [InlineData("点歌月下华尔兹")]
    [InlineData("点歌曲目 月下华尔兹")]
    [InlineData("点歌 ")]
    [InlineData("点歌 A\nB")]
    [InlineData("点歌 A\0B")]
    public void IgnoresConversationEmptyQueriesAndControls(string input) =>
        Assert.False(ChatRequestParser.TryParse(input, "点歌", out _));

    [Fact]
    public void AppliesConfiguredPrefixAndLengthLimit()
    {
        Assert.False(ChatRequestParser.TryParse("点歌 Song", "request", out _));
        Assert.True(ChatRequestParser.TryParse("request Song", "request", out var query));
        Assert.Equal("Song", query);
        Assert.False(ChatRequestParser.TryParse("点歌 " + new string('a', 257), "点歌", out _));
    }
}
