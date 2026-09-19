using BardStage.Core;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace BardStage;

public sealed record IncomingChatRequest(RequestChannel Channel, string Name, string World,
    string Query, DateTimeOffset ReceivedAtUtc, Guid SetlistId);

public sealed class ChatRequestReceiver : IDisposable
{
    private readonly IChatGui chat;
    private readonly IFramework framework;
    private readonly Func<RequestSettings> getSettings;
    private readonly Action<IncomingChatRequest> receive;
    private readonly object gate = new();
    private readonly Queue<IncomingChatRequest> messages = new();
    private readonly Dictionary<(Guid Show, RequestChannel Channel, string Name, string World, string Query), DateTimeOffset> recent = new();
    private bool disposed;
    public int DroppedCount { get; private set; }
    public int IssueCount { get; private set; }
    public string LastIssue { get; private set; } = "";

    public ChatRequestReceiver(IChatGui chat, IFramework framework, Func<RequestSettings> getSettings, Action<IncomingChatRequest> receive)
    {
        this.chat = chat;
        this.framework = framework;
        this.getSettings = getSettings;
        this.receive = receive;
        chat.ChatMessage += OnChat;
        framework.Update += Drain;
    }

    private void OnChat(IHandleableChatMessage message)
    {
        try
        {
            var channel = MapChannel(message.LogKind);
            var settings = getSettings();
            if (channel == null || !settings.IsOpen || settings.TargetSetlistId is not { } target
                || !settings.Channels.Contains(channel.Value)) return;
            var original = SeString.Parse(message.OriginalMessage.Data.Span);
            if (!ChatRequestParser.TryParse(original.TextValue, settings.Prefix, out var query)) return;
            var sender = SeString.Parse(message.OriginalSender.Data.Span);
            var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
            var name = player?.PlayerName ?? sender.TextValue.Trim();
            var world = player?.World.ValueNullable?.Name.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) return;
            var now = DateTimeOffset.UtcNow;
            lock (gate)
            {
                if (disposed) return;
                var key = (target, channel.Value, name, world, query);
                if (recent.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(2)) return;
                foreach (var old in recent.Where(x => now - x.Value >= TimeSpan.FromSeconds(2)).Select(x => x.Key).ToArray()) recent.Remove(old);
                if (messages.Count >= 256 || recent.Count >= 256)
                {
                    DroppedCount++;
                    ReportIssue("聊天点歌接收缓冲已满，部分新请求未接收。");
                    return;
                }
                recent[key] = now;
                messages.Enqueue(new IncomingChatRequest(channel.Value, name, world, query, now, target));
            }
        }
        catch (Exception ex) { lock (gate) ReportIssue("一条点歌消息未能解析：" + ex.Message); }
    }

    private void Drain(IFramework _)
    {
        lock (gate)
        {
            if (disposed) return;
            for (var i = 0; i < 32 && messages.TryDequeue(out var message); i++)
                receive(message);
        }
    }

    private void ReportIssue(string issue) { IssueCount++; LastIssue = issue; }

    public static RequestChannel? MapChannel(XivChatType type) => type switch
    {
        XivChatType.Say => RequestChannel.Say,
        XivChatType.TellIncoming => RequestChannel.Tell,
        XivChatType.Party => RequestChannel.Party,
        XivChatType.Shout => RequestChannel.Shout,
        XivChatType.Yell => RequestChannel.Yell,
        _ => null,
    };

    public void Dispose()
    {
        chat.ChatMessage -= OnChat;
        framework.Update -= Drain;
        lock (gate) { disposed = true; messages.Clear(); recent.Clear(); }
    }
}
