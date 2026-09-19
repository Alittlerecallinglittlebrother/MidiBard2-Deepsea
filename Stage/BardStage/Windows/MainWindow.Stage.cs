using System.Numerics;
using BardStage.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private Guid? stageShowId, stageQueueSelectedId;
    private bool returnToStage;
    private bool openStageSegment;
    private Guid segmentShowId;
    private EntryKind stageSegmentKind;
    private string stageSegmentTitle = "";
    private int stageSegmentMinutes = 1, stageSegmentSeconds;
    private readonly AnnouncementDraft announcementDraft = new();
    private AnnouncementKind announcementKind = AnnouncementKind.NextSong;
    private bool openTemplateSettings, openDraftHistory;
    private AnnouncementSettings templateDraft = new();
    private AnnouncementKind templateKind;
    private int historyIndex;
    private string draftError = "";

    private void DrawStage()
    {
        var state = controller.State;
        if (!state.Setlists.Any(s => s.Id == stageShowId))
            stageShowId = state.Setlists.FirstOrDefault(s => StageOperations.Current(s) != null)?.Id ?? state.SelectedSetlistId ?? state.Setlists.FirstOrDefault()?.Id;
        ImGui.SetNextItemWidth(230 * UiKit.Scale);
        if (ImGui.BeginCombo("##stageShow", state.Setlists.FirstOrDefault(s => s.Id == stageShowId)?.Name ?? "选择演出"))
        {
            foreach (var item in state.Setlists)
                if (ImGui.Selectable(item.Name + "##" + item.Id, item.Id == stageShowId)) { stageShowId = item.Id; stageQueueSelectedId = null; }
            ImGui.EndCombo();
        }
        var show = state.Setlists.FirstOrDefault(s => s.Id == stageShowId);
        if (show == null) return;
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.List, "stagePrograms", "编辑节目单")) VisitStagePage(show, false);
        ImGui.SameLine();
        var pending = state.Requests.Count(r => r.SetlistId == show.Id && r.Status is RequestStatus.Pending or RequestStatus.Deferred);
        if (ImGui.Button($"审核点歌（{pending}）")) VisitStagePage(show, true);
        ImGui.SameLine();
        var receptionTarget = state.Setlists.FirstOrDefault(s => s.Id == state.RequestSettings.TargetSetlistId);
        var receiving = state.RequestSettings.IsOpen;
        ImGui.BeginDisabled(receptionTarget?.Id != show.Id);
        if (ImGui.Checkbox("开放点歌", ref receiving)) controller.Change(s => s.RequestSettings.IsOpen = receiving);
        ImGui.EndDisabled(); UiKit.Tip("接收演出：" + (receptionTarget?.Name ?? "未设置"));
        var otherActive = state.Setlists.FirstOrDefault(s => s.Id != show.Id && StageOperations.Current(s) != null);
        if (otherActive != null)
        {
            ImGui.TextColored(UiKit.Warning, "进行中的演出：" + otherActive.Name);
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.ArrowRight, "activeShow", "查看进行中的演出")) stageShowId = otherActive.Id;
        }
        var timeline = StageOperations.Timeline(show, DateTimeOffset.UtcNow);
        ImGui.TextWrapped($"已完成 {timeline.CompletedCount}/{timeline.TotalCount}  ·  剩余约 {UiKit.Duration(timeline.RemainingSeconds)}  ·  预计结束 {timeline.EstimatedEndUtc.ToLocalTime():HH:mm:ss}");
        if (timeline.TargetVarianceSeconds is { } variance)
            ImGui.TextColored(variance > 0 ? UiKit.Warning : UiKit.Muted, variance > 0 ? "预计超时 " + UiKit.Duration(variance) : "结束时间余量 " + UiKit.Duration(-variance));
        ImGui.Separator();
        var height = Math.Max(120 * UiKit.Scale, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale);
        if (ImGui.BeginTable("StageLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("program", ImGuiTableColumnFlags.WidthStretch, 0.56f);
            ImGui.TableSetupColumn("announcement", ImGuiTableColumnFlags.WidthStretch, 0.44f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("StagePrograms", new Vector2(0, height), false)) DrawStageProgram(show, timeline);
            ImGui.EndChild(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("StageAnnouncement", new Vector2(0, height), false)) DrawStageAnnouncement(show);
            ImGui.EndChild(); ImGui.EndTable();
        }
    }

    private void VisitStagePage(ShowSetlist show, bool requests)
    {
        if (!controller.Change(s => s.SelectedSetlistId = show.Id)) return;
        returnToStage = true;
        if (requests) { selectRequestTab = true; requestFilter = 0; allRequestShows = false; requestSearch = ""; }
        else selectSetlistTab = true;
    }

    private void DrawStageReturn()
    {
        if (!returnToStage) return;
        if (UiKit.Icon(FontAwesomeIcon.ArrowLeft, "returnStage", "返回演出工作台")) { selectStageTab = true; returnToStage = false; }
        ImGui.SameLine();
        ImGui.TextWrapped("演出：" + (controller.State.Setlists.FirstOrDefault(s => s.Id == stageShowId)?.Name ?? "已删除"));
        ImGui.Separator();
    }

    private void DrawStageProgram(ShowSetlist show, StageTimeline timeline)
    {
        var active = StageOperations.Current(show);
        var next = StageOperations.Next(show);
        ImGui.TextColored(UiKit.Accent, controller.SyncEnabled ? "当前节目（MidiBard 同步）" : "当前节目（手动记录）");
        if (active != null)
        {
            DrawStageEntryDetails(active);
            ImGui.TextUnformatted($"{(active.PausedAtUtc.HasValue ? "已暂停" : "已用")} {UiKit.Duration(timeline.CurrentElapsedSeconds)}  /  计划 {UiKit.Duration(timeline.CurrentPlannedSeconds)}");
            ImGui.TextColored(timeline.CurrentOverrunSeconds > 0 ? UiKit.Warning : UiKit.Muted,
                timeline.CurrentOverrunSeconds > 0 ? "超出计划 " + UiKit.Duration(timeline.CurrentOverrunSeconds) : "预计剩余 " + UiKit.Duration(timeline.CurrentRemainingSeconds));
            if (UiKit.Icon(FontAwesomeIcon.Check, "stageComplete", "标记完成并准备下一项"))
                controller.Change(s => stageQueueSelectedId = StageOperations.CompleteAndAdvance(s, show.Id, active.Id, DateTimeOffset.UtcNow), "当前节目已完成，下一项待手动开始");
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.Stop, "stageInterrupt", "中断当前节目"))
                Confirm($"中断《{active.Title}》并记录为已跳过？", () => controller.Change(s => stageQueueSelectedId = StageOperations.SkipAndAdvance(s, show.Id, active.Id, DateTimeOffset.UtcNow), "已记录中断"));
        }
        else UiKit.MutedText("未开始");
        ImGui.Spacing(); ImGui.Separator();
        ImGui.TextUnformatted(show.LockedNextEntryId.HasValue ? "下一项（已锁定）" : "下一项");
        if (next != null)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##stageNext", next.Title))
            {
                foreach (var entry in show.Entries.Where(e => e.Status == EntryStatus.Queued).ToArray())
                    if (ImGui.Selectable(entry.Title + "##" + entry.Id, entry.Id == next.Id))
                        controller.Change(s => s.Setlists.Single(x => x.Id == show.Id).LockedNextEntryId = entry.Id, "下一项已锁定");
                ImGui.EndCombo();
            }
            DrawStageEntryDetails(next, false);
            var canStart = !controller.State.Setlists.Any(s => StageOperations.Current(s) != null);
            if (UiKit.Icon(FontAwesomeIcon.Play, "stageStart", "手动标记下一项开始", canStart))
                controller.Change(s => StageOperations.StartNext(s, show.Id, next.Id, DateTimeOffset.UtcNow), "已记录开始时间");
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.StepForward, "stageSkip", "跳过下一项"))
                Confirm($"跳过《{next.Title}》？", () => controller.Change(s => stageQueueSelectedId = StageOperations.SkipAndAdvance(s, show.Id, next.Id, DateTimeOffset.UtcNow)));
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.LockOpen, "stageUnlock", "取消下一项锁定", show.LockedNextEntryId.HasValue))
                controller.Change(s => s.Setlists.Single(x => x.Id == show.Id).LockedNextEntryId = null);
        }
        else UiKit.MutedText("没有待演节目");
        ImGui.Spacing();
        if (ImGui.Button("插入串场")) OpenStageSegment(show, EntryKind.Talk);
        ImGui.SameLine();
        if (ImGui.Button("插入休息")) OpenStageSegment(show, EntryKind.Break);
        ImGui.Separator();
        if (ImGui.CollapsingHeader("待演顺序", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var queue = StageOperations.RunOrder(show).Where(e => e.Status == EntryStatus.Queued).ToArray();
            for (var i = 0; i < queue.Length; i++)
            {
                var entry = queue[i];
                ImGui.PushID(entry.Id.ToString());
                if (UiKit.Icon(entry.Id == show.LockedNextEntryId ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen, "stageQueueLock", "设为下一项"))
                    controller.Change(s => s.Setlists.Single(x => x.Id == show.Id).LockedNextEntryId = entry.Id);
                ImGui.SameLine(); ImGui.TextWrapped($"{i + 1}. {entry.Title}  ·  {UiKit.Duration(entry.DurationSeconds / (entry.Kind == EntryKind.Song ? entry.PlaybackSpeed : 1))}");
                ImGui.PopID();
            }
        }
        if (ImGui.CollapsingHeader("已结束与补演"))
        {
            foreach (var entry in show.Entries.Where(e => e.Status is EntryStatus.Completed or EntryStatus.Skipped).ToArray())
            {
                ImGui.PushID(entry.Id.ToString());
                if (UiKit.Icon(FontAwesomeIcon.Redo, "stageReplay", "重置演出记录并设为下一项"))
                    Confirm($"重置《{entry.Title}》原有开始与结束记录，并设为下一项补演？", () => controller.Change(s => StageOperations.PrepareReplay(s, show.Id, entry.Id), "已安排补演"));
                ImGui.SameLine(); ImGui.TextWrapped(entry.Title + " · " + StatusLabel(entry.Status));
                ImGui.PopID();
            }
        }
    }

    private void DrawStageEntryDetails(SetlistEntry entry, bool title = true)
    {
        if (title) ImGui.TextWrapped(entry.Title);
        var song = controller.State.Songs.FirstOrDefault(s => s.Id == entry.SongId);
        if (song != null)
        {
            ImGui.TextWrapped(VersionLabel(song));
            if (!File.Exists(song.FilePath)) ImGui.TextColored(UiKit.Warning, "MIDI 文件缺失");
        }
        else UiKit.MutedText(KindLabel(entry.Kind) + " · " + UiKit.Duration(entry.DurationSeconds));
        if (entry.Notes.Length > 0) ImGui.TextWrapped(entry.Notes);
        var requests = controller.State.Requests.Where(r => r.SetlistEntryId == entry.Id).Select(RequesterLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requests.Length > 0) ImGui.TextWrapped("点歌观众：" + string.Join("、", requests));
    }

    private void OpenStageSegment(ShowSetlist show, EntryKind kind)
    {
        segmentShowId = show.Id; stageSegmentKind = kind;
        stageSegmentTitle = kind == EntryKind.Talk ? "主持串场" : "中场休息";
        stageSegmentMinutes = kind == EntryKind.Talk ? 1 : 5; stageSegmentSeconds = 0;
        openStageSegment = true;
    }

    private AnnouncementContext StageAnnouncementContext(ShowSetlist show, AnnouncementKind kind)
    {
        var active = StageOperations.Current(show);
        var next = StageOperations.Next(show);
        Guid? entryId = null;
        if (kind == AnnouncementKind.NextSong)
        {
            if (next?.Kind != EntryKind.Song) throw new InvalidOperationException("下一项没有歌曲节目。");
            entryId = next.Id;
        }
        else if (kind == AnnouncementKind.Thanks)
        {
            var song = active?.Kind == EntryKind.Song ? active : show.Entries.Where(e => e.Kind == EntryKind.Song && e.Status == EntryStatus.Completed).OrderByDescending(e => e.EndedAtUtc).FirstOrDefault();
            entryId = song?.Id;
        }
        else if (kind == AnnouncementKind.Intermission)
            entryId = next?.Kind is EntryKind.Talk or EntryKind.Break ? next.Id : active?.Kind is EntryKind.Talk or EntryKind.Break ? active.Id : null;
        return AnnouncementTemplates.CreateContext(controller.State, show.Id, entryId);
    }

    private void DrawStageAnnouncement(ShowSetlist show)
    {
        ImGui.TextUnformatted("报幕草稿");
        ImGui.SetNextItemWidth(Math.Max(90 * UiKit.Scale, ImGui.GetContentRegionAvail().X - 77 * UiKit.Scale));
        if (ImGui.BeginCombo("##announcementKind", AnnouncementLabel(announcementKind)))
        {
            foreach (var kind in Enum.GetValues<AnnouncementKind>())
                if (ImGui.Selectable(AnnouncementLabel(kind), announcementKind == kind)) announcementKind = kind;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Cog, "announcementSettings", "编辑报幕模板", controller.AnnouncementError.Length == 0))
        {
            templateDraft = StageController.Clone(controller.Announcements); templateKind = announcementKind; openTemplateSettings = true;
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.History, "draftHistory", "查看手动旧稿", announcementDraft.History.Count > 0))
        { historyIndex = announcementDraft.History.Count - 1; openDraftHistory = true; }
        if (controller.AnnouncementError.Length > 0) ImGui.TextColored(UiKit.Warning, "模板未载入，当前使用默认模板");
        AnnouncementContext? context = null;
        try
        {
            context = StageAnnouncementContext(show, announcementKind);
            var label = show.Name + " / " + AnnouncementLabel(announcementKind) + (context.EntryId.HasValue ? " / " + context.Title : "");
            announcementDraft.Refresh(controller.Announcements, announcementKind, context, label);
            draftError = "";
        }
        catch (Exception ex) { announcementDraft.Invalidate(); draftError = ex.Message; }
        if (context != null && draftError.Length == 0) ImGui.TextWrapped("关联：" + announcementDraft.Label);
        if (draftError.Length > 0) ImGui.TextColored(UiKit.Warning, draftError);
        ImGui.Spacing();
        var text = announcementDraft.Text;
        ImGui.BeginDisabled(draftError.Length > 0);
        if (ImGui.InputTextMultiline("##announcementText", ref text, 8192, new Vector2(-1, 180 * UiKit.Scale))) announcementDraft.Text = text;
        if (UiKit.Icon(FontAwesomeIcon.Copy, "copyAnnouncement", "复制当前草稿", context != null && !string.IsNullOrWhiteSpace(text)))
        {
            try
            {
                var freshShow = controller.State.Setlists.Single(s => s.Id == show.Id);
                ImGui.SetClipboardText(announcementDraft.TextForCopy(controller.Announcements, announcementKind, StageAnnouncementContext(freshShow, announcementKind)));
                controller.SetStatus("报幕草稿已复制");
            }
            catch (Exception ex) { controller.SetStatus(ex.Message, true); }
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Redo, "regenerateAnnouncement", "按模板重新生成草稿")) announcementDraft.Regenerate();
        ImGui.SameLine(); UiKit.MutedText($"{announcementDraft.Text.Length} 字符" + (announcementDraft.Edited ? " · 已编辑" : ""));
        ImGui.EndDisabled();
        if (announcementDraft.History.Count > 0) UiKit.MutedText($"已保留 {announcementDraft.History.Count} 份手动旧稿");
        ImGui.Spacing(); ImGui.Separator();
        if (ImGui.CollapsingHeader("预览", ImGuiTreeNodeFlags.DefaultOpen)) ImGui.TextWrapped(announcementDraft.Text);
        DrawStageSync(show);
    }

    private void DrawStageModals()
    {
        var scale = UiKit.Scale;
        if (openStageSegment) { ImGui.OpenPopup("插入下一项##Stage"); openStageSegment = false; }
        ImGui.SetNextWindowSize(new Vector2(420 * scale, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("插入下一项##Stage", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.BeginDisabled(controller.IsReadOnly || controller.IsBusy);
            ImGui.TextWrapped("演出：" + (controller.State.Setlists.FirstOrDefault(s => s.Id == segmentShowId)?.Name ?? "已删除"));
            Field("环节名称", "stageSegmentTitle", ref stageSegmentTitle, 256);
            ImGui.SetNextItemWidth(150 * scale); ImGui.InputInt("分钟", ref stageSegmentMinutes);
            ImGui.SetNextItemWidth(150 * scale); ImGui.InputInt("秒", ref stageSegmentSeconds);
            if (ImGui.Button("插入下一项", new Vector2(110 * scale, 0)))
            {
                if (controller.Change(s =>
                {
                    if (stageSegmentMinutes < 0 || stageSegmentSeconds is < 0 or > 59) throw new InvalidOperationException("分钟不能为负，秒数应为 0 至 59。");
                    stageQueueSelectedId = StageOperations.InsertNextSegment(s, segmentShowId, stageSegmentKind, stageSegmentTitle, stageSegmentMinutes * 60d + stageSegmentSeconds).Id;
                }, "临时环节已设为下一项")) ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
            if (controller.StatusIsError) ImGui.TextWrapped(controller.StatusMessage);
            ImGui.EndPopup();
        }
        DrawTemplateModal();
        if (openDraftHistory) { ImGui.OpenPopup("手动旧稿##Stage"); openDraftHistory = false; }
        ImGui.SetNextWindowSize(new Vector2(500 * scale, 390 * scale), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("手动旧稿##Stage"))
        {
            if (announcementDraft.History.Count > 0)
            {
                historyIndex = Math.Clamp(historyIndex, 0, announcementDraft.History.Count - 1);
                var item = announcementDraft.History[historyIndex];
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##oldDraft", item.Label))
                {
                    for (var i = announcementDraft.History.Count - 1; i >= 0; i--)
                        if (ImGui.Selectable($"{announcementDraft.History[i].SavedAtUtc.ToLocalTime():HH:mm:ss} {announcementDraft.History[i].Label}##{i}", i == historyIndex)) historyIndex = i;
                    ImGui.EndCombo();
                }
                item = announcementDraft.History[historyIndex];
                ImGui.TextWrapped("原关联：" + item.Label);
                var old = item.Text;
                ImGui.InputTextMultiline("##oldText", ref old, 8192, new Vector2(-1, 190 * scale), ImGuiInputTextFlags.ReadOnly);
                if (ImGui.Button("复制这份旧稿")) { ImGui.SetClipboardText(item.Text); controller.SetStatus("已复制带有原关联记录的旧稿内容"); }
                ImGui.SameLine();
            }
            if (ImGui.Button("关闭")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawTemplateModal()
    {
        if (openTemplateSettings) { ImGui.OpenPopup("报幕模板##Stage"); openTemplateSettings = false; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(520 * UiKit.Scale, 470 * UiKit.Scale), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("报幕模板##Stage")) return;
        ImGui.BeginDisabled(controller.IsReadOnly || controller.AnnouncementError.Length > 0);
        ImGui.SetNextItemWidth(150 * UiKit.Scale);
        if (ImGui.BeginCombo("##templateKind", AnnouncementLabel(templateKind)))
        {
            foreach (var kind in Enum.GetValues<AnnouncementKind>())
                if (ImGui.Selectable(AnnouncementLabel(kind), kind == templateKind)) templateKind = kind;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Undo, "resetTemplate", "恢复此类默认模板")) templateDraft.Templates[templateKind] = AnnouncementTemplates.Defaults().Templates[templateKind];
        var text = templateDraft.Templates[templateKind];
        ImGui.SameLine(); ImGui.SetNextItemWidth(140 * UiKit.Scale);
        if (ImGui.BeginCombo("##insertTemplateField", "插入字段"))
        {
            foreach (var pair in new[] { ("演出名称", "show"), ("曲目名称", "title"), ("编曲信息", "arranger"), ("演奏人数", "performers"), ("点歌观众", "requesters") })
                if (ImGui.Selectable(pair.Item1)) text += "{" + pair.Item2 + "}";
            ImGui.EndCombo();
        }
        ImGui.InputTextMultiline("##templateText", ref text, 2048, new Vector2(-1, 170 * UiKit.Scale));
        templateDraft.Templates[templateKind] = text;
        ImGui.TextUnformatted("预览");
        var valid = true;
        try
        {
            AnnouncementTemplates.Validate(templateDraft);
            var show = controller.State.Setlists.FirstOrDefault(s => s.Id == stageShowId);
            if (ImGui.BeginChild("TemplatePreview", new Vector2(0, 100 * UiKit.Scale), false))
            {
                try
                {
                    if (show == null) UiKit.MutedText("暂无对应演出");
                    else ImGui.TextWrapped(AnnouncementTemplates.Render(templateDraft, templateKind, StageAnnouncementContext(show, templateKind)));
                }
                catch (InvalidOperationException) { UiKit.MutedText("暂无对应节目"); }
            }
            ImGui.EndChild();
        }
        catch (Exception ex) { valid = false; ImGui.TextWrapped(ex.Message); }
        ImGui.BeginDisabled(!valid);
        if (ImGui.Button("保存模板", new Vector2(100 * UiKit.Scale, 0)))
            if (controller.SaveAnnouncements(templateDraft)) ImGui.CloseCurrentPopup();
        ImGui.EndDisabled(); ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
        if (controller.StatusIsError) ImGui.TextWrapped(controller.StatusMessage);
        ImGui.EndPopup();
    }

    private static string AnnouncementLabel(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Opening => "开场", AnnouncementKind.NextSong => "下一曲", AnnouncementKind.Thanks => "点歌致谢",
        AnnouncementKind.Intermission => "中场休息", _ => "谢幕",
    };
}
