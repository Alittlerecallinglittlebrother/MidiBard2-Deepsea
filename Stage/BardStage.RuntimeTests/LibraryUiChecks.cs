using System.Numerics;
using BardStage;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunLibraryChecks(string midiPath, string output)
    {
        Directory.CreateDirectory(output);
        using var controller = new StageController(Path.Combine(Path.GetTempPath(), "library-ui-" + Guid.NewGuid()));
        controller.Import([midiPath]);
        while (controller.IsBusy) { controller.Poll(); Thread.Sleep(5); }
        controller.Change(s => s.Songs[0].Title = "曲库删除检查 · 中文萨克斯八人合奏");
        controller.AddToQueue(controller.State.Songs[0].Id);
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var icons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/dalamudAssets/dev/UIRes/FontAwesomeFreeSolid.otf");
            ushort* range = stackalloc ushort[] { 0xE000, 0xF8FF, 0 };
            var iconFont = io.Fonts.AddFontFromFileTTF(icons, 17, default, range);
            UiKit.IconFont = () => iconFont;
            if (!io.Fonts.Build()) throw new InvalidOperationException("library font atlas failed");
            ImGui.StyleColorsDark();
            using var window = new MainWindow(controller);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            Click(window, 93, 73); Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "library-desktop.png"));
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "library-small.png"));
            io.FontGlobalScale = 1.4f;
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "library-small-scaled.png"));
            io.FontGlobalScale = 1;
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            Invoke(window, "ConfirmDeleteSong", controller.State.Songs[0]);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "library-delete-confirm.png"));
            if (!ImGui.IsPopupOpen("确认操作##BardStage")) throw new InvalidOperationException("library deletion confirmation is missing");
            Console.WriteLine("PASS: library single-delete confirmation renders with original-file and history preservation notice");
            ImGui.GetIO().AddKeyEvent(ImGuiKey.Escape, true); Frame(window, 1100, 740);
            ImGui.GetIO().AddKeyEvent(ImGuiKey.Escape, false); Frame(window, 1100, 740);
            Console.WriteLine("PASS: shared library renders at desktop, small window and 140 percent font size");
        }
        finally { UiKit.IconFont = null; ImGui.DestroyContext(context); }
    }
}
