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

internal static class BurstOutputChecks
{
    private static void Check(bool value, string text)
    { if (!value) throw new InvalidOperationException(text); Console.WriteLine("PASS: " + text); }

    private static BardPlayDevice.ScheduledEvent On(int pitch, double at, int channel = 0, int track = 0, int velocity = 80)
        => new(new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel },
            new(track, (long)Math.Round(at * 1000000), pitch), at);
    private static BardPlayDevice.ScheduledEvent Off(int pitch, double at, int channel = 0, int track = 0)
        => new(new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0) { Channel = (FourBitNumber)channel },
            new(track, (long)Math.Round(at * 1000000), pitch), at);

    public static void Run()
    {
        ExpiredNotes();
        FutureWindow();
        CrossCallbackGuard();
        NoteOwnership();
        PairingEdges();
        HealthyFastPassages();
        CompensationAndSpeed();
        ExpiryBoundaries();
        RecoveryBoundaries();
        ControlEvents();
        Lifecycle();
        StallMatrix();
        RandomizedRecovery();
        RealTimerRecovery();
        Console.WriteLine("BURST_OUTPUT_CHECKS_COMPLETE gameAdapter=recording realGameAudio=false");
    }

    private static void ExpiredNotes()
    {
        using var h = new Harness([On(60, 0), Off(60, .010), On(64, .020), Off(64, .030), On(67, .040), Off(67, .060)]);
        h.Tick(.025);
        Check(h.Events.SequenceEqual(new[] { (true, 16) }), "short stall skips expired note and its release instead of bursting two attacks");
        h.Tick(.030); h.Tick(.040); h.Tick(.060);
        Check(h.Events.SequenceEqual(new[] { (true, 16), (false, 16), (true, 19), (false, 19) }),
            "recovery keeps future note deadlines and paired final release without shifting the score");
        Check(h.Device.OutputIssue(h.Plan) == null && h.Device.LargePlaybackPending(h.Plan) == 0,
            "recoverable short stall drains normally without a severe timing fault");
        Check(h.Device.ScheduledRecovery == (1, 0, 1) && h.Device.TimingSummary.Contains("expiredNotes=1"),
            "recovery counters distinguish an expired attack from its safely ignored release");
    }

    private static void FutureWindow()
    {
        using var h = new Harness([On(60, 0), Off(60, .100), On(64, .020), Off(64, .120)]);
        h.Tick(.017);
        Check(h.Events.Length == 0, "long note is not started just before the next scheduled attack");
        h.Tick(.020);
        Check(h.Events.SequenceEqual(new[] { (true, 16) }), "next attack remains on its original timestamp after a near-next-note skip");
    }

    private static void CrossCallbackGuard()
    {
        using var h = new Harness([On(60, 0), On(64, .020), On(67, .100), Off(60, .200), Off(64, .210), Off(67, .220)]);
        Playlib.AfterPress = () => { Playlib.AfterPress = null; h.Advance(.012); };
        h.Tick(.001); h.Tick(.020);
        Check(h.Events.SequenceEqual(new[] { (true, 12) }), "cross-callback guard uses actual dispatch completion rather than allowing a new burst each callback");
        h.Tick(.100);
        Check(h.Events.SequenceEqual(new[] { (true, 12), (true, 19) }), "spacing guard recovers on an unchanged later deadline");
        Check(h.Device.ScheduledRecovery.CrowdedNotes == 1, "cross-callback compression has its own diagnostic counter");
    }

    private static void NoteOwnership()
    {
        using var h = new Harness([On(60, 0), On(60, .020), Off(60, .060), Off(60, .100)]);
        h.Tick(0); h.Tick(.020); h.Tick(.060);
        Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12), (true, 12) }),
            "old same-pitch NoteOff cannot release a newer overlapping note instance");
        h.Tick(.100);
        Check(h.Events.Last() == (false, 12) && h.Events.Length == 4, "new same-pitch note keeps its own final release");
    }

    private static void PairingEdges()
    {
        using (var h = new Harness([On(60, 0), On(60, .020), Off(60, .100), Off(60, .150)]))
        {
            h.Tick(.017); h.Tick(.020); h.Tick(.100);
            Check(h.Events.SequenceEqual(new[] { (true, 12) }), "release belonging to a skipped same-pitch attack cannot mute the new note");
            h.Tick(.150);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }), "skipping an attack does not remove a different note's release");
        }
        using (var h = new Harness([On(60, 0, channel: 0), On(60, .020, channel: 1), Off(60, .060, channel: 1), Off(60, .100, channel: 0)]))
        {
            h.Tick(0); h.Tick(.020); h.Tick(.060);
            Check(h.Events.Length == 4 && !h.Events.Last().Down, "pairing includes MIDI channel, not just pitch and FIFO across all channels");
            h.Tick(.100);
            Check(h.Events.Length == 4, "superseded channel release cannot touch another note instance");
        }
        using (var h = new Harness([On(60, 0, track: 0), On(60, .020, track: 1), Off(60, .060, track: 1), Off(60, .100, track: 0)]))
        {
            h.Tick(0); h.Tick(.020); h.Tick(.060);
            Check(h.Events.Length == 4 && !h.Events.Last().Down, "pairing includes track identity for equal pitch and channel");
        }
        using (var h = new Harness([On(60, 0), On(60, .020, velocity: 0)]))
        {
            h.Tick(0); h.Tick(.020);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }), "velocity-zero NoteOn is normalized to its paired release");
        }
        using (var h = new Harness([On(60, 0), Off(60, .020, channel: 1), Off(60, .100)]))
        {
            h.Tick(0); h.Tick(.020);
            Check(h.Events.SequenceEqual(new[] { (true, 12) }), "orphan NoteOff cannot release a matching pitch owned by another MIDI stream");
            h.Tick(.100);
            Check(h.Events.Length == 2 && !h.Events.Last().Down, "owned release still executes after ignoring an orphan");
        }
    }

    private static void HealthyFastPassages()
    {
        var events = Enumerable.Range(0, 24).SelectMany(i => new[] { On(60, i * .008), Off(60, i * .008 + .003) }).ToArray();
        using (var h = new Harness(events))
        {
            foreach (var e in events) h.Tick(e.Seconds + .0005);
            Check(h.Events.Length == events.Length && h.Events.Select(e => e.Down).SequenceEqual(Enumerable.Range(0, events.Length).Select(i => i % 2 == 0)),
                "healthy 8 ms same-pitch passage preserves every attack and release with 0.5 ms callback jitter");
            Check(h.Device.ScheduledRecovery == (0, 0, 0), "fast score is not throttled by a fixed 20 or 30 ms minimum interval");
        }
        using (var h = new Harness([On(60, 0), On(64, 0), On(67, 0), Off(60, .050), Off(64, .050), Off(67, .050)]))
        {
            h.Tick(.001);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (true, 16), (true, 19) }), "intentional same-score-time chord keeps its attack order");
            h.Tick(.050);
            Check(h.Events.Length == 4 && h.Events.Last() == (false, 19), "chord cleanup releases only the actually held last note");
        }
        using (var h = new Harness([On(60, 0), Off(60, 0), On(64, .020), Off(64, .040)]))
        {
            h.Tick(.001);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }), "zero-length MIDI note keeps a small explicit on-time grace and cannot leave a stuck key");
        }
        using (var h = new Harness([On(60, 0), Off(60, 0), On(64, .020), Off(64, .040)]))
        {
            h.Tick(.010);
            Check(h.Events.Length == 0, "expired zero-length note is not burst out after a short stall");
        }
        using (var h = new Harness([On(60, 0), On(10, .001), Off(10, .002), Off(60, .100)]))
        {
            h.Tick(.010);
            Check(h.Events.SequenceEqual(new[] { (true, 12) }), "silent out-of-range notes do not shorten a playable note's validity window");
            Check(h.Device.ScheduledRecovery.ExpiredNotes == 0 && h.Device.ScheduledRecovery.CrowdedNotes == 0,
                "silent out-of-range events are not misreported as recovered missing attacks");
        }
    }

    private static void CompensationAndSpeed()
    {
        using (var h = new Harness([On(60, 0), Off(60, .040), On(64, .100), Off(64, .140)], speed: 2))
        {
            h.Tick(.005); h.Tick(.020); h.Tick(.050); h.Tick(.070);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12), (true, 16), (false, 16) }), "validity windows and release deadlines scale with the published playback speed");
        }
        using (var h = new Harness([On(60, 0), Off(60, .040), On(64, .100), Off(64, .140)], speed: 2, compensate: true))
        {
            var delay = EnsembleManager.GetCompensationNew(14, 12, true) / 1000d;
            h.Tick(delay - .001);
            Check(h.Events.Length == 0, "note recovery never bypasses the original instrument compensation wait");
            h.Tick(delay + .005); h.Tick(delay + .0205); h.Tick(delay + .0505); h.Tick(delay + .0705);
            Check(h.Events.Length == 4 && h.Device.ScheduledRecovery == (0, 0, 0), "speed scales score time but does not scale or duplicate the original compensation");
        }

        (int Instrument, int Low, int High, int Delay)? sample = null;
        for (var instrument = 1; instrument <= 28 && sample == null; instrument++)
            for (var low = 0; low < 36 && sample == null; low++)
                for (var high = low + 1; high <= 36; high++)
                {
                    var delay = EnsembleManager.GetCompensationNew(instrument, low, true);
                    if (delay - EnsembleManager.GetCompensationNew(instrument, high, true) > 2)
                    { sample = (instrument, low, high, delay); break; }
                }
        if (sample is not { } s) throw new Exception("No production chord-compensation case found");
        using (var h = new Harness([On(s.Low + 48, 0), On(s.High + 48, 0), Off(s.High + 48, .001), Off(s.Low + 48, .200)], compensate: true, instrument: s.Instrument))
        {
            h.Tick(s.Delay / 1000d + .0005);
            Check(h.Events.SequenceEqual(new[] { (true, s.Low), (true, s.High), (false, s.High) }),
                "chord-adjusted attack cannot lose its release when original pitch compensation places NoteOff before NoteOn");
            Check(Plugin.AgentPerformance.noteNumber == -100, "compensation-clamped release leaves no held key");
        }
        var difference = (s.Delay - EnsembleManager.GetCompensationNew(s.Instrument, s.High, true)) / 1000d;
        using (var h = new Harness([On(s.Low + 48, 0), On(s.High + 48, difference), Off(s.Low + 48, .100), Off(s.High + 48, difference + .100)], compensate: true, instrument: s.Instrument))
        {
            h.Tick(s.Delay / 1000d + .0005);
            Check(h.Events.Count(e => e.Down) == 2 && h.Device.ScheduledRecovery.ExpiredNotes == 0 && h.Device.ScheduledRecovery.CrowdedNotes == 0,
                "different score beats deliberately sharing one compensated deadline are not treated as accidental backlog");
            h.Tick(s.Delay / 1000d + .1005);
            Check(h.Device.LargePlaybackPending(h.Plan) == 0 && Plugin.AgentPerformance.noteNumber == -100,
                "equal compensated deadlines preserve final paired release");
        }
    }

    private static void ExpiryBoundaries()
    {
        using (var h = new Harness([On(60, 0), Off(60, .020)]))
        {
            h.Tick(.010);
            Check(h.Events.Length == 1, "exact half-duration late boundary retains a still-useful note");
            h.Tick(.020);
            Check(h.Events.Length == 2, "late-but-useful note keeps its original end rather than stretching the gate");
        }
        using (var h = new Harness([On(60, 0), Off(60, .020)]))
        {
            h.Tick(.01001);
            Check(h.Events.Length == 0 && h.Device.ScheduledRecovery.ExpiredNotes == 1, "insufficient remaining gate time skips the attack immediately past its window");
        }
        using (var h = new Harness([On(60, 0), Off(60, .200), On(64, .040), Off(64, .240)]))
        {
            h.Tick(.010); h.Tick(.040);
            Check(h.Events.Length == 2 && h.Device.ScheduledRecovery.CrowdedNotes == 0, "exact 75 percent planned interval boundary is accepted despite floating point rounding");
        }
        using (var h = new Harness([On(60, 0), Off(60, .200), On(64, .040), Off(64, .240)]))
        {
            h.Tick(.01001);
            Check(h.Events.Length == 0, "near-next-attack window rejects a late onset past the 25 percent interval allowance");
        }
    }

    private static void RecoveryBoundaries()
    {
        foreach (var late in new[] { .079, .080, .081, .100 })
        {
            using var h = new Harness([On(60, 0), Off(60, .500)]);
            h.Tick(late);
            var fault = late > .080;
            Check((h.Device.OutputIssue(h.Plan) != null) == fault && h.Events.Length == (fault ? 0 : 1),
                $"hard lateness boundary remains >80 ms (injected {late * 1000:F0} ms)");
        }
        using (var h = new Harness([On(60, 0), Off(60, .010), On(64, .050), Off(64, .500)]))
        {
            h.Tick(.025); h.Tick(.050); h.Tick(.300);
            Check(h.Device.OutputIssue(h.Plan) == null && h.Events.SequenceEqual(new[] { (true, 16) }),
                "an ignored release does not create a late fault or steal ownership from a later held note");
        }
        using (var h = new Harness([On(60, 0), On(64, .020), Off(60, .100), Off(64, .500)]))
        {
            h.Tick(.017); h.Tick(.020); h.Tick(.200);
            Check(h.Device.OutputIssue(h.Plan) == null && h.Events.SequenceEqual(new[] { (true, 16) }),
                "a skipped note's stale release beyond 80 ms is ignored before hard-fault evaluation");
        }
        using (var h = new Harness([On(60, 0), Off(60, .020)]))
        {
            h.Tick(0); h.Tick(.101);
            Check(h.Device.OutputIssue(h.Plan) != null && h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }),
                "a severely late owned release still triggers the existing stop and held-key cleanup");
            var count = h.Events.Length; h.Tick(.200);
            Check(h.Events.Length == count, "hard-faulted output cannot resume when timer callbacks recover");
        }
    }

    private static void ControlEvents()
    {
        var program = new BardPlayDevice.ScheduledEvent(new ProgramChangeEvent((SevenBitNumber)17), new(0, 10000, -1), .010);
        using var h = new Harness([On(60, 0), Off(60, .008), program, On(64, .030), Off(64, .060)]);
        Plugin.config.GuitarToneMode = global::MidiBard.GuitarToneMode.Standard;
        h.Tick(.025);
        var channels = (Array)typeof(BardPlayDevice).GetField("Channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Device)!;
        var channel = channels.GetValue(0)!;
        Check((SevenBitNumber)channel.GetType().GetProperty("Program")!.GetValue(channel)! == (SevenBitNumber)17,
            "skipping stale notes still applies intervening MIDI program state");
        h.Tick(.030);
        Check(h.Events.SequenceEqual(new[] { (true, 16) }), "control state processing does not shift or suppress the next healthy attack");
    }

    private static void Lifecycle()
    {
        using (var h = new Harness([On(60, 0), Off(60, .200)]))
        {
            h.Tick(0);
            Plugin.CurrentPlayback = new() { LargePlanId = h.Plan, UseLargeInstrumentCompensation = false };
            h.Tick(.010);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }) && h.Device.LargePlaybackPending(h.Plan) == 0,
                "replacing playback with the same plan ID revokes old note ownership and releases its held key");
            h.Tick(.200);
            Check(h.Events.Length == 2, "replaced playback cannot emit its stale paired release again");
        }
        using (var h = new Harness([On(60, 0), Off(60, .200)]))
        {
            h.Tick(0); h.Device.CancelLargePlaybackOutput(h.Plan); h.Tick(.200);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }) && h.Device.LargePlaybackPending(h.Plan) == 0,
                "cancel releases the owned note once and clears future paired events");
            var next = Guid.NewGuid();
            Plugin.CurrentPlayback = new() { LargePlanId = next };
            h.Device.PrepareLargePlaybackOutput(next, 10.201, 1, false, [On(64, 0), Off(64, .020)]);
            h.Device.ActivateLargePlaybackOutput(next); h.Tick(.201); h.Tick(.221);
            Check(h.Events.TakeLast(2).SequenceEqual(new[] { (true, 16), (false, 16) }) && h.Device.ScheduledRecovery == (0, 0, 0),
                "a new plan resets spacing history and diagnostic counts without inheriting old ownership");
            h.Device.CancelLargePlaybackOutput(next);
        }
        using (var h = new Harness([On(60, 0), Off(60, .020)]))
        {
            h.Tick(0); Playlib.RejectReleases = 1; h.Tick(.020);
            Check(h.Device.OutputIssue(h.Plan) != null && h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }),
                "rejected owned release faults the plan and retries held-key cleanup");
        }
        using (var h = new Harness([On(60, 0), Off(60, .020)]))
        {
            for (var i = 0; i < 200; i++) h.Tick(-.001);
            Check(h.Events.Length == 0, "dense callbacks before the deadline cannot advance the score or emit a future attack");
        }
        using (var h = new Harness([On(60, 0), Off(60, .200)]))
        {
            h.Tick(0); h.Device.CancelLargePlaybackOutput(h.Plan);
            Plugin.CurrentPlayback = new(); EnsembleManager.EnsembleRunning = true;
            var oldMode = Plugin.config.CompensationMode;
            try
            {
                Plugin.config.CompensationMode = global::MidiBard.CompensationModes.None;
                Playlib.Events.Clear();
                h.Device.SendEventWithMetadata(On(64, 0).Event, new BardPlayDevice.MidiPlaybackMetaData(0, 0, 64));
                h.Tick(.001);
                h.Device.SendEventWithMetadata(Off(64, .020).Event, new BardPlayDevice.MidiPlaybackMetaData(0, 20, 64));
                h.Tick(.021);
                Check(h.Events.SequenceEqual(new[] { (true, 16), (false, 16) }),
                    "cancelling scheduled ownership does not leave ordinary ensemble output behind a closed activation gate");
            }
            finally { Plugin.config.CompensationMode = oldMode; EnsembleManager.EnsembleRunning = false; }
        }
        using (var h = new Harness([Off(64, .050), On(64, .030), Off(60, .010), On(60, 0)]))
        {
            h.Tick(0); h.Tick(.010); h.Tick(.030); h.Tick(.050);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12), (true, 16), (false, 16) }),
                "unsorted input is stably paired in score order before output deadlines are applied");
        }
        using (var h = new Harness([On(60, 0)]))
        {
            h.Tick(0); h.Device.CancelLargePlaybackOutput(h.Plan);
            Check(h.Events.SequenceEqual(new[] { (true, 12), (false, 12) }),
                "an incomplete MIDI without NoteOff retains explicit stop cleanup rather than a guessed duration");
        }
    }

    private static void StallMatrix()
    {
        var events = Enumerable.Range(0, 12).SelectMany(i => new[] { On(60 + i, i * .020), Off(60 + i, i * .020 + .012) }).ToArray();
        foreach (var ms in new[] { 10, 20, 40, 60, 79, 80 })
        {
            using var h = new Harness(events);
            h.Tick(0);
            var previous = h.Events.Count(e => e.Down);
            h.Tick(ms / 1000d);
            Check(h.Events.Count(e => e.Down) - previous <= 1, $"{ms} ms short stall cannot flush several sequential attacks in one callback");
            for (var tick = ms + 1; tick <= 260; tick++)
            {
                previous = h.Events.Count(e => e.Down); h.Tick(tick / 1000d);
                if (h.Events.Count(e => e.Down) - previous > 1) throw new Exception("burst after recovery");
            }
            Check(h.Device.OutputIssue(h.Plan) == null && h.Device.LargePlaybackPending(h.Plan) == 0 && Plugin.AgentPerformance.noteNumber == -100,
                $"{ms} ms stall recovers on the original timeline and drains the final release");
        }
    }

    private static void RandomizedRecovery()
    {
        var random = new Random(310031);
        long skipped = 0, played = 0;
        for (var run = 0; run < 100; run++)
        {
            var events = new List<BardPlayDevice.ScheduledEvent>();
            var planned = new List<(double At, int Pitch)>();
            double at = 0;
            for (var i = 0; i < 100; i++)
            {
                var gap = random.Next(8, 51) / 1000d; var pitch = 60 + i % 20;
                planned.Add((at, pitch - 48)); events.Add(On(pitch, at)); events.Add(Off(pitch, at + gap * .6)); at += gap;
            }
            using var h = new Harness(events);
            double tick = 0;
            while (tick < at + .1)
            {
                h.Tick(tick);
                tick += random.Next(0, 12) == 0 ? random.Next(6, 61) / 1000d : .001;
            }
            h.Tick(at + .100);
            if (h.Device.OutputIssue(h.Plan) != null || h.Device.LargePlaybackPending(h.Plan) != 0 || Plugin.AgentPerformance.noteNumber != -100)
                throw new Exception($"random recovery did not drain safely: run {run}");
            var attacks = h.Trace.Where(e => e.Down).ToArray();
            var lastIndex = -1; double lastActual = 0, lastDue = 0;
            foreach (var e in attacks)
            {
                var index = planned.FindIndex(lastIndex + 1, p => p.Pitch == e.Note && p.At <= e.At + .0000001 && e.At - p.At <= .0800001);
                if (index < 0) throw new Exception("unmatched or early random output");
                if (lastIndex >= 0 && e.At - lastActual + .0000001 < (planned[index].At - lastDue) * .75)
                    throw new Exception("cross-callback random compression");
                lastIndex = index; lastActual = e.At; lastDue = planned[index].At;
            }
            var dropped = h.Device.ScheduledRecovery.ExpiredNotes + h.Device.ScheduledRecovery.CrowdedNotes;
            if (attacks.Length + dropped != planned.Count) throw new Exception("lost note accounting");
            played += attacks.Length; skipped += dropped;
        }
        Check(true, "100 seeded randomized scores / 10000 notes preserve deadlines, spacing, note accounting and final release under short stalls");
        Console.WriteLine($"BURST_RANDOM played={played} skipped={skipped} sourceNotes={played + skipped} runs=100 syntheticClock=true");
    }

    private static void RealTimerRecovery()
    {
        foreach (var pauseMs in new[] { 25, 45 })
        {
            using var d = new BardPlayDevice();
            var ticker = (MidiClock)typeof(BardPlayDevice).GetField("PlaybackTicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(d)!;
            var plan = Guid.NewGuid();
            Plugin.CurrentPlayback = new() { LargePlanId = plan };
            Plugin.CurrentInstrumentWithTone = 14;
            Plugin.config.GuitarToneMode = global::MidiBard.GuitarToneMode.Off;
            Playlib.Events.Clear();
            var target = TransportClock.Now + .200;
            var score = Enumerable.Range(0, 8).SelectMany(i => new[] { On(60 + i, i * .030), Off(60 + i, i * .030 + .020) }).ToArray();
            d.PrepareLargePlaybackOutput(plan, target, 1, false, score);
            d.ActivateLargePlaybackOutput(plan);
            using var entered = new ManualResetEventSlim();
            var armed = 1; double pausedAt = 0, resumedAt = 0;
            void Pause(object? sender, EventArgs e)
            {
                if (TransportClock.Now < target - .010 || Interlocked.Exchange(ref armed, 0) == 0) return;
                pausedAt = TransportClock.Now; entered.Set(); Thread.Sleep(pauseMs); resumedAt = TransportClock.Now;
            }
            ticker.Ticked += Pause;
            try
            {
                Check(entered.Wait(3000), $"real prepared-score timer receives a {pauseMs} ms callback pause");
                var until = TransportClock.Now + 3;
                while (d.LargePlaybackPending(plan) > 0 && TransportClock.Now < until) Thread.Sleep(1);
                var attacks = Playlib.Events.Where(e => e.Down).ToArray();
                Check(d.OutputIssue(plan) == null && d.LargePlaybackPending(plan) == 0 && d.ScheduledRecovery.ExpiredNotes > 0 && attacks.Length > 0,
                    $"real {pauseMs} ms pause skips expired attacks and drains without a hard fault");
                for (var i = 0; i < attacks.Length; i++)
                {
                    var actual = attacks[i].At / (double)Stopwatch.Frequency;
                    var due = target + (attacks[i].Note - 12) * .030;
                    if (actual < due - .00001) throw new Exception("real attack preceded its deadline");
                    if (i > 0)
                    {
                        var gap = Stopwatch.GetElapsedTime(attacks[i - 1].At, attacks[i].At).TotalSeconds;
                        var plannedGap = (attacks[i].Note - attacks[i - 1].Note) * .030;
                        if (gap + .0001 < plannedGap * .75) throw new Exception("real cross-callback burst");
                    }
                }
                Check(Plugin.AgentPerformance.noteNumber == -100, $"real {pauseMs} ms recovery preserves final key release");
                Console.WriteLine($"BURST_REAL requestedPauseMs={pauseMs} measuredPauseMs={(resumedAt - pausedAt) * 1000:F3} played={attacks.Length} expired={d.ScheduledRecovery.ExpiredNotes} crowded={d.ScheduledRecovery.CrowdedNotes} realGameAudio=false");
            }
            finally { ticker.Ticked -= Pause; d.CancelLargePlaybackOutput(plan); }
        }
    }

    private sealed class Harness : IDisposable
    {
        private static readonly FieldInfo Ticker = typeof(BardPlayDevice).GetField("PlaybackTicker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo Drain = typeof(BardPlayDevice).GetMethod("DrainCurrentTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private double now;
        private const double Start = 10;
        public readonly BardPlayDevice Device;
        public readonly Guid Plan = Guid.NewGuid();
        public readonly List<(bool Down, int Note, double At)> Trace = new();
        public (bool Down, int Note)[] Events => Playlib.Events.Select(e => (e.Down, e.Note)).ToArray();
        public Harness(IEnumerable<BardPlayDevice.ScheduledEvent> events, double speed = 1, bool compensate = false, int instrument = 14)
        {
            Playlib.AfterPress = null; Playlib.RejectPress = false; Playlib.RejectReleases = 0; Playlib.Observe = null;
            Plugin.AgentPerformance.InPerformanceMode = true;
            Plugin.CurrentInstrumentWithTone = instrument; EnsembleManager.EnsembleRunning = false;
            Plugin.config.GuitarToneMode = global::MidiBard.GuitarToneMode.Off;
            Plugin.config.TrackStatus = [new(), new()];
            Plugin.CurrentPlayback = new() { LargePlanId = Plan, UseLargeInstrumentCompensation = compensate, TrackInfos = [new(), new()] };
            Device = new BardPlayDevice(() => now);
            ((MidiClock)Ticker.GetValue(Device)!).Stop();
            Device.PrepareLargePlaybackOutput(Plan, Start, speed, compensate, events);
            Device.ActivateLargePlaybackOutput(Plan);
            Playlib.Events.Clear();
            Playlib.Observe = (down, note) => Trace.Add((down, note, now - Start));
        }
        public void Tick(double seconds) { now = Start + seconds; Drain.Invoke(Device, null); }
        public void Advance(double seconds) => now += seconds;
        public void Dispose()
        { Playlib.AfterPress = null; Playlib.Observe = null; Playlib.RejectReleases = 0; Device.CancelLargePlaybackOutput(Plan); Device.Dispose(); Plugin.config.GuitarToneMode = global::MidiBard.GuitarToneMode.Off; }
    }
}
