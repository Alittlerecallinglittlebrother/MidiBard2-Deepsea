using System;
using Melanchall.DryWetMidi.Interaction;

namespace MidiBard.Managers;

internal enum LiveSwitchPhase { None, Holstering, Equipping, Settling, WaitingBar, Joined, Failed }

internal sealed class LiveInstrumentSwitch
{
    private readonly object sync = new();
    private LiveSwitchPhase phase;
    private uint target;
    private long deadline, settledAt, resumeTick, generation;
    private bool commandSent;

    public LiveSwitchPhase Phase { get { lock (sync) return phase; } }
    public uint Target { get { lock (sync) return target; } }
    public long ResumeTick { get { lock (sync) return resumeTick; } }
    public long Generation { get { lock (sync) return generation; } }
    public bool IsChanging => Phase is LiveSwitchPhase.Holstering or LiveSwitchPhase.Equipping or LiveSwitchPhase.Settling;

    public static long NextBar(long tick, TempoMap map)
    {
        var current = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(Math.Max(0, tick), map);
        return TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(current.Bars + 1), map);
    }

    public void Request(uint instrument, long now)
    {
        if (instrument == 0) throw new ArgumentOutOfRangeException(nameof(instrument));
        lock (sync)
        {
            generation++;
            target = instrument;
            phase = LiveSwitchPhase.Holstering;
            deadline = now + 7000;
            commandSent = false;
            resumeTick = long.MaxValue;
        }
    }

    public uint? Advance(long now, uint current, long position, long duration, TempoMap map)
    {
        lock (sync)
        {
            if (phase is LiveSwitchPhase.None or LiveSwitchPhase.Failed or LiveSwitchPhase.Joined) return null;
            if (phase == LiveSwitchPhase.WaitingBar)
            {
                if (current != target) { phase = LiveSwitchPhase.Failed; return null; }
                if (position >= resumeTick) phase = LiveSwitchPhase.Joined;
                return null;
            }
            if (now >= deadline) { phase = LiveSwitchPhase.Failed; return null; }
            if (current == target)
            {
                if (phase != LiveSwitchPhase.Settling) { phase = LiveSwitchPhase.Settling; settledAt = now; }
                if (now - settledAt >= 200) PlanBar(position, duration, map);
                return null;
            }
            if (phase == LiveSwitchPhase.Settling) { phase = LiveSwitchPhase.Holstering; commandSent = false; }
            if (phase == LiveSwitchPhase.Holstering)
            {
                if (current == 0 || (current >= 24 && target >= 24))
                {
                    phase = LiveSwitchPhase.Equipping;
                    commandSent = true;
                    return target;
                }
                if (!commandSent) { commandSent = true; return 0; }
            }
            return null;
        }
    }

    private void PlanBar(long position, long duration, TempoMap map)
    {
        resumeTick = NextBar(position, map);
        phase = resumeTick < duration ? LiveSwitchPhase.WaitingBar : LiveSwitchPhase.Failed;
    }

    public void Rebase(long position, long duration, TempoMap map)
    {
        lock (sync)
            if (phase == LiveSwitchPhase.WaitingBar) PlanBar(position, duration, map);
    }

    public bool Allows(long eventTick)
    {
        lock (sync) return phase is LiveSwitchPhase.None or LiveSwitchPhase.Joined
            || (phase == LiveSwitchPhase.WaitingBar && eventTick >= resumeTick);
    }

    public void Fail() { lock (sync) { generation++; phase = LiveSwitchPhase.Failed; } }
    public void Reset() { lock (sync) { generation++; phase = LiveSwitchPhase.None; target = 0; } }
}
