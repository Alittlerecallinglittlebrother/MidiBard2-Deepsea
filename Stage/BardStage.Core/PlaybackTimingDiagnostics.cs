namespace BardStage.Core;

/// <summary>Game-interface dispatch statistics, not measured audio latency.</summary>
public sealed record PlaybackTimingDiagnostics(long Dispatched, double MaxLatenessMs, double MaxCallbackGapMs, long Faults)
{
    public static readonly PlaybackTimingDiagnostics Empty = new(0, 0, 0, 0);
}
