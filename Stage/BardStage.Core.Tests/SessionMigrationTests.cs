using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class SessionMigrationTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-18T10:00:00Z");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-session-migration", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SchemaTwoMigrationPreservesProgramRequestsAndPermanentOriginalBytes()
    {
        var state = RequestOperationsTests.CreateState();
        var show = state.Setlists[0];
        var request = RequestOperations.Submit(state, show.Id, "Guest", "World", "Song", RequestChannel.Manual, Now);
        var entry = RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        SetlistOperations.Start(show, entry.Id, Now);
        SetlistOperations.Finish(show, entry.Id, Now.AddSeconds(40));
        var store = WriteLegacy(state);
        var originalBytes = File.ReadAllBytes(store.FilePath);

        var migrated = store.Load();

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Equal(originalBytes, File.ReadAllBytes(store.FilePath));
        Assert.False(File.Exists(Path.Combine(directory, "catalog.v2.bak")));
        var session = Assert.Single(migrated.Sessions);
        Assert.True(session.ImportedLegacy);
        Assert.Equal(show.Id, session.SetlistId);
        Assert.Equal(RecordSource.Legacy, Assert.Single(session.Attempts).Source);
        Assert.Equal(entry.Id, session.Attempts[0].Program.EntryId);
        Assert.Equal(40, SessionOperations.ActualSeconds(session.Attempts[0], Now.AddDays(1)));
        Assert.Equal(request.Id, migrated.Requests[0].Id);
        store.Save(migrated);
        var backup = Path.Combine(directory, "catalog.v2.bak");
        Assert.Equal(originalBytes, File.ReadAllBytes(backup));
        var savedSession = JsonSerializer.Serialize(migrated.Sessions);
        Assert.Equal(savedSession, JsonSerializer.Serialize(store.Load().Sessions));
        migrated.Songs[0].Notes = "Changed after upgrade";
        store.Save(migrated);
        Assert.Equal(originalBytes, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyCompletedAndSkippedWithoutTimestampsRemainUnknownButLoadable(int version)
    {
        var state = RequestOperationsTests.CreateState();
        var show = state.Setlists[0];
        var completed = SetlistOperations.AddSong(show, state.Songs[0]);
        completed.Status = EntryStatus.Completed;
        var skipped = SetlistOperations.AddSong(show, state.Songs[1]);
        skipped.Status = EntryStatus.Skipped;
        var store = WriteLegacy(state, version);

        var migrated = store.Load();

        Assert.Equal(EntryStatus.Completed, migrated.Setlists[0].Entries[0].Status);
        Assert.Equal(EntryStatus.Skipped, migrated.Setlists[0].Entries[1].Status);
        Assert.All(migrated.Setlists[0].Entries, e => { Assert.Null(e.StartedAtUtc); Assert.Null(e.EndedAtUtc); });
        var session = Assert.Single(migrated.Sessions);
        Assert.True(session.ImportedLegacy);
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(2, session.Attempts.Count);
        Assert.All(session.Attempts, a => { Assert.Null(a.StartedAtUtc); Assert.Null(a.EndedAtUtc); Assert.Equal(RecordSource.Legacy, a.Source); });
        store.Save(migrated);
        Assert.Equal(2, store.Load().Sessions[0].Attempts.Count);
        Assert.True(File.Exists(Path.Combine(directory, $"catalog.v{version}.bak")));
    }

    [Fact]
    public void PartialLegacyTimesArePreservedWithoutArchivingBeforeKnownLaterStart()
    {
        var state = RequestOperationsTests.CreateState();
        var show = state.Setlists[0];
        var earlier = SetlistOperations.AddSong(show, state.Songs[0]);
        earlier.Status = EntryStatus.Completed;
        earlier.EndedAtUtc = Now;
        var later = SetlistOperations.AddSong(show, state.Songs[1]);
        later.Status = EntryStatus.Completed;
        later.StartedAtUtc = Now.AddMinutes(1);

        var migrated = WriteLegacy(state).Load();

        var session = Assert.Single(migrated.Sessions);
        Assert.Equal(Now, session.StartedAtUtc);
        Assert.Equal(Now.AddMinutes(1), session.EndedAtUtc);
        Assert.Null(session.Attempts[0].StartedAtUtc);
        Assert.Equal(Now, session.Attempts[0].EndedAtUtc);
        Assert.Equal(Now.AddMinutes(1), session.Attempts[1].StartedAtUtc);
        Assert.Null(session.Attempts[1].EndedAtUtc);
        Assert.Equal(0, SessionOperations.ActualSeconds(session.Attempts[1], Now.AddDays(1)));
        Assert.Equal(0, SessionOperations.ActualSeconds(session.Attempts[1], Now.AddDays(10)));
        Assert.Equal(0, SetlistOperations.ElapsedSeconds(migrated.Setlists[0].Entries[1], Now.AddDays(10)));
    }

    [Fact]
    public void LegacyActiveProgramRemainsOpenAndCanBeCompletedWithTheSameAttempt()
    {
        var state = RequestOperationsTests.CreateState();
        var show = state.Setlists[0];
        var entry = SetlistOperations.AddSong(show, state.Songs[0]);
        SetlistOperations.Start(show, entry.Id, Now);
        var store = WriteLegacy(state);
        var migrated = store.Load();
        var session = Assert.Single(migrated.Sessions);
        Assert.Null(session.EndedAtUtc);
        var attemptId = Assert.Single(session.Attempts).Id;
        var previous = Clone(migrated);
        SetlistOperations.Finish(migrated.Setlists[0], entry.Id, Now.AddMinutes(1));

        SessionOperations.Capture(previous, migrated, Now.AddMinutes(1));
        SessionOperations.Archive(migrated, show.Id, Now.AddMinutes(2));
        store.Save(migrated);

        Assert.Equal(attemptId, Assert.Single(session.Attempts).Id);
        Assert.Equal(AttemptOutcome.Completed, session.Attempts[0].Outcome);
        Assert.Equal(60, SessionOperations.ActualSeconds(session.Attempts[0], Now.AddDays(1)));
    }

    [Fact]
    public void NewSchemaCannotSilentlyAcceptTerminalEntriesWithoutLegacyProvenance()
    {
        var state = RequestOperationsTests.CreateState();
        var entry = SetlistOperations.AddSong(state.Setlists[0], state.Songs[0]);
        entry.Status = EntryStatus.Completed;
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
        entry.Status = EntryStatus.Skipped;
        Assert.Throws<InvalidDataException>(() => CatalogStore.Validate(state));
    }

    [Fact]
    public void LegacyInvalidActiveTimeDoesNotWriteBackupOrTouchTheOriginal()
    {
        var state = RequestOperationsTests.CreateState();
        var entry = SetlistOperations.AddSong(state.Setlists[0], state.Songs[0]);
        entry.Status = EntryStatus.InProgress;
        var store = WriteLegacy(state);
        var bytes = File.ReadAllBytes(store.FilePath);

        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(new CatalogState()));

        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        Assert.False(File.Exists(Path.Combine(directory, "catalog.v2.bak")));
    }

    [Fact]
    public void CorruptPermanentSchemaTwoBackupPreventsAnyMigrationWrite()
    {
        var store = WriteLegacy(RequestOperationsTests.CreateState());
        var source = File.ReadAllBytes(store.FilePath);
        var migrated = store.Load();
        var backup = Path.Combine(directory, "catalog.v2.bak");
        File.WriteAllText(backup, "broken backup");

        Assert.Throws<InvalidDataException>(() => store.Save(migrated));

        Assert.Equal(source, File.ReadAllBytes(store.FilePath));
        Assert.Equal("broken backup", File.ReadAllText(backup));
        Assert.False(File.Exists(store.FilePath + ".bak"));
    }

    [Fact]
    public void DowngradedVersionMarkerCannotSilentlyDiscardExistingSessions()
    {
        var state = RequestOperationsTests.CreateState();
        SessionOperations.Begin(state, state.Setlists[0].Id, Now);
        state.SchemaVersion = 2;

        Assert.Throws<InvalidDataException>(() => CatalogStore.Deserialize(JsonSerializer.Serialize(state)));
    }

    private CatalogStore WriteLegacy(CatalogState state, int version = 2)
    {
        Directory.CreateDirectory(directory);
        var node = JsonSerializer.SerializeToNode(state)!.AsObject();
        node[nameof(CatalogState.SchemaVersion)] = version;
        node.Remove(nameof(CatalogState.Sessions));
        foreach (var show in node[nameof(CatalogState.Setlists)]!.AsArray())
            foreach (var entry in show![nameof(ShowSetlist.Entries)]!.AsArray())
            {
                entry!.AsObject().Remove(nameof(SetlistEntry.PausedAtUtc));
                entry.AsObject().Remove(nameof(SetlistEntry.PausedSeconds));
            }
        var store = new CatalogStore(directory);
        File.WriteAllText(store.FilePath, node.ToJsonString());
        return store;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
