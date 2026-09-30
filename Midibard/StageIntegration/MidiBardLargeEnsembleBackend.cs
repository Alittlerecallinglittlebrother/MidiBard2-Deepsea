#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Melanchall.DryWetMidi.Interaction;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.Control.MidiControl.PlaybackInstance;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

namespace MidiBard.StageIntegration;

internal sealed class MidiBardLargeEnsembleBackend : ILargeEnsembleBackend
{
    private BardPlayback? owned;
    private Guid loadingPlan;
    private RoomSongPlan? expectedPlan;
    private volatile bool finished;
    private MonotonicPlaybackStart? scheduled;
    private bool scheduledAnnounced;
    private double scheduledTarget;
    public bool SupportsScheduledStart => true;
    public PlaybackTimingDiagnostics OutputTiming => MidiBard.BardPlayDevice.OutputTiming;
    public double? ScheduledStartAt => scheduled?.FiredAt;
    public void ScheduleStart(double at) => ScheduleStart(at, null);
    public void ScheduleStart(double at, Func<bool>? canStart)
    {
        if (PlaybackIssue is { } issue) throw new InvalidOperationException(issue);
        scheduled?.Dispose(); scheduled = new(); scheduledAnnounced = false;
        scheduledTarget = at;
        finished = false; owned!.MoveToStart(); EnsembleManager.EnsembleTimer.Reset();
        owned.PrepareScheduledOutput(at);
        var prepared = owned;
        try
        {
            scheduled.Arm(at, () =>
            {
                prepared.Start();
                MidiBard.BardPlayDevice.ActivateLargePlaybackOutput(prepared.LargePlanId);
            }, canStart);
        }
        catch { MidiBard.BardPlayDevice.CancelLargePlaybackOutput(prepared.LargePlanId); throw; }
    }
    public void PulseScheduledStart()
    {
        if (scheduled?.Issue is { } issue) throw new InvalidOperationException(issue);
        scheduled?.Pulse();
        if (!scheduledAnnounced && scheduled?.FiredAt != null)
        {
            scheduledAnnounced = true; scheduled.StopTimer(); MidiPlayerControl.NotifyScheduledStart();
            api.PluginLog.Information($"[Timing] target={scheduledTarget:F6} fired={scheduled.FiredAt:F6} lateMs={(scheduled.FiredAt-scheduledTarget)*1000:F3}; {MidiBard.BardPlayDevice.TimingSummary}");
        }
    }
    public static Func<bool>? ModeEnabled;
    public static event Action<Guid,string,ulong>? Proof;
    public static bool Active => ModeEnabled?.Invoke() == true;
    public static void ReceiveProof(Guid room, string challenge, ulong cid) => Proof?.Invoke(room, challenge, cid);
    public static readonly string[] InstrumentNames = ["未分配", "竖琴", "钢琴", "鲁特琴", "提琴拨弦", "长笛", "双簧管", "单簧管", "横笛", "排箫", "定音鼓", "邦戈鼓", "低音鼓", "小军鼓", "镲", "小号", "长号", "大号", "圆号", "萨克斯", "小提琴", "中提琴", "大提琴", "低音提琴", "电吉他·过载", "电吉他·清音", "电吉他·闷音", "电吉他·重力", "电吉他·特殊"];
    public IReadOnlyList<string> Instruments => InstrumentNames;
    public string? EnableIssue => MidiBard.IsPlaying || PlaylistManager.IsLoading || PartyChatCommand.IsLoading
        || MidiBard.AgentMetronome.EnsembleModeRunning ? "请先停止当前演奏和歌曲载入"
        : MidiBard.Stage?.IsPreparingOrPerforming == true ? "请先停止自动点歌连播" : null;
    public bool Owns(object playback) => ReferenceEquals(owned, playback);
    public bool Draining => finished && owned is { UsesScheduledOutput: true }
        && ReferenceEquals(owned, MidiBard.CurrentPlayback) && MidiBard.BardPlayDevice.LargePlaybackPending(owned.LargePlanId) != 0;
    public bool Playing => ReferenceEquals(owned, MidiBard.CurrentPlayback) && (owned?.IsRunning == true || Draining);
    public bool Finished => finished && !Draining;
    public double PositionSeconds => owned?.GetCurrentTime<MetricTimeSpan>().TotalMicroseconds / 1000000d ?? 0;
    public string? PlaybackIssue => !ReferenceEquals(owned, MidiBard.CurrentPlayback) || owned == null ? "本机歌曲已被更换，请重新下发"
        : scheduled?.Issue is { } scheduleIssue ? scheduleIssue
        : MidiBard.BardPlayDevice.OutputIssue(owned.LargePlanId) is { } outputIssue ? outputIssue
        : scheduled?.FiredAt != null && !owned.IsRunning && !finished ? "多人合奏播放被暂停，请停止后重新下发"
        : MidiBard.AgentMetronome.EnsembleModeRunning ? "请退出游戏原生小队合奏后重新下发"
        : SwitchInstrument.SwitchingInstrument ? "本机正在切换乐器"
        : MidiBard.CurrentInstrumentWithTone != owned.GetInstrumentId() ? "本机乐器已改变，请重新下发"
        : !AssignmentMatches() ? "轨道、速度或移调设置已改变，请重新下发" : null;

