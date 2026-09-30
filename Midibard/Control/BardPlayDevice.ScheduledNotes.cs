using System;
using System.Collections.Generic;
using System.Linq;
using Melanchall.DryWetMidi.Core;

namespace MidiBard.Control;

public partial class BardPlayDevice
{
    // Musical windows, not a fixed minimum note spacing: fast passages remain fast.
    // Keep >= 75% of the planned attack interval and >= 50% of a note's gate time.
    // The existing >80 ms hard fault still takes precedence over soft recovery.
    private const double ScheduledIntervalRatio = .75;
    private const double ScheduledRemainingRatio = .50;
    private const double ScheduledZeroLengthGrace = .002;
    private const double ScheduledTimeEpsilon = .0000001;

    private sealed class ScheduledNote(double due, double scoreTime, bool playable)
    {
        public readonly double Due = due;
        public readonly double ScoreTime = scoreTime;
        public readonly bool Playable = playable;
        public double EndDue = double.PositiveInfinity;
        public double LatestStart = double.PositiveInfinity;
    }

    private object scheduledOutputOwner;
    private ScheduledNote activeScheduledNote;
    private bool hasScheduledAttack;
    private double lastScheduledDue, lastScheduledActual, lastScheduledScoreTime;
    private long expiredScheduledNotes, crowdedScheduledNotes, ignoredScheduledReleases;

    internal (long ExpiredNotes, long CrowdedNotes, long IgnoredReleases) ScheduledRecovery
    { get { lock (outputGate) return (expiredScheduledNotes, crowdedScheduledNotes, ignoredScheduledReleases); } }

    // Called under outputGate while the prepared score is still inactive. FIFO
    // pairing is deterministic for overlapping occurrences of one MIDI key.
    // Track + MIDI channel + original pitch identify the stream; each NoteOn
    // receives its own object, even when transposition maps keys to one game note.
    private void PrepareScheduledNoteLinks(List<(OutputEvent Event, double ScoreTime)> events)
    {
        scheduledOutputOwner = MidiBard.CurrentPlayback;
        var open = new Dictionary<(int Track, int Channel, int Pitch), Queue<ScheduledNote>>();
        var attacks = new List<ScheduledNote>();
        var changedRelease = false;
        foreach (var (item, scoreTime) in events)
        {
            if (!ReferenceEquals(item.Owner, scheduledOutputOwner))
                throw new InvalidOperationException("预约歌曲已被更换，请重新下发");
            if (item.Event is not NoteEvent note) continue;
            var key = (item.Metadata.TrackIndex, (int)note.Channel, (int)note.NoteNumber);
            if (item.Event is NoteOnEvent)
            {
                var span = new ScheduledNote(item.Due, scoreTime,
                    GetNoteNumberTranslatedByTrack(note.NoteNumber, item.Metadata.TrackIndex) is >= 0 and <= 36);
                item.Note = span;
                if (!open.TryGetValue(key, out var queue)) open[key] = queue = new();
                queue.Enqueue(span);
                // An out-of-range silent note must not close a playable note's window.
                if (span.Playable) attacks.Add(span);
            }
            else if (item.Event is NoteOffEvent && open.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var span = queue.Dequeue();
                item.Note = span;
                // Existing chord compensation can delay an attack more than its
                // release. Never let that release precede its own attack and vanish.
                if (item.Due < span.Due) { item.Due = span.Due; changedRelease = true; }
                span.EndDue = item.Due;
            }
        }
        if (changedRelease)
        {
            var entries = pending.UnorderedItems.ToArray();
            pending.Clear();
            foreach (var entry in entries)
                pending.Enqueue(entry.Element, (entry.Element.Due, entry.Priority.Order, entry.Priority.Sequence));
        }

        var ordered = attacks.OrderBy(n => n.Due).ToArray();
        ScheduledNote nearest = null, different = null;
        // Read a whole deadline bucket before publishing it as lookahead. Equal
        // deadlines and same-score-time chords are intentional, not catch-up bursts.
        for (var end = ordered.Length; end > 0;)
        {
            var first = end - 1;
            while (first > 0 && ordered[end - 1].Due - ordered[first - 1].Due <= ScheduledTimeEpsilon) first--;
            for (var i = first; i < end; i++)
            {
                var note = ordered[i];
                if (double.IsFinite(note.EndDue))
                {
                    var duration = note.EndDue - note.Due;
                    note.LatestStart = note.Due + (duration <= ScheduledTimeEpsilon
                        ? ScheduledZeroLengthGrace : duration * (1 - ScheduledRemainingRatio));
                }
                var next = nearest?.ScoreTime == note.ScoreTime ? different : nearest;
                if (next != null)
                    note.LatestStart = Math.Min(note.LatestStart, note.Due + (next.Due - note.Due) * (1 - ScheduledIntervalRatio));
            }
            for (var i = first; i < end; i++)
            {
                var note = ordered[i];
                if (nearest != null && nearest.ScoreTime != note.ScoreTime) different = nearest;
                nearest = note;
            }
            end = first;
        }
    }

    private bool SkipScheduledAttack(OutputEvent item, double now)
    {
        if (item.Event is not NoteOnEvent || item.Note is not { Playable: true } note) return false;
        if (now > note.LatestStart + ScheduledTimeEpsilon)
        { expiredScheduledNotes++; return true; }
        if (hasScheduledAttack && note.ScoreTime != lastScheduledScoreTime)
        {
            var plannedGap = note.Due - lastScheduledDue;
            if (plannedGap > ScheduledTimeEpsilon
                && now - lastScheduledActual + ScheduledTimeEpsilon < plannedGap * ScheduledIntervalRatio)
            { crowdedScheduledNotes++; return true; }
        }
        return false;
    }

    private void RegisterScheduledDispatch(OutputEvent item, double actual)
    {
        if (item.Event is NoteOnEvent && item.Note is { } note)
        {
            activeScheduledNote = note;
            hasScheduledAttack = true;
            lastScheduledDue = note.Due; lastScheduledActual = actual; lastScheduledScoreTime = note.ScoreTime;
        }
        else if (item.Event is NoteOffEvent) activeScheduledNote = null;
    }

    private void ClearScheduledNoteOwnership()
    {
        scheduledOutputOwner = null; activeScheduledNote = null; hasScheduledAttack = false;
        lastScheduledDue = lastScheduledActual = lastScheduledScoreTime = 0;
    }
}
