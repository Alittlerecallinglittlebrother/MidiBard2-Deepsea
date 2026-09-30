using System.Diagnostics;
using Melanchall.DryWetMidi.Multimedia;

namespace BardStage.Core;

/// <summary>Schedules only the prepared MIDI engine; no game-state access on this timer thread.</summary>
public sealed class MonotonicPlaybackStart : IDisposable
{
    private readonly object gate = new();
    private readonly MidiClock ticker;
    private Action? start;
    private Func<bool>? canStart;
    private double target, lease;
    private double? fired;
    private string? issue;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public double? FiredAt { get { lock (gate) return fired; } }
    public string? Issue { get { lock (gate) return issue; } }
    public MonotonicPlaybackStart()
    {
        ticker = new MidiClock(false, new HighPrecisionTickGenerator(), TimeSpan.FromMilliseconds(1));
        ticker.Ticked += Tick;
    }
    public void Arm(double at, Action preparedStart, Func<bool>? canStart = null)
    {
        lock (gate)
        {
            if (!double.IsFinite(at) || at - Now is < .1 or > 8) throw new InvalidOperationException("本机开演预约无效或迟到");
            start = preparedStart; target = at; lease = Now + 1.5; fired = null; issue = null;
            this.canStart = canStart;
        }
        ticker.Restart();
    }
    public void Pulse() { lock (gate) lease = Now + 1.5; }
    public void StopTimer() => ticker.Stop();
    private void Tick(object? sender, EventArgs args)
    {
        lock (gate)
        {
            if (start == null || Now < target) return;
            var action = start; start = null;
            if (Now > lease) { issue = "本机准备状态已超时，请重新下发"; return; }
            if (Now - target > .080) { issue = "本机错过开演时刻，请重新下发"; return; }
            try
            {
                if (canStart?.Invoke() == false) { issue = "开演预约已撤销或连接已失效，请重新下发"; return; }
                var at = Now; action(); fired = at;
            }
            catch (Exception ex) { issue = "本机预约开演失败：" + ex.Message; }
        }
    }
    public void Cancel()
    {
        lock (gate) { start = null; canStart = null; fired = null; issue = null; }
        ticker.Stop();
    }
    public void Dispose() { Cancel(); ticker.Ticked -= Tick; ticker.Dispose(); }
}
