using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class CatalogMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-migration-tests", Guid.NewGuid().ToString("N"));

    public CatalogMigrationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ReadingVersionOneMigratesInMemoryWithoutWritingOrCreatingBackups()
    {
        var (source, legacy) = CreateLegacy();
        var store = new CatalogStore(directory);
        File.WriteAllText(store.FilePath, legacy);
        var bytes = File.ReadAllBytes(store.FilePath);
        var modified = File.GetLastWriteTimeUtc(store.FilePath);

        var migrated = store.Load();

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Empty(migrated.Requests);
        Assert.False(migrated.RequestSettings.IsOpen);
        Assert.Equal(source.SelectedSetlistId, migrated.SelectedSetlistId);
        Assert.Equal(source.Songs[0].Id, migrated.Songs[0].Id);
        Assert.Equal(source.Songs[0].FilePath, migrated.Songs[0].FilePath);
        Assert.Equal(source.Songs[0].Aliases, migrated.Songs[0].Aliases);
        Assert.Equal(source.Setlists[0].Entries[0].StartedAtUtc, migrated.Setlists[0].Entries[0].StartedAtUtc);
        Assert.Equal(EntryStatus.Completed, migrated.Setlists[0].Entries[0].Status);
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(store.FilePath));
        Assert.Equal(["catalog.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public void FirstVersionThreeSavePreservesPermanentLegacyBytesAcrossLaterSaves()
    {
        var (_, legacy) = CreateLegacy();
        var store = new CatalogStore(directory);
        File.WriteAllText(store.FilePath, legacy);
        var original = File.ReadAllBytes(store.FilePath);
        var migrated = store.Load();

        store.Save(migrated);

        var permanent = Path.Combine(directory, "catalog.v1.bak");
        Assert.Equal(original, File.ReadAllBytes(permanent));
        Assert.Equal(original, File.ReadAllBytes(store.FilePath + ".bak"));
        using (var document = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
            Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        migrated.Songs[0].Notes = "Updated in v0.2";
        store.Save(migrated);

        Assert.Equal(original, File.ReadAllBytes(permanent));
        Assert.NotEqual(original, File.ReadAllBytes(store.FilePath + ".bak"));
        Assert.Equal("Updated in v0.2", store.Load().Songs[0].Notes);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void LegacyExportWithNumericEnumsCanBeImportedAndValidated()
    {
        var (source, legacy) = CreateLegacy();

        var migrated = CatalogStore.Deserialize(legacy);

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Equal(source.Setlists[0].Entries[0].Kind, migrated.Setlists[0].Entries[0].Kind);
        Assert.Equal(source.Setlists[0].Entries[0].Status, migrated.Setlists[0].Entries[0].Status);
        CatalogStore.Validate(migrated);
    }

    [Fact]
    public void NewVersionThreeCatalogHasNoInventedLegacyBackup()
    {
        var state = RequestOperationsTests.CreateState();
        var store = new CatalogStore(directory);
        store.Save(state);
        Assert.Equal(3, store.Load().SchemaVersion);
        Assert.False(File.Exists(Path.Combine(directory, "catalog.v1.bak")));
    }

    [Fact]
    public void InvalidPermanentBackupBlocksMigrationWithoutChangingEitherSource()
    {
        var (_, legacy) = CreateLegacy();
        var store = new CatalogStore(directory);
        File.WriteAllText(store.FilePath, legacy);
        var permanent = Path.Combine(directory, "catalog.v1.bak");
        File.WriteAllText(permanent, "not a valid backup");
        var state = store.Load();

        Assert.Throws<InvalidDataException>(() => store.Save(state));

        Assert.Equal(legacy, File.ReadAllText(store.FilePath));
        Assert.Equal("not a valid backup", File.ReadAllText(permanent));
        Assert.False(File.Exists(store.FilePath + ".bak"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"songs\":null,\"setlists\":[]}")]
    [InlineData("{\"schemaVersion\":3,\"songs\":[],\"setlists\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    public void InvalidOrAmbiguousVersionDoesNotTriggerBackupOrWrite(string json)
    {
        var store = new CatalogStore(directory);
        File.WriteAllText(store.FilePath, json);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(new CatalogState()));
        Assert.Equal(json, File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(Path.Combine(directory, "catalog.v1.bak")));
    }

    [Fact]
    public void ArrangedRequestsPersistWithProgramIdentityAndSettings()
    {
        var state = RequestOperationsTests.CreateState();
        var show = state.Setlists[0];
        state.RequestSettings.TargetSetlistId = show.Id;
        state.RequestSettings.IsOpen = true;
        var request = RequestOperations.Submit(state, show.Id, "Alice", "World", "Song", RequestChannel.Say, DateTimeOffset.UtcNow);
        var entry = RequestOperations.Arrange(state, [request.Id], state.Songs[0].Id);
        var store = new CatalogStore(directory);

        store.Save(state);
        var loaded = new CatalogStore(directory).Load();

        var restored = Assert.Single(loaded.Requests);
        Assert.Equal(request.Id, restored.Id);
        Assert.Equal(show.Id, restored.SetlistId);
        Assert.Equal(entry.Id, restored.SetlistEntryId);
        Assert.Equal(state.Songs[0].Id, restored.SongId);
        Assert.Equal(RequestStatus.Arranged, restored.Status);
        Assert.True(loaded.RequestSettings.IsOpen);
        Assert.Equal(show.Id, loaded.RequestSettings.TargetSetlistId);
        Assert.Equal("已安排", RequestOperations.DisplayStatus(loaded, restored));
        Assert.Throws<InvalidOperationException>(() => RequestOperations.Arrange(loaded, [request.Id], state.Songs[0].Id));
        Assert.Single(loaded.Setlists[0].Entries);
    }

    private static (CatalogState State, string Json) CreateLegacy()
    {
        var state = RequestOperationsTests.CreateState();
        state.SchemaVersion = 1;
        state.Songs[0].Aliases = ["Alias", "Another name"];
        var entry = SetlistOperations.AddSong(state.Setlists[0], state.Songs[0]);
        var now = DateTimeOffset.Parse("2026-09-18T09:00:00Z");
        SetlistOperations.Start(state.Setlists[0], entry.Id, now);
        SetlistOperations.Finish(state.Setlists[0], entry.Id, now.AddMinutes(2));
        var node = JsonSerializer.SerializeToNode(state)!.AsObject();
        node.Remove(nameof(CatalogState.Requests));
        node.Remove(nameof(CatalogState.RequestSettings));
        return (state, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
