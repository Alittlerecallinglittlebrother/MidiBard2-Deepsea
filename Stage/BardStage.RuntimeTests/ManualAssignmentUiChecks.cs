using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunManualAssignmentUi(string output)
    {
        Directory.CreateDirectory(output);
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            if (!io.Fonts.Build()) throw new InvalidOperationException("font build failed");
            ImGui.StyleColorsDark();
            var window = new MidiBard.PluginUI();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MidiBard.PluginUI).GetField("ShowEnsembleWindow", flags)!.SetValue(window, true);
            var draw = typeof(MidiBard.PluginUI).GetMethod("DrawEnsembleWindow", flags)!;
            MidiBard.MidiBard.config.AutoAssignEnsembleTracks = false;
            MidiBard.MidiBard.config.playOnMultipleDevices = true;
            MidiBard.MidiBard.config.EnableCrossComputerSongSync = true;
            MidiBard.MidiBard.CurrentPlayback = new()
            {
                MidiFileConfig = new() { Tracks = Enumerable.Range(0, 8).Select(i => new MidiBard.TrackFixture
                    { Index = i, Name = $"声部 {i + 1}", Enabled = true, Instrument = 2, AssignedCids = [1] }).ToList() }
            };
            Vector2 min = default, max = default;
            MidiBard.PluginUI.EnsembleItemBounds = (_, a, b) => { min = a; max = b; };
            foreach (var (width, height, scale) in new[] { (1100, 740, 1f), (760, 540, 1.4f) })
            {
                MidiBard.PluginUI.LogPath = Path.Combine(output, $"manual-{width}.log");
                File.WriteAllText(MidiBard.PluginUI.LogPath, "");
                io.FontGlobalScale = scale;
                void Frame()
                {
                    io.DisplaySize = new(width, height);
                    ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(width, height));
                    draw.Invoke(window, null); ImGui.LogFinish(); ImGui.Render();
                }
                Frame(); Frame();
                if (min.Y < 0 || max.Y >= height || max.X > width || max.X <= min.X) throw new InvalidOperationException("manual button outside viewport");
                var before = MidiBard.PartyChatCommand.ManualClicks;
                var center = (min + max) / 2;
                io.AddMousePosEvent(center.X, center.Y); Frame();
                io.AddMouseButtonEvent(0, true); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
                if (MidiBard.PartyChatCommand.ManualClicks != before + 1) throw new InvalidOperationException("manual distribute click failed");
                SoftwareRenderer.Save(Path.Combine(output, $"manual-assignment-{width}.png"));
                var text = File.ReadAllText(MidiBard.PluginUI.LogPath);
                if (!text.Contains("下发歌曲与手动分配") || text.Contains("Track assign is disabled") || text.Contains("Exception"))
                    throw new InvalidOperationException("manual mode blocked or unrendered");
                Console.WriteLine($"PASS: production manual assignment UI renders and dispatches native click at {width}x{height} / {scale:P0}");
                MidiBard.Managers.PlaylistManager.IsLoading = true;
                Frame();
                io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
                if (MidiBard.PartyChatCommand.ManualClicks != before + 1) throw new InvalidOperationException("busy manual send was not disabled");
                MidiBard.Managers.PlaylistManager.IsLoading = false;
            }
            Console.WriteLine("PASS: manual submit stays disabled during loading");
        }
        finally { MidiBard.PluginUI.EnsembleItemBounds = null; ImGui.DestroyContext(context); }
    }
}
