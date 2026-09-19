using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace BardStage;

public sealed class StagePlaybackIpc : IDisposable
{
    public const string SnapshotName = "MidiBard.Stage.PlaybackState.V1";
    public const string ChangedName = "MidiBard.Stage.PlaybackStateChanged.V1";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ConcurrentQueue<PlaybackSignal> pending = new();
    private readonly ICallGateProvider<string> snapshot;
    private readonly ICallGateProvider<string, object> changed;
    private readonly Action<Exception> reportError;
    private string snapshotJson = "{\"protocol\":1,\"state\":null}";
    private bool disposed;

    public StagePlaybackIpc(IDalamudPluginInterface pluginInterface, Action<Exception> reportError)
    {
        this.reportError = reportError;
        snapshot = pluginInterface.GetIpcProvider<string>(SnapshotName);
        changed = pluginInterface.GetIpcProvider<string, object>(ChangedName);
        snapshot.RegisterFunc(() => Volatile.Read(ref snapshotJson));
    }

    public void Receive(PlaybackSignal signal)
    {
        if (!Volatile.Read(ref disposed)) pending.Enqueue(signal);
    }

    public void Poll()
    {
        if (disposed) return;
        while (pending.TryDequeue(out var signal))
        {
            var json = JsonSerializer.Serialize(new { protocol = 1, state = signal }, Options);
            Volatile.Write(ref snapshotJson, json);
            try { changed.SendMessage(json); }
            catch (Exception ex) { reportError(ex); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Volatile.Write(ref disposed, true);
        snapshot.UnregisterFunc();
        pending.Clear();
    }
}
