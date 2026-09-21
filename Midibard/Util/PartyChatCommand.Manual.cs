#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BardStage.Core.Rooms;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;

namespace MidiBard;

internal static partial class PartyChatCommand
{
    internal static Func<RoomSongPlan, CancellationToken, Task>? ManualPlanPublisher { get; set; }
    internal static Func<Guid, string, CancellationToken, Task<RoomSongPlan>>? ManualPlanReceiver { get; set; }
    internal static bool ManualDistributionMode => MidiBard.config.EnableCrossComputerSongSync
        && MidiBard.config.playOnMultipleDevices && !MidiBard.config.AutoAssignEnsembleTracks;
    internal static bool IsLoading => activeLoad != null || PlaylistManager.IsLoading;
    internal static string ManualDistributionStatus { get; private set; } = "";
    private static string? readyManualSignature;
    private static string AssignmentSignature(MidiFileConfig? config) => config == null ? ""
        : $"{config.LeaderDistributed}/{config.AutomaticallyAssigned}/{config.Speed}/{config.AdaptNotes}/{config.ToneMode}/"
            + string.Join(";", config.Tracks.Select(t => $"{t.Index}:{t.Enabled}:{t.Instrument}:{t.Transpose}:{string.Join(",", t.AssignedCids)}"));
    private static bool ManualAssignmentChanged => readyManualSignature != null
        && readyManualSignature != AssignmentSignature(MidiBard.CurrentPlayback?.MidiFileConfig);

    internal static void InvalidateAssignment()
    {
        CancelLoad(); readyPlayback = null;
        ManualDistributionStatus = "分配尚未下发";
    }

    internal static void SendManualAssignment()
        => _ = ObserveLoad(DistributeCurrentManualAsync(CancellationToken.None));

    internal static async Task<bool> DistributeCurrentManualAsync(CancellationToken token)
    {
        if (!ManualDistributionMode) throw new InvalidOperationException("请开启跨电脑歌曲同步，并关闭队长端的自动分配");
        var playback = MidiBard.CurrentPlayback;
        if (playback?.MidiFileConfig == null || playback.IsSoloPlayback || string.IsNullOrEmpty(playback.FilePath))
            throw new InvalidOperationException("请先选择合奏歌曲并指定演奏人和乐器");
        return await SwitchToPathCoreAsync(playback.FilePath,
            PlaylistManager.FilePathList.FindIndex(s => SameSongPath(s.FilePath, playback.FilePath)), token, playback.MidiFileConfig);
    }

    private static bool SameSongPath(string? left, string? right)
        => !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right)
            && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> PrepareManualSongAsync(int index, CancellationToken token)
    {
        if (!api.PartyList.IsPartyLeader()) throw new InvalidOperationException("请由当前小队队长选择合奏歌曲");
        if (MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning) throw new InvalidOperationException("请先停止合奏再编辑分配");
        InvalidateAssignment();
        using var scope = DistributedEnsembleAssignment.Begin(null, editing: true);
        var loaded = await PlaylistManager.LoadPlayback(index, false, false, token);
        if (!loaded) throw new InvalidOperationException("队长端未能载入 MIDI");
        ManualDistributionStatus = "请指定演奏人和乐器，再点击下发歌曲与手动分配";
        return true;
    }

    internal static async Task<bool> SwitchToPathAsync(string path, int index, CancellationToken token)
    {
        if (!ManualDistributionMode) return await SwitchToPathCoreAsync(path, index, token);
        if (!api.PartyList.IsPartyLeader()) throw new InvalidOperationException("请由当前小队队长选择合奏歌曲");
        if (MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning) throw new InvalidOperationException("请先停止合奏再下发手动分配");
        var playback = MidiBard.CurrentPlayback;
        if (playback?.MidiFileConfig == null || !SameSongPath(playback.FilePath, path))
        {
            InvalidateAssignment();
            using var scope = DistributedEnsembleAssignment.Begin(null, editing: true);
            var loaded = index >= 0 ? await PlaylistManager.LoadPlayback(index, false, false, token)
                : await PlaylistManager.LoadExternalPlayback(path, token);
            if (!loaded) throw new InvalidOperationException("队长端未能载入 MIDI");
            playback = MidiBard.CurrentPlayback;
        }
        return await SwitchToPathCoreAsync(path, index, token,
            playback?.MidiFileConfig ?? throw new InvalidOperationException("请先在合奏面板完成手动分配"));
    }
}
