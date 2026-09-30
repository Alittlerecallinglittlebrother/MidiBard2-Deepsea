// Copyright (C) 2022 akira0245
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see https://github.com/akira0245/MidiBard/blob/master/LICENSE.
//
// This code is written by akira0245 and was originally used in the MidiBard project. Any usage of this code must prominently credit the author, akira0245, and indicate that it was originally used in the MidiBard project.

using System;
using System.Collections.Generic;
using System.Linq;
using BardStage.Core;
using BardStage.Core.Rooms;

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

using Midibard.Playlib;

using MidiBard.Managers;
using MidiBard.Managers.Agents;

using static Dalamud.api;

namespace MidiBard.Control;

public partial class BardPlayDevice : IOutputDevice
{
    private static GuitarToneMode PlaybackToneMode => MidiBard.CurrentPlayback?.MidiFileConfig is { LeaderDistributed: true }
        ? GuitarToneMode.OverrideByTrack : MidiBard.config.GuitarToneMode;
    private static bool PlaybackAdaptNotes => MidiBard.CurrentPlayback?.MidiFileConfig is { LeaderDistributed: true } assigned
        ? assigned.AdaptNotes : MidiBard.config.AdaptNotesOOR;
    public abstract record MidiEventMetaData;
    public record MidiDeviceMetaData : MidiEventMetaData;
    public record MidiPlaybackMetaData(int TrackIndex, long Time, int EventValue) : MidiEventMetaData
    {
        public int EventValueTransposed => EventValue >= 0 ? BardPlayDevice.GetNoteNumberTranslatedByTrack(EventValue, TrackIndex) : EventValue;
    }
    private readonly MidiClock PlaybackTicker;
    private readonly PriorityQueue<OutputEvent, (double Due, int Order, long Sequence)> pending = new();
    private readonly object outputGate = new();
    private readonly Func<double> nowClock;
    private int largePending;
    private long outputRevision, sequence, dispatched, faults;
    private double lastTick, maxGap, maxLate;
    private Guid largeOutputPlan;
    private int largePressedNote = -1;
    private bool scheduledOutput, outputActivated;
    private string outputIssue;
    private readonly Queue<string> lateSamples = new();
    private sealed record OutputEvent(MidiEvent Event, MidiPlaybackMetaData Metadata, Guid Plan, double Target, object Owner)
    {
        public double Due { get; set; } = Target;
        public ScheduledNote Note { get; set; }
    }
    internal sealed record ScheduledEvent(MidiEvent Event, MidiPlaybackMetaData Metadata, double Seconds);

    public (int PendingEvents, long Revision) PlaybackOutputState
    { get { lock (outputGate) return (pending.Count, outputRevision); } }
    internal PlaybackTimingDiagnostics OutputTiming
    { get { lock (outputGate) return new(dispatched, maxLate, maxGap, faults); } }
    internal string OutputIssue(Guid plan)
    { lock (outputGate) return plan == largeOutputPlan ? outputIssue : null; }
    internal string TimingSummary
    { get { lock (outputGate) return $"plan={largeOutputPlan:N} sent={dispatched} maxLateMs={maxLate:F3} maxTickGapMs={maxGap:F3} faults={faults} expiredNotes={expiredScheduledNotes} crowdedNotes={crowdedScheduledNotes} ignoredReleases={ignoredScheduledReleases} issue={outputIssue}; " + string.Join("; ", lateSamples); } }
    internal int LargePlaybackPending(Guid planId)
    { lock (outputGate) return planId != Guid.Empty && planId == largeOutputPlan ? largePending : 0; }

