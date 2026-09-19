using System.Numerics;
using BardStage.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private Guid? preflightShowId, sessionSelectedId;
    private int availablePerformers;

    private void DrawStageSync(ShowSetlist show)
    {
        ImGui.Spacing(); ImGui.Separator();
        var enabled = controller.SyncEnabled && controller.SyncShowId == show.Id;
        ImGui.BeginDisabled(!controller.SyncAvailable || controller.IsReadOnly);
        if (ImGui.Checkbox("MidiBard 自动状态同步", ref enabled)) controller.ConfigureSync(show.Id, enabled);
        ImGui.EndDisabled();
        if (controller.SyncAvailable) ImGui.TextWrapped(controller.SyncStatus);
        else UiKit.MutedText("播放接入未就绪");
        if (controller.LastPlaybackSignal is { } signal)
            ImGui.TextWrapped($"{PlaybackLabel(signal.Kind)} · {Path.GetFileName(signal.FilePath)}");
        if (controller.SyncBlocked && ImGui.Button("重试保存同步记录")) controller.RetryPlayback();
    }

    private void DrawPreflight()
    {
        var state = controller.State;
        if (!state.Setlists.Any(s => s.Id == preflightShowId)) preflightShowId = stageShowId ?? state.SelectedSetlistId;
        ImGui.SetNextItemWidth(240 * UiKit.Scale);
        if (ImGui.BeginCombo("##preflightShow", state.Setlists.FirstOrDefault(s => s.Id == preflightShowId)?.Name ?? "选择演出"))
        {
            foreach (var show in state.Setlists)
                if (ImGui.Selectable(show.Name + "##" + show.Id, show.Id == preflightShowId)) preflightShowId = show.Id;
            ImGui.EndCombo();
        }
        ImGui.SameLine(); ImGui.SetNextItemWidth(90 * UiKit.Scale);
        if (ImGui.InputInt("到场人数", ref availablePerformers)) availablePerformers = Math.Clamp(availablePerformers, 0, 8);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.ClipboardCheck, "checkPreflight", "检查本场 MIDI 和人数", preflightShowId.HasValue && !controller.IsChecking))
            controller.CheckPreflight(preflightShowId!.Value, availablePerformers);
        if (controller.IsChecking)
        {
            ImGui.SameLine(); if (UiKit.Icon(FontAwesomeIcon.Stop, "cancelPreflight", "取消检查")) controller.CancelPreflight();
            UiKit.MutedText("正在检查文件...");
        }
        if (controller.PreflightError.Length > 0) ImGui.TextWrapped(controller.PreflightError);
        var report = controller.Preflight;
        if (report == null || report.ShowId != preflightShowId) { UiKit.MutedText("尚未检查"); return; }
        ImGui.TextWrapped($"检查时间 {report.CheckedAtUtc.ToLocalTime():HH:mm:ss} · {report.CheckedFiles} 个文件 · {report.Issues.Count(i => i.Severity == PreflightSeverity.Error)} 个文件问题");
        if (controller.IsPreflightStale(report.ShowId, availablePerformers)) ImGui.TextColored(UiKit.Warning, "节目单或人数已变更，检查结果已过期");
        ImGui.Separator();
        if (report.Issues.Count == 0) ImGui.TextColored(UiKit.Accent, "本次检查通过");
        if (ImGui.BeginChild("PreflightResults", new Vector2(0, Math.Max(120, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale)), false))
        {
            for (var i = 0; i < report.Issues.Count; i++)
            {
                var issue = report.Issues[i]; ImGui.PushID(i);
                var show = state.Setlists.FirstOrDefault(s => s.Id == report.ShowId);
                var entry = show?.Entries.FirstOrDefault(e => e.Id == issue.EntryId);
                if (UiKit.Icon(FontAwesomeIcon.ArrowRight, "locateIssue", "定位节目", entry != null))
                {
                    controller.Change(s => s.SelectedSetlistId = show!.Id);
                    previousShowId = show!.Id; SelectEntry(entry!); selectSetlistTab = true;
                }
                var song = state.Songs.FirstOrDefault(s => s.Id == entry?.SongId);
                if (song != null && Directory.Exists(Path.GetDirectoryName(song.FilePath)))
                {
                    ImGui.SameLine();
                    if (UiKit.Icon(FontAwesomeIcon.FolderOpen, "revealIssue", "打开文件目录")) controller.OpenPath(Path.GetDirectoryName(song.FilePath)!);
                }
                ImGui.SameLine();
                ImGui.TextWrapped(issue.Title + " · " + issue.Message);
                ImGui.Separator(); ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private void DrawSessions()
    {
        var currentShow = controller.State.Setlists.FirstOrDefault(s => s.Id == stageShowId) ?? controller.CurrentSetlist;
        if (currentShow != null)
        {
            ImGui.TextWrapped("当前演出：" + currentShow.Name);
            var open = SessionOperations.Open(controller.State, currentShow.Id);
            ImGui.BeginDisabled(controller.IsReadOnly || controller.IsBusy);
            if (open == null)
            {
                if (ImGui.Button("开始记录本场")) controller.Change(s => sessionSelectedId = SessionOperations.Begin(s, currentShow.Id, DateTimeOffset.UtcNow).Id, "已建立本场记录");
            }
            else
            {
                if (ImGui.Button("查看本场记录")) sessionSelectedId = open.Id;
                ImGui.SameLine();
                if (ImGui.Button("结束并归档")) Confirm("结束本场并归档？未演节目及未处理点歌会保留在记录中。", () =>
                {
                    if (controller.Change(s => SessionOperations.Archive(s, currentShow.Id, DateTimeOffset.UtcNow), "本场已归档")) sessionSelectedId = open.Id;
                });
            }
            ImGui.EndDisabled();
        }
        ImGui.Separator();
        var sessions = controller.State.Sessions.OrderByDescending(s => s.StartedAtUtc).ToArray();
        if (!sessions.Any(s => s.Id == sessionSelectedId))
            sessionSelectedId = sessions.FirstOrDefault(s => s.SetlistId == currentShow?.Id && !s.EndedAtUtc.HasValue)?.Id ?? sessions.FirstOrDefault()?.Id;
        var selected = sessions.FirstOrDefault(s => s.Id == sessionSelectedId);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##sessionPicker", selected == null ? "尚无演出记录" : SessionLabel(selected)))
        {
            foreach (var session in sessions)
                if (ImGui.Selectable(SessionLabel(session) + "##" + session.Id, session.Id == sessionSelectedId)) sessionSelectedId = session.Id;
            ImGui.EndCombo();
        }
        selected = sessions.FirstOrDefault(s => s.Id == sessionSelectedId);
        if (selected == null) return;
        var now = DateTimeOffset.UtcNow;
        ImGui.TextWrapped($"完成 {selected.Attempts.Count(a => a.Outcome == AttemptOutcome.Completed)} · 中断 {selected.Attempts.Count(a => a.Outcome == AttemptOutcome.Interrupted)} · 跳过 {selected.Attempts.Count(a => a.Outcome == AttemptOutcome.Skipped)} · 演奏用时 {UiKit.Duration(selected.Attempts.Sum(a => SessionOperations.ActualSeconds(a, now)))}");
        if (selected.ImportedLegacy) UiKit.MutedText("旧版迁入记录，仅包含原数据中保留的时间");
        if (selected.Attempts.Any(a => a.Source == RecordSource.Legacy && (!a.StartedAtUtc.HasValue || !a.EndedAtUtc.HasValue) && a.Outcome != AttemptOutcome.InProgress))
            UiKit.MutedText("部分旧记录时长未知，未计入用时合计");
        if (UiKit.Icon(FontAwesomeIcon.FileExport, "exportSession", "导出演出记录（含未归档）")) ImGui.OpenPopup("SessionExport");
        if (ImGui.BeginPopup("SessionExport"))
        {
            var id = selected.Id;
            if (ImGui.MenuItem("JSON")) controller.ExportSessionToFolder(id, false);
            if (ImGui.MenuItem("CSV")) controller.ExportSessionToFolder(id, true);
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Copy, "copySession", "复制节目安排用于下一场", selected.EndedAtUtc.HasValue && !controller.IsReadOnly && !controller.IsBusy))
            controller.Change(s => { var show = SessionOperations.CopyForNext(s, selected.Id); stageShowId = show.Id; s.SelectedSetlistId = show.Id; selectSetlistTab = true; }, "已复制为下一场");
        ImGui.SameLine();
        var sessionId = selected.Id;
        if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteSession", "删除整场演出记录", selected.Attempts.All(a => a.Outcome != AttemptOutcome.InProgress)))
            Confirm("删除整场演出记录及明细？节目单和曲库保留。", () => controller.DeleteSession(sessionId));
        ImGui.SameLine();
        var finishedAttempts = selected.Attempts.Where(a => a.Outcome != AttemptOutcome.InProgress).Select(a => a.Id).ToArray();
        if (UiKit.Icon(FontAwesomeIcon.Eraser, "clearAttempts", "清理本场已结束的演出明细", finishedAttempts.Length > 0))
            Confirm($"删除本场 {finishedAttempts.Length} 条已结束演出明细？正在演奏的记录和节目单保留。", () => controller.DeleteAttempts(sessionId, finishedAttempts));
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.FolderOpen, "openSessionExports", "打开演出记录导出目录", Directory.Exists(controller.SessionExportDirectory)))
            controller.OpenPath(controller.SessionExportDirectory);
        if (ImGui.BeginChild("SessionDetails", new Vector2(0, Math.Max(120, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale)), false))
        {
            if (ImGui.BeginTable("AttemptTable", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("节目", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("结果", ImGuiTableColumnFlags.WidthFixed, 62 * UiKit.Scale);
                ImGui.TableSetupColumn("计划", ImGuiTableColumnFlags.WidthFixed, 65 * UiKit.Scale);
                ImGui.TableSetupColumn("记录用时", ImGuiTableColumnFlags.WidthFixed, 75 * UiKit.Scale);
                ImGui.TableSetupColumn("来源", ImGuiTableColumnFlags.WidthFixed, 80 * UiKit.Scale);
                ImGui.TableSetupColumn("删除", ImGuiTableColumnFlags.WidthFixed, 34 * UiKit.Scale);
                ImGui.TableHeadersRow();
                foreach (var attempt in selected.Attempts)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextWrapped(attempt.Program.Title);
                    UiKit.Tip($"开始：{attempt.StartedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n结束：{attempt.EndedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n点歌：{string.Join("、", attempt.Program.Requesters)}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(OutcomeLabel(attempt.Outcome));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(UiKit.Duration(attempt.Program.PlannedSeconds));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(attempt.Source == RecordSource.Legacy
                        && attempt.Outcome != AttemptOutcome.InProgress && (!attempt.StartedAtUtc.HasValue || !attempt.EndedAtUtc.HasValue)
                        ? "未知" : UiKit.Duration(SessionOperations.ActualSeconds(attempt, now)));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(attempt.Source == RecordSource.MidiBard ? "MidiBard" : attempt.Source == RecordSource.Legacy ? "旧记录" : "手动");
                    ImGui.TableNextColumn(); ImGui.PushID(attempt.Id.ToString());
                    if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteAttempt", "删除这条演出明细", attempt.Outcome != AttemptOutcome.InProgress))
                    {
                        var attemptId = attempt.Id;
                        Confirm($"删除《{attempt.Program.Title}》的这次演出明细？节目单和曲库保留。", () => controller.DeleteAttempts(sessionId, [attemptId]));
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            if (selected.EndedAtUtc.HasValue && ImGui.CollapsingHeader($"未处理点歌（{selected.UnresolvedRequests.Count}）"))
                foreach (var request in selected.UnresolvedRequests) ImGui.TextWrapped(RequesterLabel(request) + " · " + request.Query);
            if (ImGui.CollapsingHeader("节目安排"))
                foreach (var program in selected.EndedAtUtc.HasValue ? selected.FinalPlan : selected.InitialPlan)
                    ImGui.TextWrapped(program.Title + " · " + UiKit.Duration(program.PlannedSeconds)
                        + (selected.Attempts.Any(a => a.Program.EntryId == program.EntryId) ? "" : " · 未演"));
            if (ImGui.CollapsingHeader("操作记录"))
                foreach (var item in selected.Events) ImGui.TextWrapped($"{item.AtUtc.ToLocalTime():HH:mm:ss} · {item.Action} · {selected.InitialPlan.Concat(selected.FinalPlan).FirstOrDefault(p => p.EntryId == item.EntryId)?.Title}");
        }
        ImGui.EndChild();
    }

    private static string SessionLabel(ShowSession session) => $"{session.StartedAtUtc.ToLocalTime():MM-dd HH:mm} · {session.ShowName} · {(session.EndedAtUtc.HasValue ? "已归档" : "未归档")}";
    private static string OutcomeLabel(AttemptOutcome outcome) => outcome switch { AttemptOutcome.Completed => "完成", AttemptOutcome.Interrupted => "中断", AttemptOutcome.Skipped => "跳过", _ => "进行中" };
    private static string PlaybackLabel(PlaybackSignalKind kind) => kind switch { PlaybackSignalKind.Loaded => "已载入", PlaybackSignalKind.Started => "演奏中", PlaybackSignalKind.Resumed => "已继续", PlaybackSignalKind.Paused => "已暂停", PlaybackSignalKind.Finished => "自然结束", _ => "已停止" };
}
