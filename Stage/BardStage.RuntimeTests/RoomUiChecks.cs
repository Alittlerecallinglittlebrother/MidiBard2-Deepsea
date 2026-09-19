using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;

internal static unsafe partial class RuntimeUi
{
    private static void RenderRooms(string midiPath, string output)
    {
        using var f = new RoomRuntimeChecks.Fixture(Path.GetTempPath(), midiPath);
        f.Captain.Change(s => { s.Songs[0].Title = "开场序曲"; s.Songs[1].Title = "月下华尔兹"; });
        f.Connect();
        f.Remote(RoomAction.Add, song: f.First); f.Remote(RoomAction.Add, song: f.Second);
        f.Remote(RoomAction.PlayNext, entry: f.Show.Entries[1].Id);
        using var captain = new MainWindow(f.Captain);
        Frame(captain, 1100, 740); Frame(captain, 1100, 740);
        Click(captain, 176, 95);
        Frame(captain, 1100, 740); Frame(captain, 1100, 740);
        Set(captain, "roomHost", "example.natfrp.example"); Set(captain, "roomPublicPort", 12345);
        Frame(captain, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "room-captain.png"));
        ImGui.GetIO().FontGlobalScale = 1.4f;
        Frame(captain, 760, 540); Frame(captain, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "room-captain-small-scaled.png"));
        ImGui.GetIO().FontGlobalScale = 1;
        using var presenter = new MainWindow(f.Presenter);
        Frame(presenter, 1100, 740); Frame(presenter, 1100, 740);
        Click(presenter, 35, 95);
        Frame(presenter, 1100, 740); Frame(presenter, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "room-presenter-queue.png"));
        ClickItem(presenter, "queueAdd"); Frame(presenter, 1100, 740); Frame(presenter, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "room-presenter-picker.png"));
        var bounds = PickerBounds();
        var count = f.Show.Entries.Count;
        Click(presenter, bounds.X + 25, bounds.Y + 12);
        f.Until(() => !f.Presenter.Room!.OperationPending);
        if (f.Show.Entries.Count != count + 1) throw new InvalidOperationException("native presenter add did not reach captain");
        Console.WriteLine("PASS: native presenter song picker sends a real TLS queue edit to captain");
        Frame(presenter, 1100, 740); Frame(presenter, 1100, 740);
        var sourceId = f.Show.Entries[0].Id; var targetId = f.Show.Entries.Last().Id;
        var source = items["queueRow:" + sourceId]; var target = items["queueRow:" + targetId];
        Drag(presenter, (source.Min + source.Max) / 2, new System.Numerics.Vector2(target.Min.X + 20, target.Max.Y - 2));
        f.Until(() => !f.Presenter.Room!.OperationPending);
        if (f.Show.Entries.Last().Id != sourceId || f.Show.LockedNextEntryId != null)
            throw new InvalidOperationException("presenter drag failed to update captain order");
        Console.WriteLine("PASS: presenter native drag reorders captain queue over TLS and clears old next-song override");
        Frame(presenter, 760, 540); Frame(presenter, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "room-presenter-small.png"));
        ImGui.GetIO().FontGlobalScale = 1.4f;
        Frame(presenter, 760, 540); Frame(presenter, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "room-presenter-small-scaled.png"));
        ImGui.GetIO().FontGlobalScale = 1;
        Frame(presenter, 1100, 740); Frame(presenter, 1100, 740);
        Click(presenter, 89, 95); Frame(presenter, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "room-shared-library.png"));
        RenderViewerViews(f, output);
        f.Captain.Room!.Leave(); f.Until(() => !f.Presenter.Room!.Connected);
        Frame(presenter, 1100, 740); Frame(presenter, 1100, 740);
        Click(presenter, 35, 95); Frame(presenter, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "room-disconnected.png"));
        Console.WriteLine("PASS: native captain/presenter room views render at desktop, small window and 140% font scale");
    }

    private static void RenderViewerViews(RoomRuntimeChecks.Fixture f, string output)
    {
        using var viewer = new StageController(Path.Combine(Path.GetTempPath(), "viewer-ui-" + Guid.NewGuid()));
        viewer.Room = new(viewer);
        using var window = new MainWindow(viewer);
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 176, 73);
        Set(window, "roomChoice", 2);
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "viewer-join.png"));
        Set(window, "roomInvitation", f.Captain.Room!.Invite("127.0.0.1", f.Captain.Room.LocalPort, RoomRole.Viewer));
        Frame(window, 1100, 740);
        Click(window, 50, 291);
        if (!viewer.Room.IsViewer) throw new InvalidOperationException("native viewer join button failed");
        f.Until(() => viewer.Room.Connected);
        viewer.Room.Tick();
        if (Get<string>(window, "roomInvitation").Length != 0) throw new InvalidOperationException("invitation remained visible after joining");
        Console.WriteLine("PASS: native viewer join button uses read-only invitation and clears it from the form");
        f.Port.DurationTicks = 3840; f.Remote(RoomAction.Start);
        f.Until(() => viewer.QueueViewShow?.Entries.Any(e => e.Status == EntryStatus.InProgress) == true && viewer.QueueViewPlayback.Playing);
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 35, 95); Frame(window, 1100, 740); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "viewer-program.png"));
        Frame(window, 760, 540); Frame(window, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "viewer-program-small.png"));
        ImGui.GetIO().FontGlobalScale = 1.4f;
        Frame(window, 760, 540); Frame(window, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "viewer-program-small-scaled.png"));
        ImGui.GetIO().FontGlobalScale = 1;
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 108, 95); Frame(window, 1100, 740); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "viewer-room.png"));
        var completion = viewer.Room.TransportCompletion;
        Click(window, 35, 145);
        if (viewer.Room.IsRemote) throw new InvalidOperationException("native viewer leave button failed");
        completion!.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Console.WriteLine("PASS: native viewer program/current/next views render at desktop, small window and 140%; leave button works");
    }
}
