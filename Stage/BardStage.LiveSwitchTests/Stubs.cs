using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using MidiBard.Control.MidiControl.PlaybackInstance;

namespace MidiBard
{
    internal static class MidiBard
    {
        internal static BardPlayback? CurrentPlayback;
        internal static Config config = new();
        internal static Performance AgentPerformance = new();
        internal static Device BardPlayDevice = new();
        internal static int CurrentInstrumentWithTone = 1;
        internal static bool PlayingGuitar => CurrentInstrumentWithTone >= 24;
        internal static Instrument[] Instruments = Enumerable.Range(0, 29).Select(i => new Instrument(i - 24)).ToArray();
        internal static string[] InstrumentStrings = Enumerable.Range(0, 29).Select(i => "Instrument " + i).ToArray();
    }
    internal sealed class Config { public bool ExperimentalLiveInstrumentSwitch; }
    internal sealed class Performance { public bool InPerformanceMode = true; }
    internal sealed class Device { public int Clears; public void ClearPlaybackNotes() => Clears++; }
    internal sealed record Instrument(int GuitarTone);
    internal static class api
    {
        internal static FrameworkService Framework = new();
        internal static PartyService PartyList = new();
        internal static PlayerService Player = new();
        internal static ClientService ClientState = new();
        internal static LogService PluginLog = new();
    }
    internal sealed class FrameworkService
    {
        public bool Defer;
        public Queue<Action> Pending = new();
        public Task RunOnFrameworkThread(Action action)
        {
            if (Defer) Pending.Enqueue(action); else action();
            return Task.CompletedTask;
        }
        public void Flush() { while (Pending.TryDequeue(out var action)) action(); }
    }
    internal sealed class PartyService { public int Length = 8; public long PartyId = 1; }
    internal sealed class PlayerService { public ulong ContentId = 1; }
    internal sealed class ClientService { public bool IsLoggedIn = true; }
    internal sealed class LogService { public void Warning(Exception ex, string message) => Console.WriteLine(message + ": " + ex.Message); }
}
namespace MidiBard.Managers
{
    internal static class EnsembleManager { public static int Stops; public static void InvokeEnsembleStop() => Stops++; }
}
namespace MidiBard.Control.CharacterControl
{
    internal static class SwitchInstrument
    {
        public static Task SwitchToAsync(uint target) { MidiBard.CurrentInstrumentWithTone = (int)target; return Task.CompletedTask; }
    }
    internal static class PerformActions
    {
        public static Queue<Action> Pending = new();
        public static List<uint> Executed = new();
        public static void DoPerformActionOnTick(uint id, Func<bool>? valid = null, Func<bool>? guitar = null) => Pending.Enqueue(() =>
        {
            if (valid?.Invoke() == false) return;
            Executed.Add(id);
            MidiBard.CurrentInstrumentWithTone = (int)id;
            MidiBard.AgentPerformance.InPerformanceMode = id != 0;
        });
        public static void Flush() { while (Pending.TryDequeue(out var action)) action(); }
    }
}
namespace MidiBard.Control.MidiControl
{
    internal static class MidiPlayerControl
    {
        public static void Pause() => MidiBard.CurrentPlayback?.Stop();
        public static void Stop() { Managers.EnsembleContinuity.Cancel(); MidiBard.CurrentPlayback?.Stop(); }
    }
}
namespace MidiBard.Control.MidiControl.PlaybackInstance
{
    internal sealed class TimedEventWithMetadata(MidiEvent midiEvent, long time, long metadata)
        : TimedEvent(midiEvent, time), Melanchall.DryWetMidi.Common.IMetadata
    {
        public object Metadata { get; set; } = metadata;
    }
    internal class BardPlayback : Playback
    {
        public bool IsSoloPlayback, IsContinuityEnsemble;
        public BardPlayback(IEnumerable<TimedEventWithMetadata> events, TempoMap map) : base(events, map) { }
        protected override bool TryPlayEvent(MidiEvent midiEvent, object metadata) => true;
    }
}
namespace Midibard.Playlib { }