    internal void BeginLargePlaybackOutput(Guid planId)
    {
        if (planId == Guid.Empty) throw new ArgumentException("A large playback needs a plan", nameof(planId));
        lock (outputGate)
        {
            CancelLargePlaybackOutput(largeOutputPlan);
            pending.Clear(); largePending = 0; outputRevision++;
            largeOutputPlan = planId; scheduledOutput = false; outputActivated = true;
            outputIssue = null; dispatched = faults = 0; maxLate = maxGap = 0; lastTick = 0; lateSamples.Clear();
            ClearScheduledNoteOwnership();
            expiredScheduledNotes = crowdedScheduledNotes = ignoredScheduledReleases = 0;
            lastnoteon = (new MidiPlaybackMetaData(-1, -1, -1), 0);
        }
    }
    internal void PrepareLargePlaybackOutput(Guid plan, double start, double speed, bool compensate, IEnumerable<ScheduledEvent> events)
    {
        if (!double.IsFinite(start) || !double.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(start));
        lock (outputGate)
        {
            BeginLargePlaybackOutput(plan); scheduledOutput = true; outputActivated = false;
            try
            {
                var prepared = new List<(OutputEvent Event, double ScoreTime)>();
                foreach (var e in events.OrderBy(e => e.Seconds))
                {
                    if (!double.IsFinite(e.Seconds) || e.Seconds < 0) throw new ArgumentOutOfRangeException(nameof(events));
                    // Velocity-zero NoteOn is a release, not a fresh attack. Limit
                    // normalization and note-instance ownership to the scheduled path.
                    MidiEvent midiEvent = e.Event is NoteOnEvent zero && (int)zero.Velocity == 0
                        ? new NoteOffEvent(zero.NoteNumber, (SevenBitNumber)0) { Channel = zero.Channel } : e.Event;
                    prepared.Add((QueuePlaybackMidiEventLocked(midiEvent, e.Metadata, plan, start + e.Seconds / speed, compensate), e.Seconds));
                }
                PrepareScheduledNoteLinks(prepared);
            }
            catch { CancelLargePlaybackOutput(plan); throw; }
        }
    }
    internal void ActivateLargePlaybackOutput(Guid plan)
    {
        lock (outputGate)
        {
            if (plan != largeOutputPlan || !scheduledOutput) throw new InvalidOperationException("预约输出已失效");
            outputActivated = true; lastTick = 0;
        }
    }
    internal void CancelLargePlaybackOutput(Guid planId)
    {
        if (planId == Guid.Empty) return;
        lock (outputGate)
        {
            var keep = pending.UnorderedItems.Where(e => e.Element.Plan != planId).ToArray();
            pending.Clear(); foreach (var e in keep) pending.Enqueue(e.Element, e.Priority);
            if (largeOutputPlan == planId)
            {
                largeOutputPlan = Guid.Empty; largePending = 0; outputActivated = false; scheduledOutput = false;
                if (largePressedNote >= 0 && MidiBard.AgentPerformance.InPerformanceMode) KeyUp(largePressedNote);
                largePressedNote = -1;
                ClearScheduledNoteOwnership();
                lastnoteon = (new MidiPlaybackMetaData(-1, -1, -1), 0);
            }
            outputRevision++;
        }
    }
    internal void CancelLegacyPlaybackOutput()
    {
        lock (outputGate)
        {
            var keep = pending.UnorderedItems.Where(e => e.Element.Plan != Guid.Empty).ToArray();
            pending.Clear(); foreach (var e in keep) pending.Enqueue(e.Element,e.Priority);
            if (largeOutputPlan == Guid.Empty && MidiBard.AgentPerformance.InPerformanceMode)
            {
                var note = MidiBard.AgentPerformance.noteNumber - 39;
                if (note is >= 0 and <= 36) KeyUp(note);
            }
            lastnoteon = (new MidiPlaybackMetaData(-1,-1,-1),0); outputRevision++;
        }
    }
    public BardPlayDevice() : this(() => TransportClock.Now) { }
    internal BardPlayDevice(Func<double> clock)
    {
        nowClock = clock;
        Channels = new ChannelState[16]; CurrentChannel = FourBitNumber.MinValue;
        PlaybackTicker = new MidiClock(false, new HighPrecisionTickGenerator(), TimeSpan.FromMilliseconds(1));
        PlaybackTicker.Ticked += PlaybackTickerTicked; PlaybackTicker.Restart();
    }
    private void PlaybackTickerTicked(object sender, EventArgs e)
    {
        if (IsDisposed) return;
        lock (outputGate) DrainCurrentTick();
    }
    private void DrainCurrentTick()
    {
        var now = nowClock();
        if (largeOutputPlan != Guid.Empty && MidiBard.CurrentPlayback?.LargePlanId != largeOutputPlan)
            CancelLargePlaybackOutput(largeOutputPlan);
        if (largeOutputPlan != Guid.Empty && scheduledOutput && scheduledOutputOwner != null
            && !ReferenceEquals(scheduledOutputOwner, MidiBard.CurrentPlayback))
            CancelLargePlaybackOutput(largeOutputPlan);
        if (scheduledOutput && !outputActivated) return;
        if (lastTick != 0 && pending.Count > 0) maxGap = Math.Max(maxGap, (now - lastTick) * 1000);
        lastTick = now;
        while (pending.TryPeek(out var item, out _) && item.Due <= now)
        {
            pending.Dequeue();
            if (item.Plan != Guid.Empty && item.Plan == largeOutputPlan) largePending--;
            if (!ReferenceEquals(item.Owner, MidiBard.CurrentPlayback)) continue;
            if (item.Plan != Guid.Empty && item.Plan != largeOutputPlan) continue;
            // A release owns one MIDI note instance, not every later note of the
            // same pitch. Skipped/replaced/orphan releases never touch a held key.
            if (scheduledOutput && item.Plan != Guid.Empty && item.Event is NoteOffEvent
                && (item.Note == null || !ReferenceEquals(item.Note, activeScheduledNote)))
            {
                ignoredScheduledReleases++; now = nowClock(); continue;
            }
            if (!MidiBard.AgentPerformance.InPerformanceMode)
            {
                if (item.Plan != Guid.Empty && scheduledOutput) FailScheduledOutput("角色已退出演奏状态，请重新下发");
                else { pending.Clear(); outputRevision++; }
                return;
            }
            var late = (now - item.Due) * 1000; maxLate = Math.Max(maxLate, late);
            if (item.Plan != Guid.Empty && late > 10)
            {
                if (lateSamples.Count == 32) lateSamples.Dequeue();
                lateSamples.Enqueue($"midi={item.Metadata.Time} type={item.Event.EventType} due={item.Due:F6} actual={now:F6} lateMs={late:F3}");
            }
            // Do not burst an expired phrase into the game. Stop the whole plan,
            // releasing its held key; the framework propagates this fault to the group.
            if (item.Plan != Guid.Empty && scheduledOutput && late > 80 + ScheduledTimeEpsilon * 1000)
            {
                FailScheduledOutput($"音符输出迟到 {late:F0} 毫秒，已停止本机输出，请重新下发");
                return;
            }
            if (item.Plan != Guid.Empty && scheduledOutput && SkipScheduledAttack(item, now))
            { now = nowClock(); continue; }
            try
            {
                var played = PlayMidiEvent(item.Event, item.Metadata.TrackIndex, false);
                dispatched++;
                if (!played && scheduledOutput && item.Plan != Guid.Empty && item.Event is NoteEvent failedNote
                    && GetNoteNumberTranslatedByTrack(failedNote.NoteNumber, item.Metadata.TrackIndex) is >= 0 and <= 36)
                {
                    FailScheduledOutput("游戏按键接口未接受音符，已停止本机输出，请重新下发");
                    return;
                }
                if (played && item.Plan != Guid.Empty && item.Event is NoteEvent note)
                {
                    var translated = GetNoteNumberTranslatedByTrack(note.NoteNumber, item.Metadata.TrackIndex);
                    if (item.Event is NoteOnEvent) largePressedNote = translated;
                    else if (translated == largePressedNote) largePressedNote = -1;
                    if (scheduledOutput) RegisterScheduledDispatch(item, nowClock());
                }
            }
            catch (Exception ex)
            {
                if (scheduledOutput && item.Plan != Guid.Empty)
                    FailScheduledOutput("音符输出接口发生异常，已停止本机输出，请重新下发");
                PluginLog.Error(ex, "exception in deadline output");
                if (scheduledOutput && item.Plan != Guid.Empty) return;
            }
            // Re-read real time, never synthesize elapsed time from callback count.
            now = nowClock();
        }
    }

