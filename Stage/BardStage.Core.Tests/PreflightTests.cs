using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class PreflightTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-preflight-tests", Guid.NewGuid().ToString("N"));

    public PreflightTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void UnchangedFilePassesWithoutMutatingCatalogOrSource()
    {
        var (state, show, song, entry) = CreatePlan();
        var stateBefore = Snapshot(state);
        var bytesBefore = File.ReadAllBytes(song.FilePath);
        var modifiedBefore = File.GetLastWriteTimeUtc(song.FilePath);

        var report = PreflightOperations.Check(state, show.Id, 4);

        Assert.Empty(report.Issues);
        Assert.Equal(1, report.CheckedFiles);
        Assert.Equal(show.Id, report.ShowId);
        Assert.Equal(4, report.AvailablePerformers);
        Assert.Equal(PreflightOperations.Fingerprint(state, show.Id, 4), report.Fingerprint);
        Assert.NotEqual(default, report.CheckedAtUtc);
        Assert.Equal(stateBefore, Snapshot(state));
        Assert.Equal(bytesBefore, File.ReadAllBytes(song.FilePath));
        Assert.Equal(modifiedBefore, File.GetLastWriteTimeUtc(song.FilePath));
        Assert.Equal(entry.Title, show.Entries[0].Title);
    }

    [Fact]
    public void MissingFileIsAttachedToEveryAffectedProgram()
    {
        var (state, show, song, first) = CreatePlan();
        var second = SetlistOperations.AddSong(show, song);
        File.Delete(song.FilePath);

        var report = PreflightOperations.Check(state, show.Id, 4);

        Assert.Equal(1, report.CheckedFiles);
        Assert.Equal([first.Id, second.Id], report.Issues.Where(x => x.Code == "file_missing").Select(x => x.EntryId!.Value));
        Assert.All(report.Issues, issue => Assert.Equal(PreflightSeverity.Error, issue.Severity));
    }

    [Fact]
    public void ChangedSamePathIsDetectedWithoutUpdatingTheImportFingerprint()
    {
        var (state, show, song, entry) = CreatePlan();
        var originalHash = song.Sha256;
        var replacement = new byte[] { 90, 91, 92, 93, 94 };
        File.WriteAllBytes(song.FilePath, replacement);

        var report = PreflightOperations.Check(state, show.Id, 4);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("file_changed", issue.Code);
        Assert.Equal(entry.Id, issue.EntryId);
        Assert.Equal(originalHash, song.Sha256);
        Assert.Equal(replacement, File.ReadAllBytes(song.FilePath));
    }

    [Fact]
    public void SharedPathIsReadOnceButComparedAgainstEachReferencedVersion()
    {
        var (state, show, song, first) = CreatePlan();
        var second = SetlistOperations.AddSong(show, song);
        var differentVersion = new SongEntry
        {
            Title = "Different recorded version", FilePath = song.FilePath, Sha256 = new string('F', 64),
            PerformerCount = 2, DurationSeconds = 120,
        };
        state.Songs.Add(differentVersion);
        var third = SetlistOperations.AddSong(show, differentVersion);

        var report = PreflightOperations.Check(state, show.Id, 4);

        Assert.Equal(1, report.CheckedFiles);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(third.Id, issue.EntryId);
        Assert.Equal("file_changed", issue.Code);
        Assert.DoesNotContain(report.Issues, item => item.EntryId == first.Id || item.EntryId == second.Id);
    }

    [Fact]
    public void LockedFileIsReportedAsUnreadableInsteadOfMissing()
    {
        var (state, show, song, entry) = CreatePlan();
        using var locked = new FileStream(song.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = PreflightOperations.Check(state, show.Id, 4);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("file_unreadable", issue.Code);
        Assert.Equal(entry.Id, issue.EntryId);
        Assert.Equal(1, report.CheckedFiles);
    }

    [Fact]
    public void OversizedFileIsNotHashed()
    {
        var (state, show, song, _) = CreatePlan();
        using (var stream = new FileStream(song.FilePath, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(64L * 1024 * 1024 + 1);

        var report = PreflightOperations.Check(state, show.Id, 4);

        Assert.Equal("file_too_large", Assert.Single(report.Issues).Code);
        Assert.Equal(1, report.CheckedFiles);
    }

    [Fact]
    public void MissingSongReferenceProducesAnEntryIssueWithoutInspectingFiles()
    {
        var (state, show, _, entry) = CreatePlan();
        state.Songs.Clear();

        var report = PreflightOperations.Check(state, show.Id, 4);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("song_missing", issue.Code);
        Assert.Equal(entry.Id, issue.EntryId);
        Assert.Equal(0, report.CheckedFiles);
    }

    [Fact]
    public void CompletedAndSkippedSongsAreExcludedButActiveAndQueuedSongsAreChecked()
    {
        var (state, show, _, queued) = CreatePlan();
        var activeSong = CreateSong("active.mid");
        state.Songs.Add(activeSong);
        var active = SetlistOperations.AddSong(show, activeSong);
        SetlistOperations.Start(show, active.Id, DateTimeOffset.UtcNow);
        var unavailableSong = CreateSong("unavailable-history.mid");
        state.Songs.Add(unavailableSong);
        var completed = SetlistOperations.AddSong(show, unavailableSong);
        completed.Status = EntryStatus.Completed;
        completed.StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        completed.EndedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var skipped = SetlistOperations.AddSong(show, unavailableSong);
        SetlistOperations.Skip(show, skipped.Id, DateTimeOffset.UtcNow);
        File.Delete(unavailableSong.FilePath);

        var report = PreflightOperations.Check(state, show.Id, 4);

        Assert.Equal(2, report.CheckedFiles);
        Assert.Empty(report.Issues);
        Assert.Equal(EntryStatus.Queued, queued.Status);
        Assert.Equal(EntryStatus.InProgress, active.Status);
    }

    [Fact]
    public void CancellationDoesNotReturnPartialSuccessOrMutateState()
    {
        var (state, show, _, _) = CreatePlan();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = Snapshot(state);

        Assert.Throws<OperationCanceledException>(() => PreflightOperations.Check(state, show.Id, 4, cancellation.Token));

        Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("speed")]
    [InlineData("duration")]
    [InlineData("kind")]
    [InlineData("source-path")]
    [InlineData("source-hash")]
    [InlineData("performer-count")]
    [InlineData("status")]
    [InlineData("membership")]
    [InlineData("locked-next")]
    [InlineData("stored-order")]
    public void RelevantPlanChangesInvalidateTheFingerprint(string change)
    {
        var (state, show, song, entry) = CreatePlan();
        var second = SetlistOperations.AddSong(show, song);
        var original = PreflightOperations.Fingerprint(state, show.Id, 4);
        switch (change)
        {
            case "title": entry.Title = "New display title"; break;
            case "speed": entry.PlaybackSpeed = 1.5; break;
            case "duration": entry.DurationSeconds += 1; break;
            case "kind": entry.Kind = EntryKind.Talk; entry.SongId = null; break;
            case "source-path": song.FilePath = Path.Combine(directory, "new-path.mid"); break;
            case "source-hash": song.Sha256 = new string('E', 64); break;
            case "performer-count": song.PerformerCount = 6; break;
            case "status": SetlistOperations.Start(show, entry.Id, DateTimeOffset.UtcNow); break;
            case "membership": SetlistOperations.Remove(show, entry.Id); break;
            case "locked-next": show.LockedNextEntryId = second.Id; break;
            case "stored-order": SetlistOperations.Move(show, second.Id, 0); break;
        }

        Assert.NotEqual(original, PreflightOperations.Fingerprint(state, show.Id, 4));
    }

    [Fact]
    public void UnrelatedShowsSongsAndRuntimeClockDoNotInvalidateTheFingerprint()
    {
        var (state, show, _, entry) = CreatePlan();
        var unrelatedSong = CreateSong("other.mid");
        state.Songs.Add(unrelatedSong);
        var otherShow = new ShowSetlist { Name = "Other show" };
        state.Setlists.Add(otherShow);
        var original = PreflightOperations.Fingerprint(state, show.Id, 4);
        otherShow.Name = "Renamed unrelated show";
        SetlistOperations.AddSong(otherShow, unrelatedSong);
        unrelatedSong.Sha256 = new string('C', 64);
        state.SelectedSetlistId = otherShow.Id;
        entry.Notes = "An operator note";

        Assert.Equal(original, PreflightOperations.Fingerprint(state, show.Id, 4));
        Assert.Equal(original, PreflightOperations.Check(state, show.Id, 4).Fingerprint);
        Assert.NotEqual(original, PreflightOperations.Fingerprint(state, show.Id, 5));
    }

    [Fact]
    public void ZeroAttendanceMeansUnknownInsteadOfInsufficient()
    {
        var (state, show, song, _) = CreatePlan();
        song.PerformerCount = 8;

        var report = PreflightOperations.Check(state, show.Id, 0);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("attendance_unset", issue.Code);
        Assert.Equal(PreflightSeverity.Warning, issue.Severity);
        Assert.Null(issue.EntryId);
        Assert.DoesNotContain(report.Issues, item => item.Code == "insufficient_performers");
    }

    [Fact]
    public void UnknownRequiredCountAndInsufficientAttendanceAreSeparateIssues()
    {
        var (state, show, song, entry) = CreatePlan();
        song.PerformerCount = 0;
        var unknown = PreflightOperations.Check(state, show.Id, 2);
        Assert.Equal("performer_count_unset", Assert.Single(unknown.Issues).Code);
        song.PerformerCount = 6;

        var insufficient = PreflightOperations.Check(state, show.Id, 2);

        var issue = Assert.Single(insufficient.Issues);
        Assert.Equal("insufficient_performers", issue.Code);
        Assert.Equal(PreflightSeverity.Error, issue.Severity);
        Assert.Equal(entry.Id, issue.EntryId);
    }

    [Fact]
    public void EmptyRunnablePlanHasAWarningAndNoFileChecks()
    {
        var (state, show, _, entry) = CreatePlan();
        SetlistOperations.Skip(show, entry.Id, DateTimeOffset.UtcNow);

        var report = PreflightOperations.Check(state, show.Id, 4);

        Assert.Equal("empty_plan", Assert.Single(report.Issues).Code);
        Assert.Equal(0, report.CheckedFiles);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void InvalidAttendanceIsRejectedBeforeCheckingFiles(int count)
    {
        var (state, show, _, _) = CreatePlan();
        Assert.Throws<ArgumentOutOfRangeException>(() => PreflightOperations.Fingerprint(state, show.Id, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreflightOperations.Check(state, show.Id, count));
    }

    private (CatalogState State, ShowSetlist Show, SongEntry Song, SetlistEntry Entry) CreatePlan()
    {
        var song = CreateSong("song.mid");
        var show = new ShowSetlist { Name = "Evening show" };
        var state = new CatalogState { Songs = [song], Setlists = [show], SelectedSetlistId = show.Id };
        var entry = SetlistOperations.AddSong(show, song);
        return (state, show, song, entry);
    }

    private SongEntry CreateSong(string name)
    {
        var path = Path.Combine(directory, name);
        var bytes = new byte[] { 0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xe0, 0x4d, 0x54, 0x72, 0x6b, 0, 0, 0, 4, 0, 0xff, 0x2f, 0 };
        File.WriteAllBytes(path, bytes);
        return new SongEntry
        {
            Title = Path.GetFileNameWithoutExtension(name), FilePath = path,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), PerformerCount = 2, DurationSeconds = 120,
        };
    }

    private static string Snapshot(CatalogState state) => JsonSerializer.Serialize(state);

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
