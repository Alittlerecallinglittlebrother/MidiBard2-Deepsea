using System.Diagnostics;

namespace BardStage.Core;

/// <summary>Waits for queued output, then a quiet interval on a monotonic clock.</summary>
public sealed class PlaybackTailGuard
{
    private readonly Func<TimeSpan> now;
    private readonly TimeSpan protection;
    private TimeSpan? quietSince;
    private long? observedRevision;

    public PlaybackTailGuard(TimeSpan protection, Func<TimeSpan>? clock = null)
    {
        if (protection < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(protection));
        this.protection = protection;
        var origin = Stopwatch.GetTimestamp();
        now = clock ?? (() => Stopwatch.GetElapsedTime(origin));
    }

    public bool IsReady(int pendingEvents, long outputRevision)
    {
        if (pendingEvents < 0) throw new ArgumentOutOfRangeException(nameof(pendingEvents));
        if (pendingEvents != 0 || observedRevision != outputRevision) quietSince = null;
        observedRevision = outputRevision;
        if (pendingEvents != 0) return false;
        quietSince ??= now();
        return now() - quietSince.Value >= protection;
    }
}
