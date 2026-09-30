using System.Diagnostics;
using System.Text.Json;
using BardStage;
using BardStage.Core;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using Midibard.Playlib;
using MidiBard.Control;
using MidiBard.Control.MidiControl.PlaybackInstance;

if (args.Contains("--burst-output-check"))
{
    using var nativeOnly = MidiBard.Managers.Agents.AgentPerformance.Instance;
    BurstOutputChecks.Run();
    return;
}

using var device = new BardPlayDevice();
using var native = MidiBard.Managers.Agents.AgentPerformance.Instance;
using var playback = new BufferedPlayback(device);
var engineFinished = false;
var pendingAtEof = 0;
long rawAt = 0, protectedAt = 0;
var finishes = 0;
playback.Finished += (_, _) =>
{
    rawAt = Stopwatch.GetTimestamp();
    pendingAtEof = device.PlaybackOutputState.PendingEvents;
    Volatile.Write(ref engineFinished, true);
};
using var observer = new PlaybackObserver(s =>
{
    if (s.Kind == PlaybackSignalKind.Finished) { finishes++; protectedAt = Stopwatch.GetTimestamp(); }
}, _ =>
{
    // Shorten only the quiet allowance in this test; production uses six seconds.
    var tail = new PlaybackTailGuard(TimeSpan.FromMilliseconds(100));
    return () => { var state = device.PlaybackOutputState; return tail.IsReady(state.PendingEvents, state.Revision); };
});
observer.Attach(playback, "output-buffer.mid"); playback.Start();
var deadline = Stopwatch.StartNew();
while (!Volatile.Read(ref engineFinished))
{
    if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("MIDI engine did not finish");
    Thread.Sleep(2);
}
Check(pendingAtEof > 0, "real MIDI Finished precedes drain of production compensation buffer");
Check(finishes == 0, "observer has not published Finished at engine EOF");
while (finishes == 0)
{
    observer.Poll();
    if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Output did not drain");
    Thread.Sleep(2);
}
observer.Poll();
var events = Playlib.Events.ToArray();
Check(events.Select(e => (e.Down, e.Note)).SequenceEqual(new[] { (true, 12), (false, 12), (true, 16), (false, 16), (true, 19), (false, 19) }),
    "all three notes and releases reach the test game adapter through the real output buffer");
Check(events.Last().At > rawAt && protectedAt > events.Last().At && finishes == 1,
    "protected Finished occurs once after the final actual dispatch");
Check(device.PlaybackOutputState.PendingEvents == 0 && device.PlaybackOutputState.Revision == 6,
    "output snapshot reports all six events drained");
Check(Stopwatch.GetElapsedTime(events.Last().At, protectedAt).TotalMilliseconds >= 100,
    "quiet allowance is counted from output drain, not MIDI EOF");
Console.WriteLine(JsonSerializer.Serialize(new
{
    pendingAtEof,
    outputEvents = events.Length,
    finalDispatchAfterEofMs = Stopwatch.GetElapsedTime(rawAt, events.Last().At).TotalMilliseconds,
    completionAfterFinalDispatchMs = Stopwatch.GetElapsedTime(events.Last().At, protectedAt).TotalMilliseconds,
    realGameAudio = false
}));
LargeCompensationChecks.Run(device);
DeadlineOutputChecks.Run();
BurstOutputChecks.Run();

static void Check(bool value, string text)
{ if (!value) throw new InvalidOperationException(text); Console.WriteLine("PASS: " + text); }

sealed class BufferedPlayback : Playback
{
    private readonly BardPlayDevice device;
    private readonly Guid largePlan;
    public BufferedPlayback(BardPlayDevice device, Guid largePlan = default) : base(Events(), TempoMap.Default,
        new PlaybackSettings { ClockSettings = new MidiClockSettings { CreateTickGeneratorCallback = () => new HighPrecisionTickGenerator() } })
    { this.device = device; this.largePlan = largePlan; }
    protected override bool TryPlayEvent(MidiEvent e, object metadata)
    { device.SendEventWithMetadata(e, metadata, largePlan); return true; }
    private static IEnumerable<TimedEventWithMetadata> Events()
    {
        foreach (var (pitch, start, end) in new[] { (60, 0L, 24L), (64, 24L, 48L), (67, 48L, 64L) })
        {
            yield return new(new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)80), start,
                new BardPlayDevice.MidiPlaybackMetaData(0, start, pitch));
            yield return new(new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0), end,
                new BardPlayDevice.MidiPlaybackMetaData(0, end, pitch));
        }
    }
}