    // Called under outputGate. Keep the plan/fault visible to the framework so it
    // can stop the group; never resume this queue merely because callbacks resume.
    private void FailScheduledOutput(string issue)
    {
        faults++; outputIssue = issue;
        pending.Clear(); largePending = 0; outputRevision++; outputActivated = false;
        var held = largePressedNote; largePressedNote = -1;
        ClearScheduledNoteOwnership();
        try { if (held >= 0 && MidiBard.AgentPerformance.InPerformanceMode) KeyUp(held); }
        catch (Exception ex) { PluginLog.Error(ex, "failed to release key after output fault"); }
    }

    private (MidiPlaybackMetaData metadata, int delayms) lastnoteon = (new MidiPlaybackMetaData(-1, -1, -1), 0);
    public void QueuePlaybackMidiEvent(MidiEvent midiEvent, MidiPlaybackMetaData metadata, Guid largePlanId = default)
    {
        var enqueuedAt = nowClock();
        lock (outputGate)
        {
            // Include chord-order state in the cancellation boundary.
            if (largePlanId != Guid.Empty && (largePlanId != largeOutputPlan || MidiBard.CurrentPlayback?.LargePlanId != largePlanId)) return;
            QueuePlaybackMidiEventLocked(midiEvent, metadata, largePlanId, enqueuedAt);
        }
    }

