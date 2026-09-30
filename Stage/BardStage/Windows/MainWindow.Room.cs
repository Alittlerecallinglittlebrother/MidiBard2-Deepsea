using System.Numerics;
using BardStage.Core;
using BardStage.Core.Rooms;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private int roomChoice, roomLocalPort = 28765, roomPublicPort;
    private string roomHost = "", roomInvitation = "", sharedSearch = "";

    private void DrawRoom()
    {
        var room = controller.Room!;
        var large = room.LargeEnsemble?.Enabled == true;
        ImGui.TextWrapped(room.ConnectionStatus);
        if (room.IsCaptain || room.IsRemote)
        {
            ImGui.TextUnformatted(room.IsCaptain ? "我的身份：房主 / 主控" : room.IsPresenter ? "我的身份：主持人（代管点歌）" : "我的身份：演奏队员 / 查看者");
            if (ImGui.Button(room.IsCaptain ? "关闭房间" : "离开房间")) room.Leave();
            UiKit.RecordItem("roomLeave");
        }
        else ImGui.TextWrapped("同机多开无需房间。不同电脑共用歌曲、同步演奏，或让主持人代管点歌时，再连接房间。");

        if (!large && room.SongSyncEnabled != null)
        {
            WorkflowStep("小队歌曲接收", "八人以内小队：全员开启 PMD 与此开关，队员加入房间后就能接收队长的 MIDI，无需准备相同曲库。");
            var sharing = room.SongSyncEnabled();
            if (ImGui.Checkbox("自动接收队长歌曲", ref sharing)) SetupAction(() => room.SetSongSyncEnabled?.Invoke(sharing));
            UiKit.RecordItem("songSyncToggle");
            if (sharing) ImGui.TextWrapped(room.Songs?.Status ?? "等待队长选曲");
        }
        if (large) ImGui.TextWrapped("多人合奏已启用：歌曲会随主控分发自动接收，无需再开启小队歌曲接收。");
        if (setupError != "") ImGui.TextWrapped(setupError);

        if (!room.IsCaptain && !room.IsRemote)
        {
            WorkflowStep("选择你的身份", "主控创建房间并发送邀请码；演奏队员用队员邀请码加入。主持人只负责代管点歌。");
            if (ImGui.RadioButton("我是主控，创建房间", roomChoice == 0)) roomChoice = 0;
            UiKit.RecordItem("roomRoleHost");
            if (ImGui.RadioButton("我是演奏队员，加入房间", roomChoice == 2)) roomChoice = 2;
            UiKit.RecordItem("roomRoleMember");
            if (ImGui.RadioButton("我是主持人，代管点歌", roomChoice == 1)) roomChoice = 1;
            UiKit.RecordItem("roomRolePresenter");
            ImGui.Spacing();
            if (roomChoice == 0)
            {
                ImGui.TextWrapped("默认监听端口 28765。跨公网连接时，需要服务器或内网穿透把公网端口转发到本机此端口。");
                ImGui.SetNextItemWidth(140 * UiKit.Scale);
                ImGui.InputInt("本机监听端口", ref roomLocalPort, 0);
                if (ImGui.Button("创建房间", new(-1,0))) room.Create(roomLocalPort);
                UiKit.RecordItem("roomCreate");
            }
            else
            {
                ImGui.TextWrapped(roomChoice == 2 ? "请粘贴主控给你的队员邀请码；接收歌曲和演奏无需队列编辑权限。" : "请粘贴主持人邀请码；加入后可管理主控的点歌队列。");
                ImGui.InputTextMultiline("##roomInvitation", ref roomInvitation, 4096, new Vector2(-1, 85 * UiKit.Scale));
                if (ImGui.Button("加入房间", new(-1,0)))
                    if (room.Join(roomInvitation, roomChoice == 2 ? RoomRole.Viewer : RoomRole.Presenter)) roomInvitation = "";
                UiKit.RecordItem("roomJoin");
            }
            return;
        }
        if (room.IsCaptain)
        {
            WorkflowStep("邀请其他电脑加入", $"本机监听端口：{room.LocalPort}。填写其他电脑能够访问的服务器或穿透地址。");
            ImGui.TextUnformatted("服务器 / 穿透域名或 IP");
            ImGui.SetNextItemWidth(-1); ImGui.InputText("##roomHost", ref roomHost, 253);
            ImGui.SetNextItemWidth(140 * UiKit.Scale); ImGui.InputInt("公网端口", ref roomPublicPort, 0);
            if (ImGui.Button("复制队员邀请码", new(-1,0)))
                CopyRoomInvite(RoomRole.Viewer);
            UiKit.RecordItem("copyViewerInvite");
            if (!large)
            {
                if (ImGui.Button("复制主持人邀请码", new(-1,0))) CopyRoomInvite(RoomRole.Presenter);
                UiKit.RecordItem("copyRoomInvite");
            }
        }
        WorkflowStep("连接完成后", large ? "主控回到多人合奏继续选曲与分配；队员保持接收开启，等主控下发。" : "队长进入小队合奏检查分配并选曲；队员等待歌曲接收和准备确认。");
        if (ImGui.Button(large ? "返回多人合奏" : "前往小队合奏设置", new(-1,0))) Navigate(large ? Page.Large : Page.Party);
        UiKit.RecordItem("roomContinue");
        if (!large && (!room.IsViewer || room.HasRemoteControl) && ImGui.CollapsingHeader("主持人与聊天点歌设置"))
        {
            ImGui.BeginDisabled(!controller.CanEditQueue);
            ImGui.TextWrapped("由一端接收观众的聊天点歌，避免重复登记。");
            var presenter = room.ViewPresenterReceivesChat;
            if (ImGui.RadioButton("队长接收", !presenter)) controller.QueueCommand(RoomAction.ReceptionOwner, value: false);
            if (ImGui.RadioButton("主持人接收", presenter)) controller.QueueCommand(RoomAction.ReceptionOwner, value: true);
            ImGui.EndDisabled();
        }
    }

    private void CopyRoomInvite(RoomRole role)
    {
        try { ImGui.SetClipboardText(controller.Room!.Invite(roomHost,roomPublicPort,role)); controller.SetStatus("邀请码已复制，请发送给对应身份的使用者"); }
        catch(Exception ex) { controller.SetStatus(ex.Message,true); }
    }

    private void DrawSharedLibrary()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##sharedSearch", "搜索队长曲库", ref sharedSearch, 256);
        var songs = string.IsNullOrWhiteSpace(sharedSearch) ? controller.QueueState.Songs.ToArray()
            : RequestOperations.FindMatches(controller.QueueState, sharedSearch).ToArray();
        var height = Math.Max(100, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale);
        if (ImGui.BeginChild("SharedLibrary", new Vector2(0, height), false))
        {
            foreach (var song in songs)
            {
                ImGui.PushID(song.Id.ToString());
                if (UiKit.Icon(FontAwesomeIcon.Plus, "addSharedSong", "加入待演队列")) controller.QueueCommand(RoomAction.Add, song: song.Id);
                ImGui.SameLine(); ImGui.TextWrapped(song.Title);
                ImGui.TextWrapped(VersionLabel(song));
                ImGui.Separator(); ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }
}
