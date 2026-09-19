using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private static bool showHelpWindow = false;
    private static void DrawFooter()
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonDiscordNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonDiscordActive);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonDiscordHovered);
        if (ImGui.Button(" Join Discord "))
        {
            Util.Extensions.OpenUrl("https://discord.gg/ejGt2mXHJM");
        }

        ImGui.PopStyleColor(3);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonKofiNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonKofiActive);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonKofiHovered);
        if (ImGui.Button(" Support us on Ko-fi! "))
        {
            Util.Extensions.OpenUrl("https://ko-fi.com/midibard");
        }

        ImGui.PopStyleColor(3);

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonWebsiteNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonWebsiteActive);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonWebsiteHovered);
        if (ImGui.Button(" MidiBard.org "))
        {
            Util.Extensions.OpenUrl("https://midibard.org/");
        }

        ImGui.PopStyleColor(3);

        if (Language.Culture.Name.StartsWith("zh"))
        {
            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.QuestionCircle, "helpbutton"))
            {
                showHelpWindow ^= true;
            }

            DrawHelpWindow();
        }
    }
    private static void DrawHelpWindow()
    {
        if (showHelpWindow)
        {
            ImGui.SetNextWindowPos(ImGui.GetWindowPos() + new Vector2(ImGui.GetWindowSize().X + 2, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
            ImGui.Begin("helptips", ref showHelpWindow, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize);
            ImGui.SetCursorPosX(0);
            ImGui.BulletText(
                "如何开始使用MIDIBARD演奏？" +
                "\n　MIDIBARD窗口默认在角色进入演奏模式后自动弹出。" +
                "\n　点击窗口左上角的“+”按钮来将乐曲文件导入到播放列表，仅支持.mid格式的乐曲。" +
                "\n　导入时按Ctrl或Shift可以选择多个文件一同导入。" +
                "\n　双击播放列表中要演奏的乐曲后点击播放按钮开始演奏。\n");
            ImGui.SetCursorPosX(0);
            ImGui.BulletText(
                "如何使用MIDIBARD进行多人合奏？" +
                "\n　MIDIBARD使用游戏中的合奏助手来完成合奏，请在合奏时打开游戏的节拍器窗口。" +
                "\n　合奏前在播放列表中双击要合奏的乐曲，播放器下方会出现可供演奏的所有音轨，" +
                "\n　为每位合奏成员分别选择其需要演奏的音轨后队长点击节拍器窗口的“合奏准备确认”按钮，" +
                "\n　并确保合奏准备确认窗口中已勾选“使用合奏助手”选项后点击开始即可开始合奏。" +
                "\n　※考虑到不同使用环境乐曲加载速度可能不一致，为了避免切换乐曲导致的不同步，" +
                "\n　　在乐曲结束时合奏会自动停止。\n");
            ImGui.SetCursorPosX(0);
            ImGui.BulletText(
                "如何让MIDIBARD为不同乐曲自动切换音调和乐器？" +
                "\n　在导入前把要指定乐器和移调的乐曲文件名前加入“#<乐器名><移调的半音数量>#”。" +
                "\n　例如：原乐曲文件名为“demo.mid”" +
                "\n　将其重命名为“#中提琴+12#demo.mid”可在演奏到该乐曲时自动切换到中提琴并升调1个八度演奏。" +
                "\n　将其重命名为“#长笛-24#demo.mid”可在演奏到该乐曲时切换到长笛并降调2个八度演奏。" +
                "\n　※可以只添加#+12#或#竖琴#或#harp#，也会有对应的升降调或切换乐器效果。");
            ImGui.SetCursorPosX(0);
            ImGui.BulletText(
                "如何为MIDIBARD配置外部Midi输入（如虚拟Midi接口或Midi键盘）？" +
                "\n　在“输入设备”下拉菜单中选择你的Midi设备，窗口顶端出现 “正在监听Midi输入” " +
                "\n　信息后即可使用外部输入。\n");
            ImGui.SetCursorPosX(0);
            ImGui.BulletText(
                "后台演奏时有轻微卡顿不流畅怎么办？" +
                "\n　在游戏内“系统设置→显示设置→帧数限制”中取消勾选 " +
                "\n　“程序在游戏窗口处于非激活状态时限制帧数” 的选项并应用设置。\n");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Indent();
            //ImGuiHelpers.ScaledDummy(20,0); ImGui.SameLine();
            ImGui.TextUnformatted("如果你喜欢MidiBard，可以在Github上为项目送上一颗");

            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Star.ToIconString());
            ImGui.PopFont();

            ImGui.SameLine();
            ImGui.TextUnformatted("表示支持！");
            ImGui.Spacing();
            if (ImGui.Button("加入QQ群", new Vector2(ImGui.GetFrameHeight() * 5, ImGui.GetFrameHeight())))
            {
                Util.Extensions.OpenUrl("https://jq.qq.com/?_wv=1027&k=7pOgqqZK");
            }

            ImGui.SameLine();
            if (ImGui.Button("Github", new Vector2(ImGui.GetFrameHeight() * 5, ImGui.GetFrameHeight())))
            {
                Util.Extensions.OpenUrl("https://github.com/akira0245/MidiBard");
            }

            ImGui.SameLine();
            if (ImGui.Button("赞助作者", new Vector2(ImGui.GetFrameHeight() * 5, ImGui.GetFrameHeight())))
            {
                Util.Extensions.OpenUrl("https://afdian.net/a/midibard");

            }
            ImGui.Spacing();
            ImGui.End();
            ImGui.PopStyleVar();
        }
    }
}
