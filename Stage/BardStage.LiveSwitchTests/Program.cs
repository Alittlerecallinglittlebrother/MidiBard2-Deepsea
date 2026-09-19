using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using MidiBard;
using MidiBard.Managers;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl.PlaybackInstance;
using Plugin = MidiBard.MidiBard;

var checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
TempoMap Map(params MidiEvent[] events) => new MidiFile(new TrackChunk(events))
    { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) }.GetTempoMap();
var common = Map(new TimeSignatureEvent(4, 4));
Check(LiveInstrumentSwitch.NextBar(1919, common) == 1920, "4/4 boundary uses MIDI ticks");
Check(LiveInstrumentSwitch.NextBar(1920, common) == 3840, "request exactly on a downbeat waits for the next bar");
Check(LiveInstrumentSwitch.NextBar(100, Map(new TimeSignatureEvent(3, 4))) == 1440, "3/4 uses three quarter notes");
Check(LiveInstrumentSwitch.NextBar(100, Map(new TimeSignatureEvent(6, 8))) == 1440, "6/8 uses six eighth notes");
var changes = Map(new TimeSignatureEvent(4, 4), new TimeSignatureEvent(3, 4) { DeltaTime = 1920 },
    new SetTempoEvent(250000) { DeltaTime = 0 });
Check(LiveInstrumentSwitch.NextBar(2000, changes) == 3360, "time signature changes select the new bar length");
Check(TimeConverter.ConvertTo<MetricTimeSpan>(3360, changes).TotalMicroseconds == 2750000,
    "tempo changes determine real bar time without assuming fixed BPM");
Check(LiveInstrumentSwitch.NextBar(10, Map()) == 1920, "missing time signature follows MIDI default 4/4");

var state = new LiveInstrumentSwitch();
Check(state.Allows(0), "inactive switch leaves original note output unchanged");
state.Request(5, 0);
Check(!state.Allows(4000) && state.Advance(0, 1, 100, 7680, common) == 0, "switch mutes notes and holsters exactly once");
Check(state.Advance(20, 1, 110, 7680, common) == null, "does not repeatedly send holster while waiting");
Check(state.Advance(30, 0, 120, 7680, common) == 5, "equips only after observing instrument zero");
state.Advance(100, 5, 200, 7680, common);
state.Advance(299, 5, 400, 7680, common);
Check(state.Phase == LiveSwitchPhase.Settling, "instrument stabilization is required before resuming");
state.Advance(300, 5, 410, 7680, common);
Check(!state.Allows(1919) && state.Allows(1920), "first bar note can pass before the next UI frame");
state.Advance(400, 5, 1920, 7680, common);
Check(state.Phase == LiveSwitchPhase.Joined, "rejoins only after the bar boundary");
var prior = state.Generation;
state.Request(2, 500);
state.Request(19, 510);
Check(state.Target == 19 && state.Generation == prior + 2 && !state.Allows(10000), "rapid requests replace pending target and mute stale notes");
state.Advance(7510, 2, 3000, 7680, common);
Check(state.Phase == LiveSwitchPhase.Failed && !state.Allows(10000), "timeout remains silent without stopping other players");
state.Request(19, 8000); state.Advance(8000, 19, 3300, 7680, common); state.Advance(8200, 19, 3400, 7680, common);
Check(state.ResumeTick == 3840, "retry chooses a future bar rather than replaying elapsed notes");
state.Rebase(100, 7680, common);
Check(state.ResumeTick == 1920, "pause or seek reanchors a pending rejoin");
state.Request(5, 9000); state.Advance(9000, 5, 7500, 7680, common); state.Advance(9200, 5, 7550, 7680, common);
Check(state.Phase == LiveSwitchPhase.Failed, "no remaining full bar leaves the part silent at song end");
state.Request(25, 10000);
Check(state.Advance(10000, 24, 100, 7680, common) == 25, "guitar tone changes avoid holstering");
state.Reset(); Check(state.Allows(0), "cancellation releases the switch gate");

var events = Enumerable.Range(0, 64).SelectMany(i => new[]
{
    new TimedEventWithMetadata(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)80), i * 120L, i * 120L),
    new TimedEventWithMetadata(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0), i * 120L + 60, i * 120L + 60)
}).ToArray();
using var playback = new BardPlayback(events, common);
Plugin.CurrentPlayback = playback;
EnsembleContinuity.Begin(playback);
Check(!EnsembleContinuity.IsActive && !EnsembleContinuity.RequestSwitch(5), "default-off feature preserves original switching path");
Plugin.config.ExperimentalLiveInstrumentSwitch = true;
playback.IsSoloPlayback = true;
EnsembleContinuity.Begin(playback);
Check(!EnsembleContinuity.IsActive, "solo playback is not enrolled in experimental ensemble");
playback.IsSoloPlayback = false;
EnsembleContinuity.Begin(playback); playback.Start();
Thread.Sleep(80);
var before = playback.GetCurrentTime<MidiTimeSpan>().TimeSpan;
EnsembleContinuity.RequestSwitch(5); EnsembleContinuity.Tick();
Check(Plugin.BardPlayDevice.Clears > 0 && playback.IsRunning, "production coordinator mutes output without stopping the real MIDI clock");
PerformActions.Flush(); EnsembleContinuity.Tick(); PerformActions.Flush();
EnsembleContinuity.Tick(); Thread.Sleep(250); EnsembleContinuity.Tick();
Check(EnsembleContinuity.IsActive && playback.IsRunning && playback.GetCurrentTime<MidiTimeSpan>().TimeSpan > before,
    "leaving and reentering performance preserves clock and ensemble session");
