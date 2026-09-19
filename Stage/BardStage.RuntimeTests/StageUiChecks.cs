using System.Numerics;
using System.Runtime.InteropServices;
using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;

internal static unsafe partial class RuntimeUi
{
    private static void RenderStage(StageController controller, MainWindow window, string output)
    {
        Guid showId = default, currentId = default, nextId = default, alternateId = default;
        controller.Change(state =>
        {
            foreach (var old in state.Setlists)
                foreach (var active in old.Entries.Where(e => e.Status == EntryStatus.InProgress).ToArray())
                    SetlistOperations.Finish(old, active.Id, DateTimeOffset.UtcNow);
            var show = new ShowSetlist { Name = "夏夜小剧场", TargetEndUtc = DateTimeOffset.UtcNow.AddMinutes(9) };
            state.Setlists.Add(show); showId = show.Id;
            var current = SetlistOperations.AddSong(show, state.Songs[0]);
            var alternate = SetlistOperations.AddSong(show, state.Songs[1]);
            var next = SetlistOperations.AddSong(show, state.Songs[2]);
            currentId = current.Id; nextId = next.Id; alternateId = alternate.Id;
            SetlistOperations.AddSegment(show, EntryKind.Break, "中场休息", 300);
            SetlistOperations.Start(show, current.Id, DateTimeOffset.UtcNow.AddSeconds(-80));
            show.LockedNextEntryId = next.Id;
            var request = RequestOperations.Submit(state, show.Id, "听众甲", "海幻沙", "归途", RequestChannel.Manual, DateTimeOffset.UtcNow);
            RequestOperations.Arrange(state, [request.Id], state.Songs[2].Id, mergeEntryId: next.Id);
            RequestOperations.Submit(state, show.Id, "听众乙", "红玉海", "谢幕", RequestChannel.Manual, DateTimeOffset.UtcNow);
            state.SelectedSetlistId = show.Id;
        });
        Set(window, "stageShowId", showId); Set(window, "selectStageTab", true);
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "stage.png"));
        Frame(window, 760, 540); Frame(window, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "stage-small.png"));
        Frame(window, 1100, 740); Frame(window, 1100, 740);

        var io = ImGui.GetIO();
        var previousClipboard = io.SetClipboardTextFn;
        var copied = "";
        SetClipboardTextFn clipboard = (_, bytes) => copied = Marshal.PtrToStringUTF8((nint)bytes) ?? "";
        io.SetClipboardTextFn = (void*)Marshal.GetFunctionPointerForDelegate(clipboard);
        try
        {
            ShowSetlist Show() => controller.State.Setlists.Single(s => s.Id == showId);
            var draft = Get<AnnouncementDraft>(window, "announcementDraft");
            ReplaceStageText(window, 740, 262, "主持人手动稿：归途");
            StageCheck(draft.Edited && draft.Text == "主持人手动稿：归途", "native stage draft accepts manual edits");
            Click(window, 635, 432);
            StageCheck(copied == draft.Text, "native copy uses the current edited draft with an isolated clipboard callback");
            Click(window, 280, 357); Click(window, 90, 381);
            StageCheck(Show().LockedNextEntryId == alternateId && draft.Text.Contains("月下华尔兹")
                && draft.History.Any(h => h.Text == "主持人手动稿：归途" && h.Label.Contains("归途")),
                "native next selection refreshes the draft and archives the former manual text");
            Click(window, 20, 544);
            StageCheck(Show().LockedNextEntryId == nextId && draft.Text.Contains("归途"), "native queue lock selects the next program by ID");

            Click(window, 1074, 197); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "stage-history.png"));
            Click(window, 350, 458);
            StageCheck(copied == "主持人手动稿：归途", "native history copies the selected old draft");
            Click(window, 420, 458);

            Click(window, 20, 301); Frame(window, 1100, 740);
            StageCheck(Show().Entries.Single(e => e.Id == currentId).Status == EntryStatus.Completed
                && StageOperations.Current(Show()) == null && StageOperations.Next(Show())?.Id == nextId,
                "native complete prepares the locked next program without starting it");
            SoftwareRenderer.Save(Path.Combine(output, "stage-ready.png"));
            Click(window, 20, 336); Frame(window, 1100, 740);
            StageCheck(StageOperations.Current(Show())?.Id == nextId, "native start explicitly begins the prepared next program");

            Set(window, "announcementKind", AnnouncementKind.Thanks); Frame(window, 1100, 740);
            StageCheck(draft.Text.Contains("听众甲@海幻沙") && draft.Text.Contains("归途"), "native thanks binds to the current requested song");
            Click(window, 1037, 197); Frame(window, 1100, 740);
            ReplaceStageText(window, 480, 221, "感谢{requesters}点播《{title}》，{show}祝各位晚安。");
            SoftwareRenderer.Save(Path.Combine(output, "stage-template.png"));
            Click(window, 350, 505); Frame(window, 1100, 740);
            StageCheck(controller.Announcements.Templates[AnnouncementKind.Thanks].Contains("祝各位晚安")
                && draft.Text.Contains("听众甲@海幻沙") && draft.Text.Contains("归途"), "native template save refreshes the correctly associated thanks draft");

            Click(window, 328, 101); Frame(window, 1100, 740); Frame(window, 1100, 740);
            StageCheck(Get<bool>(window, "returnToStage"), "native request navigation retains the stage return target");
            controller.Change(s => s.SelectedSetlistId = s.Setlists.First(x => x.Id != showId).Id);
            Frame(window, 1100, 740); Click(window, 20, 101); Frame(window, 1100, 740); Frame(window, 1100, 740);
            StageCheck(Get<Guid?>(window, "stageShowId") == showId && !Get<bool>(window, "returnToStage"),
                "native return restores the original stage after inspecting another request show");

            Click(window, 20, 538);
            StageCheck(Show().LockedNextEntryId == alternateId, "native queue preserves a locked program before insertion");
            Click(window, 40, 479); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "stage-insert.png"));
            Set(window, "stageSegmentTitle", "临时嘉宾介绍");
            Click(window, 400, 443); Frame(window, 1100, 740);
            var inserted = StageOperations.Next(Show());
            StageCheck(inserted?.Kind == EntryKind.Talk && inserted.Title == "临时嘉宾介绍", "native segment modal inserts a temporary next program");
            Click(window, 20, 321); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "stage-segment-ready.png"));
            Click(window, 20, 292); Frame(window, 1100, 740);
            StageCheck(StageOperations.Current(Show())?.Id == inserted!.Id, "native stage starts the inserted segment explicitly");
            SoftwareRenderer.Save(Path.Combine(output, "stage-segment-active.png"));
            Click(window, 20, 280); Frame(window, 1100, 740);
            StageCheck(StageOperations.Current(Show()) == null && StageOperations.Next(Show())?.Id == alternateId
                && Show().Entries.Single(e => e.Id == inserted!.Id).Status == EntryStatus.Completed,
                "native segment completion returns to the formerly locked program without auto-start");
            SoftwareRenderer.Save(Path.Combine(output, "stage-segment-finished.png"));
            Frame(window, 760, 540); Frame(window, 760, 540);
            Click(window, 683, 197, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "stage-template-small.png"));
        }
        catch
        {
            SoftwareRenderer.Save(Path.Combine(output, "stage-interaction-last.png"));
            throw;
        }
        finally
        {
            io.SetClipboardTextFn = previousClipboard;
            GC.KeepAlive(clipboard);
        }
    }

    private static void ReplaceStageText(MainWindow window, float x, float y, string text)
    {
        Click(window, x, y);
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Frame(window, 1100, 740);
        io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
        foreach (var character in text) io.AddInputCharacter(character);
        Frame(window, 1100, 740);
        Click(window, 750, 174);
    }

    private static void StageCheck(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