    private OutputEvent QueuePlaybackMidiEventLocked(MidiEvent midiEvent, MidiPlaybackMetaData metadata, Guid largePlanId, double eventTime, bool compensate = true)
    {
        var trackIndex = metadata.TrackIndex;

        int delayMs;
        if (!compensate) delayMs = 0;
        else if (midiEvent is not NoteEvent noteEvent)
        {
            delayMs = EnsembleManager.GetCompensationNew(MidiBard.CurrentInstrumentWithTone, -1, largePlanId != Guid.Empty);
        }
        else
        {
            delayMs = EnsembleManager.GetCompensationNew(MidiBard.CurrentInstrumentWithTone, GetNoteNumberTranslatedByTrack(noteEvent.NoteNumber, trackIndex), largePlanId != Guid.Empty);

            if (midiEvent is NoteOnEvent noteOn)
            {
                //same track and same time
                if (metadata.TrackIndex == lastnoteon.metadata.TrackIndex && metadata.Time == lastnoteon.metadata.Time)
                {
                    var eventValueTransposed = metadata.EventValueTransposed;
                    var lastEventValueTransposed = lastnoteon.metadata.EventValueTransposed;
                    PluginLog.Debug($"chord note t{metadata.Time,6}/{lastnoteon.metadata.Time,-6} noteNumber:{noteOn.NoteNumber} delay:{delayMs}/{lastnoteon.delayms} eventValue:{eventValueTransposed}/{lastEventValueTransposed}");
                    //new note delay is > previous delay
                    if (delayMs < lastnoteon.delayms && eventValueTransposed > lastEventValueTransposed
                        || delayMs > lastnoteon.delayms && eventValueTransposed < lastEventValueTransposed)
                    {
                        //new note is lower than previous note
                        PluginLog.Warning($"correct delayms from {delayMs} -> {lastnoteon.delayms}");
                        delayMs = lastnoteon.delayms;
                    }
                }
                lastnoteon = (metadata, delayMs);
            }
        }

        var due = eventTime + Math.Max(0, delayMs) / 1000d;
        if (!double.IsFinite(due)) throw new ArgumentOutOfRangeException(nameof(eventTime));
        var output = new OutputEvent(midiEvent, metadata, largePlanId, due, MidiBard.CurrentPlayback);
        pending.Enqueue(output, (due, metadata.EventValueTransposed, sequence++));
        if (largePlanId != Guid.Empty) largePending++;
        outputRevision++;
        return output;
    }

    private struct ChannelState
    {
        public SevenBitNumber Program { get; set; }

