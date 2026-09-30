using System.Diagnostics;
using System.Reflection;
using BardStage.Core.Rooms;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Midibard.Playlib;
using MidiBard.Control;
using MidiBard.Managers;
using Plugin = MidiBard.MidiBard;

internal static class DeadlineOutputChecks
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
    static readonly FieldInfo Ticker = typeof(BardPlayDevice).GetField("PlaybackTicker", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo Drain = typeof(BardPlayDevice).GetMethod("DrainCurrentTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static NoteOnEvent On(int pitch) => new((SevenBitNumber)pitch, (SevenBitNumber)80);
    static BardPlayDevice.MidiPlaybackMetaData Meta(int pitch, long time = 0) => new(0,time,pitch);
    static Guid Prepare(BardPlayDevice d)
    {
        var id = Guid.NewGuid(); Plugin.CurrentPlayback = new() { LargePlanId = id, UseLargeInstrumentCompensation = true };
        d.BeginLargePlaybackOutput(id); Playlib.Events.Clear(); return id;
    }
    public static void Run()
    {
        EnsembleManager.EnsembleRunning = false; Plugin.CurrentInstrumentWithTone = 14;
        double now = 100;
        using var d = new BardPlayDevice(() => now);
        ((MidiClock)Ticker.GetValue(d)!).Stop();
        void Tick(double at) { now = at; Drain.Invoke(d,null); }
        var id = Prepare(d);
        d.SendEventWithMetadata(On(60),Meta(60),id);
        now = 100.030; d.SendEventWithMetadata(On(64),Meta(64,30),id);
        Tick(100.060); Check(Playlib.Events.IsEmpty,"60 ms lost callbacks do not fabricate elapsed compensation time");
        for (var i=0;i<200;i++) Tick(100.082);
        Check(Playlib.Events.IsEmpty,"200 catch-up callbacks cannot release a not-yet-due event");
        Tick(100.084); Check(Playlib.Events.Count==1,"first 83 ms deadline dispatches alone after simulated stall");
        Tick(100.112); Check(Playlib.Events.Count==1,"second note retains its independent deadline");
        Tick(100.114); Check(Playlib.Events.Count==2,"second note arrives 30 ms later instead of collapsing into the first");
        d.CancelLargePlaybackOutput(id);

        now=200; id=Prepare(d);
        var events=new[] {new BardPlayDevice.ScheduledEvent(On(60),Meta(60),0),new BardPlayDevice.ScheduledEvent(On(64),Meta(64,30),.030)};
        d.PrepareLargePlaybackOutput(id,201,1,true,events);
        Tick(201.010); Check(Playlib.Events.IsEmpty,"prepared score remains gated until the committed engine starts");
        d.ActivateLargePlaybackOutput(id); Tick(201.084);
        Check(Playlib.Events.Count==1,"prequeued score outputs even without any realtime MIDI producer callback");
        Tick(201.114); Check(Playlib.Events.Count==2,"score deadlines retain exact phrase spacing");
        d.CancelLargePlaybackOutput(id);

        now=300; id=Prepare(d); d.PrepareLargePlaybackOutput(id,301,2,false,events); d.ActivateLargePlaybackOutput(id);
        Tick(301.001); Check(Playlib.Events.Count==1,"compensation off still uses the absolute scheduled output path");
        Tick(301.014); Check(Playlib.Events.Count==1,"playback speed maps MIDI time, not onset compensation");
        Tick(301.016); Check(Playlib.Events.Count==2,"2x speed halves the musical interval");
        d.CancelLargePlaybackOutput(id);

        now=400; id=Prepare(d); d.PrepareLargePlaybackOutput(id,401,1,false,events); d.ActivateLargePlaybackOutput(id);
        Tick(401.001); Check(Playlib.Events.Count==1,"fault test holds first key");
        Tick(401.200);
        Check(Playlib.Events.Count==2 && !Playlib.Events.Last().Down && d.LargePlaybackPending(id)==0 && d.OutputIssue(id)!=null && d.OutputTiming.Faults==1,
            "severe lateness releases held key and cancels expired phrase instead of bursting old note-ons");
        var count=Playlib.Events.Count; Tick(500);
        Check(Playlib.Events.Count==count,"faulted plan cannot resume on later callbacks");
        d.CancelLargePlaybackOutput(id);

        now=600; id=Prepare(d); d.PrepareLargePlaybackOutput(id,601,1,true,events);
        d.CancelLargePlaybackOutput(id); Tick(602);
        Check(Playlib.Events.IsEmpty,"countdown cancellation clears all prequeued future notes");
        now=700; id=Prepare(d);
        var off = new NoteOffEvent((SevenBitNumber)60,(SevenBitNumber)0);
        var repeated = new[] { new BardPlayDevice.ScheduledEvent(On(60),Meta(60),0),
            new BardPlayDevice.ScheduledEvent(off,Meta(60,15),.015),
            new BardPlayDevice.ScheduledEvent(On(60),Meta(60,15),.015),
            new BardPlayDevice.ScheduledEvent(off,Meta(60,30),.030) };
        d.PrepareLargePlaybackOutput(id,701,1,false,repeated); d.ActivateLargePlaybackOutput(id);
        Tick(701.001); Tick(701.016); Tick(701.031);
        Check(Playlib.Events.Select(e=>e.Down).SequenceEqual(new[]{true,false,true,false}) && d.LargePlaybackPending(id)==0,
            "prepared same-pitch repetitions preserve NoteOff/NoteOn ordering and final release");
        d.CancelLargePlaybackOutput(id);
        now=800; id=Prepare(d);
        d.PrepareLargePlaybackOutput(id,801,1,false,repeated); d.ActivateLargePlaybackOutput(id);
        Plugin.CurrentPlayback=new() { LargePlanId=id,UseLargeInstrumentCompensation=true };
        Tick(801.031);
        Check(Playlib.Events.IsEmpty,"even matching plan IDs cannot dispatch events owned by a replaced playback object");
        d.CancelLargePlaybackOutput(id);
        now=850; id=Prepare(d);
        d.PrepareLargePlaybackOutput(id,851,1,false,repeated); d.ActivateLargePlaybackOutput(id);
        Plugin.AgentPerformance.InPerformanceMode=false;
        Tick(851.001);
        Check(Playlib.Events.IsEmpty && d.OutputTiming.Faults==1 && d.LargePlaybackPending(id)==0,
            "leaving performance mode cancels scheduled output before touching the key interface");
        Plugin.AgentPerformance.InPerformanceMode=true; d.CancelLargePlaybackOutput(id);
        now=860; id=Prepare(d);
        d.PrepareLargePlaybackOutput(id,861,1,false,repeated); d.ActivateLargePlaybackOutput(id);
        Playlib.RejectPress=true; Tick(861.001); Playlib.RejectPress=false;
        Check(Playlib.Events.IsEmpty && d.OutputTiming.Faults==1 && d.OutputIssue(id)!=null,
            "a rejected game key is reported instead of silently continuing with missing notes");
        d.CancelLargePlaybackOutput(id);
        now=900; Plugin.CurrentPlayback=new(); Plugin.CurrentInstrumentWithTone=2;
        EnsembleManager.EnsembleRunning=true;
        Plugin.config.CompensationMode=global::MidiBard.CompensationModes.ByInstrumentNote;
        Playlib.Events.Clear(); d.SendEventWithMetadata(On(60),Meta(60));
        d.CancelLegacyPlaybackOutput(); Playlib.Events.Clear(); Tick(901);
        Check(Playlib.Events.IsEmpty && d.PlaybackOutputState.PendingEvents==0,"legacy pause/seek cancellation removes pending compensated output");
        EnsembleManager.EnsembleRunning=false; Plugin.CurrentInstrumentWithTone=14;
        RealTimerPause();
    }
    static void RealTimerPause()
    {
        using var d = new BardPlayDevice();
        var ticker=(MidiClock)Ticker.GetValue(d)!;
        for (var run=0;run<3;run++)
        {
            var id=Prepare(d);
            using var entered=new ManualResetEventSlim(); var armed=1;
            void Pause(object? sender,EventArgs args)
            { if(Interlocked.Exchange(ref armed,0)==0)return; entered.Set(); Thread.Sleep(60); }
            ticker.Ticked+=Pause;
            try
            {
                Check(entered.Wait(2000),"real timer pause injected");
                var first=Stopwatch.GetTimestamp(); d.SendEventWithMetadata(On(60),Meta(60),id);
                Thread.Sleep(30);
                var second=Stopwatch.GetTimestamp(); d.SendEventWithMetadata(On(64),Meta(64,30),id);
                var until=TransportClock.Now+3;
                while(Playlib.Events.Count<2 && TransportClock.Now<until)Thread.Sleep(1);
                var outputs=Playlib.Events.ToArray(); Check(outputs.Length==2,"real timer outputs both notes");
                var gap=Stopwatch.GetElapsedTime(outputs[0].At,outputs[1].At).TotalMilliseconds;
                var wait=Stopwatch.GetElapsedTime(second,outputs[1].At).TotalMilliseconds;
                Check(wait>=82.9 && gap>=20,"real 60 ms callback stall no longer shortens the second 83 ms compensation wait");
                Console.WriteLine($"TIMING_REAL run={run} inputGapMs={Stopwatch.GetElapsedTime(first,second).TotalMilliseconds:F3} outputGapMs={gap:F3} secondWaitMs={wait:F3}");
            }
            finally { ticker.Ticked-=Pause; d.CancelLargePlaybackOutput(id); }
        }
    }
}
