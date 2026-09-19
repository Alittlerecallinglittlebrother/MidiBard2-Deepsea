using System.Numerics;
using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;

internal static unsafe partial class RuntimeUi
{
    private static void RenderCleanup(StageController source, string output)
    {
        using var controller = new StageController(Path.Combine(Path.GetTempPath(), "cleanup-ui-" + Guid.NewGuid()));
        controller.Change(s => s.Songs = StageController.Clone(source.State.Songs));
        controller.EnsureAutomaticQueue();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        controller.Change(s =>
        {
            var show = s.Setlists[0]; show.Name = "观众点歌";
            RequestOperations.Submit(s, show.Id, "观众甲", "", s.Songs[0].Title, RequestChannel.Manual, start);
            RequestOperations.Submit(s, show.Id, "观众乙", "", s.Songs[1].Title, RequestChannel.Manual, start);
            SetlistOperations.AddSong(show, s.Songs[2]);
        });
        var show = controller.CurrentSetlist!;
        var first = show.Entries[0].Id; var second = show.Entries[1].Id;
        controller.Change(s => SetlistOperations.Start(s.Setlists[0], first, start), atUtc: start);
        controller.Change(s => SetlistOperations.Finish(s.Setlists[0], first, start.AddSeconds(30)), atUtc: start.AddSeconds(30));
        controller.Change(s => SetlistOperations.Skip(s.Setlists[0], second, start.AddSeconds(40)), atUtc: start.AddSeconds(40));
        using var window = new MainWindow(controller);
        SeedNarrowSetlistColumns();
        Set(window, "requestFilter", 1);
        foreach (var (method, name) in new[] { ("DrawAutomaticQueue", "cleanup-queue"), ("DrawSetlists", "cleanup-setlist"), ("DrawSessions", "cleanup-sessions"), ("DrawLibrary", "library-delete") })
        {
            CleanupFrame(window, method); CleanupFrame(window, method);
            if (method == "DrawSetlists") CheckEntryActions();
            if (method == "DrawAutomaticQueue") { CleanupClick(window, method, 70, 201); CleanupFrame(window, method); }
            SoftwareRenderer.Save(Path.Combine(output, name + ".png"));
            ImGui.GetIO().FontGlobalScale = 1.4f;
            CleanupFrame(window, method, 760, 540); CleanupFrame(window, method, 760, 540);
            if (method == "DrawSetlists") CheckEntryActions();
            SoftwareRenderer.Save(Path.Combine(output, name + "-small-scaled.png"));
            ImGui.GetIO().FontGlobalScale = 1;
        }
        Console.WriteLine("PASS: cleanup views render at desktop and 140% compact window using native ImGui");
        foreach (var (width, scale) in new[] { (1300, 1.2f), (1500, 1.4f), (960, 1f) })
        {
            ImGui.GetIO().FontGlobalScale = scale;
            CleanupFrame(window, "DrawSetlists", width); CleanupFrame(window, "DrawSetlists", width);
            CheckEntryActions();
            SoftwareRenderer.Save(Path.Combine(output, $"setlist-actions-{width}-{scale:0.0}.png"));
        }
        ImGui.GetIO().FontGlobalScale = 1;
        Console.WriteLine("PASS: setlist actions remain inside their clip rectangle after narrow legacy widths and font scaling");
        CleanupFrame(window, "DrawSessions"); CleanupFrame(window, "DrawSessions");
        CleanupClick(window, "DrawSessions", 21, 142);
        CleanupFrame(window, "DrawSessions");
        SoftwareRenderer.Save(Path.Combine(output, "cleanup-export-menu.png"));
        var popup = CleanupPopupBounds();
        CleanupClick(window, "DrawSessions", popup.X + 20, popup.Y + 12);
        CleanupFrame(window, "DrawSessions"); CleanupFrame(window, "DrawSessions");
        SoftwareRenderer.Save(Path.Combine(output, "cleanup-export-result.png"));
        CleanupCheck(!controller.StatusIsError && File.Exists(controller.LastSessionExportPath)
            && Path.GetExtension(controller.LastSessionExportPath) == ".json" && controller.State.Sessions.Single().EndedAtUtc == null,
            "native unarchived export menu writes a real JSON file without archiving");
        var jsonPath = controller.LastSessionExportPath;
        CleanupClick(window, "DrawSessions", 21, 142);
        CleanupFrame(window, "DrawSessions");
        popup = CleanupPopupBounds();
        CleanupClick(window, "DrawSessions", popup.X + 20, popup.Y + 12 + ImGui.GetTextLineHeightWithSpacing());
        CleanupFrame(window, "DrawSessions");
        CleanupCheck(!controller.StatusIsError && File.Exists(controller.LastSessionExportPath)
            && Path.GetExtension(controller.LastSessionExportPath) == ".csv" && File.Exists(jsonPath)
            && File.ReadAllBytes(controller.LastSessionExportPath).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })
            && controller.State.Sessions.Single().EndedAtUtc == null,
            "native CSV export writes an Excel-compatible file and preserves the prior JSON export");

        CleanupFrame(window, "DrawAutomaticQueue"); CleanupFrame(window, "DrawAutomaticQueue");
        CleanupClickItem(window, "DrawAutomaticQueue", "deleteFinished");
        ConfirmCleanup(window, "DrawAutomaticQueue");
        CleanupCheck(controller.CurrentSetlist!.Entries.Count == 2, "native ended-row trash removes a finished program");
        CleanupClickItem(window, "DrawAutomaticQueue", "clearQueueHistory");
        ConfirmCleanup(window, "DrawAutomaticQueue");
        CleanupCheck(controller.CurrentSetlist.Entries.Count == 1 && controller.CurrentSetlist.Entries[0].Status == EntryStatus.Queued,
            "native clear-ended button preserves the waiting queue");

        CleanupFrame(window, "DrawSetlists"); CleanupFrame(window, "DrawSetlists");
        CleanupClickItem(window, "DrawSetlists", "deleteEntryRow");
        ConfirmCleanup(window, "DrawSetlists");
        CleanupCheck(controller.CurrentSetlist.Entries.Count == 0 && controller.State.Songs.Count > 0, "native program row deletion preserves the song library");

        CleanupFrame(window, "DrawSessions"); CleanupFrame(window, "DrawSessions");
        CleanupClick(window, "DrawSessions", 1072, 193);
        ConfirmCleanup(window, "DrawSessions");
        CleanupCheck(controller.State.Sessions.Single().Attempts.Count == 1, "native attempt-row trash removes one performance detail");
        CleanupClick(window, "DrawSessions", 96, 142);
        ConfirmCleanup(window, "DrawSessions");
        CleanupCheck(controller.State.Sessions.Count == 0 && controller.State.Songs.Count > 0, "native session trash deletes an idle unarchived session");

        CleanupFrame(window, "DrawLibrary"); CleanupFrame(window, "DrawLibrary");
        var song = controller.State.Songs[0]; var songCount = controller.State.Songs.Count;
        CleanupClickItem(window, "DrawLibrary", "deleteLibrarySong:" + song.Id);
        CleanupCheck(controller.State.Songs.Count == songCount, "library delete waits for confirmation");
        ConfirmCleanup(window, "DrawLibrary");
        CleanupCheck(controller.State.Songs.Count == songCount - 1 && controller.State.Songs.All(s => s.Id != song.Id) && File.Exists(song.FilePath),
            "native library row delete removes only the selected entry and preserves MIDI");
        CleanupFrame(window, "DrawLibrary");
        CleanupClickItem(window, "DrawLibrary", "clearLibrary"); ConfirmCleanup(window, "DrawLibrary");
        CleanupCheck(controller.State.Songs.Count == 0 && File.Exists(song.FilePath), "native clear library removes catalog entries without deleting MIDI files");
    }

    private static void CleanupClickItem(MainWindow window, string method, string id)
    {
        var bounds = items[id]; var point = (bounds.Min + bounds.Max) / 2;
        CleanupClick(window, method, point.X, point.Y);
    }

    private static void CheckEntryActions()
    {
        var clip = items["entryActionsClip"];
        foreach (var name in new[] { "up", "down", "lock", "deleteEntryRow" })
        {
            var bounds = items[name];
            if (bounds.Min.X < clip.Min.X || bounds.Max.X > clip.Max.X || bounds.Min.Y < clip.Min.Y || bounds.Max.Y > clip.Max.Y)
                throw new InvalidOperationException($"clipped setlist action {name}: {bounds}, clip {clip}");
        }
    }

    private static void SeedNarrowSetlistColumns()
    {
        ImGui.GetIO().DisplaySize = new Vector2(1100, 740);
        ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new Vector2(1100, 740));
        ImGui.Begin("midibard2-深海回响特供版 · 记录管理##Cleanup", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
        if (ImGui.BeginTable("ShowLayout", 2, ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("entries", ImGuiTableColumnFlags.WidthStretch, .67f);
            ImGui.TableSetupColumn("inspector", ImGuiTableColumnFlags.WidthStretch, .33f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("ShowEntries", new Vector2(0, 400), false))
            {
                if (ImGui.BeginTable("Entries", 5, ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -1)))
                {
                    ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28);
                    ImGui.TableSetupColumn("节目", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("时长", ImGuiTableColumnFlags.WidthFixed, 57);
                    ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 65);
                    ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableHeadersRow(); ImGui.EndTable();
                }
            }
            ImGui.EndChild(); ImGui.EndTable();
        }
        ImGui.End(); ImGui.Render();
    }

    private static void ConfirmCleanup(MainWindow window, string method)
    {
        if (Get<Action?>(window, "confirmAction") == null) throw new InvalidOperationException("delete button did not open confirmation: " + method);
        CleanupFrame(window, method); CleanupFrame(window, method);
        var popup = CleanupPopupBounds();
        CleanupClick(window, method, popup.X + 45, popup.W - 20);
        CleanupFrame(window, method);
        if (Get<Action?>(window, "confirmAction") != null)
        {
            var path = Path.Combine(Path.GetTempPath(), "cleanup-confirm-failure.png");
            SoftwareRenderer.Save(path);
            throw new InvalidOperationException($"native confirmation button missed; bounds={popup}; screenshot={path}");
        }
    }

    private static Vector4 CleanupPopupBounds()
    {
        var draw = ImGui.GetDrawData();
        if (draw.CmdListsCount < 2) throw new InvalidOperationException("cleanup popup did not open");
        var size = ImGui.GetIO().DisplaySize;
        // Modal dimming uses a full-screen command after the popup's own content.
        for (var i = draw.CmdListsCount - 1; i >= 0; i--)
        {
            ImDrawListPtr list = draw.CmdLists[i];
            for (var j = list.CmdBuffer.Size - 1; j >= 0; j--)
            {
                var bounds = list.CmdBuffer[j].ClipRect;
                if (bounds.Z > bounds.X && bounds.W > bounds.Y
                    && bounds.Z - bounds.X < size.X - 1 && bounds.W - bounds.Y < size.Y - 1) return bounds;
            }
        }
        throw new InvalidOperationException("cleanup popup has no content bounds");
    }

    private static void CleanupCheck(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }

    private static void CleanupFrame(MainWindow window, string method, int width = 1100, int height = 740)
    {
        ImGui.GetIO().DisplaySize = new Vector2(width, height);
        ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.Begin("midibard2-深海回响特供版 · 记录管理##Cleanup", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
        Invoke(window, method); Invoke(window, "DrawModals");
        ImGui.End(); window.DrawDialogs(); ImGui.Render();
        if (ImGui.GetDrawData().TotalVtxCount == 0) throw new InvalidOperationException("blank cleanup view");
    }

    private static void CleanupClick(MainWindow window, string method, float x, float y)
    {
        var io = ImGui.GetIO(); io.AddMousePosEvent(x, y); CleanupFrame(window, method);
        io.AddMouseButtonEvent(0, true); CleanupFrame(window, method);
        io.AddMouseButtonEvent(0, false); CleanupFrame(window, method);
    }
}
