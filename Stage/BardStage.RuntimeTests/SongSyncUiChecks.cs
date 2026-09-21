using BardStage;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunSongSyncUi(string output)
    {
        Directory.CreateDirectory(output);
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var icons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/dalamudAssets/dev/UIRes/FontAwesomeFreeSolid.otf");
            if (File.Exists(icons))
            {
                ushort* ranges = stackalloc ushort[] { 0xE000, 0xF8FF, 0 };
                var font = io.Fonts.AddFontFromFileTTF(icons, 17, default, ranges);
                UiKit.IconFont = () => font;
            }
            if (!io.Fonts.Build()) throw new InvalidOperationException("font build failed");
            ImGui.StyleColorsDark();
            using var controller = new StageController(Path.Combine(output, "ui-data"));
            controller.Room = new(controller);
            var enabled = false;
            controller.Room.SongSyncEnabled = () => enabled;
            controller.Room.SetSongSyncEnabled = value => enabled = value;
            using var window = new MainWindow(controller);
            UiKit.ItemBounds = (id, min, max) => items[id] = (min, max);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            ClickItem(window, "roomTab");
            Frame(window, 1100, 740);
            if (enabled || !items.ContainsKey("songSyncToggle")) throw new InvalidOperationException("song-sync default/toggle missing");
            ClickItem(window, "songSyncToggle");
            if (!enabled) throw new InvalidOperationException("native toggle failed");
            if (!controller.Room.Create(0)) throw new InvalidOperationException(controller.StatusMessage);
            io.AddMousePosEvent(-100, -100);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "song-sync-room.png"));
            io.FontGlobalScale = 1.4f;
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "song-sync-room-small-140.png"));
            io.AddMousePosEvent(450, 350); io.AddMouseWheelEvent(0, -15);
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "song-sync-room-small-scrolled.png"));
            ClickItem(window, "roomLeave", 760, 540);
            if (controller.Room.IsCaptain) throw new InvalidOperationException("compact room leave button is unreachable");
            Console.WriteLine("PASS: native room toggle defaults off, responds to clicks and renders at normal/compact sizes");
            RenderEnsembleRegression(output);
        }
        finally { UiKit.ItemBounds = null; UiKit.IconFont = null; ImGui.DestroyContext(context); }
    }
}
