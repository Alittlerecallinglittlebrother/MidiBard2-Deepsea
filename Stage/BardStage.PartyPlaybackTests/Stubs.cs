using System.Reflection;

namespace BardStage.Core { public enum QueuePlaybackMode { Solo, Ensemble } }
namespace BardStage
{
    public interface IStagePlaybackPort { }
    public sealed record RoomPartyState(long PartyId, ulong SelfCid, ulong LeaderCid);
    public interface IRoomEnsembleBackend : IStagePlaybackPort { }
}
namespace Melanchall.DryWetMidi.Multimedia
{
    public class Playback
    {
        public string FilePath { get; set; } = "";
        public object MidiFileConfig { get; set; } = new();
        public object TrackInfos { get; set; } = new();
        public void SyncTrackStatusWithMidiFileConfig() { }
        public uint GetInstrumentId() => 1;
    }
}
namespace MidiBard
{
    internal static class MidiBard
    {
        internal static readonly Configuration config = new();
        internal static bool SlaveMode;
        internal static bool IsPlaying;
        internal static Melanchall.DryWetMidi.Multimedia.Playback? CurrentPlayback;
        internal static readonly Performance AgentPerformance = new();
        internal static readonly Metronome AgentMetronome = new();
        internal static readonly UiStub Ui = new();
    }
    internal sealed class Configuration
    {
        public bool playOnMultipleDevices = true, AutoAssignEnsembleTracks = true, SyncClients = true, MonitorOnEnsemble = true;
        public bool UpdateInstrumentBeforeReadyCheck, useChatPlaylistSync;
        public float PlaySpeed;
        public void SetTransposeGlobal(int value) { }
    }
    internal sealed class Performance { public bool InPerformanceMode = true; }
    internal sealed class Metronome { public bool EnsembleModeRunning; }
    internal sealed class UiStub { public void OpenMainWindow() { } }
    internal static class api
    {
        internal static readonly ClientStateStub ClientState = new();
        internal static readonly PlayerStub Player = new();
        internal static readonly PartyListStub PartyList = new();
        internal static readonly LogStub PluginLog = new();
        internal static readonly ChatGuiStub ChatGui = new();
        internal static void LogDebug(string text) { }
    }
    internal sealed class ClientStateStub { public bool IsLoggedIn = true; }
    internal sealed class PlayerStub { public ulong ContentId = 1; }
    internal sealed class Member(ulong id, string name, uint world)
    {
        public ulong ContentId = id;
        public string Name = name;
        public WorldStub World = new(world);
    }
    internal sealed class WorldStub(uint id) { public uint RowId = id; }
    internal sealed class PartyListStub : List<Member>
    {
        public int Length => Count;
        public long PartyId = 1;
        public ulong Leader = 1;
    }
    internal sealed class LogStub { public void Warning(Exception ex, string message) { } }
    internal sealed class ChatGuiStub { public void PrintError(string message) { } }
}
namespace MidiBard.Managers.Ipc
{
    internal static class PartyExtensions
    {
        public static global::MidiBard.Member? GetPartyLeader(this global::MidiBard.PartyListStub list) => list.FirstOrDefault(m => m.ContentId == list.Leader);
        public static bool IsPartyLeader(this global::MidiBard.PartyListStub list) => list.Leader == global::MidiBard.api.Player.ContentId;
    }
}
namespace MidiBard.Managers
{
    internal static class PlaylistManager
    {
        public static List<SongStub> FilePathList = [];
        public static bool IsLoading;
        public static object CurrentContainer = new();
        public static readonly List<int> Loads = [];
        public static TaskCompletionSource? Barrier;
        public static async Task<bool> LoadPlayback(int index, bool startPlaying = false, bool sync = true, CancellationToken token = default)
        {
            Loads.Add(index);
            if (Barrier != null) await Barrier.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            MidiBard.CurrentPlayback = new() { FilePath = FilePathList[index].FilePath };
            return true;
        }
        public static Task AddAsync(string[] paths) { FilePathList.AddRange(paths.Select(p => new SongStub(p))); return Task.CompletedTask; }
        public static void RemoveLocal(int index) { }
        public static void MoveSongToIndexLocal(int source, int target) { }
        public static object LoadLastPlaylist() => new();
    }
    internal sealed record SongStub(string FilePath);
    internal static class AutomaticEnsembleAssignment
    {
        public static bool IsEnabled = true;
        public static string CaptureOrderToken() => "auto=1,2";
        public static bool AcceptOrderToken(string token, ulong leader) => leader == api.PartyList.Leader;
        public static object Create(object tracks, object config) => new();
        public static IDisposable BeginSoloLoad() => new Scope();
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }
    internal static class MidiFileConfigManager
    {
        public static void LoadDefaultPerformer() { }
        public static object GetMidiConfigFromFile(string path) => new();
    }
    internal static class EnsembleManager
    {
        public static int ReadyCount;
        public static void BeginEnsembleReadyCheck() { ReadyCount++; }
        public static void StopEnsemble() { }
    }
}
namespace MidiBard.IPC
{
    internal static class IPCHandles
    {
        public static void UpdateMidiFileConfig(object config) { }
        public static void UpdateInstrument(bool value) { }
    }
}
namespace MidiBard.Control.CharacterControl
{
    internal static class SwitchInstrument
    {
        public static bool SwitchingInstrument;
        public static Task SwitchToAsync(uint id) => Task.CompletedTask;
        public static void SwitchToContinue(uint id) { }
    }
}
namespace MidiBard.Control.MidiControl
{
    internal static class MidiPlayerControl
    {
        public static void StopLrc() { }
        public static void Stop() { MidiBard.IsPlaying = false; }
        public static void DoPlay() { MidiBard.IsPlaying = true; }
        public static void Play() { MidiBard.IsPlaying = true; }
        public static void Pause() { MidiBard.IsPlaying = false; }
    }
}
namespace MidiBard.Util
{
    internal static class Chat
    {
        public static readonly List<string> Sent = [];
        public static bool DeferMessages;
        public static readonly Queue<(string Message, Func<bool>? Valid)> Pending = [];
        public static void SendMessage(string message, Func<bool>? stillValid = null)
        {
            if (DeferMessages) Pending.Enqueue((message, stillValid));
            else if (stillValid?.Invoke() != false) Sent.Add(message);
        }
        public static void Flush()
        {
            while (Pending.TryDequeue(out var item))
                if (item.Valid?.Invoke() != false) Sent.Add(item.Message);
        }
    }
}
namespace MidiBard.StageIntegration
{
    internal static class EnsembleTransport
    {
        public static readonly List<string> Sent = [];
        public static void Receive(string[] values) { }
        public static void Send(string value) { Sent.Add(value); }
    }
}
namespace BardMusicPlayer.XIVMIDI
{
    internal enum BMLDownload { Playback }
    internal sealed class XIVMidiApi
    {
        public static readonly XIVMidiApi Instance = new();
        public void GetMidiFile(string name, BMLDownload mode, bool bmp) { }
    }
}
public class MessageProxy : DispatchProxy
{
    internal Dictionary<string, object> Values = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Values.GetValueOrDefault(method!.Name[4..]);
}
