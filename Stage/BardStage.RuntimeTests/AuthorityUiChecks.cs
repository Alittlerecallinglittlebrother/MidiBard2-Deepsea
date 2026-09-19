using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    public static void RunHandoffChecks(StageController controller, Action promote, Action paused, Action demote, string output)
    {
        using var native = new NativeLibraryContext(Path.Combine(Path.GetDirectoryName(typeof(RuntimeUi).Assembly.Location)!, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            UiKit.ItemBounds = (id, min, max) => items[id] = (min, max);
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var icons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/dalamudAssets/dev/UIRes/FontAwesomeFreeSolid.otf");
            if (File.Exists(icons))
            {
                ushort* ranges = stackalloc ushort[] { 0xE000, 0xF8FF, 0 };
                var iconFont = io.Fonts.AddFontFromFileTTF(icons, 17, default, ranges);
                UiKit.IconFont = () => iconFont;
                if (!io.Fonts.Build()) throw new InvalidOperationException("Handoff icon atlas failed");
            }
            if (!io.Fonts.Build()) throw new InvalidOperationException("Handoff font atlas failed");
            ImGui.StyleColorsDark();
            using var window = new MainWindow(controller);
            items.Clear(); Frame(window, 1100, 740); Frame(window, 1100, 740);
            if (items.ContainsKey("queuePlay")) throw new InvalidOperationException("Viewer received playback controls before promotion");
            promote(); items.Clear(); Frame(window, 1100, 740); Frame(window, 1100, 740);
            if (!items.ContainsKey("queuePause")) throw new InvalidOperationException("Promoted viewer did not receive playback controls");
            ClickItem(window, "queuePause"); paused();
            io.AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "handoff-promoted-leader.png"));
            io.FontGlobalScale = 1.4f; Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "handoff-promoted-leader-small-140.png"));
            Console.WriteLine("PASS: native promoted viewer gains controller UI and pauses current leader playback over TLS");
            demote(); items.Clear(); Frame(window, 760, 540); Frame(window, 760, 540);
            if (items.ContainsKey("queuePlay") || items.ContainsKey("queuePause")) throw new InvalidOperationException("Demoted viewer retained transport buttons");
            SoftwareRenderer.Save(Path.Combine(output, "handoff-demoted-viewer-small-140.png"));
            Console.WriteLine("PASS: native demotion removes playback controls and renders read-only program at compact 140 percent scale");
        }
        finally { UiKit.ItemBounds = null; UiKit.IconFont = null; ImGui.DestroyContext(context); }
    }

    public static void RunAuthorityChecks(string midiPath, string output)
    {
        Directory.CreateDirectory(output);
        using var controller = new StageController(Path.Combine(Path.GetTempPath(), "authority-ui-" + Guid.NewGuid())) { SyncAvailable = true };
        controller.Import([midiPath]);
        while (controller.IsBusy) { controller.Poll(); Thread.Sleep(5); }
        controller.EnsureAutomaticQueue();
        controller.Change(s =>
        {
            s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble;
            SetlistOperations.AddSong(s.Setlists[0], s.Songs[0]);
        });
        var leader = false;
        controller.LocalEnsembleControlIssue = () => leader ? null : "当前小队队长已变更，合奏队列只读";
        using var port = new AutoQueueRuntimeChecks.EnginePort(controller);
        using var player = new AutoQueuePlayer(controller, port);
        controller.QueuePlayer = player;
        using var native = new NativeLibraryContext(Path.Combine(Path.GetDirectoryName(typeof(RuntimeUi).Assembly.Location)!, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            UiKit.ItemBounds = (id, min, max) => items[id] = (min, max);
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var iconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/dalamudAssets/dev/UIRes/FontAwesomeFreeSolid.otf");
            if (File.Exists(iconPath))
            {
                ushort* range = (ushort*)System.Runtime.InteropServices.NativeMemory.Alloc(3, sizeof(ushort));
                range[0] = 0xE000; range[1] = 0xF8FF; range[2] = 0;
                var iconFont = io.Fonts.AddFontFromFileTTF(iconPath, 17, default, range);
                UiKit.IconFont = () => iconFont;
                if (!io.Fonts.Build()) throw new InvalidOperationException("Authority UI icon atlas failed");
                System.Runtime.InteropServices.NativeMemory.Free(range);
            }
            if (!io.Fonts.Build()) throw new InvalidOperationException("Authority UI font atlas failed");
            ImGui.StyleColorsDark();
            using var window = new MainWindow(controller);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            ClickItem(window, "queuePlay"); controller.Poll(); player.Tick();
            if (player.IsRunning || port.StartCalls != 0 || controller.CanEditQueue)
                throw new InvalidOperationException("Read-only ensemble play button remained active");
            io.AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "authority-follower.png"));
            Console.WriteLine("PASS: native ensemble controls are disabled after local leadership loss");

            ClickItem(window, "authoritySolo");
            if (controller.State.RequestSettings.PlaybackMode != QueuePlaybackMode.Solo || !controller.CanEditQueue)
                throw new InvalidOperationException("Read-only follower could not select independent solo mode");
            Console.WriteLine("PASS: native follower can switch to independent solo mode without recreating the window");

            controller.Change(s => s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble);
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "authority-follower-small.png"));
            leader = true; Frame(window, 760, 540); Frame(window, 760, 540);
            ClickItem(window, "queuePlay", 760, 540);
            if (!player.IsRunning || !controller.CanEditQueue)
                throw new InvalidOperationException("New leader controls did not become active immediately");
            io.AddMousePosEvent(-100, -100); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "authority-new-leader-small.png"));
            Console.WriteLine("PASS: native new leader can start the ensemble queue immediately in the existing window");

            leader = false; player.Tick();
            Set(window, "selectSetlistTab", true);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            ClickItem(window, "lock");
            if (controller.CurrentSetlist!.LockedNextEntryId != null)
                throw new InvalidOperationException("Advanced setlist editor bypassed lost leader authority");
            Console.WriteLine("PASS: native advanced setlist editor cannot bypass former leader's read-only queue");
        }
        finally
        {
            UiKit.ItemBounds = null;
            UiKit.IconFont = null;
            ImGui.DestroyContext(context);
        }
    }
}