Check(PerformActions.Executed.TakeLast(2).SequenceEqual(new uint[] { 0, 5 }), "switch uses the normal game action sequence");
playback.Stop(); EnsembleContinuity.Tick(); Thread.Sleep(80); EnsembleContinuity.Tick();
Check(!playback.IsRunning, "pause wins: waiting for a bar never restarts playback");
playback.Start();
EnsembleContinuity.RequestSwitch(2); EnsembleContinuity.Tick();
var actionCount = PerformActions.Executed.Count;
EnsembleContinuity.Cancel(); PerformActions.Flush();
Check(PerformActions.Executed.Count == actionCount, "stop cancels deferred native instrument actions");
EnsembleContinuity.Begin(playback); api.Framework.Defer = true;
EnsembleContinuity.RequestSwitch(19); EnsembleContinuity.Cancel(); api.Framework.Flush(); api.Framework.Defer = false;
Check(!EnsembleContinuity.IsSwitching, "delayed request cannot resurrect a canceled session");
EnsembleContinuity.Begin(playback); api.Framework.Defer = true;
EnsembleContinuity.RequestSwitch(19); EnsembleContinuity.Cancel(); EnsembleContinuity.Begin(playback);
api.Framework.Flush(); api.Framework.Defer = false;
Check(!EnsembleContinuity.IsSwitching, "delayed request cannot enter a new session of the same playback");
EnsembleContinuity.RequestSwitch(0);
Check(!playback.IsRunning && !EnsembleContinuity.IsActive && Plugin.CurrentInstrumentWithTone == 0,
    "explicit holster stops local continuity instead of scheduling a rejoin");
Plugin.CurrentInstrumentWithTone = 1; Plugin.AgentPerformance.InPerformanceMode = true;
playback.Start(); EnsembleContinuity.Begin(playback);
Plugin.AgentPerformance.InPerformanceMode = false; EnsembleContinuity.Tick();
Check(playback.IsRunning && !EnsembleContinuity.Allows(10000), "unexpected performance exit mutes notes while the clock continues");
Plugin.AgentPerformance.InPerformanceMode = true;
EnsembleContinuity.Cancel();
EnsembleContinuity.Begin(playback); api.PartyList.PartyId++;
EnsembleContinuity.Tick();
Check(!playback.IsRunning && !EnsembleContinuity.IsActive, "party departure cancels continuity and pauses local playback");
api.PartyList.PartyId--;
playback.Start(); EnsembleContinuity.Begin(playback);
api.ClientState.IsLoggedIn = false; EnsembleContinuity.Tick(); api.ClientState.IsLoggedIn = true;
Check(!playback.IsRunning && !EnsembleContinuity.IsActive, "logout cancels the session");

using (var ending = new BardPlayback(events.Take(2), common))
{
    Plugin.CurrentPlayback = ending;
    EnsembleContinuity.Begin(ending); ending.MoveToTime(ending.GetDuration<MidiTimeSpan>());
    EnsembleContinuity.Tick();
    Check(EnsembleContinuity.IsActive, "natural completion keeps compensation alive for final note-offs");
    Thread.Sleep(520); EnsembleContinuity.Tick();
    Check(!EnsembleContinuity.IsActive && ending.IsContinuityEnsemble,
        "completed session cancels after buffer drain and retains the ensemble completion marker");
}
Plugin.CurrentPlayback = playback;

var gates = Enumerable.Range(0, 8).Select(_ => new LiveInstrumentSwitch()).ToArray();
var played = Enumerable.Range(0, 8).Select(_ => new ConcurrentQueue<long>()).ToArray();
var players = gates.Select((g, i) => new RecordingPlayback(events, common, g, played[i])).ToArray();
try
{
    foreach (var player in players) player.Start();
    Thread.Sleep(250);
    for (var i = 0; i < 8; i++)
    {
        gates[i].Request((uint)(i + 2), 0);
        gates[i].Advance(0, (uint)(i + 2), 400, 7680, common);
        gates[i].Advance(200, (uint)(i + 2), 410, 7680, common);
    }
    var mutedAt = played.Select(p => p.Count).ToArray();
    Thread.Sleep(500);
    Check(players.All(p => p.IsRunning) && players.All(p => p.GetCurrentTime<MidiTimeSpan>().TimeSpan > 600),
        "all eight real playback clocks continue through concurrent instrument switches");
    Check(played.Select((p, i) => p.Count == mutedAt[i]).All(v => v), "all eight changed parts stay silent before their bar");
    Thread.Sleep(1500);
    Check(played.All(p => p.Any(t => t >= 1920)), "all eight parts resume on the next bar without restarting the song");
    Check(played.All(p => !p.Any(t => t > 300 && t < 1920)), "elapsed notes are skipped instead of replayed in a burst");
}
finally { foreach (var player in players) player.Dispose(); }
Console.WriteLine($"Completed {checks} live-switch checks.");

sealed class RecordingPlayback(IEnumerable<TimedEventWithMetadata> events, TempoMap map, LiveInstrumentSwitch gate, ConcurrentQueue<long> played)
    : Playback(events, map)
{
    protected override bool TryPlayEvent(MidiEvent midiEvent, object metadata)
    {
        var tick = (long)metadata;
        if (midiEvent is NoteOnEvent && gate.Allows(tick)) played.Enqueue(tick);
        return true;
    }
}
