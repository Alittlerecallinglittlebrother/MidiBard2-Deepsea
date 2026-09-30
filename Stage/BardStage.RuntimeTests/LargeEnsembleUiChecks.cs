using System.Reflection;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunLargeEnsembleUi(string output)
    {
        Directory.CreateDirectory(output);
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory,"cimgui.dll"));
        ImGui.InitApi(native); var ctx = ImGui.CreateContext();
        try
        {
            var io=ImGui.GetIO(); io.IniFilename=null; io.DeltaTime=1f/60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc",17,default,io.Fonts.GetGlyphRangesChineseFull());
            if(!io.Fonts.Build()) throw new InvalidOperationException("font build failed");
            ImGui.StyleColorsDark();
            using var fixture=new LargeEnsembleRuntimeChecks.Fixture(Path.Combine(output,"fixture"));
            var host=fixture.Host; host.Tick();
            using var window=new MainWindow(host.Controller);
            UiKit.ItemBounds=(id,min,max)=>items[id]=(min,max);
            Frame(window,1100,740); Frame(window,1100,740); ClickItem(window,"largeTab");
            Frame(window,1100,740); ClickItem(window,"largeEnable");
            LargeEnsembleRuntimeChecks.Check(host.Large.Enabled,"native checkbox enables large mode");
            host.Room.Create(0); host.Tick();
            LargeEnsembleRuntimeChecks.Check(host.Room.Server!.ViewerCapacity==7,"UI opt-in creates seven follower slots");
            Frame(window,1100,740); ClickItem(window,"largeStep1");
            Frame(window,1100,740); ClickItem(window,"largeCompensation");
            LargeEnsembleRuntimeChecks.Check(!host.Large.UseInstrumentCompensation,"captain can turn compensation off for precompensated MIDI");
            Frame(window,1100,740); ClickItem(window,"largeCompensation");
            LargeEnsembleRuntimeChecks.Check(host.Large.UseInstrumentCompensation,"captain can restore shared instrument compensation");
            var song=new SongEntry { Id=Guid.NewGuid(),Title="八人合奏测试曲",FilePath=fixture.Midi };
            host.Controller.State.Songs.Add(song);
            typeof(MainWindow).GetField("largeSong",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,song.Id);
            Frame(window,1100,740); ClickItem(window,"largeInspect"); host.Tick();
            Frame(window,1100,740); Frame(window,1100,740);
            LargeEnsembleRuntimeChecks.Check(host.Large.Draft?.Tracks.Length==8,"native inspect button exposes all eight assignment rows");
            SoftwareRenderer.Save(Path.Combine(output,"large-1100-top.png"));
            ClickItem(window,"largeEditor");
            Frame(window,1100,740); Frame(window,1100,740);
            SoftwareRenderer.Save(Path.Combine(output,"large-1100-roster.png"));
            ClickItem(window,"largeStep2"); Frame(window,1100,740);
            ClickItem(window,"largeDistribute");
            LargeEnsembleRuntimeChecks.Check(host.Large.Phase==BardStage.Core.Rooms.LargePhase.Stopped,"UI refuses dispatch while seven performers are missing");
            // UI-only sample reports: verify the new diagnostics with all eight
            // rows populated, not just a roster of disconnected placeholders.
            var sample = host.Large.Reports.First();
            var diagnosticReports = host.Large.Context.Members.Select(m => sample with
            {
                Cid=m.Cid, Playing=true, ClockReady=true, Rtt=.022, Jitter=.003, Drift=.001,
                StartLateMs=1.9, Output=new PlaybackTimingDiagnostics(1234,12.5,14.2,0),
            }).ToArray();
            typeof(BardStage.RoomLargeEnsemble).GetField("reports",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host.Large,diagnosticReports);
            io.FontGlobalScale=1.4f;
            Frame(window,760,540); Frame(window,760,540);
            io.AddMousePosEvent(400,300); io.AddMouseWheelEvent(0,80);
            Frame(window,760,540); Frame(window,760,540);
            SoftwareRenderer.Save(Path.Combine(output,"large-760-top.png"));
            var stop=items["largeStop"]; var panel=items["largePanel"];
            LargeEnsembleRuntimeChecks.Check(stop.Item1.Y>=panel.Item1.Y && stop.Item2.Y<panel.Item2.Y,"compact stop remains reachable at the top");
            for(var i=0;i<3;i++) { io.AddMousePosEvent(420,320); io.AddMouseWheelEvent(0,-30); Frame(window,760,540); Frame(window,760,540); }
            SoftwareRenderer.Save(Path.Combine(output,"large-760-roster-bottom.png"));
            var end=items["largeRosterEnd"]; panel=items["largePanel"];
            Console.WriteLine($"UI geometry: last={end}, panel={panel}");
            LargeEnsembleRuntimeChecks.Check(end.Item2.Y<=panel.Item2.Y && end.Item1.Y>panel.Item1.Y,"eighth performer status is reachable in compact scroll panel");
            ShowItem(window,"largeDiagnostics",760,540,"largePanel");
            ClickItem(window,"largeDiagnostics",760,540);
            ShowItem(window,"largeTiming8",760,540,"largePanel");
            SoftwareRenderer.Save(Path.Combine(output,"large-760-diagnostics-bottom.png"));
            panel=items["largePanel"];
            var timing=items["largeTiming8"];
            LargeEnsembleRuntimeChecks.Check(timing.Item1.X>=panel.Item1.X && timing.Item2.X<=panel.Item2.X
                && timing.Item1.Y>=panel.Item1.Y && timing.Item2.Y<=panel.Item2.Y,
                "all eight timing diagnostics wrap within the compact panel at 140 percent scale");
            io.FontGlobalScale=1; Frame(window,1100,740);
            io.AddMousePosEvent(500,320); io.AddMouseWheelEvent(0,100); Frame(window,1100,740); Frame(window,1100,740);
            ClickItem(window,"largeStep1"); Frame(window,1100,740);
            io.AddMousePosEvent(500,330); io.AddMouseWheelEvent(0,-37); Frame(window,1100,740); Frame(window,1100,740);
            SoftwareRenderer.Save(Path.Combine(output,"large-1100-assignment-scroll.png"));
        }
        finally { UiKit.ItemBounds=null; ImGui.DestroyContext(ctx); }
    }
}