        public ChannelState(SevenBitNumber? program)
        {
            this.Program = program ?? SevenBitNumber.MinValue;
        }
    }

    private readonly ChannelState[] Channels;

    private FourBitNumber CurrentChannel;

    public void ResetChannelStates()
    {
        for (var i = 0; i < Channels.Length; i++)
        {
            Channels[i].Program = SevenBitNumber.MinValue;
        }
    }

    public event EventHandler<MidiEventSentEventArgs> EventSent;

    public void PrepareForEventsSending()
    {
    }

    [Obsolete("Use SendEventWithMetadata Instead", true)]
    public void SendEvent(MidiEvent midiEvent)
    {
    }

    public void SendEventWithMetadata(MidiEvent midiEvent, object metadata, Guid largePlanId = default)
    {
        if (IsDisposed) return;
        if (!MidiBard.AgentPerformance.InPerformanceMode) return;

        switch (metadata)
        {
            case MidiDeviceMetaData:
                {
                    PlayMidiEvent(midiEvent, 0, true);
                    return;
                }
            case MidiPlaybackMetaData midiPlaybackMeta:
                {
                    if (largePlanId != Guid.Empty && MidiBard.CurrentPlayback?.LargePlanId != largePlanId) return;
                    if (MidiBard.CurrentPlayback?.TrackInfos[midiPlaybackMeta.TrackIndex].IsPlaying != true) return;
                    if (largePlanId != Guid.Empty && MidiBard.CurrentPlayback.UseLargeInstrumentCompensation)
                    {
                        QueuePlaybackMidiEvent(midiEvent, midiPlaybackMeta, largePlanId);
                        return;
                    }
                    if (EnsembleManager.EnsembleRunning)
                    {
                        QueuePlaybackMidiEvent(midiEvent, midiPlaybackMeta);
                        return;
                    }

                    PlayMidiEvent(midiEvent, midiPlaybackMeta.TrackIndex, false);
                    break;
                }
        }
    }

    private unsafe bool PlayMidiEvent(MidiEvent midiEvent, int trackIndex, bool isDevice)
    {
        if (IsDisposed) return false;

        switch (midiEvent)
        {
            case ProgramChangeEvent programChangeEvent:
                if ((bool)(MidiBard.CurrentPlayback?.TrackInfos[trackIndex].IsProgramElectricGuitar) && PlaybackToneMode == GuitarToneMode.ProgramElectricGuitarMode)
                    Channels[programChangeEvent.Channel].Program = programChangeEvent.ProgramNumber;
                else
                    ProcessProgramChange(programChangeEvent);
                break;
            case NoteEvent noteEvent:
                var noteNum = isDevice ? GetNoteNumberTranslated(noteEvent.NoteNumber) : GetNoteNumberTranslatedByTrack(noteEvent.NoteNumber, trackIndex);
                if (noteNum is < 0 or > 36) return false;

                if (MidiBard.PlayingGuitar)
                {
                    if ((MidiBard.CurrentPlayback != null) && (bool)(MidiBard.CurrentPlayback?.TrackInfos[trackIndex].IsProgramElectricGuitar) && PlaybackToneMode == GuitarToneMode.ProgramElectricGuitarMode)
                    {
                        ApplyToneByChannel(noteEvent.Channel);
                    }
                    else
                    {
                        switch (PlaybackToneMode)
                        {
                            case GuitarToneMode.Off:
                                break;
                            case GuitarToneMode.Standard:
                            case GuitarToneMode.Simple:
                                {
                                    ApplyToneByChannel(noteEvent.Channel);
                                    break;
                                }
                            case GuitarToneMode.OverrideByTrack when !isDevice:
                                {
                                    ApplyToneByTrack(trackIndex);
                                    break;
                                }
                        }
                    }
                }

                return noteEvent switch
                {
                    NoteOnEvent => KeyDown(noteNum),
                    NoteOffEvent => KeyUp(noteNum),
                    _ => false,
                };
        }

        return false;
    }

