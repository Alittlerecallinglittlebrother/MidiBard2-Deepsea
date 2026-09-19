using System.Globalization;
using System.Numerics;
using BardStage.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private void DrawSetlists()
    {
        var scale = UiKit.Scale;
        ImGui.SetNextItemWidth(240 * scale);
        if (ImGui.BeginCombo("##show", controller.CurrentSetlist?.Name ?? "选择节目单"))
        {
            foreach (var show in controller.State.Setlists.ToArray())
                if (ImGui.Selectable(show.Name + "##" + show.Id, controller.State.SelectedSetlistId == show.Id))
                    controller.Change(state => state.SelectedSetlistId = show.Id);
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Plus, "newShow", "新建节目单")) OpenName("new", "新演出");
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Pen, "renameShow", "重命名节目单", controller.CurrentSetlist != null)) OpenName("rename", controller.CurrentSetlist!.Name);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Copy, "cloneShow", "复制为新的演出（重置状态）", controller.CurrentSetlist != null)) OpenName("clone", controller.CurrentSetlist!.Name + " 副本");
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.FileExport, "exportShow", "导出节目单与曲目资料", controller.CurrentSetlist != null))
            dialogs.SaveFileDialog("导出节目单", ".json", "setlist", ".json", (ok, path) => { if (ok) controller.ExportCurrentSetlist(path); });
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.FileImport, "importShow", "导入节目单"))
            dialogs.OpenFileDialog("导入节目单", ".json", (ok, path) => { if (ok) controller.ImportSetlist(path); });
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteShow", "删除节目单", controller.CurrentSetlist is { } target && target.Entries.All(e => e.Status != EntryStatus.InProgress)))
        {
            var id = controller.CurrentSetlist!.Id;
            Confirm($"删除节目单《{controller.CurrentSetlist.Name}》？本场记录会先归档，曲库和演出记录保留。", () => controller.DeleteShow(id));
        }
        if (controller.CurrentSetlist is not { } current) return;
        if (previousShowId != current.Id)
        {
            previousShowId = current.Id; selectedEntryId = null;
            gapSeconds = current.GapSeconds;
            targetEnd = current.TargetEndUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        }
        DrawTimelineSummary(current);
        ImGui.Separator();
        if (UiKit.Icon(FontAwesomeIcon.Music, "addSong", "从曲库加入歌曲", controller.State.Songs.Count > 0)) ImGui.OpenPopup("选择曲目##AddSong");
        ImGui.SameLine();
        if (ImGui.Button("加入串场")) AddSegment(current, EntryKind.Talk, "主持串场", 60);
        ImGui.SameLine();
        if (ImGui.Button("加入休息")) AddSegment(current, EntryKind.Break, "休息", 600);
        ImGui.SameLine();
        UiKit.MutedText($"{current.Entries.Count} 个节目");
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Trash, "clearSetlistFinished", "清理本节目单的已结束条目", current.Entries.Any(e => e.Status is EntryStatus.Completed or EntryStatus.Skipped)))
            ConfirmClearFinished(current);
        var pickerSize = new Vector2(Math.Min(520 * scale, ImGui.GetIO().DisplaySize.X - 32), Math.Min(400 * scale, ImGui.GetIO().DisplaySize.Y - 60));
        ImGui.SetNextWindowSize(pickerSize, ImGuiCond.Always);
        if (ImGui.BeginPopup("选择曲目##AddSong"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##songPickerSearch", "搜索曲目", ref pathDraft, 256);
            if (ImGui.BeginChild("SongPickerList", new Vector2(0, Math.Max(80, pickerSize.Y - ImGui.GetFrameHeightWithSpacing() - 28 * scale)), false))
            {
                foreach (var song in controller.State.Songs.Where(s => s.Title.Contains(pathDraft, StringComparison.OrdinalIgnoreCase) || s.Aliases.Any(a => a.Contains(pathDraft, StringComparison.OrdinalIgnoreCase))).ToArray())
                {
                    if (!ImGui.Selectable(song.Title + "##" + song.Id)) continue;
                    var showId = current.Id; var songId = song.Id;
                    controller.Change(state =>
                    {
                        var entry = SetlistOperations.AddSong(state.Setlists.Single(s => s.Id == showId), state.Songs.Single(s => s.Id == songId));
                        SelectEntry(entry);
                    }, "曲目已加入节目单");
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        var height = Math.Max(170, ImGui.GetContentRegionAvail().Y - 65 * scale);
        if (ImGui.GetContentRegionAvail().X < 900 * scale)
        {
            if (ImGui.BeginTabBar("CompactShowViews"))
            {
                if (ImGui.BeginTabItem("节目列表"))
                {
                    if (ImGui.BeginChild("CompactShowEntries", new Vector2(0, Math.Max(100, height - ImGui.GetFrameHeightWithSpacing())), false)) DrawEntryTable(current);
                    ImGui.EndChild(); ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("节目编辑"))
                {
                    if (ImGui.BeginChild("CompactShowInspector", new Vector2(0, Math.Max(100, height - ImGui.GetFrameHeightWithSpacing())), false)) DrawEntryInspector(current);
                    ImGui.EndChild(); ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            return;
        }
        if (ImGui.BeginTable("ShowLayout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("entries", ImGuiTableColumnFlags.WidthStretch, 0.67f);
            ImGui.TableSetupColumn("inspector", ImGuiTableColumnFlags.WidthStretch, 0.33f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("ShowEntries", new Vector2(0, height), false)) DrawEntryTable(current);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("EntryInspector", new Vector2(0, height), false)) DrawEntryInspector(current);
            ImGui.EndChild();
            ImGui.EndTable();
        }
    }

    private void OpenName(string mode, string name) { createMode = mode; createName = name; openCreate = true; }

    private void DrawTimelineSummary(ShowSetlist show)
    {
        var now = DateTimeOffset.UtcNow;
        var remaining = StageOperations.Timeline(show, now).RemainingSeconds;
        var estimatedEnd = now.AddSeconds(remaining).ToLocalTime();
        ImGui.TextUnformatted($"总计划 {UiKit.Duration(SetlistOperations.TotalSeconds(show))}  ·  剩余约 {UiKit.Duration(remaining)}  ·  预计结束 {estimatedEnd:HH:mm:ss}");
        var active = show.Entries.FirstOrDefault(e => e.Status == EntryStatus.InProgress);
        var next = show.Entries.FirstOrDefault(e => e.Id == show.LockedNextEntryId) ?? show.Entries.FirstOrDefault(e => e.Status == EntryStatus.Queued);
        ImGui.PushStyleColor(ImGuiCol.Text, active == null ? UiKit.Muted : UiKit.Accent);
        ImGui.TextWrapped("当前（手动记录）：" + (active?.Title ?? "未开始"));
        ImGui.PopStyleColor();
        ImGui.TextWrapped((show.LockedNextEntryId.HasValue ? "下一项（已锁定）：" : "下一项：") + (next?.Title ?? "无"));
        if (show.TargetEndUtc.HasValue)
        {
            var difference = (estimatedEnd - show.TargetEndUtc.Value).TotalSeconds;
            ImGui.TextColored(difference > 0 ? UiKit.Warning : UiKit.Muted,
                difference > 0 ? $"预计超时 {UiKit.Duration(difference)}" : $"距计划结束余量 {UiKit.Duration(-difference)}");
        }
    }

    private void AddSegment(ShowSetlist current, EntryKind kind, string title, double duration)
    {
        var id = current.Id;
        controller.Change(state => SelectEntry(SetlistOperations.AddSegment(state.Setlists.Single(s => s.Id == id), kind, title, duration)), "节目已加入");
    }

    private unsafe void DrawEntryTable(ShowSetlist show)
    {
        if (show.Entries.Count == 0) { UiKit.MutedText("节目单为空"); return; }
        // Legacy saved column widths can clip the final action after a font-scale change.
        if (!ImGui.BeginTable("Entries", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.NoSavedSettings, new Vector2(0, -1))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28 * UiKit.Scale);
        ImGui.TableSetupColumn("节目", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("时长", ImGuiTableColumnFlags.WidthFixed, 57 * UiKit.Scale);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 65 * UiKit.Scale);
        var actionWidth = (4 * 29 + 3 * 2) * UiKit.Scale;
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, actionWidth);
        ImGui.TableHeadersRow();
        var entries = show.Entries.ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index]; var id = entry.Id; var showId = show.Id;
            ImGui.PushID(id.ToString());
            ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted((index + 1).ToString());
            ImGui.TableNextColumn();
            if (ImGui.Selectable(entry.Title + "##entry", selectedEntryId == id)) SelectEntry(entry);
            UiKit.Tip(KindLabel(entry.Kind) + "\n" + entry.Title + (entry.Notes.Length > 0 ? "\n" + entry.Notes : ""));
            if (ImGui.BeginDragDropSource())
            {
                ImGui.SetDragDropPayload("BARDSTAGE_ENTRY", id.ToByteArray());
                ImGui.TextUnformatted(entry.Title);
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("BARDSTAGE_ENTRY");
                if (!payload.IsNull && payload.DataSize == sizeof(Guid))
                {
                    var dragged = *(Guid*)payload.Data;
                    var newIndex = index;
                    controller.Change(state => SetlistOperations.Move(state.Setlists.Single(s => s.Id == showId), dragged, newIndex));
                }
                ImGui.EndDragDropTarget();
            }
            ImGui.TableNextColumn(); ImGui.TextUnformatted(UiKit.Duration(entry.DurationSeconds / (entry.Kind == EntryKind.Song ? entry.PlaybackSpeed : 1)));
            ImGui.TableNextColumn();
            ImGui.TextColored(entry.Status == EntryStatus.InProgress ? UiKit.Accent : UiKit.Muted, StatusLabel(entry.Status));
            ImGui.TableNextColumn();
            UiKit.ItemBounds?.Invoke("entryActionsClip", ImGui.GetWindowDrawList().GetClipRectMin(), ImGui.GetWindowDrawList().GetClipRectMax());
            var destination = index - 1;
            if (UiKit.Icon(FontAwesomeIcon.ArrowUp, "up", "上移", index > 0))
                controller.Change(state => SetlistOperations.Move(state.Setlists.Single(s => s.Id == showId), id, destination));
            ImGui.SameLine(0, 2 * UiKit.Scale);
            destination = index + 1;
            if (UiKit.Icon(FontAwesomeIcon.ArrowDown, "down", "下移", index < entries.Length - 1))
                controller.Change(state => SetlistOperations.Move(state.Setlists.Single(s => s.Id == showId), id, destination));
            ImGui.SameLine(0, 2 * UiKit.Scale);
            if (UiKit.Icon(show.LockedNextEntryId == id ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen, "lock", show.LockedNextEntryId == id ? "取消下一项锁定" : "锁定为下一项", entry.Status == EntryStatus.Queued))
                controller.Change(state => { var s = state.Setlists.Single(s => s.Id == showId); s.LockedNextEntryId = s.LockedNextEntryId == id ? null : id; });
            ImGui.SameLine(0, 2 * UiKit.Scale);
            if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteEntryRow", "删除节目", entry.Status != EntryStatus.InProgress && controller.QueuePlayer?.ActiveEntryId != id))
                ConfirmDeleteEntry(show, entry);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void SelectEntry(SetlistEntry entry)
    {
        selectedEntryId = entry.Id; entryTitle = entry.Title; entryNotes = entry.Notes;
        entryMinutes = (int)(entry.DurationSeconds / 60); entrySeconds = (int)(entry.DurationSeconds % 60);
        entrySpeed = (float)entry.PlaybackSpeed;
    }

    private void DrawEntryInspector(ShowSetlist show)
    {
        var entry = show.Entries.FirstOrDefault(e => e.Id == selectedEntryId);
        if (entry != null)
        {
            ImGui.TextUnformatted(KindLabel(entry.Kind)); ImGui.Separator();
            Field("节目名称", "entryTitle", ref entryTitle, 256);
            if (entry.Kind == EntryKind.Song)
            {
                var song = controller.State.Songs.FirstOrDefault(s => s.Id == entry.SongId);
                if (song != null)
                {
                    UiKit.MutedText($"{(song.PerformerCount == 0 ? "人数未填" : song.PerformerCount + " 人")}  ·  {song.Arranger}");
                    ImGui.TextColored(File.Exists(song.FilePath) ? UiKit.Muted : UiKit.Warning, File.Exists(song.FilePath) ? "MIDI 文件可用" : "MIDI 文件缺失");
                    if (UiKit.Icon(FontAwesomeIcon.Copy, "entryPath", "复制 MIDI 路径")) ImGui.SetClipboardText(song.FilePath);
                    ImGui.SameLine();
                    if (UiKit.Icon(FontAwesomeIcon.FolderOpen, "entryReveal", "显示 MIDI 文件", File.Exists(song.FilePath))) controller.OpenPath(song.FilePath, true);
                }
                ImGui.TextUnformatted("计划速度"); ImGui.SetNextItemWidth(-1);
                ImGui.SliderFloat("##entrySpeed", ref entrySpeed, 0.1f, 4f, "%.2f x");
            }
            else
            {
                ImGui.TextUnformatted("计划时长");
                ImGui.SetNextItemWidth(105 * UiKit.Scale); ImGui.InputInt("分##segmentMin", ref entryMinutes);
                ImGui.SetNextItemWidth(105 * UiKit.Scale); ImGui.InputInt("秒##segmentSec", ref entrySeconds);
            }
            ImGui.TextUnformatted("备注");
            ImGui.InputTextMultiline("##entryNotes", ref entryNotes, 4096, new Vector2(-1, 66 * UiKit.Scale));
            var showId = show.Id; var id = entry.Id;
            if (ImGui.Button("保存节目"))
                controller.Change(state =>
                {
                    var e = state.Setlists.Single(s => s.Id == showId).Entries.Single(e => e.Id == id);
                    e.Title = entryTitle.Trim(); e.Notes = entryNotes.Trim();
                    if (e.Kind == EntryKind.Song) e.PlaybackSpeed = Math.Round(entrySpeed, 2);
                    else
                    {
                        if (entryMinutes < 0 || entrySeconds is < 0 or > 59) throw new InvalidOperationException("分钟不能为负，秒数应为 0 至 59。");
                        e.DurationSeconds = (double)entryMinutes * 60 + entrySeconds;
                    }
                }, "节目已保存");
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.Undo, "undoEntry", "撤销未保存的节目编辑")) SelectEntry(entry);
            ImGui.Separator();
            ImGui.TextUnformatted("手动演出记录");
            if (UiKit.Icon(FontAwesomeIcon.Play, "startEntry", "标记开始（不控制 MidiBard）", entry.Status == EntryStatus.Queued && !controller.State.Setlists.SelectMany(s => s.Entries).Any(e => e.Status == EntryStatus.InProgress)))
                controller.Change(state => SetlistOperations.Start(state.Setlists.Single(s => s.Id == showId), id, DateTimeOffset.UtcNow), "已记录开始时间");
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.Check, "finishEntry", "标记完成", entry.Status == EntryStatus.InProgress))
                controller.Change(state => SetlistOperations.Finish(state.Setlists.Single(s => s.Id == showId), id, DateTimeOffset.UtcNow), "已记录完成时间");
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.StepForward, "skipEntry", entry.Status == EntryStatus.InProgress ? "中断当前节目" : "跳过", entry.Status is EntryStatus.Queued or EntryStatus.InProgress))
                controller.Change(state => SetlistOperations.Skip(state.Setlists.Single(s => s.Id == showId), id, DateTimeOffset.UtcNow));
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.Redo, "resetEntry", "重置记录，安排补演", entry.Status is EntryStatus.Completed or EntryStatus.Skipped))
                Confirm("重置这一项的演出记录并安排补演？", () => controller.Change(state => SetlistOperations.ResetEntry(state.Setlists.Single(s => s.Id == showId), id)));
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.Trash, "removeEntry", "移出节目单", entry.Status != EntryStatus.InProgress && controller.QueuePlayer?.ActiveEntryId != id))
                ConfirmDeleteEntry(show, entry);
            if (entry.StartedAtUtc is { } start) UiKit.MutedText("开始 " + start.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            if (entry.EndedAtUtc is { } end) UiKit.MutedText("结束 " + end.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            var requests = controller.State.Requests.Where(r => r.SetlistEntryId == entry.Id).ToArray();
            if (requests.Length > 0)
            {
                ImGui.TextUnformatted($"点歌观众（{requests.Length}）");
                ImGui.TextWrapped(string.Join("、", requests.Select(RequesterLabel)));
            }
            ImGui.Spacing();
        }
        else UiKit.MutedText("未选择节目");
        ImGui.Separator();
        ImGui.TextUnformatted("演出计划");
        ImGui.TextUnformatted("连续歌曲间隔（秒）"); ImGui.SetNextItemWidth(-1);
        ImGui.InputInt("##gap", ref gapSeconds);
        Field("计划结束（当地时间）", "targetEnd", ref targetEnd, 32);
        UiKit.Tip("yyyy-MM-dd HH:mm；留空取消结束时间");
        if (ImGui.Button("保存计划"))
        {
            DateTimeOffset? planned = null;
            if (!string.IsNullOrWhiteSpace(targetEnd))
            {
                if (!DateTime.TryParseExact(targetEnd.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                { controller.SetStatus("结束时间格式应为 yyyy-MM-dd HH:mm。", true); return; }
                planned = new DateTimeOffset(date).ToUniversalTime();
            }
            var showId = show.Id;
            controller.Change(state => { var s = state.Setlists.Single(s => s.Id == showId); s.GapSeconds = gapSeconds; s.TargetEndUtc = planned; }, "演出计划已保存");
        }
    }

    private static string KindLabel(EntryKind kind) => kind switch { EntryKind.Song => "歌曲", EntryKind.Talk => "串场", _ => "休息" };
    private static string StatusLabel(EntryStatus status) => status switch { EntryStatus.Queued => "待演", EntryStatus.InProgress => "进行中", EntryStatus.Completed => "已完成", _ => "已跳过" };
}
