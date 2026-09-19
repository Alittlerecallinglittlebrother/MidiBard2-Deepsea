using BardStage;
using BardStage.Core;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

internal static class LibrarySyncRegressionChecks
{
    internal static void Run(string scratch)
    {
        Directory.CreateDirectory(scratch);
        var good = Path.Combine(scratch, "good.mid");
        WriteMidi(good, 60);
        var broken = Path.Combine(scratch, "broken.mid");
        File.WriteAllText(broken, "not a MIDI file");
        using (var controller = new StageController(Path.Combine(scratch, "partial-import")))
        {
            Check(controller.SynchronizePlayerLibrary([good, broken]), "initial shared-library synchronization starts");
            Complete(controller);
            Check(controller.StatusIsError && controller.LastPlayerLibrarySyncSaved && controller.State.Songs.Count == 1,
                "partial import saves valid songs and separately records file warnings");
            var state = controller.State;
            var revision = controller.QueueRevision;
            var catalog = Path.Combine(controller.DataDirectory, "catalog.json");
            var bytes = File.ReadAllBytes(catalog);
            var writeTime = File.GetLastWriteTimeUtc(catalog);
            var message = controller.StatusMessage;
            for (var i = 0; i < 120; i++)
            {
                CheckQuiet(!controller.SynchronizePlayerLibrary(i % 2 == 0 ? [good, broken] : [broken, good, broken]), "unchanged warning retried at poll " + i);
                controller.Poll();
                CheckQuiet(!controller.IsBusy && ReferenceEquals(state, controller.State), "unchanged warning replaced state at poll " + i);
            }
            Check(controller.QueueRevision == revision && controller.StatusMessage == message
                && File.ReadAllBytes(catalog).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(catalog) == writeTime,
                "120 unchanged polls preserve status, catalog revision, file bytes and idle state after partial warning");

            controller.SetStatus("unrelated later warning", true);
            Check(!controller.SynchronizePlayerLibrary([good, broken]) && controller.StatusMessage == "unrelated later warning",
                "unrelated status errors cannot restart a completed synchronization");
            Check(controller.SynchronizePlayerLibrary([good, broken], force: true), "explicit refresh retries unchanged warning once");
            Complete(controller);
            Check(!controller.SynchronizePlayerLibrary([good, broken]) && !controller.IsBusy,
                "explicit refresh completion does not restore the retry loop");

            WriteMidi(broken, 62);
            File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddSeconds(2));
            Check(controller.SynchronizePlayerLibrary([good, broken]), "changed failed MIDI is automatically retried");
            Complete(controller);
            Check(!controller.StatusIsError && controller.LastPlayerLibrarySyncSaved && controller.State.Songs.Count == 2,
                "repaired failed MIDI imports and clears the prior warning");
            var repaired = controller.State.Songs.Single(song => song.FilePath == broken);
            Check(controller.SynchronizePlayerLibrary([good]), "playlist removal changes synchronization input");
            Complete(controller);
            Check(controller.State.Songs.Count == 1 && controller.State.Songs.All(song => song.Id != repaired.Id) && File.Exists(broken),
                "playlist removal updates catalog without deleting original MIDI");
        }

        using (var controller = new StageController(Path.Combine(scratch, "save-failure")))
        {
            var file = Path.Combine(controller.DataDirectory, "catalog.json");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(controller.SynchronizePlayerLibrary([good]), "save-failure synchronization starts");
                Complete(controller);
                Check(controller.StatusIsError && !controller.LastPlayerLibrarySyncSaved && controller.State.Songs.Count == 0,
                    "save failure never acknowledges an unsaved library");
                for (var i = 0; i < 120; i++)
                    CheckQuiet(!controller.SynchronizePlayerLibrary([good]) && !controller.IsBusy, "save failure spun at poll " + i);
                Check(true, "120 identical polls do not spin while catalog storage is blocked");
            }
            Check(controller.SynchronizePlayerLibrary([good], force: true), "manual refresh can retry after storage recovers");
            Complete(controller);
            Check(controller.LastPlayerLibrarySyncSaved && !controller.StatusIsError && controller.State.Songs.Count == 1,
                "storage recovery saves once and acknowledges completion");
        }

        var missing = Path.Combine(scratch, "missing.mid");
        using (var controller = new StageController(Path.Combine(scratch, "missing-file")))
        {
            Check(controller.SynchronizePlayerLibrary([missing]), "missing path reports an initial synchronization issue");
            Complete(controller);
            Check(controller.StatusIsError && !controller.SynchronizePlayerLibrary([missing]), "unchanged missing path remains quiet");
            WriteMidi(missing, 64);
            Check(controller.SynchronizePlayerLibrary([missing]), "previously missing file appearance triggers a new attempt");
            Complete(controller);
            Check(controller.State.Songs.Count == 1 && !controller.StatusIsError, "newly available MIDI is imported automatically");
        }
    }

    internal static void WriteMidi(string path, byte pitch)
    {
        new MidiFile(new TrackChunk(new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)80),
            new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0) { DeltaTime = 480 }))
            { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) }.Write(path, overwriteFile: true);
    }

    internal static void Complete(StageController controller)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (controller.IsBusy && DateTimeOffset.UtcNow < until) { controller.Poll(); Thread.Sleep(5); }
        CheckQuiet(!controller.IsBusy, "library synchronization did not complete in ten seconds");
    }

    private static void Check(bool value, string message)
    {
        CheckQuiet(value, message);
        Console.WriteLine("PASS: " + message);
    }

    private static void CheckQuiet(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
