using System.Diagnostics;

namespace BardStage.Core.Rooms;

public sealed class ClockExchange
{
    public Guid Id { get; set; }
    public double Sent { get; set; }
    public double Received { get; set; }
    public double Replied { get; set; }
}

public sealed record TransportClockState(bool Ready, double Offset, double Rtt, double Jitter, double Age)
{
    public static readonly TransportClockState Unavailable = new(false, 0, 0, 0, double.PositiveInfinity);
}

/// <summary>Connection-scoped four-timestamp clock. No framework/UI polling participates.</summary>
public sealed class TransportClock(Func<double>? clock = null)
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private readonly Func<double> now = clock ?? (() => Now);
    private readonly object gate = new();
    private readonly EnsembleClock estimate = new();
    private readonly Dictionary<Guid, double> pending = [];
    private double last = double.NegativeInfinity;
    public TransportClockState State
    {
        get
        {
            lock (gate)
            {
                var age = now() - last;
                return new(estimate.Ready && age is >= 0 and < 2.5, estimate.Offset,
                    double.IsFinite(estimate.Rtt) ? estimate.Rtt : 0,
                    double.IsFinite(estimate.Jitter) ? estimate.Jitter : 0, age);
            }
        }
    }
    public void Sending(ClockExchange probe)
    {
        lock (gate)
        {
            probe.Sent = now();
            foreach (var id in pending.Where(p => probe.Sent - p.Value > 2).Select(p => p.Key).ToArray()) pending.Remove(id);
            if (pending.Count >= 16) pending.Clear();
            pending[probe.Id] = probe.Sent;
        }
    }
    public bool Accept(ClockExchange reply, double received)
    {
        lock (gate)
        {
            if (!pending.Remove(reply.Id, out var sent) || sent != reply.Sent || !double.IsFinite(received)) return false;
            if (received - last >= 2.5) estimate.Reset();
            if (!estimate.Add(sent, reply.Received, reply.Replied, received)) return false;
            last = received;
            return true;
        }
    }
}
