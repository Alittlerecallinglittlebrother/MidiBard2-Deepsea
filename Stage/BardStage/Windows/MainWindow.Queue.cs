using System.Numerics;
using BardStage.Core;
using BardStage.Core.Rooms;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private string queueSearch = "";
    private Guid? resolveRequestId;
    private bool openQueueSettings;
    private int queueGap;

    private void DrawAutomaticQueue()
    {
        var player = controller.QueueViewPlayback;
        var show = controller.QueueViewShow;
        var reception = controller.QueueState.RequestSettings.IsOpen;
        if (ImGui.Checkbox("接收点歌", ref reception)) controller.QueueCommand(RoomAction.Reception, value: reception);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Cog, "queueSettings", "点歌频道、口令与限制"))
        {
            settingsDraft = StageController.Clone(controller.QueueState.RequestSettings);
            queueGap = show?.GapSeconds ?? 3;
            openQueueSettings = true;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(player.ActiveEntryId != null || player.Loading);
        var mode = controller.QueueState.RequestSettings.PlaybackMode;
        if (ImGui.RadioButton("单人演奏##queueSolo", mode == QueuePlaybackMode.Solo))
            controller.QueueCommand(RoomAction.PlaybackMode, number: (int)QueuePlaybackMode.Solo);
        UiKit.RecordItem("queueSolo");
        ImGui.SameLine();
        if (ImGui.RadioButton("合奏主控##queueEnsemble", mode == QueuePlaybackMode.Ensemble))
            controller.QueueCommand(RoomAction.PlaybackMode, number: (int)QueuePlaybackMode.Ensemble);
        UiKit.RecordItem("queueEnsemble");
        ImGui.EndDisabled();
        var command = "口令：" + controller.QueueState.RequestSettings.Prefix + " 曲名";
        if (ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X - ImGui.GetItemRectMax().X
            > ImGui.CalcTextSize(command).X + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
        UiKit.MutedText(command);
        ImGui.Spacing();
        if (UiKit.Icon(FontAwesomeIcon.Play, "queuePlay", player.Paused ? "继续演奏" : "播放", !player.Playing && !player.Loading && (controller.Room?.HasRemoteControl == true || controller.QueuePlayer != null))) controller.QueueCommand(RoomAction.Start);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Pause, "queuePause", "暂停当前演奏", player.Playing)) controller.QueueCommand(RoomAction.Pause);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Stop, "queueStop", "停止当前演奏", player.ActiveEntryId != null || player.Loading)) controller.QueueCommand(RoomAction.Stop);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.StepForward, "queueNext", "跳过当前曲目", show?.Entries.Any(e => e.Status is EntryStatus.Queued or EntryStatus.InProgress) == true)) controller.QueueCommand(RoomAction.Skip);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Plus, "queueAdd", "从曲库加歌")) { queueSearch = ""; resolveRequestId = null; ImGui.OpenPopup("加歌##QueuePicker"); }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Sync, "queueImport", "导入 MidiBard 当前播放列表", controller.ImportPlayerLibrary != null && controller.Room?.IsRemote != true)) controller.ImportPlayerLibrary?.Invoke();
        ImGui.SameLine();
        var continuous = player.Enabled;
        if (ImGui.Checkbox("自动连播", ref continuous)) controller.QueueCommand(RoomAction.Continuous, value: continuous);
        UiKit.RecordItem("queueContinuous");
        UiKit.Tip("本曲结束后自动播放下一首");
        ImGui.TextWrapped(player.Status);
        if (controller.Room?.IsPresenter != true && (controller.SyncBlocked || controller.ChatWriteBlocked))
            if (ImGui.Button("重试保存")) { controller.RetryPlayback(); controller.RetryPendingChat(); }
        ImGui.Separator();

        var height = Math.Max(80, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale);
        if (ImGui.BeginChild("QueueContent", new Vector2(0, height), false))
        {
            show = controller.QueueViewShow;
            var active = controller.QueueState.Setlists.SelectMany(s => s.Entries).FirstOrDefault(e => e.Status == EntryStatus.InProgress);
            if (active != null && player?.ActiveEntryId != active.Id)
            {
                ImGui.TextColored(UiKit.Warning, "上次未结束：" + active.Title);
                if (ImGui.Button("重新排队##recover")) controller.QueueCommand(RoomAction.Requeue, entry: active.Id);
            }
            var entries = show?.Entries.Where(e => e.Status is EntryStatus.Queued or EntryStatus.InProgress).ToArray() ?? [];
            ImGui.TextUnformatted($"待演队列  {entries.Length} 首");
            if (entries.Length == 0) UiKit.MutedText("暂无待演曲目");
            else DrawQueueRows(show!, entries, false);

            var unresolved = controller.QueueState.Requests.Where(r => r.SetlistId == show?.Id && r.Status is RequestStatus.Pending or RequestStatus.Deferred)
                .OrderBy(r => r.ReceivedAtUtc).ToArray();
            if (unresolved.Length > 0)
            {
                ImGui.Spacing(); ImGui.Separator();
                ImGui.TextColored(UiKit.Warning, $"需要处理  {unresolved.Length} 条");
                foreach (var request in unresolved)
                {
                    ImGui.PushID(request.Id.ToString());
                    ImGui.TextWrapped(request.Query + "  /  " + RequesterLabel(request));
                    var matches = RequestOperations.FindMatches(controller.QueueState, request.Query);
                    UiKit.MutedText(matches.Count == 0 ? "曲库未匹配" : $"{matches.Count} 个候选版本");
                    if (ImGui.Button("选择歌曲"))
                    { queueSearch = request.Query; resolveRequestId = request.Id; ImGui.OpenPopup("加歌##QueuePicker"); }
                    UiKit.RecordItem("resolveRequest:" + request.Id);
                    ImGui.SameLine();
                    if (UiKit.Icon(FontAwesomeIcon.Times, "reject", "取消这条点歌"))
                        controller.QueueCommand(RoomAction.Reject, request: request.Id);
                    DrawQueuePicker();
                    ImGui.PopID();
                }
            }
            var history = show?.Entries.Where(e => e.Status is EntryStatus.Completed or EntryStatus.Skipped).Reverse().ToArray() ?? [];
            ImGui.Spacing();
            if (ImGui.CollapsingHeader($"已结束  {history.Length} 首###QueueHistoryHeader"))
            {
                if (UiKit.Icon(FontAwesomeIcon.Trash, "clearQueueHistory", "清理全部已结束节目", history.Length > 0)) ConfirmClearFinished(show!, true);
                ImGui.SameLine(); UiKit.MutedText("清理已结束");
                DrawQueueRows(show!, history, true);
            }
        }
        ImGui.EndChild();
        DrawQueuePicker();
        DrawQueueSettings();
    }

    private unsafe void DrawQueueRows(ShowSetlist show, SetlistEntry[] entries, bool history)
    {
        if (!ImGui.BeginTable(history ? "QueueHistory" : "QueueRows", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30 * UiKit.Scale);
        ImGui.TableSetupColumn("歌曲", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("观众", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 65 * UiKit.Scale);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 70 * UiKit.Scale);
        ImGui.TableHeadersRow();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            ImGui.PushID(entry.Id.ToString());
            ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted((i + 1).ToString());
            ImGui.TableNextColumn();
            var mutable = entry.Status == EntryStatus.Queued && controller.QueueViewPlayback.ActiveEntryId != entry.Id;
            var titleSize = ImGui.CalcTextSize(entry.Title, false, ImGui.GetContentRegionAvail().X);
            var rowStart = ImGui.GetCursorScreenPos();
            ImGui.Selectable("##dragSong", false, ImGuiSelectableFlags.None, new Vector2(0, Math.Max(ImGui.GetTextLineHeight(), titleSize.Y)));
            UiKit.RecordItem("queueRow:" + entry.Id);
            var rowEnd = ImGui.GetItemRectMax();
            if (!history && mutable && controller.CanEditQueue)
            {
                UiKit.Tip("拖动调整演奏顺序");
                if (ImGui.BeginDragDropSource())
                {
                    ImGui.SetDragDropPayload("BARDSTAGE_QUEUE", entry.Id.ToByteArray());
                    ImGui.TextUnformatted(entry.Title);
                    ImGui.EndDragDropSource();
                }
                if (ImGui.BeginDragDropTarget())
                {
                    var after = ImGui.GetMousePos().Y > (rowStart.Y + rowEnd.Y) / 2;
                    var payload = ImGui.AcceptDragDropPayload("BARDSTAGE_QUEUE", ImGuiDragDropFlags.AcceptBeforeDelivery);
                    if (!payload.IsNull && payload.DataSize == sizeof(Guid))
                    {
                        var y = after ? rowEnd.Y : rowStart.Y;
                        ImGui.GetWindowDrawList().AddLine(new Vector2(rowStart.X, y), new Vector2(rowEnd.X, y), ImGui.GetColorU32(UiKit.Accent), 2);
                        if (payload.IsDelivery()) controller.QueueCommand(RoomAction.MoveTo, entry: *(Guid*)payload.Data, targetEntry: entry.Id, value: after);
                    }
                    ImGui.EndDragDropTarget();
                }
            }
            var nextPosition = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(rowStart); ImGui.TextWrapped(entry.Title); ImGui.SetCursorScreenPos(nextPosition);
            ImGui.TableNextColumn();
            var names = controller.QueueState.Requests.Where(r => r.SetlistEntryId == entry.Id).Select(RequesterLabel).ToArray();
            ImGui.TextWrapped(names.Length == 0 ? "手动加歌" : string.Join("、", names));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(show.LockedNextEntryId == entry.Id ? "下一首" : StatusLabel(entry.Status));
            ImGui.TableNextColumn();
            if (history)
            {
                if (UiKit.Icon(FontAwesomeIcon.Redo, "requeue", "重新排队")) controller.QueueCommand(RoomAction.Requeue, entry: entry.Id);
                ImGui.SameLine(0, 2 * UiKit.Scale);
                if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteFinished", "删除已结束节目")) ConfirmDeleteEntry(show, entry, true);
            }
            else
            {
                if (UiKit.Icon(FontAwesomeIcon.StepForward, "playNext", "设为下一首", mutable)) controller.QueueCommand(RoomAction.PlayNext, entry: entry.Id);
                ImGui.SameLine(0, 2 * UiKit.Scale);
                if (UiKit.Icon(FontAwesomeIcon.Times, "cancel", "取消排队", mutable))
                    controller.QueueCommand(RoomAction.Remove, entry: entry.Id);
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawQueuePicker()
    {
        var size = new Vector2(Math.Min(550 * UiKit.Scale, ImGui.GetIO().DisplaySize.X - 32), Math.Min(420 * UiKit.Scale, ImGui.GetIO().DisplaySize.Y - 50));
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (!ImGui.BeginPopup("加歌##QueuePicker")) return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##queueSearch", "搜索曲名或别名", ref queueSearch, 256);
        var songs = string.IsNullOrWhiteSpace(queueSearch) ? controller.QueueState.Songs.ToArray() : RequestOperations.FindMatches(controller.QueueState, queueSearch).ToArray();
        if (ImGui.BeginChild("QueueSongPicker", new Vector2(0, Math.Max(80, size.Y - ImGui.GetFrameHeightWithSpacing() - 30 * UiKit.Scale)), false))
        {
            if (songs.Length == 0) UiKit.MutedText("没有匹配的曲目");
            foreach (var song in songs)
            {
                ImGui.PushID(song.Id.ToString());
                if (ImGui.Selectable(song.Title + "##song"))
                {
                    if (resolveRequestId is { } requestId)
                        controller.QueueCommand(RoomAction.Resolve, request: requestId, song: song.Id);
                    else controller.QueueCommand(RoomAction.Add, song: song.Id);
                    if (!controller.StatusIsError) ImGui.CloseCurrentPopup();
                }
                UiKit.Tip(song.FilePath);
                ImGui.TextWrapped(VersionLabel(song));
                ImGui.Separator(); ImGui.PopID();
            }
        }
        ImGui.EndChild(); ImGui.EndPopup();
    }

    private void DrawQueueSettings()
    {
        if (openQueueSettings) { ImGui.OpenPopup("点歌设置##Queue"); openQueueSettings = false; }
        var size = new Vector2(Math.Min(480 * UiKit.Scale, ImGui.GetIO().DisplaySize.X - 32), Math.Min(430 * UiKit.Scale, ImGui.GetIO().DisplaySize.Y - 50));
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (!ImGui.BeginPopup("点歌设置##Queue")) return;
        ImGui.TextUnformatted("接收频道");
        foreach (var channel in new[] { RequestChannel.Say, RequestChannel.Tell, RequestChannel.Party, RequestChannel.Shout, RequestChannel.Yell })
        {
            var enabled = settingsDraft.Channels.Contains(channel);
            if (ImGui.Checkbox(ChannelLabel(channel), ref enabled))
            { if (enabled) settingsDraft.Channels.Add(channel); else settingsDraft.Channels.Remove(channel); }
            if (channel != RequestChannel.Yell) ImGui.SameLine();
        }
        var prefix = settingsDraft.Prefix;
        Field("点歌口令", "queuePrefix", ref prefix, 32); settingsDraft.Prefix = prefix;
        var limit = settingsDraft.MaxOutstandingPerPerson;
        var cooldown = settingsDraft.DuplicateCooldownSeconds;
        var capacity = settingsDraft.MaxQueueSize;
        ImGui.SetNextItemWidth(110 * UiKit.Scale); ImGui.InputInt("每人待演上限", ref limit);
        ImGui.SetNextItemWidth(110 * UiKit.Scale); ImGui.InputInt("重复点歌间隔（秒）", ref cooldown);
        ImGui.SetNextItemWidth(110 * UiKit.Scale); ImGui.InputInt("队列上限", ref capacity);
        ImGui.SetNextItemWidth(110 * UiKit.Scale); ImGui.InputInt("歌曲间隔（秒）", ref queueGap);
        settingsDraft.MaxOutstandingPerPerson = limit; settingsDraft.DuplicateCooldownSeconds = cooldown; settingsDraft.MaxQueueSize = capacity;
        if (ImGui.Button("保存设置"))
            if (controller.QueueCommand(RoomAction.Settings, settings: StageController.Clone(settingsDraft), number: queueGap)) ImGui.CloseCurrentPopup();
        if (controller.StatusIsError) ImGui.TextWrapped(controller.StatusMessage);
        ImGui.EndPopup();
    }
}
