using System.Diagnostics;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Midibard.Playlib;
using MidiBard;
using MidiBard.Control;
using MidiBard.Managers;
using Plugin = MidiBard.MidiBard;

internal static class LargeCompensationChecks
{
    public static void Run(BardPlayDevice device)
    {
        Plugin.config.CompensationMode = CompensationModes.ByInstrumentNote;
        for (var instrument = 1; instrument <= 28; instrument++)
            for (var note = -1; note <= 36; note++)
            {
                var normal = EnsembleManager.GetCompensationNew(instrument,note);
                var large = EnsembleManager.GetCompensationNew(instrument,note,true);
                if (normal != large || large < 0 || large >= 500) throw new Exception("Compensation table mismatch");
            }
        Check(true,"all 28 instruments / 37 pitches and control events share the exact production eight-person table");
        Check(EnsembleManager.GetCompensationNew(12,0,true) != EnsembleManager.GetCompensationNew(12,36,true),
            "production compensation accounts for pitch-dependent bass-drum onset");

        EnsembleManager.EnsembleRunning = false;
        Plugin.config.CompensationMode = CompensationModes.None;
        Plugin.CurrentInstrumentWithTone = 2;
        Plugin.CurrentPlayback = new();
        Playlib.Events.Clear();
        Send(device,true,60); Send(device,false,60);
        Check(Playlib.Events.Count == 2 && device.PlaybackOutputState.PendingEvents == 0,"ordinary solo output remains immediate");

        var plan = Prepare(device);
        var delay = EnsembleManager.GetCompensationNew(2,12,true);
        Check(EnsembleManager.GetCompensationNew(2,12)==0 && delay>0,"shared large compensation overrides local None without modifying it");
        var begin = Stopwatch.GetTimestamp();
        Send(device,true,60,plan);
        Check(device.LargePlaybackPending(plan)==1 && Playlib.Events.IsEmpty,"large note enters compensation queue even with native ensemble off");
        Wait(()=>!Playlib.Events.IsEmpty);
        Check(Stopwatch.GetElapsedTime(begin,Playlib.Events.First().At).TotalMilliseconds >= delay-10,
            "large note onset follows the production piano compensation delay");
        Send(device,false,60,plan); Send(device,true,64,plan);
        device.CancelLargePlaybackOutput(plan);
        Check(device.LargePlaybackPending(plan)==0 && Playlib.Events.Last() is { Down: false, Note: 12 },
            "manual stop clears queued notes and immediately releases the sounding large note");
        var stoppedCount = Playlib.Events.Count;
        Send(device,true,67,plan);
        Thread.Sleep(400);
        Check(Playlib.Events.Count==stoppedCount,"late callbacks cannot requeue output after cancellation");
        Check(!EnsembleManager.EnsembleRunning && Plugin.config.CompensationMode==CompensationModes.None,
            "large compensation does not turn on native ensemble or overwrite saved settings");

        plan = Prepare(device);
        Send(device,true,60,plan);
        Plugin.CurrentPlayback = new();
        Wait(()=>device.LargePlaybackPending(plan)==0);
        Thread.Sleep(300);
        Check(Playlib.Events.IsEmpty,"replacing a song discards old compensated notes before they reach a different instrument");

        plan = Prepare(device);
        Plugin.CurrentPlayback.UseLargeInstrumentCompensation = false;
        Send(device,true,60,plan); Send(device,false,60,plan);
        Check(Playlib.Events.Count==2 && device.LargePlaybackPending(plan)==0,"captain-selected Off bypasses compensation for precompensated MIDI");
        device.CancelLargePlaybackOutput(plan);

        plan = Prepare(device);
        var rawFinished = false;
        var pendingAtEnd = 0;
        using (var playback = new BufferedPlayback(device,plan))
        {
            playback.Finished += (_,_) => { pendingAtEnd=device.LargePlaybackPending(plan); Volatile.Write(ref rawFinished,true); };
            playback.Start();
            Wait(()=>Volatile.Read(ref rawFinished));
            Check(pendingAtEnd>0,"real large MIDI EOF still has compensated output pending");
            Wait(()=>device.LargePlaybackPending(plan)==0);
            Check(Playlib.Events.Select(e=>(e.Down,e.Note)).SequenceEqual(new[] {(true,12),(false,12),(true,16),(false,16),(true,19),(false,19)}),
                "natural large completion drains every final note and release through production output");
        }
        device.CancelLargePlaybackOutput(plan);
        var old = plan;
        plan = Prepare(device);
        Send(device,true,60,old);
        Check(device.LargePlaybackPending(plan)==0 && Playlib.Events.IsEmpty,"old-plan events cannot contaminate a newly distributed plan");
        Send(device,true,64,plan); Send(device,false,64,plan);
        Wait(()=>device.LargePlaybackPending(plan)==0);
        Check(Playlib.Events.Count==2,"a new plan still plays after stop and replacement");
        device.CancelLargePlaybackOutput(plan);
        Plugin.CurrentPlayback = new();
    }
    private static Guid Prepare(BardPlayDevice device)
    {
        Playlib.Events.Clear();
        var id=Guid.NewGuid();
        Plugin.CurrentPlayback=new() { LargePlanId=id, UseLargeInstrumentCompensation=true };
        device.BeginLargePlaybackOutput(id);
        return id;
    }
    private static void Send(BardPlayDevice device,bool on,int pitch,Guid plan=default)
    {
        MidiEvent e=on ? new NoteOnEvent((SevenBitNumber)pitch,(SevenBitNumber)80) : new NoteOffEvent((SevenBitNumber)pitch,(SevenBitNumber)0);
        device.SendEventWithMetadata(e,new BardPlayDevice.MidiPlaybackMetaData(0,Stopwatch.GetTimestamp(),pitch),plan);
    }
    private static void Wait(Func<bool> done)
    {
        var timer=Stopwatch.StartNew();
        while(!done()) { if(timer.Elapsed.TotalSeconds>5) throw new TimeoutException("output did not reach expected state"); Thread.Sleep(2); }
    }
    private static void Check(bool value,string message)
    { if(!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: "+message); }
}
