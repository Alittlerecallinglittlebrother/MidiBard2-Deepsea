using System.Text.Json;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;

internal static class CleanupRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        var data = Path.Combine(scratch, "cleanup");
        using var controller = new StageController(data);
        controller.Import([midiPath]);
        while (controller.IsBusy) { controller.Poll(); Thread.Sleep(5); }
        controller.EnsureAutomaticQueue();
        controller.Change(s => SetlistOperations.AddSong(s.Setlists[0], s.Songs[0]));
        var show = controller.CurrentSetlist!; var entry = show.Entries[0];
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        controller.Change(s => SetlistOperations.Start(s.Setlists[0], entry.Id, start), atUtc: start);
        controller.Change(s => SetlistOperations.Pause(s.Setlists[0], entry.Id, start.AddSeconds(20)), atUtc: start.AddSeconds(20));
        var sessionId = controller.State.Sessions.Single().Id;
        var catalogPath = Path.Combine(data, "catalog.json");
        var before = File.ReadAllBytes(catalogPath);
        var jsonPath = Path.Combine(scratch, "open-session.json");
        var csvPath = Path.Combine(scratch, "open-session.csv");
        controller.ExportSession(sessionId, jsonPath, false);
        using (var json = JsonDocument.Parse(File.ReadAllText(jsonPath)))
            Check(json.RootElement.GetProperty("isLiveSnapshot").GetBoolean()
                && json.RootElement.GetProperty("endedAtUtc").ValueKind == JsonValueKind.Null
                && json.RootElement.GetProperty("attempts")[0].GetProperty("outcome").GetString() == "InProgress",
                "unarchived active session exports JSON without ending the performance");
        controller.ExportSession(sessionId, csvPath, true);
        Check(!controller.StatusIsError && File.ReadAllText(csvPath).Contains("\"20.00\"")
            && File.ReadAllBytes(csvPath).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })
            && before.SequenceEqual(File.ReadAllBytes(catalogPath)), "live CSV export uses paused elapsed time, Excel UTF-8 BOM and leaves catalog unchanged");
        controller.ExportSession(sessionId, catalogPath, false);
        Check(controller.StatusIsError && before.SequenceEqual(File.ReadAllBytes(catalogPath)), "live export cannot overwrite the active catalog");
        Check(!controller.DeleteSession(sessionId) && !controller.DeleteEntries(show.Id, [entry.Id]), "controller blocks deletion of playing records and programs");
        controller.Change(s => SetlistOperations.Finish(s.Setlists[0], entry.Id, DateTimeOffset.UtcNow));
        var attemptId = controller.State.Sessions.Single().Attempts.Single().Id;
        using (var blocker = new FileStream(catalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!controller.DeleteEntries(show.Id, [entry.Id]) && controller.CurrentSetlist!.Entries.Any(e => e.Id == entry.Id), "failed cleanup save rolls back the program deletion");
        Check(controller.DeleteEntries(show.Id, [entry.Id]) && controller.State.Sessions.Single().Attempts.Count == 1,
            "finished program cleanup preserves performance details");
        Check(controller.DeleteAttempts(sessionId, [attemptId]) && controller.DeleteSession(sessionId), "controller deletes a detail and an idle unarchived session");
        using (var reopened = new StageController(data))
            Check(reopened.State.Sessions.Count == 0 && reopened.CurrentSetlist!.Entries.Count == 0 && reopened.State.Songs.Count == 1,
                "deleted history stays deleted after loading saved data while library remains");
        RoomChecks(scratch, midiPath);
    }

    private static void RoomChecks(string scratch, string midiPath)
    {
        using var f = new RoomRuntimeChecks.Fixture(scratch, midiPath);
        f.Connect(); f.Remote(RoomAction.Add, song: f.First); f.Remote(RoomAction.Add, song: f.Second);
        var first = f.Show.Entries[0].Id;
        f.Send(new RoomCommand { Action = RoomAction.DeleteFinished, EntryId = first }, false);
        Check(f.Show.Entries.Count == 2, "remote history deletion cannot delete waiting songs");
        f.Remote(RoomAction.Remove, entry: first);
        var attempts = f.Captain.State.Sessions.Single().Attempts.Count;
        f.Remote(RoomAction.DeleteFinished, entry: first);
        Check(f.Show.Entries.Count == 1 && f.Captain.State.Sessions.Single().Attempts.Count == attempts,
            "presenter deletes ended entry over TLS while captain retains performance history");
        f.Remote(RoomAction.Remove, entry: f.Show.Entries[0].Id);
        f.Remote(RoomAction.ClearFinished);
        Check(f.Show.Entries.Count == 0 && f.Presenter.QueueViewShow!.Entries.Count == 0, "remote clear-ended action updates captain and presenter together");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
