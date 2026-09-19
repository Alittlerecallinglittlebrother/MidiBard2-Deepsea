using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class CoreBehaviorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-tests", Guid.NewGuid().ToString("N"));

    public CoreBehaviorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ImportUsesTempoMapAndCountsOnlyTracksWithNotes()
    {
        var midiPath = CreateMidi("tempo.mid");
        var state = new CatalogState();
        var report = new MidiLibraryImporter().Import([midiPath], state);

        Assert.Equal(1, report.Added);
        Assert.Empty(report.Errors);
        var song = Assert.Single(state.Songs);
        Assert.Equal(1.5, song.DurationSeconds, precision: 5);
        Assert.Equal(1, song.TrackCount);
        Assert.Equal(0, song.PerformerCount);
        Assert.Equal(Path.GetFullPath(midiPath), song.FilePath);
        Assert.Equal(64, song.Sha256.Length);
    }

    [Fact]
    public void RecursiveImportKeepsSameFilenameDifferentContentsAndDeduplicatesCopies()
    {
        var original = CreateMidi("a/song.mid");
        var variant = CreateMidi("b/song.mid", secondTempo: 750000);
        var copy = Path.Combine(directory, "b", "copy.midi");
        File.Copy(original, copy);
        File.WriteAllText(Path.Combine(directory, "b", "bad.mid"), "not midi");
        File.WriteAllText(Path.Combine(directory, "b", "notes.txt"), "readme");
        var state = new CatalogState();

        var report = new MidiLibraryImporter().Import([directory, variant], state);

        Assert.Equal(2, report.Added);
        Assert.Equal(2, report.Duplicates);
        Assert.Single(report.Errors);
        Assert.Equal(2, state.Songs.Count);
        Assert.All(state.Songs, x => Assert.Equal("song", x.Title));
        Assert.NotEqual(state.Songs[0].Sha256, state.Songs[1].Sha256);
    }

    [Fact]
    public void MovedIdenticalFilePreservesSongIdentityAndMetadata()
    {
        var original = CreateMidi("original.mid");
        var state = new CatalogState();
        var importer = new MidiLibraryImporter();
        importer.Import([original], state);
        var song = state.Songs[0];
        song.Title = "Custom title";
        song.Aliases.Add("Alias");
        var moved = Path.Combine(directory, "moved.mid");
        File.Move(original, moved);

        var report = importer.Import([moved], state);

        Assert.Equal(1, report.Relocated);
        Assert.Same(song, Assert.Single(state.Songs));
        Assert.Equal("Custom title", song.Title);
        Assert.Equal("Alias", Assert.Single(song.Aliases));
        Assert.Equal(moved, song.FilePath);
    }

    [Fact]
    public void ChangedSourcePathIsReportedWithoutReplacingMetadata()
    {
        var original = CreateMidi("original.mid");
        var state = new CatalogState();
        var importer = new MidiLibraryImporter();
        importer.Import([original], state);
        var originalHash = state.Songs[0].Sha256;
        CreateMidi("original.mid", secondTempo: 750000);

        var report = importer.Import([original], state);

        Assert.Equal(0, report.Added);
        Assert.Contains("内容已改变", Assert.Single(report.Errors));
        Assert.Equal(originalHash, Assert.Single(state.Songs).Sha256);
    }

    [Fact]
    public void ImportNeverWritesSourceMidi()
    {
        var path = CreateMidi("source.mid");
        var originalBytes = File.ReadAllBytes(path);
        var originalTime = File.GetLastWriteTimeUtc(path);

        new MidiLibraryImporter().Import([path], new CatalogState());

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void CancellationDoesNotCommitTheCallersPublishedState()
    {
        var path = CreateMidi("source.mid");
        var privateSnapshot = new CatalogState();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new MidiLibraryImporter().Import([path], privateSnapshot, cancellation.Token));
        Assert.Empty(privateSnapshot.Songs);
    }

    [Fact]
    public void StorePersistsMetadataSetlistStateAndCreatesPreviousVersionBackup()
    {
        var state = CreateCatalog();
        var setlist = state.Setlists[0];
        var first = SetlistOperations.AddSong(setlist, state.Songs[0]);
        var second = SetlistOperations.AddSegment(setlist, EntryKind.Break, "Rest", 120);
        setlist.LockedNextEntryId = second.Id;
        var now = DateTimeOffset.Parse("2026-09-18T12:00:00Z");
        SetlistOperations.Start(setlist, first.Id, now);
        var store = new CatalogStore(Path.Combine(directory, "storage"));
        store.Save(state);
        SetlistOperations.Finish(setlist, first.Id, now.AddSeconds(90));
        store.Save(state);

        var loaded = new CatalogStore(Path.Combine(directory, "storage")).Load();

        Assert.Equal(state.SelectedSetlistId, loaded.SelectedSetlistId);
        Assert.Equal(state.Songs[0].Aliases, loaded.Songs[0].Aliases);
        Assert.Equal(EntryStatus.Completed, loaded.Setlists[0].Entries[0].Status);
        Assert.Equal(now, loaded.Setlists[0].Entries[0].StartedAtUtc);
        Assert.Equal(now.AddSeconds(90), loaded.Setlists[0].Entries[0].EndedAtUtc);
        Assert.Equal(second.Id, loaded.Setlists[0].LockedNextEntryId);
        Assert.True(File.Exists(store.FilePath + ".bak"));
        Assert.Contains("inProgress", File.ReadAllText(store.FilePath + ".bak"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReorderKeepsActiveEntryAndLockedNextIdentity()
    {
        var state = CreateCatalog();
        var setlist = state.Setlists[0];
        var first = SetlistOperations.AddSong(setlist, state.Songs[0]);
        var second = SetlistOperations.AddSong(setlist, state.Songs[0]);
        var third = SetlistOperations.AddSegment(setlist, EntryKind.Talk, "Talk", 30);
        SetlistOperations.Start(setlist, first.Id, DateTimeOffset.UtcNow);
        setlist.LockedNextEntryId = third.Id;

        SetlistOperations.Move(setlist, first.Id, 2);
        SetlistOperations.Move(setlist, third.Id, 0);

        Assert.Same(first, setlist.Entries.Single(x => x.Status == EntryStatus.InProgress));
        Assert.Equal(third.Id, setlist.LockedNextEntryId);
        Assert.Throws<InvalidOperationException>(() => SetlistOperations.Start(setlist, second.Id, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => SetlistOperations.Remove(setlist, first.Id));
    }

    [Fact]
    public void InterruptedEntryCanBeResetAndReplayedWithoutCompletingTheInterruptedRun()
    {
        var state = CreateCatalog();
        var setlist = state.Setlists[0];
        var entry = SetlistOperations.AddSong(setlist, state.Songs[0]);
        var now = DateTimeOffset.UtcNow;
        setlist.LockedNextEntryId = entry.Id;
        SetlistOperations.Start(setlist, entry.Id, now);
        Assert.Null(setlist.LockedNextEntryId);
        SetlistOperations.Skip(setlist, entry.Id, now.AddSeconds(10));
        Assert.Equal(EntryStatus.Skipped, entry.Status);
        Assert.Equal(now.AddSeconds(10), entry.EndedAtUtc);
        SetlistOperations.ResetEntry(setlist, entry.Id);
        Assert.Null(entry.StartedAtUtc);
        Assert.Null(entry.EndedAtUtc);
        SetlistOperations.Start(setlist, entry.Id, now.AddSeconds(20));
        SetlistOperations.Finish(setlist, entry.Id, now.AddSeconds(80));
        Assert.Equal(EntryStatus.Completed, entry.Status);
        Assert.Equal(now.AddSeconds(20), entry.StartedAtUtc);
    }

    [Fact]
    public void TimelineIncludesPlaybackSpeedAndOnlyAdjacentSongGaps()
    {
        var setlist = new ShowSetlist { Name = "Live", GapSeconds = 3 };
        var song = new SongEntry { Title = "Song", DurationSeconds = 120 };
        var first = SetlistOperations.AddSong(setlist, song);
        first.PlaybackSpeed = 2;
        var talk = SetlistOperations.AddSegment(setlist, EntryKind.Talk, "Talk", 20);
        SetlistOperations.AddSong(setlist, song);
        SetlistOperations.AddSong(setlist, song);
        Assert.Equal(323, SetlistOperations.TotalSeconds(setlist));

        var now = DateTimeOffset.UtcNow;
        SetlistOperations.Start(setlist, first.Id, now);
        Assert.Equal(313, SetlistOperations.RemainingSeconds(setlist, now.AddSeconds(10)));
        SetlistOperations.Skip(setlist, talk.Id, now);
        Assert.Equal(296, SetlistOperations.RemainingSeconds(setlist, now.AddSeconds(10)));
        Assert.Equal(246, SetlistOperations.RemainingSeconds(setlist, now.AddSeconds(1000)));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"schemaVersion\":999,\"songs\":[],\"setlists\":[]}")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1,\"songs\":null,\"setlists\":[]}")]
    public void CorruptOrUnsupportedCatalogRemainsUntouchedOnLoadAndSave(string damaged)
    {
        var store = new CatalogStore(directory);
        File.WriteAllText(store.FilePath, damaged, Encoding.UTF8);
        var bytes = File.ReadAllBytes(store.FilePath);

        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(new CatalogState()));

        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".bak"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void SavingInvalidMutationDoesNotChangeThePersistedCatalog()
    {
        var state = CreateCatalog();
        SetlistOperations.AddSong(state.Setlists[0], state.Songs[0]);
        var store = new CatalogStore(Path.Combine(directory, "storage"));
        store.Save(state);
        var bytes = File.ReadAllBytes(store.FilePath);
        state.Songs.Clear();

        var exception = Assert.Throws<InvalidDataException>(() => store.Save(state));

        Assert.Contains("引用的曲目不存在", exception.Message);
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        Assert.Single(store.Load().Songs);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void InvalidPerformerCountsAreRejected(int count)
    {
        var state = CreateCatalog();
        state.Songs[0].PerformerCount = count;
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
    }

    private CatalogState CreateCatalog()
    {
        var state = new CatalogState();
        new MidiLibraryImporter().Import([CreateMidi("catalog-song.mid")], state);
        state.Songs[0].Aliases = ["Alias", "Another name"];
        state.Songs[0].Arranger = "Test arranger";
        state.Songs[0].PerformerCount = 6;
        var setlist = new ShowSetlist { Name = "Evening show" };
        state.Setlists.Add(setlist);
        state.SelectedSetlistId = setlist.Id;
        return state;
    }

    private string CreateMidi(string relativePath, long secondTempo = 1000000)
    {
        var file = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tempoTrack = new TrackChunk(
            new SetTempoEvent(500000),
            new SetTempoEvent(secondTempo) { DeltaTime = 480 });
        var noteTrack = new TrackChunk(
            new SequenceTrackNameEvent("Notes"),
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90),
            new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 960 });
        var metadataTrack = new TrackChunk(new SequenceTrackNameEvent("Metadata"));
        var midi = new MidiFile(tempoTrack, noteTrack, metadataTrack) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        midi.Write(file, overwriteFile: true);
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
