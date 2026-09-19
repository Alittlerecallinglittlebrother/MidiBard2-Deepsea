#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BardStage;
using BardStage.Windows;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Melanchall.DryWetMidi.Multimedia;
using Dalamud.Utility;
using MidiBard.Managers.Ipc;

namespace MidiBard.StageIntegration;

internal sealed class StageFeature : IDisposable
{
    private readonly StageController controller;
    private readonly MainWindow window;
    private readonly WindowSystem windows = new("MidiBard.Stage");
    private readonly ChatRequestReceiver requests;
    private readonly PlaybackObserver playback;
    private readonly StagePlaybackIpc ipc;
    private readonly MidiBardQueuePort queuePort;
    private readonly RoomPlaybackCoordinator roomPlayback;
    private readonly AutoQueuePlayer queue;
    private int requestIssueCount;
    private string? synchronizedPlayerPaths;
    private string? pendingPlayerPaths;
    private DateTimeOffset nextLibraryPoll;
    private bool sharedLibrarySeeded;

    public StageFeature()
    {
        controller = new StageController(Path.Combine(api.PluginInterface.GetPluginConfigDirectory(), "Stage")) { SyncAvailable = true };
        controller.EnsureAutomaticQueue();
        queuePort = new MidiBardQueuePort();
        controller.LocalEnsembleControlIssue = () => !api.ClientState.IsLoggedIn ? "等待登录游戏"
            : api.PartyList.Length < 2 ? "合奏主控需要加入小队"
            : !api.PartyList.IsPartyLeader() ? $"当前队长：{api.PartyList.GetPartyLeader()?.Name}，本机已切为队员"
            : MidiBard.SlaveMode ? "当前是从控，请在演奏主控端操作" : null;
        controller.Room = new StageRoom(controller)
        {
            CanHost = () => !api.ClientState.IsLoggedIn ? "等待登录游戏"
                : MidiBard.SlaveMode ? "请在演奏主控端创建房间"
                : api.PartyList.Length >= 2 && !api.PartyList.IsPartyLeader() ? "请由小队队长创建演出房间" : null,
        };
        roomPlayback = new RoomPlaybackCoordinator(controller, controller.Room, queuePort);
        queue = new AutoQueuePlayer(controller, roomPlayback);
        controller.QueuePlayer = queue;
        PartyChatCommand.StageProof += roomPlayback.ReceiveProof;
        controller.ImportPlayerLibrary = ImportPlaylist;
        controller.PreparePlayerLibraryEdit = PlaylistManager.BeginStageLibraryEdit;
        controller.PlayerLibraryRemovalIssue = NativePlaybackRemovalIssue;
        PlaylistManager.StageRemovalIssue = LibraryRemovalIssue;
        SynchronizeLibrary();
        window = new MainWindow(controller);
        UiKit.IconFont = () => UiBuilder.IconFont;
        windows.AddWindow(window);
        requests = new ChatRequestReceiver(api.ChatGui, api.Framework, () => controller.Room.ReceptionSettings, controller.Room.ReceiveChat);
        ipc = new StagePlaybackIpc(api.PluginInterface, ex => api.PluginLog.Warning(ex, "Stage playback status subscriber failed."));
        playback = new PlaybackObserver(signal => { roomPlayback.Receive(signal); ipc.Receive(signal); });
        api.Framework.Update += Update;
        api.PluginInterface.UiBuilder.Draw += Draw;
    }

    public void Open() => window.IsOpen = true;
    public bool IsOpen => window.IsOpen;
    public bool IsPreparingOrPerforming => queue.IsLoading || queue.ActiveEntryId.HasValue;
    public void Attach(Playback value, string? path) => playback.Attach(value, path);
    public void StopPlayback() => playback.Stop();
    public bool OwnsPlayback(object value) => queuePort.OwnsPlayback(value);

    private void ImportPlaylist()
    {
        synchronizedPlayerPaths = null;
        nextLibraryPoll = DateTimeOffset.MinValue;
        SynchronizeLibrary();
    }

    private void SynchronizeLibrary()
    {
        if (controller.IsBusy || controller.IsReadOnly || controller.Room?.IsRemote == true || DateTimeOffset.UtcNow < nextLibraryPoll) return;
        nextLibraryPoll = DateTimeOffset.UtcNow.AddSeconds(1);
        try
        {
            if (!sharedLibrarySeeded)
            {
                if (!controller.State.SharedLibraryInitialized && controller.State.Songs.Count > 0)
                {
                    using var migration = PlaylistManager.BeginStageLibraryEdit(Array.Empty<BardStage.Core.SongEntry>(), controller.State.Songs);
                    migration.Commit();
                }
                sharedLibrarySeeded = true;
            }
            if (pendingPlayerPaths != null)
            {
                if (!controller.StatusIsError) synchronizedPlayerPaths = pendingPlayerPaths;
                pendingPlayerPaths = null;
            }
            var paths = PlaylistManager.FilePathList.Select(s => s.FilePath)
                .Where(p => Path.GetExtension(p).Equals(".mid", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".midi", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var signature = string.Join('\0', paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            if (signature != synchronizedPlayerPaths && controller.SynchronizePlayerLibrary(paths)) pendingPlayerPaths = signature;
        }
        catch (Exception ex) { controller.SetStatus("共享曲库同步未完成：" + ex.Message, true); }
    }

    private string? NativePlaybackRemovalIssue(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        if (PlaylistManager.IsLoading) return "MidiBard 正在载入曲目，请稍后再删除。";
        var currentPath = MidiBard.CurrentPlayback?.FilePath;
        if (MidiBard.IsPlaying && currentPath != null && paths.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
            return "不能删除正在演奏的曲目，请先停止演奏。";
        return null;
    }

    private string? LibraryRemovalIssue(IReadOnlyList<string> paths)
    {
        var issue = controller.IsBusy ? "曲库正在同步，请稍后再删除。" : NativePlaybackRemovalIssue(paths);
        if (issue == null && controller.State.Songs.Any(s => paths.Contains(s.FilePath, StringComparer.OrdinalIgnoreCase) && !controller.CanDeleteSong(s.Id)))
            issue = "不能删除正在准备或演奏的曲目，请先停止演奏。";
        if (issue != null) controller.SetStatus(issue, true);
        return issue;
    }

    private void Update(IFramework framework)
    {
        PartyChatCommand.Tick();
        roomPlayback.Tick();
        if (!roomPlayback.IsCoordinated && !controller.Room!.IsRemote && controller.LocalQueueControlIssue is { } reason)
        {
            queue.RevokeControl(reason);
            queuePort.RevokeControl();
        }
        controller.Poll();
        SynchronizeLibrary();
        controller.Room!.Tick();
        if (!controller.Room.IsRemote) queue.Tick();
        queuePort.Tick();
        if (requests.IssueCount != requestIssueCount) { requestIssueCount = requests.IssueCount; controller.SetStatus(requests.LastIssue, true); }
        ipc.Poll();
    }

    private void Draw() { windows.Draw(); window.DrawDialogs(); }

    public void Dispose()
    {
        api.Framework.Update -= Update;
        api.PluginInterface.UiBuilder.Draw -= Draw;
        PlaylistManager.StageRemovalIssue = null;
        PartyChatCommand.CancelLoad();
        PartyChatCommand.StageProof -= roomPlayback.ReceiveProof;
        queue.Dispose(); roomPlayback.Dispose(); playback.Dispose(); requests.Dispose(); ipc.Dispose();
        windows.RemoveAllWindows(); window.Dispose(); controller.Dispose(); UiKit.IconFont = null;
    }
}
