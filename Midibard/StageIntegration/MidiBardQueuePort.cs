#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BardStage;
using BardStage.Core;
using Dalamud.Utility;
using Melanchall.DryWetMidi.Multimedia;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.IPC;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

namespace MidiBard.StageIntegration;

internal sealed class MidiBardQueuePort : IRoomEnsembleBackend
{
    private readonly ConditionalWeakTable<Playback, object> owned = new();
    private Playback? expected;
    private bool pendingReady;
    private QueuePlaybackMode loadedMode;
    private long loadedParty;
    private ulong loadedLeader;
    private DateTimeOffset readyAfter;
    public bool IsPlaying => MidiBard.IsPlaying;
    public RoomPartyState Party => !api.ClientState.IsLoggedIn || MidiBard.SlaveMode || api.PartyList.Length < 2
        ? new(0, 0, 0) : new(api.PartyList.PartyId, api.Player.ContentId, api.PartyList.GetPartyLeader()?.ContentId ?? 0);
    public string Fingerprint(string filePath) => PartySongIdentity.Hash(filePath);
    public string? ResolveSong(string hash)
    {
        var paths = PlaylistManager.FilePathList.Select(s => s.FilePath).ToArray();
        var index = PartySongIdentity.Resolve(paths, -1, hash);
        return index < 0 ? null : paths[index];
    }
    public void SendIdentityProof(Guid roomId, string challenge) => PartyChatCommand.SendStageProof(roomId, challenge);
    public bool Adopt(string hash) => AdoptEnsemblePlayback(hash);
    // Owner loads use their cancellation token; follower loads track the actual party leader.
    public void Revoke() => RevokeControl();
    public bool OwnsPlayback(object value) => value is Playback playback && owned.TryGetValue(playback, out _);

    public string? BlockReason(QueuePlaybackMode mode)
    {
        if (!api.ClientState.IsLoggedIn) return "等待登录游戏";
        if (MidiBard.SlaveMode) return "当前是从控，请在主控端开启连播";
        if (PlaylistManager.IsLoading) return "等待 MidiBard 完成载入";
        if (SwitchInstrument.SwitchingInstrument) return "等待乐器切换完成";
        if (MidiBard.AgentMetronome.EnsembleModeRunning) return "等待上一首合奏结束";
        if (!MidiBard.AgentPerformance.InPerformanceMode && !(mode == QueuePlaybackMode.Ensemble && MidiBard.config.AutoAssignEnsembleTracks)) return "等待进入乐器演奏模式";
        if (mode == QueuePlaybackMode.Ensemble)
        {
            if (api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader()) return "合奏连播需要由小队队长启动";
            if (!MidiBard.config.MonitorOnEnsemble) return "请在 MidiBard 开启合奏监听";
            if (!MidiBard.config.playOnMultipleDevices && !MidiBard.config.SyncClients) return "请在 MidiBard 开启客户端同步";
        }
        return null;
    }

