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
        ImGui.TextWrapped(room.ConnectionStatus);
        ImGui.Spacing();
        if (!room.IsCaptain && !room.IsRemote)
        {
            if (ImGui.RadioButton("队长 / 演奏端", roomChoice == 0)) roomChoice = 0;
            ImGui.SameLine();
            if (ImGui.RadioButton("主持人", roomChoice == 1)) roomChoice = 1;
            ImGui.SameLine();
            if (ImGui.RadioButton("队员查看", roomChoice == 2)) roomChoice = 2;
            ImGui.Spacing();
            if (roomChoice == 0)
            {
                ImGui.SetNextItemWidth(140 * UiKit.Scale);
                ImGui.InputInt("本地监听端口", ref roomLocalPort, 0);
                if (ImGui.Button("创建演出房间")) room.Create(roomLocalPort);
            }
            else
            {
                ImGui.TextUnformatted(roomChoice == 2 ? "队员查看邀请口令" : "主持人邀请口令");
                ImGui.InputTextMultiline("##roomInvitation", ref roomInvitation, 4096, new Vector2(-1, 110 * UiKit.Scale));
                if (ImGui.Button("加入演出房间"))
                    if (room.Join(roomInvitation, roomChoice == 2 ? RoomRole.Viewer : RoomRole.Presenter)) roomInvitation = "";
            }
            return;
        }

        if (room.IsCaptain)
        {
            UiKit.MutedText($"本地监听  127.0.0.1:{room.LocalPort}");
            ImGui.Spacing();
            ImGui.SetNextItemWidth(Math.Min(400 * UiKit.Scale, ImGui.GetContentRegionAvail().X - 130 * UiKit.Scale));
            ImGui.InputText("樱花节点域名 / IP", ref roomHost, 253);
            ImGui.SetNextItemWidth(140 * UiKit.Scale);
            ImGui.InputInt("公网端口", ref roomPublicPort, 0);
            if (UiKit.Icon(FontAwesomeIcon.Copy, "copyRoomInvite", "复制主持人邀请口令"))
            {
                try { ImGui.SetClipboardText(room.Invite(roomHost, roomPublicPort)); controller.SetStatus("邀请口令已复制"); }
                catch (Exception ex) { controller.SetStatus(ex.Message, true); }
            }
            ImGui.SameLine(); ImGui.TextUnformatted("主持人邀请");
            if (UiKit.Icon(FontAwesomeIcon.Copy, "copyViewerInvite", "复制队员查看邀请码（只读）"))
            {
                try { ImGui.SetClipboardText(room.Invite(roomHost, roomPublicPort, RoomRole.Viewer)); controller.SetStatus("队员查看邀请码已复制"); }
                catch (Exception ex) { controller.SetStatus(ex.Message, true); }
            }
            ImGui.SameLine(); ImGui.TextUnformatted("队员查看邀请");
            ImGui.Spacing(); ImGui.Separator();
        }

        if (!room.IsViewer || room.HasRemoteControl)
        {
            ImGui.BeginDisabled(!controller.CanEditQueue);
            ImGui.TextUnformatted("聊天点歌接收端");
            var presenter = room.ViewPresenterReceivesChat;
            if (ImGui.RadioButton("队长接收", !presenter)) controller.QueueCommand(RoomAction.ReceptionOwner, value: false);
            ImGui.SameLine();
            if (ImGui.RadioButton("主持人接收", presenter)) controller.QueueCommand(RoomAction.ReceptionOwner, value: true);
            ImGui.EndDisabled();
        }
        ImGui.Spacing();
        if (ImGui.Button(room.IsCaptain ? "关闭房间" : "离开房间")) room.Leave();
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
