using Melanchall.DryWetMidi.Multimedia;

namespace BardStage;

public enum PlaybackSignalKind { Loaded, Started, Paused, Resumed, Finished, Stopped }
public sealed record PlaybackSignal(Guid PlaybackId, long Sequence, string FilePath, PlaybackSignalKind Kind, DateTimeOffset AtUtc);

public sealed class PlaybackObserver(Action<PlaybackSignal> receive) : IDisposable
{
    private readonly object gate = new();
    private Playback? playback;
    private string path = "";
    private Guid playbackId;
    private long sequence;
    private PlaybackSignalKind state;
    private bool disposed;

    public void Attach(Playback next, string? filePath)
    {
        lock (gate)
        {
            if (disposed || ReferenceEquals(playback, next)) return;
            Detach(true);
            playback = next; path = string.IsNullOrWhiteSpace(filePath) ? "" : Path.GetFullPath(filePath); playbackId = Guid.NewGuid();
            playback.Started += OnStarted; playback.Stopped += OnPaused; playback.Finished += OnFinished;
            Emit(PlaybackSignalKind.Loaded);
            if (playback.IsRunning) Emit(PlaybackSignalKind.Started);
        }
    }

    public void Stop()
    {
        lock (gate) { if (!disposed) Detach(true); }
    }

    private void OnStarted(object? sender, EventArgs args)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, playback) || disposed) return;
            if (state == PlaybackSignalKind.Finished) playbackId = Guid.NewGuid();
            Emit(state == PlaybackSignalKind.Paused ? PlaybackSignalKind.Resumed : PlaybackSignalKind.Started);
        }
    }

    private void OnPaused(object? sender, EventArgs args)
    {
        lock (gate)
            if (!disposed && ReferenceEquals(sender, playback) && state is PlaybackSignalKind.Started or PlaybackSignalKind.Resumed)
                Emit(PlaybackSignalKind.Paused);
    }

    private void OnFinished(object? sender, EventArgs args)
    {
        lock (gate)
            if (!disposed && ReferenceEquals(sender, playback) && state != PlaybackSignalKind.Finished)
                Emit(PlaybackSignalKind.Finished);
    }

    private void Emit(PlaybackSignalKind kind)
    {
        state = kind;
        receive(new PlaybackSignal(playbackId, ++sequence, path, kind, DateTimeOffset.UtcNow));
    }

    private void Detach(bool reportStop)
    {
        if (playback == null) return;
        playback.Started -= OnStarted; playback.Stopped -= OnPaused; playback.Finished -= OnFinished;
        if (reportStop && state is PlaybackSignalKind.Started or PlaybackSignalKind.Resumed or PlaybackSignalKind.Paused) Emit(PlaybackSignalKind.Stopped);
        playback = null;
    }

    public void Dispose()
    {
        lock (gate) { Detach(false); disposed = true; }
    }
}
