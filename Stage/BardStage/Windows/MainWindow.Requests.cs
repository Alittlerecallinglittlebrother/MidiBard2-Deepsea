using System.Numerics;
using BardStage.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private Guid? focusedRequestId;
    private Guid? requestShowId;
    private Guid? requestSongId;
    private Guid? mergeEntryId;
    private readonly HashSet<Guid> checkedRequests = [];
    private string requestSearch = "", matchSearch = "", rejectReason = "";
    private int requestFilter;
    private bool allRequestShows;
    private int arrangeMode, insertPosition = 1;
    private bool openManualRequest, openRequestSettings;
    private string manualName = "", manualWorld = "", manualQuery = "";
    private Guid? manualShowId;
    private RequestSettings settingsDraft = new();

    private void DrawRequests()
    {
        DrawStageReturn();
        var state = controller.State;
        var scale = UiKit.Scale;
        if (requestShowId != state.SelectedSetlistId)
        {
            requestShowId = state.SelectedSetlistId;
            focusedRequestId = null;
            checkedRequests.Clear();
        }
        checkedRequests.RemoveWhere(id => !state.Requests.Any(r => r.Id == id && r.Status is RequestStatus.Pending or RequestStatus.Deferred));
        ImGui.SetNextItemWidth(230 * scale);
        if (ImGui.BeginCombo("##requestShow", controller.CurrentSetlist?.Name ?? "选择节目单"))
        {
            foreach (var show in state.Setlists.ToArray())
                if (ImGui.Selectable(show.Name + "##" + show.Id, show.Id == state.SelectedSetlistId))
                    controller.Change(s => s.SelectedSetlistId = show.Id);
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Plus, "manualRequest", "登记点歌", controller.CurrentSetlist != null))
        {
            manualShowId = controller.CurrentSetlist!.Id;
            manualName = ""; manualWorld = ""; manualQuery = "";
            openManualRequest = true;
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Cog, "requestSettings", "点歌接收设置"))
        {
            settingsDraft = StageController.Clone(state.RequestSettings);
            openRequestSettings = true;
        }
        ImGui.SameLine();
        ImGui.Checkbox("全部演出", ref allRequestShows);
        ImGui.SameLine();
        UiKit.MutedText($"待处理 {state.Requests.Count(r => (allRequestShows || r.SetlistId == state.SelectedSetlistId) && r.Status == RequestStatus.Pending)} 条");

        var settings = state.RequestSettings;
        var target = state.Setlists.FirstOrDefault(s => s.Id == settings.TargetSetlistId);
        var isOpen = settings.IsOpen;
        ImGui.BeginDisabled(target == null);
        if (ImGui.Checkbox("开放聊天点歌", ref isOpen))
            controller.Change(s => s.RequestSettings.IsOpen = isOpen, isOpen ? "已开放聊天点歌" : "已暂停聊天点歌");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextWrapped("接收演出：" + (target?.Name ?? "未设置"));
        UiKit.Tip("点歌前缀：" + settings.Prefix + "\n频道：" + string.Join("、", settings.Channels.Select(ChannelLabel)));
        if (controller.PendingChatCount > 0)
        {
            ImGui.TextColored(UiKit.Warning, $"待保存 {controller.PendingChatCount} 条点歌");
            if (controller.ChatWriteBlocked)
            {
                ImGui.SameLine();
                if (UiKit.Icon(FontAwesomeIcon.Redo, "retryChat", "重试保存点歌")) controller.RetryPendingChat();
            }
        }
        if (controller.DroppedChatCount > 0) ImGui.TextColored(UiKit.Warning, $"缓冲超限：{controller.DroppedChatCount} 条未接收");
        ImGui.Separator();
        ImGui.SetNextItemWidth(120 * scale);
        var filters = new[] { "待处理与暂缓", "全部状态", "已安排", "进行中", "已完成", "已跳过", "已拒绝", "已取消" };
        if (ImGui.BeginCombo("##requestFilter", filters[requestFilter]))
        {
            for (var i = 0; i < filters.Length; i++)
                if (ImGui.Selectable(filters[i], i == requestFilter)) requestFilter = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * scale);
        ImGui.InputTextWithHint("##requestSearch", "搜索曲名或观众", ref requestSearch, 256);
        ImGui.SameLine();
        UiKit.MutedText($"已选 {checkedRequests.Count} 条");
        ImGui.SameLine();
        var finishedRequests = VisibleRequests().Where(r => CleanupOperations.IsFinishedRequest(state, r)).Select(r => r.Id).ToArray();
        if (UiKit.Icon(FontAwesomeIcon.Trash, "clearRequestHistory", "清理当前筛选下的已完成、已跳过、已拒绝和已取消点歌", finishedRequests.Length > 0))
            Confirm($"删除当前筛选下 {finishedRequests.Length} 条已处理点歌记录？节目单和演出记录保留。", () => controller.DeleteRequests(finishedRequests));
        var height = Math.Max(120 * scale, ImGui.GetContentRegionAvail().Y - 65 * scale);
        if (ImGui.GetContentRegionAvail().X < 900 * scale)
        {
            if (ImGui.BeginTabBar("CompactRequestViews"))
            {
                if (ImGui.BeginTabItem("点歌列表"))
                {
                    if (ImGui.BeginChild("CompactRequestList", new Vector2(0, Math.Max(100, height - ImGui.GetFrameHeightWithSpacing())), false)) DrawRequestTable();
                    ImGui.EndChild(); ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("点歌处理"))
                {
                    if (ImGui.BeginChild("CompactRequestInspector", new Vector2(0, Math.Max(100, height - ImGui.GetFrameHeightWithSpacing())), false)) DrawRequestInspector();
                    ImGui.EndChild(); ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            return;
        }
        if (ImGui.BeginTable("RequestsLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("requests", ImGuiTableColumnFlags.WidthStretch, 0.58f);
            ImGui.TableSetupColumn("decision", ImGuiTableColumnFlags.WidthStretch, 0.42f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("RequestList", new Vector2(0, height), false)) DrawRequestTable();
            ImGui.EndChild(); ImGui.TableNextColumn();
            if (ImGui.BeginChild("RequestInspector", new Vector2(0, height), false)) DrawRequestInspector();
            ImGui.EndChild(); ImGui.EndTable();
        }
    }

    private void DrawRequestTable()
    {
        var state = controller.State;
        var rows = VisibleRequests();
        if (rows.Length == 0) { UiKit.MutedText("暂无点歌记录"); return; }
        if (!ImGui.BeginTable("Requests", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable, new Vector2(0, -1))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##checked", ImGuiTableColumnFlags.WidthFixed, 24 * UiKit.Scale);
        ImGui.TableSetupColumn("点歌曲目", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("观众", ImGuiTableColumnFlags.WidthFixed, 94 * UiKit.Scale);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 62 * UiKit.Scale);
        ImGui.TableSetupColumn("收到", ImGuiTableColumnFlags.WidthFixed, 49 * UiKit.Scale);
        ImGui.TableSetupColumn("删除", ImGuiTableColumnFlags.WidthFixed, 34 * UiKit.Scale);
        ImGui.TableHeadersRow();
        foreach (var request in rows)
        {
            ImGui.PushID(request.Id.ToString());
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            var selected = checkedRequests.Contains(request.Id);
            ImGui.BeginDisabled(request.Status is not (RequestStatus.Pending or RequestStatus.Deferred));
            if (ImGui.Checkbox("##selected", ref selected))
            {
                if (selected) { checkedRequests.Add(request.Id); FocusRequest(request, false); }
                else checkedRequests.Remove(request.Id);
            }
            ImGui.EndDisabled(); ImGui.TableNextColumn();
            if (ImGui.Selectable(request.Query + "##focus", focusedRequestId == request.Id)) FocusRequest(request, true);
            UiKit.Tip(request.Query + "\n" + request.SetlistName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(request.RequesterName); UiKit.Tip(RequesterLabel(request));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(RequestOperations.DisplayStatus(state, request));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(request.ReceivedAtUtc.ToLocalTime().ToString("HH:mm"));
            ImGui.TableNextColumn();
            var active = request.SetlistEntryId.HasValue && (controller.QueuePlayer?.ActiveEntryId == request.SetlistEntryId
                || state.Setlists.SelectMany(s => s.Entries).Any(e => e.Id == request.SetlistEntryId && e.Status == EntryStatus.InProgress));
            if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteRequestRow", "删除点歌记录", !active)) ConfirmDeleteRequest(request);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private SongRequest[] VisibleRequests()
    {
        var query = requestSearch.Trim();
        return controller.State.Requests.Where(r => (allRequestShows || r.SetlistId == controller.State.SelectedSetlistId)
            && (query.Length == 0 || r.Query.Contains(query, StringComparison.OrdinalIgnoreCase) || RequesterLabel(r).Contains(query, StringComparison.OrdinalIgnoreCase))
            && MatchesRequestFilter(r)).OrderBy(r => r.ReceivedAtUtc).ToArray();
    }

    private bool MatchesRequestFilter(SongRequest request)
    {
        if (requestFilter == 0) return request.Status is RequestStatus.Pending or RequestStatus.Deferred;
        if (requestFilter == 1) return true;
        return RequestOperations.DisplayStatus(controller.State, request) == requestFilter switch
        {
            2 => "已安排", 3 => "进行中", 4 => "已完成", 5 => "已跳过", 6 => "已拒绝", _ => "已取消",
        };
    }

    private void FocusRequest(SongRequest request, bool replaceSelection)
    {
        if (replaceSelection)
        {
            checkedRequests.Clear();
            if (request.Status is RequestStatus.Pending or RequestStatus.Deferred) checkedRequests.Add(request.Id);
        }
        focusedRequestId = request.Id;
        requestSongId = null;
        mergeEntryId = null;
        matchSearch = request.Query;
        rejectReason = "";
        arrangeMode = 0;
        insertPosition = 1;
    }

    private void DrawRequestInspector()
    {
        var state = controller.State;
        var request = state.Requests.FirstOrDefault(r => r.Id == focusedRequestId);
        if (request == null) { UiKit.MutedText("未选择点歌"); return; }
        ImGui.TextWrapped(request.Query);
        UiKit.MutedText(RequestOperations.DisplayStatus(state, request));
        ImGui.TextWrapped(RequesterLabel(request));
        UiKit.MutedText($"{ChannelLabel(request.Channel)} · {request.ReceivedAtUtc.ToLocalTime():MM-dd HH:mm:ss}");
        ImGui.TextWrapped("所属演出：" + request.SetlistName);
        if (request.ResolutionNote.Length > 0) ImGui.TextWrapped(request.ResolutionNote);
        ImGui.Separator();
        var show = state.Setlists.FirstOrDefault(s => s.Id == request.SetlistId);
        if (request.Status == RequestStatus.Arranged)
        {
            var entry = show?.Entries.FirstOrDefault(e => e.Id == request.SetlistEntryId);
            if (entry != null)
            {
                ImGui.TextWrapped("已安排：" + entry.Title);
                var song = state.Songs.FirstOrDefault(s => s.Id == entry.SongId);
                if (song != null) ImGui.TextWrapped(VersionLabel(song));
                ImGui.TextWrapped("点歌观众：" + string.Join("、", state.Requests.Where(r => r.SetlistEntryId == entry.Id).Select(RequesterLabel)));
                if (ImGui.Button("查看节目"))
                {
                    controller.Change(s => s.SelectedSetlistId = show!.Id);
                    previousShowId = show!.Id;
                    gapSeconds = show.GapSeconds;
                    targetEnd = show.TargetEndUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
                    SelectEntry(entry); selectSetlistTab = true;
                }
            }
            return;
        }
        if (request.Status is RequestStatus.Rejected or RequestStatus.Cancelled)
        {
            if (UiKit.Icon(FontAwesomeIcon.Redo, "reopenRequest", "重新处理点歌", show != null))
                controller.Change(s => RequestOperations.Reopen(s, request.Id), "点歌已恢复为待处理");
            return;
        }
        var selection = state.Requests.Where(r => checkedRequests.Contains(r.Id)).ToArray();
        if (selection.Length > 0)
        {
            ImGui.TextWrapped($"本次处理 {selection.Length} 条：" + string.Join("、", selection.Select(r => r.Query + "（" + RequesterLabel(r) + "）")));
            if (selection.Any(r => r.SetlistId != request.SetlistId)) ImGui.TextColored(UiKit.Warning, "选中的点歌属于不同演出");
        }
        ImGui.TextUnformatted("曲库匹配");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##matchSearch", "曲名或别名", ref matchSearch, 256)) { requestSongId = null; mergeEntryId = null; }
        var candidates = RequestOperations.FindMatches(state, matchSearch);
        if (!requestSongId.HasValue && AutoQueueOperations.Match(state, matchSearch) is { } single) requestSongId = single.Id;
        if (ImGui.BeginChild("RequestCandidates", new Vector2(0, 130 * UiKit.Scale), true))
        {
            if (candidates.Count == 0) UiKit.MutedText("曲库中没有匹配版本");
            foreach (var song in candidates)
            {
                ImGui.PushID(song.Id.ToString());
                if (ImGui.Selectable(song.Title + "##match", requestSongId == song.Id)) { requestSongId = song.Id; mergeEntryId = null; }
                UiKit.Tip(VersionLabel(song) + "\n" + song.FilePath);
                ImGui.TextWrapped(VersionLabel(song));
                ImGui.Separator(); ImGui.PopID();
            }
        }
        ImGui.EndChild();
        var chosen = state.Songs.FirstOrDefault(s => s.Id == requestSongId);
        if (chosen != null)
        {
            ImGui.TextWrapped("确认版本：" + chosen.Title + " · " + VersionLabel(chosen));
            if (!File.Exists(chosen.FilePath)) ImGui.TextColored(UiKit.Warning, "该版本 MIDI 文件缺失");
        }
        ImGui.TextUnformatted("安排位置");
        ImGui.SetNextItemWidth(-1);
        var modes = new[] { "节目单末尾", "指定位置", "合并到已有节目" };
        if (ImGui.BeginCombo("##arrangeMode", modes[arrangeMode]))
        {
            for (var i = 0; i < modes.Length; i++)
                if (ImGui.Selectable(modes[i], arrangeMode == i)) { arrangeMode = i; mergeEntryId = null; }
            ImGui.EndCombo();
        }
        if (arrangeMode == 1)
        {
            ImGui.SetNextItemWidth(-1); ImGui.InputInt("##insertPosition", ref insertPosition);
            UiKit.Tip($"插入为第 1 至 {(show?.Entries.Count ?? 0) + 1} 项；下一项锁定保持不变");
        }
        if (arrangeMode == 2)
        {
            var compatible = show?.Entries.Where(e => e.SongId == requestSongId && e.Kind == EntryKind.Song && e.Status == EntryStatus.Queued).ToArray() ?? [];
            ImGui.SetNextItemWidth(-1);
            var destination = compatible.FirstOrDefault(e => e.Id == mergeEntryId);
            if (ImGui.BeginCombo("##mergeDestination", destination?.Title ?? "选择同版本待演节目"))
            {
                foreach (var entry in compatible)
                    if (ImGui.Selectable($"{show!.Entries.IndexOf(entry) + 1}. {entry.Title}##{entry.Id}", entry.Id == mergeEntryId)) mergeEntryId = entry.Id;
                ImGui.EndCombo();
            }
        }
        var canArrange = chosen != null && selection.Length > 0 && selection.All(r => r.SetlistId == request.SetlistId)
            && show != null && (arrangeMode != 2 || mergeEntryId.HasValue);
        ImGui.BeginDisabled(!canArrange);
        if (ImGui.Button($"确认安排（{selection.Length}）", new Vector2(-1, 0)))
        {
            var ids = selection.Select(r => r.Id).ToArray();
            if (controller.Change(s => RequestOperations.Arrange(s, ids, chosen!.Id,
                arrangeMode == 1 ? insertPosition - 1 : null, arrangeMode == 2 ? mergeEntryId : null), "点歌已关联到节目单"))
                checkedRequests.Clear();
        }
        ImGui.EndDisabled(); ImGui.Separator();
        Field("处理备注", "rejectReason", ref rejectReason, 512);
        ImGui.BeginDisabled(selection.Length == 0);
        if (UiKit.Icon(FontAwesomeIcon.Clock, "deferRequest", "暂缓选中的点歌"))
            ApplyRequestDecision(selection, (s, id) => RequestOperations.Defer(s, id), "点歌已暂缓");
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Times, "rejectRequest", "拒绝选中的点歌"))
            ApplyRequestDecision(selection, (s, id) => RequestOperations.Reject(s, id, rejectReason), "点歌已拒绝");
        ImGui.EndDisabled();
    }

    private void ApplyRequestDecision(SongRequest[] selection, Action<CatalogState, Guid> action, string message)
    {
        var ids = selection.Select(r => r.Id).ToArray();
        if (controller.Change(state => { foreach (var id in ids) action(state, id); }, message)) checkedRequests.Clear();
    }

    private void DrawRequestModals()
    {
        var scale = UiKit.Scale;
        if (openManualRequest) { ImGui.OpenPopup("登记点歌##BardStage"); openManualRequest = false; }
        ImGui.SetNextWindowSize(new Vector2(440 * scale, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("登记点歌##BardStage", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.BeginDisabled(controller.IsBusy || controller.IsReadOnly);
            ImGui.TextWrapped("所属演出：" + (controller.State.Setlists.FirstOrDefault(s => s.Id == manualShowId)?.Name ?? "已删除"));
            Field("观众姓名", "manualName", ref manualName, 128);
            Field("服务器（可留空）", "manualWorld", ref manualWorld, 128);
            Field("点歌曲名", "manualQuery", ref manualQuery, 256);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(manualName) || string.IsNullOrWhiteSpace(manualQuery) || manualShowId == null);
            if (ImGui.Button("登记", new Vector2(90 * scale, 0)))
            {
                Guid? created = null;
                if (controller.Change(s => created = RequestOperations.Submit(s, manualShowId!.Value, manualName, manualWorld, manualQuery, RequestChannel.Manual, DateTimeOffset.UtcNow).Id, "点歌已登记"))
                {
                    var request = controller.State.Requests.Single(r => r.Id == created);
                    requestFilter = 0; FocusRequest(request, true); ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndDisabled(); ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(90 * scale, 0))) ImGui.CloseCurrentPopup();
            if (controller.StatusIsError) ImGui.TextWrapped(controller.StatusMessage);
            ImGui.EndPopup();
        }
        if (openRequestSettings) { ImGui.OpenPopup("点歌设置##BardStage"); openRequestSettings = false; }
        ImGui.SetNextWindowSize(new Vector2(450 * scale, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("点歌设置##BardStage", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.BeginDisabled(controller.IsBusy || controller.IsReadOnly);
            ImGui.TextUnformatted("接收演出"); ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##receiveShow", controller.State.Setlists.FirstOrDefault(s => s.Id == settingsDraft.TargetSetlistId)?.Name ?? "未设置"))
            {
                if (ImGui.Selectable("未设置", settingsDraft.TargetSetlistId == null)) { settingsDraft.TargetSetlistId = null; settingsDraft.IsOpen = false; }
                foreach (var show in controller.State.Setlists)
                    if (ImGui.Selectable(show.Name + "##" + show.Id, settingsDraft.TargetSetlistId == show.Id)) settingsDraft.TargetSetlistId = show.Id;
                ImGui.EndCombo();
            }
            ImGui.TextUnformatted("接收频道");
            var channels = new[] { RequestChannel.Say, RequestChannel.Tell, RequestChannel.Party, RequestChannel.Shout, RequestChannel.Yell };
            for (var i = 0; i < channels.Length; i++)
            {
                var channel = channels[i]; var enabled = settingsDraft.Channels.Contains(channel);
                if (i > 0) ImGui.SameLine();
                if (ImGui.Checkbox(ChannelLabel(channel), ref enabled))
                {
                    if (enabled) settingsDraft.Channels.Add(channel); else settingsDraft.Channels.Remove(channel);
                }
            }
            var prefix = settingsDraft.Prefix;
            Field("点歌前缀", "requestPrefix", ref prefix, 32); settingsDraft.Prefix = prefix;
            var perPerson = settingsDraft.MaxOutstandingPerPerson;
            var cooldown = settingsDraft.DuplicateCooldownSeconds;
            var capacity = settingsDraft.MaxQueueSize;
            ImGui.TextUnformatted("每人未完成点歌上限"); ImGui.SetNextItemWidth(-1); ImGui.InputInt("##requestPerPerson", ref perPerson);
            ImGui.TextUnformatted("同曲重复冷却（秒）"); ImGui.SetNextItemWidth(-1); ImGui.InputInt("##requestCooldown", ref cooldown);
            ImGui.TextUnformatted("每场未完成点歌上限"); ImGui.SetNextItemWidth(-1); ImGui.InputInt("##requestCapacity", ref capacity);
            settingsDraft.MaxOutstandingPerPerson = perPerson; settingsDraft.DuplicateCooldownSeconds = cooldown; settingsDraft.MaxQueueSize = capacity;
            if (ImGui.Button("保存设置", new Vector2(100 * scale, 0)))
                if (controller.Change(s =>
                {
                    s.RequestSettings = StageController.Clone(settingsDraft);
                    s.RequestSettings.Prefix = s.RequestSettings.Prefix.Trim();
                }, "点歌设置已保存")) ImGui.CloseCurrentPopup();
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(90 * scale, 0))) ImGui.CloseCurrentPopup();
            if (controller.StatusIsError) ImGui.TextWrapped(controller.StatusMessage);
            ImGui.EndPopup();
        }
    }

    private static string RequesterLabel(SongRequest request) => request.RequesterName + (request.RequesterWorld.Length > 0 ? "@" + request.RequesterWorld : "");
    private static string VersionLabel(SongEntry song) => $"{(song.PerformerCount == 0 ? "人数未填" : song.PerformerCount + " 人")} · {UiKit.Duration(song.DurationSeconds)} · {(song.Arranger.Length == 0 ? "版本未填写" : song.Arranger)}";
    private static string ChannelLabel(RequestChannel channel) => channel switch
    {
        RequestChannel.Say => "说话", RequestChannel.Tell => "悄悄话", RequestChannel.Party => "小队",
        RequestChannel.Shout => "喊话", RequestChannel.Yell => "呼喊", _ => "手动",
    };
}
