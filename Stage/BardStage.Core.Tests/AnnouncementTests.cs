using System.Text.Json;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class AnnouncementTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-announcements-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AnnouncementKind.Opening)]
    [InlineData(AnnouncementKind.NextSong)]
    [InlineData(AnnouncementKind.Thanks)]
    [InlineData(AnnouncementKind.Intermission)]
    [InlineData(AnnouncementKind.Closing)]
    public void DefaultTemplatesProduceUsableDrafts(AnnouncementKind kind)
    {
        var text = AnnouncementTemplates.Render(AnnouncementTemplates.Defaults(), kind, Context());
        Assert.NotEmpty(text); Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void ContextUsesExactProgramIdentityAndDeduplicatesAudience()
    {
        var song = new SongEntry { Title = "同名曲目", Arranger = "六人版", PerformerCount = 6, DurationSeconds = 120 };
        var show = new ShowSetlist { Name = "晚场" };
        var first = SetlistOperations.AddSong(show, song);
        first.Title = "节目单标题快照";
        var second = SetlistOperations.AddSong(show, song);
        var state = new CatalogState { Songs = [song], Setlists = [show] };
        state.Requests.AddRange([
            Request(show, first, "A", "World"), Request(show, first, "A", "World"),
            Request(show, first, "A", "Other"), Request(show, second, "Wrong", "World")]);
        var context = AnnouncementTemplates.CreateContext(state, show.Id, first.Id);
        Assert.Equal("节目单标题快照", context.Title);
        Assert.Equal("六人版", context.Arranger); Assert.Equal("6 人", context.Performers);
        Assert.Equal("A@World、A@Other", context.Requesters);
        Assert.Throws<InvalidOperationException>(() => AnnouncementTemplates.CreateContext(state, Guid.NewGuid(), first.Id));
        Assert.Throws<InvalidOperationException>(() => AnnouncementTemplates.CreateContext(state, show.Id, Guid.NewGuid()));
    }

    [Fact]
    public void ContextDeduplicatesWidthCaseAndWhitespaceIdentityVariantsButKeepsDifferentWorlds()
    {
        var song = new SongEntry { Title = "Song", DurationSeconds = 120 };
        var show = new ShowSetlist { Name = "Show" };
        var entry = SetlistOperations.AddSong(show, song);
        var state = new CatalogState { Songs = [song], Setlists = [show] };
        state.Requests.AddRange([
            Request(show, entry, "Ａｌｉｃｅ　Ｓｍｉｔｈ", "Ｍｏｏｎ　Ｗｏｒｌｄ"),
            Request(show, entry, "alice smith", "moon world"),
            Request(show, entry, " Alice   Smith ", " Moon  World "),
            Request(show, entry, "Alice Smith", "Other World"),
            Request(show, entry, "ＡＬＩＣＥ　ＳＭＩＴＨ", "ＯＴＨＥＲ　ＷＯＲＬＤ"),
            Request(show, entry, "Alice Smith", ""),
            Request(show, entry, "alice  smith", ""),
        ]);

        var context = AnnouncementTemplates.CreateContext(state, show.Id, entry.Id);

        Assert.Equal("Ａｌｉｃｅ　Ｓｍｉｔｈ@Ｍｏｏｎ　Ｗｏｒｌｄ、Alice Smith@Other World、Alice Smith", context.Requesters);
        Assert.Equal(7, state.Requests.Count);
        Assert.Equal(" Alice   Smith ", state.Requests[2].RequesterName);
        Assert.Equal(" Moon  World ", state.Requests[2].RequesterWorld);
    }

    [Fact]
    public void InsertingPlaceholderLikeValuesNeverExpandsThemAgain()
    {
        var settings = AnnouncementTemplates.Defaults(); settings.Templates[AnnouncementKind.NextSong] = "{title} / {requesters}";
        var context = Context(); context.Title = "{requesters}";
        Assert.Equal("{requesters} / Guest", AnnouncementTemplates.Render(settings, AnnouncementKind.NextSong, context));
    }

    [Theory]
    [InlineData("{unknown}")]
    [InlineData("{title")]
    [InlineData("title}")]
    [InlineData("{{title}}")]
    [InlineData("{invalid_key}")]
    [InlineData("bad\0text")]
    [InlineData("")]
    public void InvalidTemplatesAreRejectedBeforeSaving(string template)
    {
        var settings = AnnouncementTemplates.Defaults(); settings.Templates[AnnouncementKind.Closing] = template;
        Assert.Throws<InvalidDataException>(() => new AnnouncementStore(directory).Save(settings));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void FingerprintSeparatesIdenticalTitlesAndTracksMetadata()
    {
        var settings = AnnouncementTemplates.Defaults(); var context = Context();
        var original = AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context);
        context.EntryId = Guid.NewGuid();
        Assert.NotEqual(original, AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context));
        var entryFingerprint = AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context);
        context.ShowId = Guid.NewGuid();
        Assert.NotEqual(entryFingerprint, AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context));
        var showFingerprint = AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context);
        context.Requesters = "New audience";
        Assert.NotEqual(showFingerprint, AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context));
        var peopleFingerprint = AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context);
        settings.Templates[AnnouncementKind.NextSong] = "New {title}";
        Assert.NotEqual(peopleFingerprint, AnnouncementTemplates.Fingerprint(settings, AnnouncementKind.NextSong, context));
    }

    [Fact]
    public void StaleEditedDraftCannotBeCopiedAndIsArchivedWithItsOriginalLabel()
    {
        var settings = AnnouncementTemplates.Defaults(); var first = Context(); var second = Context();
        var draft = new AnnouncementDraft(); draft.Refresh(settings, AnnouncementKind.NextSong, first, "first program");
        draft.Text = "主持人手动改稿";
        Assert.Equal(draft.Text, draft.TextForCopy(settings, AnnouncementKind.NextSong, first));
        Assert.Throws<InvalidOperationException>(() => draft.TextForCopy(settings, AnnouncementKind.NextSong, second));
        Assert.True(draft.Refresh(settings, AnnouncementKind.NextSong, second, "second program"));
        Assert.False(draft.Edited);
        Assert.Equal("主持人手动改稿", Assert.Single(draft.History).Text);
        Assert.Equal("first program", draft.History[0].Label);
        Assert.False(draft.Refresh(settings, AnnouncementKind.NextSong, second, "second program"));
        Assert.Single(draft.History);
    }

    [Fact]
    public void MissingProgramInvalidatesDraftWithoutLosingManualText()
    {
        var settings = AnnouncementTemplates.Defaults(); var context = Context(); var draft = new AnnouncementDraft();
        draft.Refresh(settings, AnnouncementKind.Thanks, context, "谢词原节目"); draft.Text = "谢谢大家";
        draft.Invalidate(); draft.Invalidate();
        Assert.Empty(draft.Text); Assert.Single(draft.History);
        Assert.Throws<InvalidOperationException>(() => draft.TextForCopy(settings, AnnouncementKind.Thanks, context));
    }

    [Fact]
    public void TemplateChangesRefreshDraftAndRegenerationRetainsEdits()
    {
        var settings = AnnouncementTemplates.Defaults(); var context = Context(); var draft = new AnnouncementDraft();
        draft.Refresh(settings, AnnouncementKind.NextSong, context, "原节目"); draft.Text = "old edit";
        settings.Templates[AnnouncementKind.NextSong] = "新版：{title}";
        draft.Refresh(settings, AnnouncementKind.NextSong, context, "原节目");
        Assert.StartsWith("新版：", draft.Text); Assert.Single(draft.History);
        draft.Text = "another edit"; draft.Regenerate();
        Assert.Equal(2, draft.History.Count); Assert.Equal(draft.GeneratedText, draft.Text);
    }

    [Fact]
    public void ReadingDefaultsIsReadOnlyAndSavingKeepsPreviousTemplateBackup()
    {
        var store = new AnnouncementStore(directory); var settings = store.Load();
        Assert.False(Directory.Exists(directory));
        store.Save(settings); var original = File.ReadAllBytes(store.FilePath);
        settings.Templates[AnnouncementKind.Opening] = "欢迎来到 {show}"; store.Save(settings);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath + ".bak"));
        Assert.Equal(settings.Templates[AnnouncementKind.Opening], store.Load().Templates[AnnouncementKind.Opening]);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":99,\"templates\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"templates\":null}")]
    public void CorruptTemplateFileIsNeverOverwritten(string broken)
    {
        Directory.CreateDirectory(directory); var store = new AnnouncementStore(directory);
        File.WriteAllText(store.FilePath, broken);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(AnnouncementTemplates.Defaults()));
        Assert.Equal(broken, File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".bak"));
    }

    [Fact]
    public void RequiredProgramAndOutputBoundsAreEnforced()
    {
        var settings = AnnouncementTemplates.Defaults(); var context = Context(); context.EntryId = null;
        Assert.Throws<InvalidOperationException>(() => AnnouncementTemplates.Render(settings, AnnouncementKind.NextSong, context));
        Assert.NotEmpty(AnnouncementTemplates.Render(settings, AnnouncementKind.Opening, context));
        context.EntryId = Guid.NewGuid(); context.Requesters = new string('a', 8192);
        Assert.Throws<InvalidOperationException>(() => AnnouncementTemplates.Render(settings, AnnouncementKind.Thanks, context));
    }

    private static AnnouncementContext Context() => new()
    {
        ShowId = Guid.NewGuid(), EntryId = Guid.NewGuid(), ShowName = "晚场", Title = "月下华尔兹",
        Arranger = "六人版", Performers = "6 人", Requesters = "Guest",
    };
    private static SongRequest Request(ShowSetlist show, SetlistEntry entry, string name, string world) => new()
    {
        SetlistId = show.Id, SetlistEntryId = entry.Id, SongId = entry.SongId,
        Status = RequestStatus.Arranged, RequesterName = name, RequesterWorld = world,
    };
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
