using System.Numerics;
using BardStage.Core;
using Dalamud.Bindings.ImGui;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private void DrawViewerQueue()
    {
        var height = Math.Max(80, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale);
        if (ImGui.BeginChild("ViewerQueue", new Vector2(0, height), false))
        {
            var show = controller.QueueViewShow;
            var playback = controller.QueueViewPlayback;
            if (show == null) UiKit.MutedText("等待队长同步节目单");
            else
            {
                ImGui.TextWrapped(show.Name);
                ImGui.TextWrapped(playback.Status);
                var current = StageOperations.Current(show) ?? show.Entries.FirstOrDefault(e => e.Id == playback.ActiveEntryId);
                var waiting = StageOperations.RunOrder(show).Where(e => e.Kind == EntryKind.Song && e.Id != current?.Id).ToArray();
                ImGui.TextWrapped((current?.Status == EntryStatus.Queued ? "准备中：" : "当前：") + (current?.Title ?? "暂无"));
                ImGui.TextWrapped("下一首：" + (waiting.FirstOrDefault()?.Title ?? "暂无"));
                ImGui.Separator();
                ImGui.TextUnformatted($"待演  {waiting.Length} 首");
                if (ImGui.BeginTable("ViewerRunOrder", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
                {
                    ImGui.TableSetupColumn("顺序", ImGuiTableColumnFlags.WidthFixed, 55 * UiKit.Scale);
                    ImGui.TableSetupColumn("曲目", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 100 * UiKit.Scale);
                    ImGui.TableHeadersRow();
                    if (current != null) ViewerRow("当前", current, current.Status == EntryStatus.Queued ? "准备中" : playback.Paused ? "已暂停" : "演奏中");
                    for (var i = 0; i < waiting.Length; i++) ViewerRow((i + 1).ToString(), waiting[i], i == 0 ? "下一首" : "待演");
                    ImGui.EndTable();
                }
            }
        }
        ImGui.EndChild();
    }

    private static void ViewerRow(string position, SetlistEntry entry, string status)
    {
        ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(position);
        ImGui.TableNextColumn(); ImGui.TextWrapped(entry.Title);
        ImGui.TableNextColumn(); ImGui.TextUnformatted(status);
    }
}
