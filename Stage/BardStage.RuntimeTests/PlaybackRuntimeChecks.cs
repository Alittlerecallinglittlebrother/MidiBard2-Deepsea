using System.Collections.Concurrent;
using System.Text.Json;
using BardStage;
using BardStage.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

internal static class PlaybackRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        Engine(scratch, midiPath);
        Controller(scratch, midiPath);
        Ipc();
    }

    private static Playback NewPlayback() => new MidiFile(new TrackChunk(
        new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)80),
        new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 96 }))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) }.GetPlayback();

    private static void Engine(string scratch, string path)
    {
        using var controller = Setup(Path.Combine(scratch, "engine-data"), path);
        var events = new ConcurrentQueue<PlaybackSignal>();
        using var observer = new PlaybackObserver(s => { events.Enqueue(s); controller.ReceivePlayback(s); });
        using var playback = NewPlayback();
        observer.Attach(playback, path);
        controller.Poll();
        Check(controller.CurrentSetlist!.Entries[0].Status == EntryStatus.Queued, "real engine load does not start a program");
        playback.Start();
        Await(() => events.Any(s => s.Kind == PlaybackSignalKind.Started)); controller.Poll();
        Check(controller.CurrentSetlist.Entries[0].Status == EntryStatus.InProgress, "real DryWetMidi Started reaches the controller");
        playback.Stop();
        Await(() => events.Any(s => s.Kind == PlaybackSignalKind.Paused)); controller.Poll();
        Check(controller.CurrentSetlist.Entries[0].PausedAtUtc.HasValue, "real DryWetMidi Stop is a pause");
        playback.Start();
        Await(() => events.Any(s => s.Kind == PlaybackSignalKind.Resumed)); controller.Poll();
        Check(!controller.CurrentSetlist.Entries[0].PausedAtUtc.HasValue, "real engine restart resumes the same attempt");
        Await(() => events.Any(s => s.Kind == PlaybackSignalKind.Finished)); controller.Poll();
        Check(controller.CurrentSetlist.Entries[0].Status == EntryStatus.Completed
            && controller.State.Sessions.Single().Attempts.Count == 1, "natural engine Finished completes exactly one attempt");
        var firstId = events.First().PlaybackId;
        playback.MoveToTime(new MidiTimeSpan(0)); playback.Start();
        Await(() => events.Count(s => s.Kind == PlaybackSignalKind.Started) == 2); controller.Poll();
        Check(events.Last().PlaybackId != firstId && controller.CurrentSetlist.Entries[1].Status == EntryStatus.InProgress,
            "same-object repeat has a new playback identity and matches the next repeated song");
        observer.Stop(); playback.Stop(); controller.Poll();
        Check(controller.CurrentSetlist.Entries[1].Status == EntryStatus.Skipped
            && controller.State.Sessions.Single().Attempts.Last().Outcome == AttemptOutcome.Interrupted, "explicit stop records interruption instead of completion");
        using var replacement = NewPlayback();
        observer.Attach(replacement, path); replacement.Start();
        Await(() => events.Last().Kind == PlaybackSignalKind.Started);
        using var remote = NewPlayback();
        observer.Attach(remote, null);
        Check(events.TakeLast(2).Select(s => s.Kind).SequenceEqual(new[] { PlaybackSignalKind.Stopped, PlaybackSignalKind.Loaded })
            && events.Last().FilePath == "", "replacement interrupts the old playback; remote stream accepts an absent local path");
        var count = events.Count;
        replacement.Stop();
        Check(events.Count == count, "replaced playback subscriptions are removed");
        observer.Dispose(); remote.Start(); remote.Stop();
        Check(events.Count == count, "dispose detaches subscriptions without fabricating a finish");
        Check(events.Select(s => s.Sequence).SequenceEqual(Enumerable.Range(1, events.Count).Select(n => (long)n)),
            "engine event sequence is strictly ordered");
    }

    private static StageController Setup(string directory, string path)
    {
        var controller = new StageController(directory) { SyncAvailable = true };
        controller.Import([path]);
        Await(() => { controller.Poll(); return !controller.IsBusy; });
        Check(controller.Change(s =>
        {
            for (var i = 0; i < 4; i++) SetlistOperations.AddSong(s.Setlists[0], s.Songs[0]);
        }), "playback fixture imported and saved");
        controller.ConfigureSync(controller.CurrentSetlist!.Id, true);
        return controller;
    }

    private static void Controller(string scratch, string path)
    {
        var directory = Path.Combine(scratch, "sync-data");
        using var controller = Setup(directory, path);
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var id = Guid.NewGuid(); long sequence = 0;
        void Send(PlaybackSignalKind kind, int seconds, string? file = null, Guid? playId = null)
        {
            controller.ReceivePlayback(new(playId ?? id, ++sequence, file ?? path, kind, now.AddSeconds(seconds)));
            controller.Poll();
        }
        SetlistEntry First() => controller.CurrentSetlist!.Entries[0];
        Send(PlaybackSignalKind.Loaded, 0);
        Send(PlaybackSignalKind.Started, 0, Path.Combine(scratch, "another.mid"));
        Send(PlaybackSignalKind.Started, 0, "");
        Check(First().Status == EntryStatus.Queued && controller.State.Sessions.Count == 0, "load, mismatched path and remote-only playback cannot start a program");
        var other = new ShowSetlist { Name = "Other show" };
        controller.Change(s => { s.Setlists.Add(other); s.SelectedSetlistId = other.Id; });
        Send(PlaybackSignalKind.Started, 0);
        Check(controller.State.Setlists[0].Entries[0].Status == EntryStatus.InProgress && controller.CurrentSetlist!.Id == other.Id,
            "view selection cannot redirect bound playback synchronization");
        controller.Change(s => s.SelectedSetlistId = s.Setlists[0].Id);
        var sessionId = controller.State.Sessions.Single().Id;
        controller.ReceivePlayback(new(id, sequence, path, PlaybackSignalKind.Finished, now.AddSeconds(99))); controller.Poll();
        Check(First().Status == EntryStatus.InProgress, "duplicate sequence cannot prematurely finish a program");
        Send(PlaybackSignalKind.Paused, 10); Send(PlaybackSignalKind.Resumed, 40); Send(PlaybackSignalKind.Finished, 60);
        Check(First().PausedSeconds == 30 && SessionOperations.ActualSeconds(controller.State.Sessions.Single().Attempts.Single(), now.AddSeconds(90)) == 30,
            "pause duration is excluded from persisted actual performance time");
        var currentId = Guid.NewGuid();
        var filePath = Path.Combine(directory, "catalog.json");
        using (var blocker = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Send(PlaybackSignalKind.Started, 70, playId: currentId);
            Check(controller.SyncBlocked && controller.CurrentSetlist!.Entries[1].Status == EntryStatus.Queued
                && controller.State.Sessions.Single().Attempts.Count == 1, "failed start persistence rolls back attempt and program together");
            Send(PlaybackSignalKind.Finished, 90, playId: currentId);
        }
        controller.RetryPlayback(); controller.Poll(); controller.Poll();
        Check(!controller.SyncBlocked && controller.State.Sessions.Single().Attempts.Count == 2
            && controller.CurrentSetlist!.Entries[1].Status == EntryStatus.Completed, "retry replays retained start and finish exactly once");
        var thirdId = Guid.NewGuid(); Send(PlaybackSignalKind.Started, 100, playId: thirdId);
        controller.Change(s => StageOperations.SkipAndAdvance(s, s.Setlists[0].Id, s.Setlists[0].Entries[2].Id, now.AddSeconds(110)));
        Send(PlaybackSignalKind.Finished, 120, playId: thirdId);
        Check(controller.CurrentSetlist!.Entries[2].Status == EntryStatus.Skipped && controller.CurrentSetlist.Entries[3].Status == EntryStatus.Queued,
            "old finish cannot overwrite a manual interruption or start another program");
        Check(controller.Change(s => SessionOperations.Archive(s, s.Setlists[0].Id, now.AddSeconds(130))), "archive synchronized session with original event timestamps");
        var export = Path.Combine(scratch, "session.json");
        controller.ExportSession(sessionId, export, false);
        using (var json = JsonDocument.Parse(File.ReadAllText(export)))
            Check(json.RootElement.GetProperty("attempts").GetArrayLength() == 3, "archive JSON includes every completion and interruption");
        controller.ExportSession(sessionId, Path.Combine(scratch, "session.csv"), true);
        Check(File.ReadAllLines(Path.Combine(scratch, "session.csv")).Length == 4, "archive CSV exports one row per attempt");
        var bytes = File.ReadAllBytes(filePath);
        controller.ExportSession(sessionId, filePath, false);
        Check(controller.StatusIsError && File.ReadAllBytes(filePath).SequenceEqual(bytes), "archive export protects the live catalog");
        controller.ConfigureSync(controller.CurrentSetlist.Id, false);
        Send(PlaybackSignalKind.Started, 140, playId: Guid.NewGuid());
        Check(controller.CurrentSetlist.Entries[3].Status == EntryStatus.Queued, "disabled synchronization keeps events observational");
        controller.ConfigureSync(controller.CurrentSetlist.Id, true);
        var replayId = Guid.NewGuid();
        Send(PlaybackSignalKind.Started, 150, playId: replayId);
        Check(controller.Change(s => SetlistOperations.Skip(s.Setlists[0], s.Setlists[0].Entries[3].Id, now.AddSeconds(160)))
            && controller.Change(s => SetlistOperations.ResetEntry(s.Setlists[0], s.Setlists[0].Entries[3].Id))
            && controller.Change(s => SetlistOperations.Start(s.Setlists[0], s.Setlists[0].Entries[3].Id, DateTimeOffset.UtcNow)),
            "manual takeover resets and restarts the same stable program ID");
        Send(PlaybackSignalKind.Finished, 170, playId: replayId);
        Check(controller.CurrentSetlist.Entries[3].Status == EntryStatus.InProgress && !controller.SyncBlocked,
            "finish from the old playback cannot complete a manually restarted attempt of the same program");
    }

    private static void Ipc()
    {
        var plugin = ServiceProxy.Create<IDalamudPluginInterface>();
        var snapshot = ServiceProxy.Create<ICallGateProvider<string>>();
        var changed = ServiceProxy.Create<ICallGateProvider<string, object>>();
        Func<string>? read = null; var registered = false; var removed = false; var notifications = new List<string>(); var errors = 0;
        ((ServiceProxy)(object)plugin).MethodHandler = (method, args) => args![0] switch
        {
            StagePlaybackIpc.SnapshotName => snapshot,
            StagePlaybackIpc.ChangedName => changed,
            _ => throw new InvalidOperationException("unexpected IPC provider"),
        };
        ((ServiceProxy)(object)snapshot).MethodHandler = (method, args) =>
        {
            if (method.Name == "RegisterFunc") { read = (Func<string>)args![0]!; registered = true; }
            if (method.Name == "UnregisterFunc") removed = true;
            return null;
        };
        ((ServiceProxy)(object)changed).MethodHandler = (method, args) =>
        {
            if (method.Name == "SendMessage") { notifications.Add((string)args![0]!); if (notifications.Count == 1) throw new InvalidOperationException("subscriber error"); }
            return null;
        };
        using var ipc = new StagePlaybackIpc(plugin, _ => errors++);
        Check(registered && JsonDocument.Parse(read!()).RootElement.GetProperty("state").ValueKind == JsonValueKind.Null,
            "Dalamud IPC registers an initially empty snapshot");
        var signal = new PlaybackSignal(Guid.NewGuid(), 1, "test.mid", PlaybackSignalKind.Started, DateTimeOffset.UtcNow);
        ipc.Receive(signal); ipc.Poll();
        ipc.Receive(signal with { Sequence = 2, Kind = PlaybackSignalKind.Finished }); ipc.Poll();
        using (var json = JsonDocument.Parse(read!()))
            Check(json.RootElement.GetProperty("protocol").GetInt32() == 1
                && json.RootElement.GetProperty("state").GetProperty("kind").GetString() == "Finished"
                && notifications.Count == 2 && errors == 1, "IPC publishes JSON states and isolates subscriber failures");
        ipc.Dispose(); ipc.Receive(signal); ipc.Poll();
        Check(removed && notifications.Count == 2, "IPC unregisters its snapshot and stops publication on unload");
    }

    private static void Await(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5))) throw new TimeoutException("Playback runtime check timed out.");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