    public async Task LoadAsync(string filePath, QueuePlaybackMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = PlaylistManager.FilePathList.FindIndex(s => SamePath(s.FilePath, filePath));
        var remote = mode == QueuePlaybackMode.Ensemble && MidiBard.config.playOnMultipleDevices;
        if (index < 0)
        {
            if (remote) throw new InvalidOperationException("多设备合奏的歌曲必须已在本机播放列表中");
            await PlaylistManager.AddAsync(new[] { filePath });
            index = PlaylistManager.FilePathList.FindIndex(s => SamePath(s.FilePath, filePath));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (index < 0) throw new InvalidOperationException("无法导入 MIDI 文件");
        bool loaded;
        var party = api.PartyList.PartyId;
        var leader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        if (remote)
        {
            loaded = await PartyChatCommand.SwitchToAsync(index, cancellationToken);
        }
        else
        {
            using var solo = mode == QueuePlaybackMode.Solo ? AutomaticEnsembleAssignment.BeginSoloLoad() : null;
            loaded = await PlaylistManager.LoadPlayback(index, false, mode == QueuePlaybackMode.Ensemble, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!loaded || MidiBard.CurrentPlayback == null || !SamePath(MidiBard.CurrentPlayback.FilePath, filePath))
            throw new InvalidOperationException("MidiBard 未能载入所选歌曲");
        expected = MidiBard.CurrentPlayback;
        loadedMode = mode;
        loadedParty = party;
        loadedLeader = leader;
        owned.GetValue(expected, _ => new object());
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void RevokeControl() { pendingReady = false; }

    public bool AdoptEnsemblePlayback(string hash)
    {
        if (!api.ClientState.IsLoggedIn || MidiBard.SlaveMode || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader()) return false;
        var current = MidiBard.CurrentPlayback;
        if (current == null || string.IsNullOrEmpty(current.FilePath)) return false;
        try
        {
            if (!PartySongIdentity.Hash(current.FilePath).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        pendingReady = false;
        expected = current;
        loadedMode = QueuePlaybackMode.Ensemble;
        loadedParty = api.PartyList.PartyId;
        loadedLeader = api.Player.ContentId;
        owned.GetValue(current, _ => new object());
        return true;
    }

    public void Start(QueuePlaybackMode mode)
    {
        RequireLoaded();
        if (mode == QueuePlaybackMode.Solo) { MidiPlayerControl.DoPlay(); return; }
        if (!HasLoadedAuthority) throw new InvalidOperationException("小队队长已改变，请由当前队长重新播放");
        if (MidiBard.config.AutoAssignEnsembleTracks || MidiBard.config.UpdateInstrumentBeforeReadyCheck)
        {
            if (MidiBard.CurrentPlayback?.MidiFileConfig is { } config) IPCHandles.UpdateMidiFileConfig(config);
            if (MidiBard.config.playOnMultipleDevices) PartyChatCommand.SendUpdateInstrument();
            else IPCHandles.UpdateInstrument(true);
        }
        pendingReady = true;
        readyAfter = DateTimeOffset.UtcNow.AddSeconds(1);
    }

    public void Tick()
    {
        if (!pendingReady) return;
        if (!HasLoadedAuthority) { RevokeControl(); return; }
        if (DateTimeOffset.UtcNow < readyAfter) return;
        if (!ReferenceEquals(expected, MidiBard.CurrentPlayback)) { pendingReady = false; return; }
        if (BlockReason(QueuePlaybackMode.Ensemble) != null || !MidiBard.AgentPerformance.InPerformanceMode) return;
        pendingReady = false;
        EnsembleManager.BeginEnsembleReadyCheck();
    }

    public void Pause() { RequireLoaded(); if (loadedMode == QueuePlaybackMode.Ensemble) EnsembleTransport.Send("pause"); else MidiPlayerControl.Pause(); }
    public void Resume() { RequireLoaded(); if (loadedMode == QueuePlaybackMode.Ensemble) EnsembleTransport.Send("resume"); else MidiPlayerControl.Play(); }
    public void Finish(QueuePlaybackMode mode)
    {
        if (mode == QueuePlaybackMode.Ensemble && HasLoadedAuthority && ReferenceEquals(expected, MidiBard.CurrentPlayback) && MidiBard.AgentMetronome.EnsembleModeRunning)
            EnsembleManager.StopEnsemble();
    }
    public void Stop(QueuePlaybackMode mode, bool keepInstruments = false)
    {
        pendingReady = false;
        if (mode == QueuePlaybackMode.Ensemble && !HasLoadedAuthority) return;
        if (!ReferenceEquals(expected, MidiBard.CurrentPlayback) || expected == null) return;
        if (mode == QueuePlaybackMode.Ensemble)
        {
            EnsembleTransport.Send("stop");
            if (MidiBard.AgentMetronome.EnsembleModeRunning) EnsembleManager.StopEnsemble();
            if (!keepInstruments)
            {
                if (MidiBard.config.playOnMultipleDevices) PartyChatCommand.SendClose();
                else IPCHandles.UpdateInstrument(false);
            }
        }
        MidiPlayerControl.Stop();
    }
    private void RequireLoaded()
    {
        if (loadedMode == QueuePlaybackMode.Ensemble && !HasLoadedAuthority)
            throw new InvalidOperationException("小队队长已改变，请由当前队长操作");
        if (expected == null || !ReferenceEquals(expected, MidiBard.CurrentPlayback)) throw new InvalidOperationException("播放文件已被切换，请重新开始连播");
    }
    private bool HasLoadedAuthority => api.ClientState.IsLoggedIn && loadedParty == api.PartyList.PartyId
        && loadedLeader == api.Player.ContentId && api.PartyList.IsPartyLeader();
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