    private static unsafe bool KeyUp(int noteNum)
    {
        var agentPerformance = AgentPerformance.Instance;
        //not holding same note. skip.
        if (agentPerformance.Struct->CurrentPressingNote - 39 != noteNum)
        {
            // PluginLog.Verbose($"[SkipKUp] {noteNum} != {agentPerformance.Struct->CurrentPressingNote - 39}");
            return true;
        }

        // only release a key when it been pressing
        if (Playlib.ReleaseKey(noteNum))
        {
            // PluginLog.Debug($"[KeyUp  ] {noteNum}");
            agentPerformance.Struct->CurrentPressingNote = -100;
            return true;
        }

        return false;
    }

    private static unsafe bool KeyDown(int noteNum)
    {
        var agentPerformance = AgentPerformance.Instance;
        //currently holding the same note?
        if (agentPerformance.noteNumber - 39 == noteNum)
        {
            // release repeated note in order to press it again
            if (Playlib.ReleaseKey(noteNum))
            {
                agentPerformance.Struct->CurrentPressingNote = -100;
                // PluginLog.Verbose($"[ReKeyUp] {noteNum}");
            }
        }

        if (Playlib.PressKey(noteNum, ref agentPerformance.Struct->NoteOffset, ref agentPerformance.Struct->OctaveOffset))
        {
            agentPerformance.Struct->CurrentPressingNote = noteNum + 39;
            // PluginLog.Debug($"[KeyDown] {noteNum}");
            return true;
        }

        return false;
    }

    private void ApplyToneByTrack(int trackIndex)
    {
        int tone = MidiBard.config.TrackStatus[trackIndex].Tone;
        Playlib.GuitarSwitchTone(tone);
    }

    private void ApplyToneByChannel(FourBitNumber channel)
    {
        CurrentChannel = channel;
        if (!TryGetToneFromProgram(Channels[channel].Program, out var tone)) return;
        Playlib.GuitarSwitchTone(tone);
    }

    private void ProcessProgramChange(ProgramChangeEvent programChangeEvent)
    {
        switch (PlaybackToneMode)
        {
            case GuitarToneMode.Off:
                break;
            case GuitarToneMode.Standard:
                Channels[programChangeEvent.Channel].Program = programChangeEvent.ProgramNumber;
                break;
            case GuitarToneMode.Simple:
                for (var i = 0; i < Channels.Length; i++)
                {
                    Channels[i].Program = programChangeEvent.ProgramNumber;
                }
                break;
            case GuitarToneMode.OverrideByTrack:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        return;
    }

    private static bool TryGetToneFromProgram(SevenBitNumber program, out int tone)
    {
        tone = 0;
        if (!MidiBard.ProgramInstruments.TryGetValue(program, out var instrumentId)) return false;
        var instrument = MidiBard.Instruments[instrumentId];
        if (!instrument.IsGuitar) return false;
        tone = instrument.GuitarTone;
        return true;
    }

    static string GetNoteName(NoteEvent note) => $"{note.GetNoteName().ToString().Replace("Sharp", "#")}{note.GetNoteOctave()}";

    public static int GetNoteNumberTranslatedByTrack(int noteNumber, int trackIndex)
    {
        noteNumber += MidiBard.config.TrackStatus[trackIndex].Transpose;
        return GetNoteNumberTranslated(noteNumber);
    }

    private static int GetNoteNumberTranslated(int noteNumber)
    {
        noteNumber = noteNumber - 48 + MidiBard.config.TransposeGlobal;

        if (PlaybackAdaptNotes)
        {
            if (noteNumber < 0)
            {
                noteNumber = (noteNumber + 1) % 12 + 11;
            }
            else if (noteNumber > 36)
            {
                noteNumber = (noteNumber - 1) % 12 + 25;
            }
        }

        return noteNumber;
    }

    private bool IsDisposed;
    private void ReleaseUnmanagedResources()
    {
        IsDisposed = true;
        PlaybackTicker.Ticked -= PlaybackTickerTicked;
        PlaybackTicker.Stop();
        PlaybackTicker.Dispose();
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~BardPlayDevice()
    {
        ReleaseUnmanagedResources();
    }
}
