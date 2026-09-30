using BardStage;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunLocalEnsembleUi(string output)
    {
        Directory.CreateDirectory(output);
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory,"cimgui.dll"));
        ImGui.InitApi(native); var ctx = ImGui.CreateContext();
        var controllers = new List<StageController>();
        var drivers = new List<LocalEnsembleRuntimeChecks.Driver>();
        var nodes = new List<RoomLargeEnsemble>();
        try
        {
            var io=ImGui.GetIO(); io.IniFilename=null; io.DeltaTime=1f/60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc",17,default,io.Fonts.GetGlyphRangesChineseFull());
            if (!io.Fonts.Build()) throw new InvalidOperationException("font build failed");
            ImGui.StyleColorsDark();
            var bus="Local\\MidiBard.UiTest."+Guid.NewGuid().ToString("N");
            for (ulong cid=1;cid<=8;cid++)
            {
                var id=cid; var controller=new StageController(Path.Combine(output,"fixture",id.ToString()));
                controller.Room=new(controller); controllers.Add(controller);
                var driver=new LocalEnsembleRuntimeChecks.Driver(()=>new(id,LocalEnsembleRuntimeChecks.Membership(8),100,10));
                drivers.Add(driver);
                var large=new RoomLargeEnsemble(controller.Room,driver)
                    { LocalFactory=()=>new(Path.Combine(output,"cache"),bus) };
                controller.Room.LargeEnsemble=large; nodes.Add(large);
                if (id!=1) { large.SetLocalMode(true); large.SetEnabled(true); }
                large.Tick();
            }
            void Pump(int milliseconds=80)
            { var until=Environment.TickCount64+milliseconds; do { foreach(var n in nodes) n.Tick(); Thread.Sleep(5); } while(Environment.TickCount64<until); }
            var host=nodes[0]; using var window=new MainWindow(controllers[0]);
            UiKit.ItemBounds=(id,min,max)=>items[id]=(min,max);
            Frame(window,1100,740); Frame(window,1100,740); ClickItem(window,"largeTab");
            Frame(window,1100,740); ClickItem(window,"largeLocalMode");
            Frame(window,1100,740); ClickItem(window,"largeEnable"); Pump(200);
            LocalEnsembleRuntimeChecks.Check(host.LocalMode && host.Enabled && controllers.All(c=>!c.Room!.IsCaptain && !c.Room.IsRemote),
                "native UI selects local mode and enables reception without a network room");
            Frame(window,1100,740); ClickItem(window,"largeLocalHost"); Pump(200);
            LocalEnsembleRuntimeChecks.Check(host.IsCaptain,"native UI makes the selected character local conductor");
            void Show(string id,int width=1100,int height=740)
            {
                for(var i=0;i<60;i++)
                {
                    Pump(5); Frame(window,width,height); Frame(window,width,height);
                    var r=items[id]; var panel=items["largePanel"];
                    if(r.Item1.Y>=panel.Item1.Y && r.Item2.Y<=panel.Item2.Y) return;
                    io.AddMousePosEvent(panel.Item1.X+200,(panel.Item1.Y+panel.Item2.Y)/2);
                    io.AddMouseWheelEvent(0,r.Item1.Y<panel.Item1.Y?3:-3);
                }
                SoftwareRenderer.Save(Path.Combine(output,"unreachable.png"));
                throw new InvalidOperationException($"Unreachable UI item: {id}: {items[id]}, panel={items["largePanel"]}");
            }
            for(var cid=2;cid<=8;cid++) { Show("largeLocalMember"+cid); ClickItem(window,"largeLocalMember"+cid); Pump(); }
            Pump(200);
            LocalEnsembleRuntimeChecks.Check(host.Participants.Length==8 && host.Reports.Count==8,"all eight participants can be selected through native checkboxes");
            Show("largeLocalMembersEnd"); SoftwareRenderer.Save(Path.Combine(output,"local-1100-members.png"));
            Show("largeLocalMembers"); ClickItem(window,"largeLocalMembers");
            Show("largeStop"); SoftwareRenderer.Save(Path.Combine(output,"local-1100-top.png"));
            io.FontGlobalScale=1.4f; Frame(window,760,540); Frame(window,760,540);
            Show("largeLocalMode",760,540); SoftwareRenderer.Save(Path.Combine(output,"local-760-top.png"));
            var stop=items["largeStop"]; var panel=items["largePanel"];
            LocalEnsembleRuntimeChecks.Check(stop.Item1.Y>=panel.Item1.Y && stop.Item2.Y<panel.Item2.Y,
                "local emergency stop is visible at 760x540 and 140 percent scale");
            Show("largeLocalMembers",760,540); ClickItem(window,"largeLocalMembers",760,540);
            Show("largeLocalMembersEnd",760,540); SoftwareRenderer.Save(Path.Combine(output,"local-760-members-end.png"));
            LocalEnsembleRuntimeChecks.Check(items["largeLocalMember8"].Item2.Y<items["largePanel"].Item2.Y,
                "eighth local participant remains reachable in compact scrolled panel");
            Show("largeLocalRelease",760,540); ClickItem(window,"largeLocalRelease",760,540); Pump(200);
            LocalEnsembleRuntimeChecks.Check(nodes.All(n=>n.Local?.Connected==false),"native release button frees all locally selected characters");
        }
        finally
        {
            foreach(var node in nodes) node.Dispose(); foreach(var driver in drivers) driver.Dispose(); foreach(var c in controllers) c.Dispose();
            UiKit.ItemBounds=null; ImGui.DestroyContext(ctx);
        }
    }
}