    private bool AssignmentMatches()
    {
        var actual = owned?.MidiFileConfig;
        if (actual == null || expectedPlan == null || actual.Tracks.Count != expectedPlan.Tracks.Length
            || owned!.Speed != expectedPlan.Speed || !actual.LeaderDistributed || actual.AdaptNotes != expectedPlan.AdaptNotes
            || MidiBard.config.SoloedTrack != null || MidiBard.config.TransposeGlobal != 0) return false;
        for (var i=0;i<actual.Tracks.Count;i++)
        {
            var a = actual.Tracks[i]; var e = expectedPlan.Tracks[i];
            if (a.Enabled != e.Enabled || a.Instrument != e.Instrument || a.Transpose != e.Transpose
                || (e.PerformerCid == 0 ? a.AssignedCids.Count != 0 : a.AssignedCids.Count != 1 || a.AssignedCids[0] != e.PerformerCid)
                || MidiBard.config.TrackStatus[i].Enabled != (e.Enabled && e.PerformerCid == api.Player.ContentId)
                || MidiBard.config.TrackStatus[i].Transpose != e.Transpose
                || MidiBard.config.TrackStatus[i].Tone != InstrumentHelper.GetGuitarTone(e.Instrument)) return false;
        }
        return true;
    }

    public LargeContext Capture()
    {
        var self = api.ObjectTable.LocalPlayer;
        var issue = !api.ClientState.IsLoggedIn || self == null ? "等待登录游戏"
            : MidiBard.SlaveMode ? "请使用本机独立演奏端"
            : self.IsDead || api.Condition[ConditionFlag.InCombat] || api.Condition[ConditionFlag.BetweenAreas]
                || api.Condition[ConditionFlag.BetweenAreas51] || api.Condition[ConditionFlag.WatchingCutscene]
                ? "角色正忙或正在切换场景" : null;
        // The disabled module only uses the existing managed party service.
        var members = Active ? ReadMembers() : api.PartyList.Select(p => new LargeMember(p.ContentId,p.Name.TextValue,p.World.RowId,0)).ToArray();
        var visible = Active ? api.ObjectTable.OfType<IPlayerCharacter>().Select(p => (p.Name.TextValue,p.HomeWorld.RowId)).ToHashSet() : null;
        if (issue == null && visible != null && members.Any(m => !visible.Contains((m.Name,m.World))))
            issue = "请让全体团队成员进入同一场景并靠近总指挥";
        return new(api.Player.ContentId, members, api.ClientState.TerritoryType, self?.CurrentWorld.RowId ?? 0, issue);
    }
    public static LargeMember[] ReadMembers()
    {
        if (!api.ClientState.IsLoggedIn) return [];
        // Public multiplayer uses the same single-party roster as native ensemble.
        // Do not enumerate alliance or cross-realm groups beyond this party.
        return api.PartyList.Where(p => p.ContentId != 0)
            .Select(p => new LargeMember(p.ContentId, p.Name.TextValue, p.World.RowId, 0)).ToArray();
    }
    public Task<LargeDraft> InspectAsync(string path, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var scope = DistributedEnsembleAssignment.Begin(null, editing: true, large: true);
        using var playback = BardPlayback.GetBardPlayback(PlaylistManager.LoadSongFile(path), path);
        if (playback.TrackInfos.Length is < 1 or > 100) throw new InvalidOperationException("请选择 1～100 条有音符轨道的 MIDI");
        var config = playback.MidiFileConfig;
        return new LargeDraft(path, PartySongIdentity.Hash(path), playback.TrackInfos.Select((t,i) =>
            new LargeTrack(i,t.TrackName,config.Tracks[i].Instrument,config.Tracks[i].Transpose,0,true)).ToArray());
    }, token);
    public Task LoadAsync(string path, LargePlan plan, CancellationToken token)
        => api.Framework.Run(() => LoadOnFramework(path, plan, token), token);
    private async Task LoadOnFramework(string path, LargePlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        plan.ValidateContext(Capture(), plan.Song.LeaderCid);
        if (EnableIssue is { } issue) throw new InvalidOperationException(issue);
        PartyChatCommand.CancelLoad(); finished = false; loadingPlan = plan.Id;
        using var scope = DistributedEnsembleAssignment.Begin(plan.Song, large: true);
        var loaded = await PlaylistManager.LoadExternalPlayback(path, token);
        token.ThrowIfCancellationRequested();
        if (!loaded || MidiBard.CurrentPlayback?.MidiFileConfig?.LeaderDistributed != true)
            throw new InvalidOperationException("歌曲载入失败");
        owned = MidiBard.CurrentPlayback;
        owned.UseLargeInstrumentCompensation = plan.CompensationVersion == LargePlan.CurrentCompensationVersion;
        expectedPlan = plan.Song.Copy();
        owned.Finished += (sender,_) => { if (ReferenceEquals(sender, owned)) finished = true; };
        if (PlaybackIssue is { } reason) throw new InvalidOperationException(reason);
    }
    public void Start()
    {
        throw new InvalidOperationException("多人合奏必须使用独立预约起播");
    }
    public void Stop()
    {
        scheduled?.Dispose(); scheduled = null; scheduledAnnounced = false;
        if (MidiBard.CurrentPlayback is { } loading && loading.LargePlanId == loadingPlan && loadingPlan != Guid.Empty) owned = loading;
        if (owned != null)
        {
            api.PluginLog.Information("[Timing] stop: " + MidiBard.BardPlayDevice.TimingSummary);
            try
            {
                if (ReferenceEquals(owned, MidiBard.CurrentPlayback))
                { owned.Stop(); owned.MoveToStart(); MidiPlayerControl.StopLrc(); }
            }
            finally { MidiBard.BardPlayDevice.CancelLargePlaybackOutput(owned.LargePlanId); }
        }
        finished = false;
    }
    public void SendProof(Guid room, string challenge)
    {
        var members = Capture(); var cid = members.SelfCid;
        Chat.SendMessage($"/p mblargeproof {room:N} {challenge}", () => Active && api.Player.ContentId == cid);
    }
}
