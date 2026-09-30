using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunWorkspaceUi(string output)
    {
        Directory.CreateDirectory(output);
        using var native=new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory,"cimgui.dll"));
        ImGui.InitApi(native); var context=ImGui.CreateContext();
        try
        {
            var io=ImGui.GetIO(); io.IniFilename=null; io.DeltaTime=1f/60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc",17,default,io.Fonts.GetGlyphRangesChineseFull());
            if(!io.Fonts.Build()) throw new InvalidOperationException("font build failed");
            ImGui.StyleColorsDark();
            using var controller=new StageController(Path.Combine(output,"data-"+Guid.NewGuid()));
            controller.Room=new(controller);
            using var driver=new LocalEnsembleRuntimeChecks.Driver(()=>new(1,LocalEnsembleRuntimeChecks.Membership(8),100,10));
            using var large=new RoomLargeEnsemble(controller.Room,driver);
            controller.Room.LargeEnsemble=large; large.Tick();
            var state=new EnsembleSetupState(0,false,true,false,false,true,false,null);
            controller.EnsembleSetup=()=>state;
            controller.SetEnsembleSetup=(option,value)=>state=option switch
            {
                EnsembleSetupOption.LocalControl=>state with { LocalControl=value },
                EnsembleSetupOption.RemoteControl=>state with { RemoteControl=value },
                EnsembleSetupOption.FollowReady=>state with { FollowReady=value },
                _=>state with { ReceiveSongs=value }
            };
            var opened=0; controller.OpenEnsemblePanel=()=>opened++;
            using var window=new MainWindow(controller);
            UiKit.ItemBounds=(id,min,max)=>items[id]=(min,max);
            Frame(window,1100,740); Frame(window,1100,740);
            Check(Get<object>(window,"page").ToString()=="Home","first launch starts at a scenario chooser");
            SoftwareRenderer.Save(Path.Combine(output,"home-1100.png"));
            ClickItem(window,"homeSolo"); Frame(window,1100,740);
            Check(Get<object>(window,"page").ToString()=="Library","empty solo library leads directly to import");
            SoftwareRenderer.Save(Path.Combine(output,"library-empty-1100.png"));
            NavigateUi(window,"homeTab"); ClickItem(window,"homeParty"); Frame(window,1100,740);
            ShowItem(window,"openNativeEnsemble"); ClickItem(window,"openNativeEnsemble");
            Check(opened==1,"ungrouped user can open the native panel from setup");
            ClickItem(window,"partyQueue");
            Check(Get<object>(window,"page").ToString()=="Party","ungrouped setup cannot dispatch a party queue");
            SoftwareRenderer.Save(Path.Combine(output,"party-ungrouped-1100.png"));
            ShowItem(window,"setupRemoteControl"); ClickItem(window,"setupRemoteControl");
            Check(state.RemoteControl,"member reception preference can be configured before grouping");
            state=state with { Members=8,Leader=false,Issue="等待队长选择歌曲" };
            Frame(window,1100,740); SoftwareRenderer.Save(Path.Combine(output,"party-member-1100.png"));
            ShowItem(window,"partyQueue"); ClickItem(window,"partyQueue");
            Check(Get<object>(window,"page").ToString()=="Party","ordinary member cannot use the leader queue action");
            NavigateUi(window,"homeTab"); ClickItem(window,"homeLocal"); Frame(window,1100,740);
            Check(large.LocalMode && !controller.Room.IsCaptain,"same-computer entry selects local connection without creating a room");
            NavigateUi(window,"homeTab"); ClickItem(window,"homeRemote"); Frame(window,1100,740);
            Check(!large.LocalMode,"cross-computer entry selects room connection");
            SoftwareRenderer.Save(Path.Combine(output,"large-connect-1100.png"));
            io.FontGlobalScale=1.4f;
            NavigateUi(window,"homeTab",760,540);
            SoftwareRenderer.Save(Path.Combine(output,"home-760-140.png"));
            ShowItem(window,"homeParty",760,540); ClickItem(window,"homeParty",760,540);
            ShowItem(window,"openNativeEnsemble",760,540); ClickItem(window,"openNativeEnsemble",760,540);
            Check(opened==2,"native panel entry stays reachable at compact 140 percent scale");
            SoftwareRenderer.Save(Path.Combine(output,"party-member-760-140.png"));
            NavigateUi(window,"queueTab",760,540); Frame(window,760,540);
            ShowItem(window,"queueSettings",760,540); ClickItem(window,"queueSettings",760,540);
            Frame(window,760,540); SoftwareRenderer.Save(Path.Combine(output,"queue-settings-760-140.png"));
            ClickItem(window,"queueSettingsCancel",760,540);
            NavigateUi(window,"roomTab",760,540);
            SoftwareRenderer.Save(Path.Combine(output,"room-navigation-debug.png"));
            Check(Get<object>(window,"page").ToString()=="Room","compact navigation reaches the room page");
            ShowItem(window,"roomRoleMember",760,540); ClickItem(window,"roomRoleMember",760,540);
            ShowItem(window,"roomJoin",760,540); SoftwareRenderer.Save(Path.Combine(output,"room-join-760-140.png"));
            Check(items["roomJoin"].Max.Y<540,"room join is reachable without hidden role tabs");
        }
        finally { UiKit.ItemBounds=null; items.Clear(); ImGui.DestroyContext(context); }
        static void Check(bool okay,string message)
        { if(!okay) throw new InvalidOperationException(message); Console.WriteLine("PASS: "+message); }
    }
}
