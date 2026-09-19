using BardStage;
using BardStage.Core;

internal static class StageRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        var directory = Path.Combine(scratch, "stage-data");
        using (var controller = new StageController(directory))
        {
            controller.Import([midiPath]);
            while (controller.IsBusy) { Thread.Sleep(10); controller.Poll(); }
            var showId = controller.CurrentSetlist!.Id;
            controller.Change(s =>
            {
                var show = s.Setlists[0]; var song = s.Songs[0];
                SetlistOperations.AddSong(show, song); SetlistOperations.AddSong(show, song);
                show.LockedNextEntryId = show.Entries[1].Id;
                var request = RequestOperations.Submit(s, show.Id, "Guest", "World", song.Title, RequestChannel.Manual, DateTimeOffset.UtcNow);
                RequestOperations.Arrange(s, [request.Id], song.Id, mergeEntryId: show.Entries[1].Id);
                StageOperations.StartNext(s, showId, show.Entries[1].Id, DateTimeOffset.UtcNow);
            });
            var activeId = StageOperations.Current(controller.CurrentSetlist!)!.Id;
            var originalNext = StageOperations.Next(controller.CurrentSetlist!)!.Id;
            var catalogPath = Path.Combine(directory, "catalog.json");
            using (var blocked = new FileStream(catalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!controller.Change(s => StageOperations.CompleteAndAdvance(s, showId, activeId, DateTimeOffset.UtcNow))
                    && RequestOperations.DisplayStatus(controller.State, controller.State.Requests[0]) == "进行中",
                    "stage completion failure preserves active program and request status");
            controller.Change(s => StageOperations.CompleteAndAdvance(s, showId, activeId, DateTimeOffset.UtcNow));
            Check(StageOperations.Current(controller.CurrentSetlist!) == null && StageOperations.Next(controller.CurrentSetlist!)?.Id == originalNext
                && RequestOperations.DisplayStatus(controller.State, controller.State.Requests[0]) == "已完成", "stage completion prepares next without starting playback or next program");
            var template = StageController.Clone(controller.Announcements); template.Templates[AnnouncementKind.Opening] = "Good evening {show}";
            Check(controller.SaveAnnouncements(template), "announcement templates save separately");
            using (var second = new StageController(directory))
                Check(second.IsReadOnly && !second.SaveAnnouncements(AnnouncementTemplates.Defaults()) && second.Announcements.Templates[AnnouncementKind.Opening] == template.Templates[AnnouncementKind.Opening],
                    "second instance cannot overwrite announcement templates");
            var templatePath = Path.Combine(directory, "announcements.json");
            var bytes = File.ReadAllBytes(templatePath);
            controller.ExportCurrentSetlist(templatePath);
            Check(controller.StatusIsError && File.ReadAllBytes(templatePath).SequenceEqual(bytes), "program export cannot overwrite announcement templates");
            using (var blocked = new FileStream(templatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!controller.SaveAnnouncements(AnnouncementTemplates.Defaults()) && controller.Announcements.Templates[AnnouncementKind.Opening] == template.Templates[AnnouncementKind.Opening],
                    "failed template save preserves active templates");
            Check(controller.State.SchemaVersion == 3, "stage sessions use the v0.4 catalog format");
        }
        using (var reopened = new StageController(directory))
            Check(reopened.Announcements.Templates[AnnouncementKind.Opening] == "Good evening {show}" && reopened.State.Requests.Count == 1,
                "templates and audience associations restore after reload");
        File.WriteAllText(Path.Combine(directory, "announcements.json"), "{broken");
        using var damaged = new StageController(directory);
        Check(!damaged.IsReadOnly && damaged.AnnouncementError.Length > 0 && !damaged.SaveAnnouncements(AnnouncementTemplates.Defaults())
            && damaged.Change(s => s.Setlists[0].Name = "仍可管理演出"), "corrupt templates are protected while catalog operations remain usable");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
