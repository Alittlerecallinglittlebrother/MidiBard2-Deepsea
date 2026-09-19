using System.Numerics;
using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    private static readonly Dictionary<string, (Vector2 Min, Vector2 Max)> items = new();
    private static void ClickItem(MainWindow window, string id, int width = 1100, int height = 740)
    {
        var bounds = items[id];
        var point = (bounds.Min + bounds.Max) / 2;
        Click(window, point.X, point.Y, width, height);
    }

    public static void Run(StageController source, string output)
    {
        using var controller = new StageController(Path.Combine(Path.GetTempPath(), "queue-ui-" + Guid.NewGuid())) { SyncAvailable = true };
        controller.Change(s =>
        {
            s.Songs = StageController.Clone(source.State.Songs);
            s.Setlists[0].Name = "观众点歌";
        });
        controller.EnsureAutomaticQueue(); controller.SetReception(true);
        controller.Change(s =>
        {
            var show = s.Setlists[0];
            RequestOperations.Submit(s, show.Id, "观众甲", "海幻沙", "开场序曲", RequestChannel.Say, DateTimeOffset.UtcNow);
            RequestOperations.Submit(s, show.Id, "观众乙", "红玉海", "月下华尔兹", RequestChannel.Tell, DateTimeOffset.UtcNow);
            RequestOperations.Submit(s, show.Id, "观众丙", "", "归途", RequestChannel.Say, DateTimeOffset.UtcNow);
            RequestOperations.Submit(s, show.Id, "观众丁", "", "未收录歌曲", RequestChannel.Say, DateTimeOffset.UtcNow);
        });
        using var port = new AutoQueueRuntimeChecks.EnginePort(controller);
        using var player = new AutoQueuePlayer(controller, port);
        controller.QueuePlayer = player;
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            UiKit.ItemBounds = (id, min, max) => items[id] = (min, max);
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var iconsPath = Path.Combine(output, "..", "fa-solid-900.ttf");
            if (!File.Exists(iconsPath)) iconsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/dalamudAssets/dev/UIRes/FontAwesomeFreeSolid.otf");
            if (File.Exists(iconsPath))
            {
                ushort* range = (ushort*)System.Runtime.InteropServices.NativeMemory.Alloc(3, sizeof(ushort));
                range[0] = 0xE000; range[1] = 0xF8FF; range[2] = 0;
                var iconFont = io.Fonts.AddFontFromFileTTF(iconsPath, 17, default, range);
                UiKit.IconFont = () => iconFont;
                if (!io.Fonts.Build()) throw new InvalidOperationException("font atlas failed");
                System.Runtime.InteropServices.NativeMemory.Free(range);
            }
            else if (!io.Fonts.Build()) throw new InvalidOperationException("font atlas failed");
            ImGui.StyleColorsDark();
            ImGui.GetStyle().WindowRounding = 0; ImGui.GetStyle().FrameRounding = 3;
            using var window = new MainWindow(controller);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            ClickItem(window, "queueEnsemble");
            if (controller.State.RequestSettings.PlaybackMode != QueuePlaybackMode.Ensemble) throw new InvalidOperationException("ensemble mode selection failed");
            ClickItem(window, "queueSolo");
            if (controller.State.RequestSettings.PlaybackMode != QueuePlaybackMode.Solo) throw new InvalidOperationException("solo mode selection failed");
            Console.WriteLine("PASS: native mode controls switch and persist solo or ensemble explicitly");
            SoftwareRenderer.Save(Path.Combine(output, "queue.png"));
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "queue-small.png"));
            io.FontGlobalScale = 1.4f;
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "queue-small-scaled.png"));
            io.FontGlobalScale = 1;
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            var originalOrder = controller.QueueShow!.Entries.Select(e => e.Id).ToArray();
            var dragSource = items["queueRow:" + originalOrder[0]];
            var dragTarget = items["queueRow:" + originalOrder[2]];
            Drag(window, (dragSource.Min + dragSource.Max) / 2, new Vector2(dragTarget.Min.X + 20, dragTarget.Max.Y - 2));
            if (controller.QueueShow.Entries.Last().Id != originalOrder[0]) throw new InvalidOperationException("queue drag to end failed");
            Frame(window, 1100, 740);
            dragSource = items["queueRow:" + originalOrder[0]]; dragTarget = items["queueRow:" + originalOrder[1]];
            Drag(window, (dragSource.Min + dragSource.Max) / 2, new Vector2(dragTarget.Min.X + 20, dragTarget.Min.Y + 2));
            if (!controller.QueueShow.Entries.Select(e => e.Id).SequenceEqual(originalOrder)) throw new InvalidOperationException("queue drag to front failed");
            Console.WriteLine("PASS: native queue drag moves a song before and after other songs by stable ID");
            port.DurationTicks = 9600;
            ClickItem(window, "queuePlay");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!port.IsPlaying || player.ActiveEntryId == null)
            {
                controller.Poll(); player.Tick(); Thread.Sleep(5);
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("queue play timeout: " + player.Status);
            }
            controller.Poll(); player.Tick(); Frame(window, 1100, 740);
            ClickItem(window, "queuePause"); controller.Poll(); player.Tick();
            if (!player.IsPaused || port.IsPlaying || !player.Enabled) throw new InvalidOperationException("queue pause button failed");
            ClickItem(window, "queuePlay"); controller.Poll(); player.Tick();
            if (!port.IsPlaying || player.IsPaused) throw new InvalidOperationException("queue resume button failed");
            ClickItem(window, "queueContinuous");
            if (!port.IsPlaying || player.Enabled || !controller.State.RequestSettings.IsOpen) throw new InvalidOperationException("continuous checkbox affected music or reception");
            Frame(window, 1100, 740); ClickItem(window, "queueStop"); controller.Poll(); player.Tick();
            if (port.IsPlaying || player.IsRunning || player.Enabled) throw new InvalidOperationException("queue stop button failed");
            Console.WriteLine("PASS: native play, pause, resume, stop and continuous controls are independent of reception");
            ClickItem(window, "queueAdd"); Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "queue-picker.png"));
            Set(window, "queueSearch", "归途"); Frame(window, 1100, 740); Frame(window, 1100, 740);
            var bounds = PickerBounds();
            var count = controller.QueueShow!.Entries.Count;
            Click(window, bounds.X + 25, bounds.Y + 10);
            if (controller.QueueShow.Entries.Count != count + 1 || controller.QueueShow.Entries.Last().Title != "归途") throw new InvalidOperationException("search picker did not add filtered song");
            Console.WriteLine("PASS: native mouse chooses a searched song directly into the queue");
            foreach (var scale in new[] { 1f, 1.4f })
            {
                io.FontGlobalScale = scale;
                Frame(window, 760, 540); Frame(window, 760, 540);
                ClickItem(window, "queueAdd", 760, 540);
                Frame(window, 760, 540); Frame(window, 760, 540);
                SoftwareRenderer.Save(Path.Combine(output, scale == 1 ? "queue-picker-small.png" : "queue-picker-small-scaled.png"));
                bounds = PickerBounds();
                if (bounds.W - bounds.Y < 200 || bounds.Z - bounds.X < 200) throw new InvalidOperationException("song picker collapsed");
                count = controller.QueueShow.Entries.Count;
                Click(window, bounds.X + 25, bounds.Y + 12, 760, 540);
                if (controller.QueueShow.Entries.Count != count + 1) throw new InvalidOperationException("small song picker row not clickable");
            }
            Console.WriteLine("PASS: small song picker has visible clickable rows at 100% and 140% font scale");
            io.FontGlobalScale = 1;
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            controller.Change(s =>
            {
                var alternate = StageController.Clone(s.Songs[1]); alternate.Id = Guid.NewGuid(); alternate.Sha256 = new string('A', 64); alternate.Arranger = "六人版"; s.Songs.Add(alternate);
                RequestOperations.Submit(s, s.Setlists[0].Id, "观众戊", "", alternate.Title, RequestChannel.Say, DateTimeOffset.UtcNow);
            });
            // Keep the pending rows near the top while exercising the native resolution popup.
            controller.Change(s => { foreach (var e in s.Setlists[0].Entries) SetlistOperations.Skip(s.Setlists[0], e.Id, DateTimeOffset.UtcNow); });
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "queue-unresolved.png"));
            ClickItem(window, "resolveRequest:" + controller.State.Requests.Last().Id); Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "queue-versions.png"));
            bounds = PickerBounds();
            Click(window, bounds.X + 25, bounds.Y + 10);
            if (controller.State.Requests.Last().Status != RequestStatus.Arranged || controller.QueueShow.Entries.Last().Title != "月下华尔兹")
                throw new InvalidOperationException("version selection did not arrange the pending audience request");
            Console.WriteLine("PASS: native version selection directly arranges an ambiguous audience request");
            Set(window, "selectSetlistTab", true);
            Frame(window, 1100, 740); Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "advanced-setlist.png"));
            Frame(window, 760, 540); Frame(window, 760, 540);
            Click(window, 23, 221, 760, 540); Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "advanced-picker-small.png"));
            bounds = PickerBounds(); count = controller.CurrentSetlist!.Entries.Count;
            if (bounds.W - bounds.Y < 200) throw new InvalidOperationException("legacy search popup still collapsed");
            Click(window, bounds.X + 25, bounds.Y + 10, 760, 540);
            if (controller.CurrentSetlist.Entries.Count != count + 1) throw new InvalidOperationException("legacy search popup song not selectable");
            Console.WriteLine("PASS: original setlist search popup regression fixed and selected song persists");
            Click(window, 89, 73, 760, 540); Invoke(window, "SelectSong", controller.State.Songs[0]);
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "library-small.png"));
            Console.WriteLine("PASS: default queue rendered at 1100x740, 760x540 and 140% font scale");
            RenderRooms(source.State.Songs[0].FilePath, output);
            RenderCleanup(source, output);
            RenderEnsembleRegression(output);
        }
        finally { UiKit.IconFont = null; UiKit.ItemBounds = null; items.Clear(); ImGui.DestroyContext(context); }
    }

    private static Vector4 PickerBounds()
    {
        var draw = ImGui.GetDrawData();
        if (draw.CmdListsCount < 3) throw new InvalidOperationException("picker did not open");
        ImDrawListPtr list = draw.CmdLists[draw.CmdListsCount - 1];
        return list.CmdBuffer[list.CmdBuffer.Size - 1].ClipRect;
    }
}
