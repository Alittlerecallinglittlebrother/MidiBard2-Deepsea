using System.Numerics;
using BardStage.Core;
using BardStage.Core.Rooms;
using Dalamud.Bindings.ImGui;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private enum Page { Home, Library, Queue, Party, Large, Room, Movement, Setlists, Sessions }
    private Page page;
    private bool resetPageScroll;
    private string setupError = "";
    private static string PageTitle(Page value) => value switch
    {
        Page.Home => "开始使用", Page.Library => "曲库", Page.Queue => "点歌队列", Page.Party => "小队合奏",
        Page.Large => "多人合奏", Page.Room => "连接房间", Page.Movement => "移动与队形",
        Page.Setlists => "节目单", _ => "演出记录"
    };
    private void Navigate(Page value) { page = value; resetPageScroll = true; }
    private void SetupAction(Action action)
    { try { action(); setupError = ""; } catch (Exception ex) { setupError = ex.Message; } }
    private bool Viewer => controller.Room is { IsViewer: true, HasRemoteControl: false };
    private bool Available(Page value) => value switch
    {
        Page.Room => controller.Room != null, Page.Large => controller.Room?.LargeEnsemble != null,
        Page.Movement => controller.Room?.Movement != null,
        Page.Library => !Viewer, Page.Setlists or Page.Sessions => controller.Room?.IsRemote != true,
        _ => true
    };
    private static string NavId(Page value) => value switch
    {
        Page.Home => "homeTab", Page.Library => "libraryTab", Page.Queue => "queueTab", Page.Party => "partyTab",
        Page.Large => "largeTab", Page.Room => "roomTab", Page.Movement => "movementTab",
        Page.Setlists => "setlistTab", _ => "sessionsTab"
    };
    private void NavButton(Page value, bool fullWidth)
    {
        if (!Available(value)) return;
        var selected=page==value;
        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.12f,.36f,.44f,1));
        if (ImGui.Button(PageTitle(value)+"##nav"+value, new(fullWidth ? -1 : 0, 0))) Navigate(value);
        UiKit.RecordItem(NavId(value));
        if (selected) ImGui.PopStyleColor();
    }
    private void DrawWorkspace()
    {
        if (selectSetlistTab) { Navigate(Page.Setlists); selectSetlistTab = false; }
        if (!Available(page)) Navigate(Page.Home);
        var compact = ImGui.GetContentRegionAvail().X / UiKit.Scale < 900;
        ImGui.TextUnformatted("midibard2-深海回响改 · 演出助手"); ImGui.SameLine();
        UiKit.MutedText(controller.Room?.LargeEnsemble is { Enabled: true } large
            ? large.LocalMode ? "同机多人" : "跨电脑多人"
            : Viewer ? "队员接收端" : controller.Room?.IsPresenter == true ? "主持人"
            : controller.QueueState.RequestSettings.PlaybackMode == QueuePlaybackMode.Ensemble ? "小队合奏" : "单人演奏");
        var contactWidth = ImGui.CalcTextSize("联系作者").X+ImGui.GetStyle().FramePadding.X*2;
        ImGui.SameLine(); ImGui.SetCursorPosX(ImGui.GetCursorPosX()+Math.Max(0,ImGui.GetContentRegionAvail().X-contactWidth));
        if (ImGui.Button("联系作者##contactAuthor")) controller.ContactAuthor();
        UiKit.RecordItem("contactAuthor");
        DrawPluginNotice(); ImGui.Separator();
        if (compact)
        {
            NavButton(Page.Home,false); ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(100,ImGui.GetContentRegionAvail().X));
            var navigationOpen = ImGui.BeginCombo("##workspacePage",PageTitle(page));
            UiKit.RecordItem("workspacePage");
            if (navigationOpen)
            {
                foreach (var value in Enum.GetValues<Page>().Where(Available))
                {
                    if (ImGui.Selectable(PageTitle(value),page==value)) Navigate(value);
                    UiKit.RecordItem(NavId(value));
                }
                ImGui.EndCombo();
            }
        }
        var height=Math.Max(80,ImGui.GetContentRegionAvail().Y-ImGui.GetTextLineHeightWithSpacing()*2);
        if (!compact)
        {
            if (ImGui.BeginChild("WorkspaceNav",new(140*UiKit.Scale,height),true))
            {
                UiKit.MutedText("演奏");
                foreach(var value in new[]{Page.Home,Page.Library,Page.Queue,Page.Party,Page.Large}) NavButton(value,true);
                ImGui.Spacing(); ImGui.Separator(); UiKit.MutedText("协作与管理");
                foreach(var value in new[]{Page.Room,Page.Movement,Page.Setlists,Page.Sessions}) NavButton(value,true);
            }
            ImGui.EndChild(); ImGui.SameLine();
        }
        var contentName=page switch { Page.Large=>"LargeEnsemblePanel",Page.Movement=>"MovementPanel",Page.Room=>"RoomSettings",_=>"WorkspacePage"+page };
        if (ImGui.BeginChild(contentName,new(0,height),false))
        {
            if (resetPageScroll) { ImGui.SetScrollY(0); resetPageScroll=false; }
            // All pages share one scroll container; essential controls precede long lists.
            if (page!=Page.Large) { ImGui.TextColored(UiKit.Accent,PageTitle(page)); ImGui.Separator(); }
            switch(page)
            {
                case Page.Home: DrawWelcome(); break;
                case Page.Party: DrawPartySetup(); break;
                case Page.Library:
                    ImGui.TextWrapped("先导入 MIDI，再选中歌曲加入点歌队列。多人合奏请到“多人合奏”选曲下发。");
                    if(controller.Room?.IsRemote==true) { ImGui.BeginDisabled(!controller.CanEditQueue); DrawSharedLibrary(); ImGui.EndDisabled(); }
                    else DrawLibrary();
                    break;
                case Page.Queue:
                    if(controller.Room?.LargeEnsemble?.Enabled==true)
                    {
                        ImGui.TextWrapped("当前使用多人合奏。请在“多人合奏”中选曲、分配并预约开演；点歌队列用于单人或八人以内小队连播。");
                        if(ImGui.Button("前往多人合奏")) Navigate(Page.Large);
                    }
                    else if(Viewer) DrawViewerQueue();
                    else { DrawQueueAuthority(); ImGui.BeginDisabled(!controller.CanEditQueue); DrawAutomaticQueue(); ImGui.EndDisabled(); }
                    break;
                case Page.Large: DrawLargeEnsemble(); break;
                case Page.Room: DrawRoom(); break;
                case Page.Movement: DrawMovement(); break;
                case Page.Setlists: ImGui.BeginDisabled(!controller.CanEditQueue); DrawSetlists(); ImGui.EndDisabled(); break;
                case Page.Sessions: DrawSessions(); break;
            }
            ImGui.Dummy(new(0,8*UiKit.Scale));
        }
        ImGui.EndChild();
        UiKit.RecordItem(page==Page.Large?"largePanel":page==Page.Movement?"movementPanel":"workspacePanel");
        ImGui.Separator();
        if (ImGui.BeginChild("Status",new(0,0),false))
        {
            ImGui.PushStyleColor(ImGuiCol.Text,controller.StatusIsError ? UiKit.Warning : UiKit.Muted);
            ImGui.TextWrapped(string.IsNullOrEmpty(controller.StatusMessage)?"设置和曲库保存在本机":controller.StatusMessage);
            ImGui.PopStyleColor();
        }
        ImGui.EndChild();
        DrawModals(); DrawRequestModals(); DrawStageModals();
    }
    private void DrawQueueAuthority()
    {
        if(controller.LocalQueueControlIssue is not { } issue || controller.Room?.IsRemote==true) return;
        ImGui.TextWrapped(issue);
        ImGui.BeginDisabled(controller.IsReadOnly || controller.IsBusy || controller.QueueViewPlayback.ActiveEntryId!=null);
        if(ImGui.Button("改为单人演奏##authoritySolo")) controller.QueueCommand(RoomAction.PlaybackMode,number:(int)QueuePlaybackMode.Solo);
        UiKit.RecordItem("authoritySolo"); ImGui.EndDisabled();
    }
    private void DrawWelcome()
    {
        if (Viewer)
        {
            ImGui.TextUnformatted("你已作为队员加入房间");
            ImGui.TextWrapped("本机不需要导入曲库。先确认接收设置，再等待主控下发歌曲；歌曲与配器由主控统一安排。");
            ImGui.TextWrapped(controller.Room!.ConnectionStatus);
            var largeReceiver=controller.Room.LargeEnsemble?.Enabled==true;
            if(ImGui.Button(largeReceiver?"查看多人合奏接收状态":"检查本机小队接收设置",new(-1,0)))
            { largeStep=2; Navigate(largeReceiver?Page.Large:Page.Party); }
            UiKit.RecordItem("homeReceiver");
            if(ImGui.Button("查看演出队列",new(-1,0))) Navigate(Page.Queue);
            if(ImGui.Button("查看房间连接 / 离开房间",new(-1,0))) Navigate(Page.Room);
            return;
        }
        if(controller.Room?.IsPresenter==true)
        {
            ImGui.TextUnformatted("你已作为主持人加入房间");
            ImGui.TextWrapped("打开点歌队列，为主控加歌、排序和控制播放。歌曲使用主控的曲库。");
            if(ImGui.Button("管理点歌队列",new(-1,0))) Navigate(Page.Queue);
            if(ImGui.Button("查看房间连接 / 离开房间",new(-1,0))) Navigate(Page.Room);
            return;
        }
        ImGui.TextUnformatted("想怎样演奏？");
        ImGui.TextWrapped("选一种方式，按页面上的步骤完成准备。第一次使用，先导入一首 MIDI 试奏。");
        if (setupError!="") ImGui.TextColored(UiKit.Warning,setupError);
        var columns=ImGui.GetContentRegionAvail().X/UiKit.Scale>=500?2:1;
        if(ImGui.BeginTable("GettingStarted",columns,ImGuiTableFlags.SizingStretchSame))
        {
            WelcomeChoice("一个人演奏","导入 MIDI，加入点歌队列，再播放；可开启自动连播。","homeSolo",()=>
            {
                if(controller.Room?.LargeEnsemble?.Enabled==true) throw new InvalidOperationException("请先在多人合奏页停止并关闭接收，再切换演奏方式");
                if(!controller.QueueCommand(RoomAction.PlaybackMode,number:(int)QueuePlaybackMode.Solo)) throw new InvalidOperationException(controller.StatusMessage);
                Navigate(controller.QueueState.Songs.Count==0?Page.Library:Page.Queue);
            });
            WelcomeChoice("八人以内小队","使用游戏原生准备确认。队长选曲，队员跟随，可接收观众点歌。","homeParty",()=>Navigate(Page.Party));
            if(controller.Room?.LargeEnsemble!=null)
            {
                WelcomeChoice("同机多开 · 2～8 人","一台电脑运行多个角色，无需房间。主控选参与者、分轨和乐器。","homeLocal",()=>OpenLargeMode(true));
                WelcomeChoice("跨电脑多人 · 2～8 人","主控创建房间，队员加入；统一分曲、分轨，再预约开演。","homeRemote",()=>OpenLargeMode(false));
            }
            ImGui.EndTable();
        }
        ImGui.Spacing(); ImGui.Separator();
        ImGui.TextWrapped($"本机曲库：{controller.QueueState.Songs.Count} 首。队员接收主控分发的歌曲时，可以使用空曲库。");
        if(controller.Room!=null)
        {
            if(ImGui.Button("我有邀请码，去加入房间")) { roomChoice=2; Navigate(Page.Room); }
            UiKit.RecordItem("homeJoin");
            UiKit.MutedText("主持人代管点歌、查看节目单，也从连接房间进入。");
        }
        if(controller.IsReadOnly) ImGui.TextWrapped("曲库当前只读：另一个本机角色可能正在管理同一曲库。接收演奏不要求写入曲库。");
        if(ImGui.Button("打开本机数据目录")) controller.OpenPath(controller.DataDirectory);
        UiKit.RecordItem("data");
    }
    private void WelcomeChoice(string title,string detail,string id,Action action)
    {
        ImGui.TableNextColumn();
        ImGui.Spacing();
        if(ImGui.Button(title+"##"+id,new(-1,38*UiKit.Scale))) SetupAction(action);
        UiKit.RecordItem(id); ImGui.TextWrapped(detail); ImGui.Spacing();
    }
    private void OpenLargeMode(bool local)
    {
        controller.Room!.LargeEnsemble!.SetLocalMode(local);
        Navigate(Page.Large);
    }
    private static void WorkflowStep(string title,string detail)
    {
        ImGui.Spacing(); ImGui.TextColored(UiKit.Accent,title);
        if(detail!="") ImGui.TextWrapped(detail);
        ImGui.Spacing();
    }
    private void DrawPartySetup()
    {
        WorkflowStep("1  确认身份与组队","小队合奏最多 8 人，由游戏小队队长选曲和发起准备确认。");
        var state=controller.EnsembleSetup?.Invoke();
        ImGui.TextWrapped(state==null?"游戏状态不可用，请在游戏内打开此页面。":state.Members<2
            ?"尚未组队。现在可以打开原生面板，提前设置本机接收方式。"
            :state.Leader?$"当前是队长 · 小队 {state.Members} 人":$"当前是队员 · 小队 {state.Members} 人；等队长选曲即可。");
        WorkflowStep("2  准备本机接收","同机多开使用同机控制；不同电脑使用跨电脑控制。全员开启跟随准备确认。");
        if(state!=null)
        {
            void Toggle(string label,EnsembleSetupOption option,bool current)
            { if(ImGui.Checkbox(label,ref current)) SetupAction(()=>controller.SetEnsembleSetup?.Invoke(option,current)); UiKit.RecordItem("setup"+option); }
            Toggle("接收同机控制",EnsembleSetupOption.LocalControl,state.LocalControl);
            Toggle("接收跨电脑控制（PMD）",EnsembleSetupOption.RemoteControl,state.RemoteControl);
            Toggle("跟随游戏准备确认",EnsembleSetupOption.FollowReady,state.FollowReady);
            if(state.RemoteControl)
            {
                Toggle("自动接收队长歌曲（需要房间）",EnsembleSetupOption.ReceiveSongs,state.ReceiveSongs);
                if(ImGui.Button("去连接房间")) { roomChoice=state.Leader?0:2; Navigate(Page.Room); }
            }
            if(!state.LocalControl && !state.RemoteControl) ImGui.TextColored(UiKit.Warning,"尚未开启任何控制接收方式。");
            if(!state.FollowReady) ImGui.TextColored(UiKit.Warning,"跟随准备确认已关闭，本机不会随游戏合奏启动。");
            if(state.Issue!=null) ImGui.TextWrapped(state.Issue);
        }
        if(setupError!="") ImGui.TextColored(UiKit.Warning,setupError);
        WorkflowStep("3  选曲、分配，再开演","队长打开原生面板检查轨道和乐器。队员收到自动分配后按本曲执行，无需把自己的自动分配开关也打开。");
        ImGui.BeginDisabled(controller.OpenEnsemblePanel==null);
        if(ImGui.Button("打开原生合奏管理面板",new(-1,0))) controller.OpenEnsemblePanel?.Invoke();
        UiKit.RecordItem("openNativeEnsemble"); ImGui.EndDisabled();
        ImGui.BeginDisabled(state is not { Members: >= 2, Leader: true } || controller.Room?.LargeEnsemble?.Enabled==true);
        if(ImGui.Button("队长：前往点歌队列",new(-1,0)))
            SetupAction(()=>
            {
                if(!controller.QueueCommand(RoomAction.PlaybackMode,number:(int)QueuePlaybackMode.Ensemble)) throw new InvalidOperationException(controller.StatusMessage);
                Navigate(Page.Queue);
            });
        UiKit.RecordItem("partyQueue"); ImGui.EndDisabled();
    }
}
