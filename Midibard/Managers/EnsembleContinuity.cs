#nullable enable
using System;
using System.Threading;
using Melanchall.DryWetMidi.Interaction;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.Control.MidiControl.PlaybackInstance;

namespace MidiBard.Managers;

internal static class EnsembleContinuity
{
    private static readonly LiveInstrumentSwitch change = new();
    private static volatile BardPlayback? session;
    private static long sessionVersion;
    private static long party;
    private static ulong character;
    private static bool wasPerforming;
    private static long finishedAt;
    private static string status = "";
    public static bool IsActive => session != null && ReferenceEquals(session, MidiBard.CurrentPlayback);
    public static bool IsSwitching => IsActive && change.IsChanging;
    public static string Status => IsActive ? status : "";
    public static int? GuitarTone => IsActive && change.Target >= 24 && change.Phase is LiveSwitchPhase.WaitingBar or LiveSwitchPhase.Joined
        ? MidiBard.Instruments[change.Target].GuitarTone : null;

    public static void Begin(BardPlayback playback)
    {
        Cancel();
        playback.IsContinuityEnsemble = MidiBard.config.ExperimentalLiveInstrumentSwitch
            && !playback.IsSoloPlayback && api.PartyList.Length >= 2;
        if (!playback.IsContinuityEnsemble) return;
        party = api.PartyList.PartyId;
        character = api.Player.ContentId;
        wasPerforming = MidiBard.AgentPerformance.InPerformanceMode;
        finishedAt = 0;
        status = "合奏：同步播放中";
        session = playback;
    }

    public static bool RequestSwitch(uint instrument)
    {
        var version = Interlocked.Read(ref sessionVersion);
        var playback = session;
        if (playback == null || !ReferenceEquals(playback, MidiBard.CurrentPlayback)) return false;
        _ = api.Framework.RunOnFrameworkThread(() =>
        {
            if (version != Interlocked.Read(ref sessionVersion)
                || !ReferenceEquals(playback, session) || !ReferenceEquals(playback, MidiBard.CurrentPlayback)) return;
            if (instrument == 0)
            {
                MidiPlayerControl.Stop();
                _ = SwitchInstrument.SwitchToAsync(0);
                return;
            }
            if (instrument >= MidiBard.Instruments.Length) return;
            if (instrument == MidiBard.CurrentInstrumentWithTone && change.Phase is LiveSwitchPhase.None or LiveSwitchPhase.Joined) return;
            change.Request(instrument, Environment.TickCount64);
            MidiBard.BardPlayDevice.ClearPlaybackNotes();
            status = $"正在切换：{MidiBard.InstrumentStrings[instrument]}";
        });
        return true;
    }

    public static bool Allows(long eventTick) => !IsActive || change.Allows(eventTick);

    public static void Rebase()
    {
        if (!IsActive || session is not { } playback) return;
        change.Rebase(playback.GetCurrentTime<MidiTimeSpan>().TimeSpan,
            playback.GetDuration<MidiTimeSpan>().TimeSpan, playback.TempoMap);
    }

    public static void Tick()
    {
        var playback = session;
        if (playback == null) return;
        if (!ReferenceEquals(playback, MidiBard.CurrentPlayback)) { Cancel(); return; }
        if (!api.ClientState.IsLoggedIn || character != api.Player.ContentId || party != api.PartyList.PartyId || api.PartyList.Length < 2)
        {
            MidiPlayerControl.Pause();
            Cancel();
            return;
        }
        try
        {
            var position = playback.GetCurrentTime<MidiTimeSpan>().TimeSpan;
            var duration = playback.GetDuration<MidiTimeSpan>().TimeSpan;
            if (!playback.IsRunning && position >= duration)
            {
                if (finishedAt == 0) finishedAt = Environment.TickCount64;
                // Let the existing instrument compensation buffer drain at natural completion.
                if (Environment.TickCount64 - finishedAt >= 500) Cancel();
                return;
            }
            finishedAt = 0;
            var performing = MidiBard.AgentPerformance.InPerformanceMode;
            if (wasPerforming && !performing && !change.IsChanging)
            {
                change.Fail();
                MidiBard.BardPlayDevice.ClearPlaybackNotes();
                status = "当前声部静音：请重新选择乐器";
            }
            wasPerforming = performing;
            var action = change.Advance(Environment.TickCount64, (uint)MidiBard.CurrentInstrumentWithTone, position, duration, playback.TempoMap);
            if (action is { } target)
            {
                var generation = change.Generation;
                PerformActions.DoPerformActionOnTick(target, () => ReferenceEquals(playback, session)
                    && ReferenceEquals(playback, MidiBard.CurrentPlayback) && generation == change.Generation && change.IsChanging,
                    () => MidiBard.PlayingGuitar && target >= 24);
            }
            if (change.Phase == LiveSwitchPhase.WaitingBar)
            {
                var bar = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(change.ResumeTick, playback.TempoMap).Bars + 1;
                status = $"{MidiBard.InstrumentStrings[change.Target]}：等待第 {bar} 小节";
            }
            else if (change.Phase == LiveSwitchPhase.Joined) status = $"已恢复：{MidiBard.InstrumentStrings[change.Target]}";
            else if (change.Phase == LiveSwitchPhase.Failed) status = "换乐器未完成或已近曲尾：声部静音，可重试";
        }
        catch (Exception ex)
        {
            change.Fail();
            status = "换乐器失败：声部保持静音";
            api.PluginLog.Warning(ex, "Live instrument switch failed");
        }
    }

    public static void Cancel()
    {
        Interlocked.Increment(ref sessionVersion);
        var previous = session;
        session = null;
        change.Reset();
        status = "";
        if (previous != null)
        {
            MidiBard.BardPlayDevice.ClearPlaybackNotes();
            EnsembleManager.InvokeEnsembleStop();
        }
    }
}
