using Melanchall.DryWetMidi.Multimedia;

namespace BardStage;

public enum PlaybackSignalKind { Loaded, Started, Paused, Resumed, Finished, Stopped }
public sealed record PlaybackSignal(Guid PlaybackId, long Sequence, string FilePath, PlaybackSignalKind Kind, DateTimeOffset AtUtc);

public sealed class PlaybackObserver(Action<PlaybackSignal> receive,
    Func<Playback, Func<bool>?>? createFinishBarrier = null) : IDisposable
{
    private readonly object gate = new();
    private Playback? playback;
    private string path = "";
    private Guid playbackId;
    private long sequence;
    private PlaybackSignalKind state;
    private bool disposed;
    private bool pendingFinish, finishBarrierCreated;
    private Func<bool>? finishBarrier;

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

    public void StopPendingFinish()
    {
        lock (gate)
        {
            if (disposed || !pendingFinish) return;
            CancelPendingFinish();
            Emit(PlaybackSignalKind.Stopped);
        }
    }

    // Called by the framework thread. The MIDI callback only reports logical EOF;
    // native output and the completion policy must be checked on the game thread.
    public void Poll()
    {
        lock (gate)
        {
            if (disposed || !pendingFinish || playback == null) return;
            if (!finishBarrierCreated)
            {
                finishBarrier = createFinishBarrier?.Invoke(playback);
                finishBarrierCreated = true;
            }
            if (finishBarrier?.Invoke() == false) return;
            CancelPendingFinish();
            Emit(PlaybackSignalKind.Finished);
        }
    }

    private void OnStarted(object? sender, EventArgs args)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, playback) || disposed) return;
            if (pendingFinish)
            {
                Emit(PlaybackSignalKind.Stopped);
                playbackId = Guid.NewGuid();
            }
            else if (state is PlaybackSignalKind.Finished or PlaybackSignalKind.Stopped) playbackId = Guid.NewGuid();
            CancelPendingFinish();
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
            if (!disposed && ReferenceEquals(sender, playback) && state != PlaybackSignalKind.Finished && !pendingFinish)
            {
                if (createFinishBarrier == null) Emit(PlaybackSignalKind.Finished);
                else pendingFinish = true;
            }
    }

    private void Emit(PlaybackSignalKind kind)
    {
        state = kind;
        receive(new PlaybackSignal(playbackId, ++sequence, path, kind, DateTimeOffset.UtcNow));
    }

    private void Detach(bool reportStop)
    {
        CancelPendingFinish();
        if (playback == null) return;
        playback.Started -= OnStarted; playback.Stopped -= OnPaused; playback.Finished -= OnFinished;
        if (reportStop && state is PlaybackSignalKind.Started or PlaybackSignalKind.Resumed or PlaybackSignalKind.Paused) Emit(PlaybackSignalKind.Stopped);
        playback = null;
    }

    private void CancelPendingFinish()
    {
        pendingFinish = finishBarrierCreated = false;
        finishBarrier = null;
    }

    public void Dispose()
    {
        lock (gate) { Detach(false); disposed = true; }
    }
}
