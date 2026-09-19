using System.Globalization;
using System.Numerics;
using BardStage.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

namespace BardStage.Windows;

public sealed partial class MainWindow : Window, IDisposable
{
    private const string PluginNotice = "本插件基于midibard2开源代码魔改制作，完全免费，旨在打造一个更低门槛、更有活力的游戏演奏环境";
    private readonly StageController controller;
    private readonly FileDialogManager dialogs = new();
    private string search = "";
    private int performerFilter = -1;
    private Guid? selectedSongId;
    private Guid? selectedEntryId;
    private Guid? previousShowId;
    private string songTitle = "", songAliases = "", songArranger = "", songNotes = "";
    private int songPerformers;
    private string entryTitle = "", entryNotes = "";
    private int entryMinutes, entrySeconds;
    private float entrySpeed = 1;
    private string createName = "";
    private string createMode = "";
    private bool openCreate;
    private bool openConfirm;
    private string confirmText = "";
    private Action? confirmAction;
    private bool selectSetlistTab;
    private bool selectRequestTab;
    private bool selectStageTab;
    private string targetEnd = "";
    private int gapSeconds;
    private string pathDraft = "";

    public MainWindow(StageController controller)
        : base("midibard2-深海回响特供版 · 自动点歌##BardStage", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.controller = controller;
        Size = new Vector2(1100, 740);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 540), MaximumSize = new Vector2(2400, 1600) };
    }

    public void DrawDialogs() => dialogs.Draw();
    public void Dispose() => dialogs.Reset();

    public override void Draw()
    {
        var scale = UiKit.Scale;
        var viewer = controller.Room is { IsViewer: true, HasRemoteControl: false };
        ImGui.TextUnformatted("midibard2-深海回响特供版 / 自动点歌");
        ImGui.SameLine();
        UiKit.MutedText(viewer ? "队员查看" : $"曲库 {controller.QueueState.Songs.Count} 首");
        if (!viewer)
        {
            ImGui.SameLine();
            if (UiKit.Icon(FontAwesomeIcon.FolderOpen, "data", "打开数据目录")) controller.OpenPath(controller.DataDirectory);
        }
        var contactWidth = ImGui.CalcTextSize("联系作者").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < contactWidth) ImGui.NewLine();
        else ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - contactWidth);
        if (ImGui.Button("联系作者##contactAuthor", new Vector2(contactWidth, 0))) controller.ContactAuthor();
        UiKit.RecordItem("contactAuthor");
        UiKit.Tip(StageController.AuthorWebsite);
        if (controller.IsReadOnly && controller.Room?.IsRemote != true) ImGui.TextColored(UiKit.Warning, "当前只读");
        if (controller.Room is { } room && (room.IsCaptain || room.IsRemote)) ImGui.TextWrapped(room.ConnectionStatus);
        if (controller.LocalQueueControlIssue is { } controlIssue && controller.Room?.IsCaptain != true)
        {
            ImGui.TextWrapped(controlIssue);
            if (controller.State.RequestSettings.PlaybackMode == QueuePlaybackMode.Ensemble
                && controller.QueuePlayer?.IsLoading != true && controller.QueuePlayer?.IsEnginePlaying != true
                && !controller.State.Setlists.Any(s => StageOperations.Current(s) != null))
            {
                ImGui.BeginDisabled(controller.IsReadOnly || controller.IsBusy);
                if (ImGui.RadioButton("单人演奏##authoritySolo", false))
                    controller.QueueCommand(BardStage.Core.Rooms.RoomAction.PlaybackMode, number: (int)QueuePlaybackMode.Solo);
                UiKit.RecordItem("authoritySolo");
                ImGui.EndDisabled();
            }
        }
        ImGui.Separator();
        var tabPosition = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var style = ImGui.GetStyle();
        float TabWidth(string label) => ImGui.CalcTextSize(label).X + style.FramePadding.X * 2 + style.ItemInnerSpacing.X;
        var tabsWidth = TabWidth(viewer ? "演出列表" : "点歌队列")
            + (!viewer ? TabWidth("曲库") : 0)
            + (controller.Room?.IsRemote != true ? TabWidth("更多") : 0)
            + (controller.Room != null ? TabWidth("演出房间") : 0);
        var noticeWidth = ImGui.CalcTextSize(PluginNotice).X;
        var noticeInline = tabsWidth + style.ItemSpacing.X * 2 + noticeWidth <= availableWidth;
        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem(viewer ? "演出列表" : "点歌队列"))
            {
                if (!noticeInline) DrawPluginNotice();
                if (viewer) DrawViewerQueue();
                else
                {
                    ImGui.BeginDisabled(!controller.CanEditQueue);
                    DrawAutomaticQueue();
                    ImGui.EndDisabled();
                }
                ImGui.EndTabItem();
            }
            if (!viewer && ImGui.BeginTabItem("曲库"))
            {
                if (!noticeInline) DrawPluginNotice();
                ImGui.BeginDisabled(!controller.CanEditQueue);
                if (controller.Room?.IsRemote == true) DrawSharedLibrary();
                else DrawLibrary();
                ImGui.EndDisabled();
                ImGui.EndTabItem();
            }
            if (controller.Room?.IsRemote != true && ImGui.BeginTabItem("更多", selectSetlistTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                if (!noticeInline) DrawPluginNotice();
                ImGui.BeginDisabled(controller.IsReadOnly || controller.IsBusy);
                if (ImGui.BeginTabBar("AdvancedTabs"))
                {
                    if (ImGui.BeginTabItem("节目单", selectSetlistTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    {
                        selectSetlistTab = false;
                        ImGui.BeginDisabled(!controller.CanEditQueue);
                        DrawSetlists();
                        ImGui.EndDisabled();
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("演出记录")) { DrawSessions(); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
                ImGui.EndDisabled();
                ImGui.EndTabItem();
            }
            if (controller.Room != null && ImGui.BeginTabItem("演出房间"))
            {
                if (!noticeInline) DrawPluginNotice();
                DrawRoom(); ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            if (noticeInline)
            {
                var contentPosition = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new Vector2(tabPosition.X + availableWidth - noticeWidth, tabPosition.Y + style.FramePadding.Y));
                DrawPluginNotice();
                ImGui.SetCursorScreenPos(contentPosition);
            }
        }
        var footerHeight = 55 * scale;
        var targetY = ImGui.GetWindowHeight() - footerHeight;
        if (ImGui.GetCursorPosY() < targetY) ImGui.SetCursorPosY(targetY);
        ImGui.Separator();
        if (ImGui.BeginChild("Status", new Vector2(0, 42 * scale), false))
        {
            if (!string.IsNullOrEmpty(controller.StatusMessage))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, controller.StatusIsError ? UiKit.Warning : UiKit.Muted);
                ImGui.TextWrapped(controller.StatusMessage);
                ImGui.PopStyleColor();
            }
            else UiKit.MutedText("已保存至本地");
        }
        ImGui.EndChild();
        DrawModals();
        DrawRequestModals();
        DrawStageModals();
    }

    private static void DrawPluginNotice()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Muted);
        ImGui.TextWrapped(PluginNotice);
        UiKit.RecordItem("pluginNotice");
        ImGui.PopStyleColor();
    }

    private void DrawLibrary()
    {
        if (UiKit.Icon(FontAwesomeIcon.FileImport, "importFiles", "导入 MIDI 文件"))
            dialogs.OpenFileDialog("导入 MIDI", ".mid,.midi", (ok, paths) => { if (ok) controller.Import(paths); }, 9999, null!, false);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.FolderPlus, "importFolder", "导入文件夹及子文件夹"))
            dialogs.OpenFolderDialog("导入 MIDI 文件夹", (ok, path) => { if (ok) controller.Import([path]); });
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Sync, "importPlayer", "刷新共享曲库", controller.ImportPlayerLibrary != null)) controller.ImportPlayerLibrary?.Invoke();
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.TrashAlt, "clearLibrary", "清空曲库", controller.State.Songs.Count > 0 && !controller.IsBusy))
            Confirm($"清空全部 {controller.State.Songs.Count} 首歌曲？将同步清空 MidiBard 中的对应歌曲，并移除节目单中的关联条目。MIDI 原文件和演出记录保留。", () =>
            {
                if (controller.ClearLibrary()) selectedSongId = null;
            });
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(120, Math.Min(230 * UiKit.Scale, ImGui.GetContentRegionAvail().X - 140 * UiKit.Scale)));
        ImGui.InputTextWithHint("##search", "搜索曲名、别名、编曲者", ref search, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * UiKit.Scale);
        if (ImGui.BeginCombo("##performerFilter", performerFilter < 0 ? "全部人数" : performerFilter == 0 ? "人数未填写" : $"{performerFilter} 人"))
        {
            for (var i = -1; i <= 8; i++)
                if (ImGui.Selectable(i < 0 ? "全部人数" : i == 0 ? "人数未填写" : $"{i} 人", performerFilter == i)) performerFilter = i;
            ImGui.EndCombo();
        }
        ImGui.Spacing();
        var height = Math.Max(200, ImGui.GetContentRegionAvail().Y - 65 * UiKit.Scale);
        if (ImGui.BeginTable("LibraryLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthStretch, 0.62f);
            ImGui.TableSetupColumn("editor", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("SongList", new Vector2(0, height), false)) DrawSongTable();
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("SongInspector", new Vector2(0, height), false)) DrawSongInspector();
            ImGui.EndChild();
            ImGui.EndTable();
        }
    }

    private void DrawSongTable()
    {
        var filter = search.Trim();
        var songs = controller.State.Songs.Where(s =>
            (performerFilter < 0 || s.PerformerCount == performerFilter) &&
            (filter.Length == 0 || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
             s.Arranger.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (songs.Length == 0)
        {
            UiKit.MutedText(controller.State.Songs.Count == 0 ? "曲库为空" : "没有匹配的曲目");
            return;
        }
        if (!ImGui.BeginTable("Songs", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -1))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("曲目", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("人数", ImGuiTableColumnFlags.WidthFixed, 42 * UiKit.Scale);
        ImGui.TableSetupColumn("时长", ImGuiTableColumnFlags.WidthFixed, 64 * UiKit.Scale);
        ImGui.TableSetupColumn("编曲", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("删除", ImGuiTableColumnFlags.WidthFixed, 38 * UiKit.Scale);
        ImGui.TableHeadersRow();
        foreach (var song in songs)
        {
            ImGui.PushID(song.Id.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable(song.Title + "##select", selectedSongId == song.Id)) SelectSong(song);
            UiKit.Tip(song.FilePath);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(song.PerformerCount == 0 ? "未填" : song.PerformerCount.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(UiKit.Duration(song.DurationSeconds));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(song.Arranger);
            ImGui.TableNextColumn();
            if (UiKit.Icon(FontAwesomeIcon.TrashAlt, "deleteLibrarySong", "从共享曲库删除", controller.CanDeleteSong(song.Id))) ConfirmDeleteSong(song);
            UiKit.RecordItem("deleteLibrarySong:" + song.Id);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void SelectSong(SongEntry song)
    {
        selectedSongId = song.Id;
        songTitle = song.Title;
        songAliases = string.Join("; ", song.Aliases);
        songArranger = song.Arranger;
        songPerformers = song.PerformerCount;
        songNotes = song.Notes;
    }

    private void DrawSongInspector()
    {
        var song = controller.State.Songs.FirstOrDefault(s => s.Id == selectedSongId);
        if (song == null) { UiKit.MutedText("未选择曲目"); return; }
        ImGui.TextUnformatted("曲目资料");
        ImGui.Separator();
        Field("展示曲名", "songTitle", ref songTitle, 256);
        Field("别名（分号分隔）", "aliases", ref songAliases, 1024);
        Field("编曲者 / 版本", "arranger", ref songArranger, 256);
        ImGui.TextUnformatted("演奏人数");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##songPerformers", songPerformers == 0 ? "未填写" : $"{songPerformers} 人"))
        {
            for (var i = 0; i <= 8; i++)
                if (ImGui.Selectable(i == 0 ? "未填写" : $"{i} 人", songPerformers == i)) songPerformers = i;
            ImGui.EndCombo();
        }
        ImGui.TextUnformatted("演奏备注");
        ImGui.InputTextMultiline("##songNotes", ref songNotes, 4096, new Vector2(-1, 85 * UiKit.Scale));
        if (ImGui.Button("保存资料"))
        {
            var id = song.Id;
            controller.Change(state =>
            {
                var target = state.Songs.Single(s => s.Id == id);
                target.Title = songTitle.Trim();
                target.Aliases = songAliases.Split([';', '；', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                target.Arranger = songArranger.Trim();
                target.PerformerCount = songPerformers;
                target.Notes = songNotes.Trim();
            }, "曲目资料已保存");
        }
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Undo, "resetSongDraft", "撤销未保存的资料编辑")) SelectSong(song);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"{song.TrackCount} 音轨  ·  {UiKit.Duration(song.DurationSeconds)}");
        var exists = File.Exists(song.FilePath);
        ImGui.TextColored(exists ? UiKit.Accent : UiKit.Warning, exists ? "文件可用" : "文件缺失");
        ImGui.TextWrapped(song.FilePath);
        UiKit.MutedText("SHA256 " + (song.Sha256.Length > 16 ? song.Sha256[..16] : song.Sha256));
        UiKit.Tip(song.Sha256);
        if (UiKit.Icon(FontAwesomeIcon.Copy, "copyPath", "复制 MIDI 路径")) ImGui.SetClipboardText(song.FilePath);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.FolderOpen, "revealSong", "在文件夹中显示", exists)) controller.OpenPath(song.FilePath, true);
        ImGui.SameLine();
        if (UiKit.Icon(FontAwesomeIcon.Trash, "deleteSong", "从共享曲库删除", controller.CanDeleteSong(song.Id))) ConfirmDeleteSong(song);
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("加入点歌队列", new Vector2(-1, 0))) controller.AddToQueue(song.Id);
    }

    private void ConfirmDeleteSong(SongEntry song) => Confirm($"删除《{song.Title}》？将同步移除 MidiBard 中的歌曲和节目单关联条目。MIDI 原文件和演出记录保留。", () =>
    {
        if (controller.DeleteSong(song.Id) && selectedSongId == song.Id) selectedSongId = null;
    });

    private void DrawShowCombo(string id)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##" + id, controller.CurrentSetlist?.Name ?? "选择节目单")) return;
        foreach (var show in controller.State.Setlists.ToArray())
            if (ImGui.Selectable(show.Name + "##" + show.Id, controller.State.SelectedSetlistId == show.Id))
                controller.Change(state => state.SelectedSetlistId = show.Id);
        ImGui.EndCombo();
    }

    private void Confirm(string text, Action action)
    {
        confirmText = text;
        confirmAction = action;
        openConfirm = true;
    }

    private void DrawModals()
    {
        if (openConfirm) { ImGui.OpenPopup("确认操作##BardStage"); openConfirm = false; }
        ImGui.SetNextWindowSizeConstraints(new Vector2(440 * UiKit.Scale, 0), new Vector2(440 * UiKit.Scale, float.MaxValue));
        ImGui.SetNextWindowSize(new Vector2(440 * UiKit.Scale, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("确认操作##BardStage", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(confirmText);
            ImGui.Spacing();
            if (ImGui.Button("确认", new Vector2(90 * UiKit.Scale, 0)))
            {
                confirmAction?.Invoke(); confirmAction = null; ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(90 * UiKit.Scale, 0))) { confirmAction = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (openCreate) { ImGui.OpenPopup("节目单名称##BardStage"); openCreate = false; }
        ImGui.SetNextWindowSizeConstraints(new Vector2(420 * UiKit.Scale, 0), new Vector2(420 * UiKit.Scale, float.MaxValue));
        ImGui.SetNextWindowSize(new Vector2(420 * UiKit.Scale, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("节目单名称##BardStage", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##createName", ref createName, 128);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(createName));
            if (ImGui.Button("保存", new Vector2(90 * UiKit.Scale, 0)))
            {
                var currentId = controller.CurrentSetlist?.Id;
                controller.Change(state =>
                {
                    if (createMode == "rename") state.Setlists.Single(s => s.Id == currentId).Name = createName.Trim();
                    else
                    {
                        var created = createMode == "clone" && currentId.HasValue
                            ? StageController.Clone(state.Setlists.Single(s => s.Id == currentId)) : new ShowSetlist();
                        created.Id = Guid.NewGuid(); created.Name = createName.Trim(); created.LockedNextEntryId = null; created.TargetEndUtc = null;
                        foreach (var entry in created.Entries)
                        {
                            entry.Id = Guid.NewGuid(); entry.Status = EntryStatus.Queued; entry.StartedAtUtc = null; entry.EndedAtUtc = null;
                        }
                        state.Setlists.Add(created); state.SelectedSetlistId = created.Id;
                    }
                });
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(90 * UiKit.Scale, 0))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private static void Field(string label, string id, ref string value, int maxLength)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##" + id, ref value, maxLength);
    }
}
