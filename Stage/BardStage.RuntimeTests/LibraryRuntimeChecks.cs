using BardStage;
using BardStage.Core;

internal static class LibraryRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        var directory = Path.Combine(scratch, "library");
        using var controller = new StageController(directory);
        var mirror = new Mirror();
        controller.PreparePlayerLibraryEdit = mirror.Begin;
        controller.Import([midiPath]); Complete(controller);
        Check(controller.State.Songs.Count == 1 && mirror.Paths.SequenceEqual(new[] { midiPath }), "module import updates native library transaction");
        var song = controller.State.Songs[0];
        controller.AddToQueue(song.Id);
        var file = Path.Combine(directory, "catalog.json");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!controller.DeleteSong(song.Id) && controller.State.Songs.Count == 1 && mirror.Paths.SequenceEqual(new[] { midiPath }), "catalog write failure rolls back library and player together");
        mirror.Fail = true;
        Check(!controller.DeleteSong(song.Id) && controller.State.Songs.Count == 1 && controller.QueueShow!.Entries.Count == 1,
            "native playlist write failure preserves catalog and queued entries");
        mirror.Fail = false;
        Check(controller.DeleteSong(song.Id) && controller.State.Songs.Count == 0 && mirror.Paths.Count == 0 && controller.QueueShow!.Entries.Count == 0,
            "single delete removes library and native playlist entry together");
        Check(File.Exists(midiPath), "library deletion preserves original MIDI bytes");
        controller.SynchronizePlayerLibrary([midiPath]); Complete(controller);
        Check(controller.State.Songs.Count == 1 && controller.State.SharedLibraryInitialized && mirror.Paths.Count == 0,
            "native import mirrors into stage without writing a feedback update");
        controller.SynchronizePlayerLibrary([]); Complete(controller);
        Check(controller.State.Songs.Count == 0 && new CatalogStore(directory).Load().Songs.Count == 0,
            "native clear persists empty library across reload");
        Check(new CatalogStore(directory).Load().SharedLibraryInitialized, "empty shared library retains initialization marker");
        controller.SynchronizePlayerLibrary([midiPath]); Complete(controller);
        controller.AddToQueue(controller.State.Songs[0].Id);
        var entryId = controller.QueueShow!.Entries[0].Id;
        controller.Change(s => SetlistOperations.Start(s.Setlists.Single(x => x.Id == s.RequestSettings.TargetSetlistId), entryId, DateTimeOffset.UtcNow));
        Check(!controller.SynchronizePlayerLibrary([]) && !controller.ClearLibrary() && controller.State.Songs.Count == 1,
            "playing song blocks both native synchronization removal and clear library");
    }

    private static void Complete(StageController controller)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (controller.IsBusy && DateTimeOffset.UtcNow < until) { controller.Poll(); Thread.Sleep(5); }
        Check(!controller.IsBusy && !controller.StatusIsError, "library async transaction completed");
    }

    private sealed class Mirror
    {
        public List<string> Paths = [];
        public bool Fail;
        public ILibraryEdit Begin(IReadOnlyList<SongEntry> before, IReadOnlyList<SongEntry> after)
        {
            if (Fail) throw new IOException("Native playlist blocked");
            return new Edit(this, after.Select(s => s.FilePath).ToList());
        }
        private sealed class Edit : ILibraryEdit
        {
            private readonly Mirror owner;
            private readonly List<string> previous;
            private bool committed;
            public Edit(Mirror owner, List<string> paths) { this.owner = owner; previous = owner.Paths; owner.Paths = paths; }
            public void Commit() => committed = true;
            public void Dispose() { if (!committed) owner.Paths = previous; }
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
