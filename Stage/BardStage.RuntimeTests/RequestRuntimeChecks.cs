using System.Reflection;
using System.Text;
using System.Text.Json;
using BardStage;
using BardStage.Core;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Lumina.Text.ReadOnly;

internal static class RequestRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        var directory = Path.Combine(scratch, "request-data");
        Guid originalShow;
        using (var controller = new StageController(directory))
        {
            originalShow = controller.CurrentSetlist!.Id;
            controller.Change(s =>
            {
                s.RequestSettings.TargetSetlistId = originalShow;
                s.RequestSettings.IsOpen = true;
                s.Setlists.Add(new ShowSetlist { Name = "第二场演出" });
            });
            var chat = ServiceProxy.Create<IChatGui>();
            var framework = ServiceProxy.Create<IFramework>();
            var chatProxy = (ServiceProxy)(object)chat;
            var frameworkProxy = (ServiceProxy)(object)framework;
            using (var receiver = new ChatRequestReceiver(chat, framework, () => controller.ReceptionSettings, controller.ReceiveChat))
            {
                Check(!controller.Change(s => s.RequestSettings.Channels.Clear()) && controller.ReceptionSettings.Channels.Contains(RequestChannel.Say),
                    "invalid open-without-channels setting rolls back to working reception policy");
                controller.Import([midiPath]);
                chatProxy.Emit("ChatMessage", Message(XivChatType.Party, "其他观众", "点歌 不接收的频道"));
                chatProxy.Emit("ChatMessage", Message(XivChatType.TellOutgoing, "其他观众", "点歌 发出的私聊"));
                chatProxy.Emit("ChatMessage", Message(XivChatType.Say, "观众甲", "我想点歌 开场序曲"));
                chatProxy.Emit("ChatMessage", Message(XivChatType.Say, "观众甲", "点歌 开场序曲"));
                chatProxy.Emit("ChatMessage", Message(XivChatType.Say, "观众甲", "点歌 开场序曲"));
                chatProxy.Emit("ChatMessage", Message(XivChatType.TellIncoming, "观众乙", "点歌 月下华尔兹"));
                frameworkProxy.Emit("Update", framework);
                Check(controller.IsBusy && controller.PendingChatCount == 2 && controller.State.Requests.Count == 0,
                    "chat adapter filters channels/prefix, suppresses duplicate events, buffers while importing");
                while (controller.IsBusy) { Thread.Sleep(10); controller.Poll(); }
                Check(controller.State.Requests.Count == 2 && controller.PendingChatCount == 0 && receiver.IssueCount == 0,
                    "buffered requests persist after import");
                Check(controller.State.Requests[0].RequesterName == "观众甲" && controller.State.Requests[0].RequesterWorld == "",
                    "chat identity comes from sender; unknown world remains empty");
                chatProxy.Emit("ChatMessage", Message(XivChatType.Say, "观众丙", "点歌 第三首"));
                controller.Change(s => s.SelectedSetlistId = s.Setlists[1].Id);
                frameworkProxy.Emit("Update", framework); controller.Poll();
                Check(controller.State.Requests.Last().SetlistId == originalShow,
                    "viewing another show does not redirect chat requests");
                receiver.Dispose();
                chatProxy.Emit("ChatMessage", Message(XivChatType.Say, "离场观众", "点歌 不应接收"));
                frameworkProxy.Emit("Update", framework); controller.Poll();
                Check(controller.State.Requests.Count == 3, "chat subscriptions are removed on disposal");
            }
            var requestId = controller.State.Requests[0].Id;
            var songId = controller.State.Songs[0].Id;
            var file = Path.Combine(directory, "catalog.json");
            using (var blocker = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var changed = controller.Change(s => RequestOperations.Arrange(s, [requestId], songId));
                Check(!changed && controller.State.Requests[0].SetlistEntryId == null && controller.State.Setlists[0].Entries.Count == 0,
                    "failed disk save rolls back request approval and program together");
            }
            Check(controller.Change(s => RequestOperations.Arrange(s, [requestId], songId)), "approval persists in one transaction");
            var entryId = controller.State.Requests[0].SetlistEntryId!.Value;
            Check(!controller.Change(s => RequestOperations.Arrange(s, [requestId], songId)) && controller.State.Setlists[0].Entries.Count == 1,
                "repeated approval cannot create a duplicate program");
            controller.Change(s => SetlistOperations.Start(s.Setlists[0], entryId, DateTimeOffset.UtcNow));
            controller.Change(s => SetlistOperations.Finish(s.Setlists[0], entryId, DateTimeOffset.UtcNow));
            Check(RequestOperations.DisplayStatus(controller.State, controller.State.Requests[0]) == "已完成", "request completion follows manual program completion");
            controller.Change(s => SetlistOperations.Remove(s.Setlists[0], entryId));
            Check(controller.State.Requests[0].Status == RequestStatus.Deferred && controller.State.Requests[0].SetlistEntryId == null,
                "removing an associated program returns request for review");
            controller.ReceiveChat(new IncomingChatRequest(RequestChannel.Say, "重试观众", "", "保存重试", DateTimeOffset.UtcNow, originalShow));
            using (var blocker = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                controller.Poll();
                Check(controller.ChatWriteBlocked && controller.PendingChatCount == 1 && controller.State.Requests.Count == 3,
                    "failed chat save retains the buffered request");
            }
            controller.RetryPendingChat(); controller.Poll(); controller.Poll();
            Check(controller.PendingChatCount == 0 && controller.State.Requests.Count == 4, "retry saves buffered request exactly once");
            var bytesBefore = File.ReadAllBytes(file);
            var backupBefore = File.ReadAllBytes(file + ".bak");
            controller.ReceiveChat(new IncomingChatRequest(RequestChannel.Say, "重试观众", "", "保存重试", DateTimeOffset.UtcNow, originalShow));
            controller.Poll();
            Check(File.ReadAllBytes(file).SequenceEqual(bytesBefore) && File.ReadAllBytes(file + ".bak").SequenceEqual(backupBefore),
                "rejected duplicate does not rewrite catalog or rolling backup");
            controller.Change(s => s.SelectedSetlistId = originalShow);
            var export = Path.Combine(scratch, "requests-export.json");
            controller.ExportCurrentSetlist(export);
            using (var json = JsonDocument.Parse(File.ReadAllText(export)))
                Check(json.RootElement.GetProperty("requests").GetArrayLength() == 0 && !json.RootElement.GetProperty("requestSettings").GetProperty("isOpen").GetBoolean(),
                    "program export excludes audience history and reception settings");
            controller.ImportSetlist(export);
            Check(!controller.StatusIsError && controller.State.Requests.Count == 4, "program import does not duplicate audience history");
            Check(!controller.Change(s => s.Setlists.RemoveAll(show => show.Id == originalShow)),
                "deleting a show with an unarchived session is blocked");
            Check(controller.Change(s => SessionOperations.Archive(s, originalShow, DateTimeOffset.UtcNow)), "archive before deleting the show");
            controller.Change(s => { s.Setlists.RemoveAll(show => show.Id == originalShow); s.SelectedSetlistId = s.Setlists[0].Id; });
            Check(controller.State.Requests.All(r => r.Status == RequestStatus.Cancelled && r.SetlistId == null)
                && !controller.State.RequestSettings.IsOpen && controller.State.RequestSettings.TargetSetlistId == null,
                "deleting reception show cancels associations and closes reception");
        }
        using var reopened = new StageController(directory);
        Check(!reopened.IsReadOnly && reopened.State.Requests.Count == 4 && reopened.State.Requests.All(r => r.SetlistName.Length > 0),
            "request history and show snapshots restore after restart");
    }

    private static IHandleableChatMessage Message(XivChatType type, string sender, string text)
    {
        var message = ServiceProxy.Create<IHandleableChatMessage>();
        var proxy = (ServiceProxy)(object)message;
        proxy.Properties["LogKind"] = type;
        proxy.Properties["OriginalSender"] = new ReadOnlySeString(Encoding.UTF8.GetBytes(sender));
        proxy.Properties["OriginalMessage"] = new ReadOnlySeString(Encoding.UTF8.GetBytes(text));
        return message;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class ServiceProxy : DispatchProxy
{
    public Dictionary<string, object> Properties { get; } = [];
    public Func<MethodInfo, object?[]?, object?>? MethodHandler { get; set; }
    private readonly Dictionary<string, Delegate?> events = [];
    public static T Create<T>() where T : class => Create<T, ServiceProxy>();
    public void Emit(string name, params object[] args) { if (events.TryGetValue(name, out var handler)) handler?.DynamicInvoke(args); }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal))
        {
            var key = name[4..]; events.TryGetValue(key, out var previous);
            events[key] = Delegate.Combine(previous, (Delegate)args![0]!); return null;
        }
        if (name.StartsWith("remove_", StringComparison.Ordinal))
        {
            var key = name[7..]; events.TryGetValue(key, out var previous);
            events[key] = Delegate.Remove(previous, (Delegate)args![0]!); return null;
        }
        if (name.StartsWith("get_", StringComparison.Ordinal) && Properties.TryGetValue(name[4..], out var value)) return value;
        if (MethodHandler != null) return MethodHandler(targetMethod, args);
        return targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType ? null : Activator.CreateInstance(targetMethod.ReturnType);
    }
}
