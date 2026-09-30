using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Common;

namespace Dalamud
{
    internal static class api
    {
        public static readonly Log PluginLog = new();
        internal sealed class Log
        {
            public void Debug(string text) { }
            public void Warning(string text) { }
            public void Error(Exception ex, string text) => throw new InvalidOperationException(text, ex);
        }
    }
}
namespace MidiBard
{
    public enum GuitarToneMode { Off, Standard, Simple, OverrideByTrack, ProgramElectricGuitarMode }
    public enum CompensationModes { None, ByInstrument, ByInstrumentNote }
    internal static class MidiBard
    {
        public static readonly Config config = new();
        public static PlaybackConfig CurrentPlayback = new();
        public static readonly Dictionary<SevenBitNumber, uint> ProgramInstruments = new();
        public static readonly Dictionary<uint, Instrument> Instruments = new();
        public static bool PlayingGuitar => false;
        public static int CurrentInstrumentWithTone { get; set; } = 1;
        public static Managers.Agents.AgentPerformance AgentPerformance => Managers.Agents.AgentPerformance.Instance;
    }
    internal sealed class Instrument { public int GuitarTone => 0; public bool IsGuitar => false; }
    internal sealed class Config
    {
        public CompensationModes CompensationMode = CompensationModes.ByInstrument;
        public int[] ManualInstrumentCompensation = Enumerable.Range(0,29).Select(i=>i==1?0:200).ToArray();
        public GuitarToneMode GuitarToneMode { get; set; } = GuitarToneMode.Off;
        public bool AdaptNotesOOR => false;
        public int TransposeGlobal => 0;
        public Track[] TrackStatus = [new()];
    }
    internal sealed class Track
    {
        public bool IsPlaying => true;
        public bool IsProgramElectricGuitar => false;
        public int Tone => 0;
        public int Transpose => 0;
    }
    internal sealed class PlaybackConfig
    {
        public Guid LargePlanId;
        public bool UseLargeInstrumentCompensation;
        public Track[] TrackInfos = [new()];
        public SongConfig MidiFileConfig = new();
    }
    internal sealed class SongConfig { public bool LeaderDistributed => false; public bool AdaptNotes => false; }
}
namespace MidiBard.Managers
{
    internal partial class EnsembleManager
    {
        public static bool EnsembleRunning { get; set; } = true;
    }
}
namespace MidiBard.Managers.Agents
{
    internal unsafe sealed class AgentPerformance : IDisposable
    {
        public static readonly AgentPerformance Instance = new();
        public struct Native { public int CurrentPressingNote, NoteOffset, OctaveOffset; }
        public Native* Struct = (Native*)NativeMemory.AllocZeroed((nuint)sizeof(Native));
        public int noteNumber => Struct->CurrentPressingNote;
        public bool InPerformanceMode { get; set; } = true;
        public void Dispose() { NativeMemory.Free(Struct); Struct = null; }
    }
}
namespace Midibard.Playlib
{
    internal static class Playlib
    {
        public static readonly ConcurrentQueue<(bool Down, int Note, long At)> Events = new();
        public static bool RejectPress;
        public static int RejectReleases;
        public static Action? AfterPress;
        public static Action<bool, int>? Observe;
        public static bool PressKey(int note, ref int offset, ref int octave)
        { if (RejectPress) return false; Events.Enqueue((true, note, System.Diagnostics.Stopwatch.GetTimestamp())); Observe?.Invoke(true, note); AfterPress?.Invoke(); return true; }
        public static bool ReleaseKey(int note)
        { if (RejectReleases > 0) { RejectReleases--; return false; } Events.Enqueue((false, note, System.Diagnostics.Stopwatch.GetTimestamp())); Observe?.Invoke(false, note); return true; }
        public static void GuitarSwitchTone(int tone) { }
    }
}
