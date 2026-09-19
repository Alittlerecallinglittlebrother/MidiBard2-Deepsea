using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BardStage.Core;

public enum AnnouncementKind { Opening, NextSong, Thanks, Intermission, Closing }

public sealed class AnnouncementSettings
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<AnnouncementKind, string> Templates { get; set; } = new()
    {
        [AnnouncementKind.Opening] = "欢迎来到{show}，感谢大家到场。演出即将开始。",
        [AnnouncementKind.NextSong] = "接下来为大家演奏《{title}》，编曲：{arranger}。",
        [AnnouncementKind.Thanks] = "感谢{requesters}对《{title}》的支持，也感谢大家的聆听。",
        [AnnouncementKind.Intermission] = "演出稍作休息，感谢大家的陪伴。",
        [AnnouncementKind.Closing] = "{show}到这里就结束了，感谢各位观众，我们下次再见。",
    };
}

public sealed class AnnouncementContext
{
    public Guid ShowId { get; set; }
    public Guid? EntryId { get; set; }
    public string ShowName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Arranger { get; set; } = "";
    public string Performers { get; set; } = "";
    public string Requesters { get; set; } = "";
}

public static partial class AnnouncementTemplates
{
    private static readonly HashSet<string> Keys = ["show", "title", "arranger", "performers", "requesters"];
    [GeneratedRegex(@"\{(?<key>[a-z]+)\}|(?<brace>[{}])", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    public static AnnouncementSettings Defaults() => new();

    public static AnnouncementContext CreateContext(CatalogState state, Guid showId, Guid? entryId)
    {
        var show = state.Setlists.FirstOrDefault(s => s.Id == showId) ?? throw new InvalidOperationException("报幕对应的演出已不存在。");
        var context = new AnnouncementContext { ShowId = show.Id, ShowName = show.Name };
        if (entryId is not { } id) return context;
        var entry = show.Entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("报幕对应的节目已不存在。");
        var song = state.Songs.FirstOrDefault(s => s.Id == entry.SongId);
        context.EntryId = entry.Id;
        context.Title = entry.Title;
        context.Arranger = string.IsNullOrWhiteSpace(song?.Arranger) ? "编曲信息未填写" : song.Arranger;
        context.Performers = song?.PerformerCount is > 0 ? song.PerformerCount + " 人" : "人数未填写";
        var people = state.Requests.Where(r => r.SetlistId == show.Id && r.SetlistEntryId == id && r.Status == RequestStatus.Arranged)
            .DistinctBy(r => (RequestOperations.Normalize(r.RequesterName), RequestOperations.Normalize(r.RequesterWorld)))
            .Select(r => r.RequesterName + (r.RequesterWorld.Length == 0 ? "" : "@" + r.RequesterWorld))
            .ToArray();
        context.Requesters = people.Length == 0 ? "现场观众" : string.Join("、", people);
        return context;
    }

    public static void Validate(AnnouncementSettings settings)
    {
        if (settings == null || settings.SchemaVersion != 1) throw new InvalidDataException("不支持的报幕模板版本。");
        if (settings.Templates == null || settings.Templates.Count != Enum.GetValues<AnnouncementKind>().Length
            || settings.Templates.Keys.Any(k => !Enum.IsDefined(k))) throw new InvalidDataException("报幕模板必须包含开场、下一曲、致谢、休息与谢幕。");
        foreach (var template in settings.Templates.Values) ValidateTemplate(template);
    }

    private static void ValidateTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 2048 || template.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("报幕模板不能为空，最多 2048 个字符，且不能包含控制字符。");
        foreach (Match match in Tokens().Matches(template))
            if (match.Groups["brace"].Success || !Keys.Contains(match.Groups["key"].Value))
                throw new InvalidDataException("报幕模板字段无效，仅支持 {show}、{title}、{arranger}、{performers}、{requesters}。");
    }

    public static string Render(AnnouncementSettings settings, AnnouncementKind kind, AnnouncementContext context)
    {
        Validate(settings);
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("报幕类型无效。");
        if (context.ShowId == Guid.Empty) throw new InvalidOperationException("报幕需要关联演出。");
        if (kind is AnnouncementKind.NextSong or AnnouncementKind.Thanks && !context.EntryId.HasValue)
            throw new InvalidOperationException("当前没有可关联的歌曲节目。");
        var values = new Dictionary<string, string>
        {
            ["show"] = context.ShowName, ["title"] = context.Title, ["arranger"] = context.Arranger,
            ["performers"] = context.Performers, ["requesters"] = context.Requesters,
        };
        var text = Tokens().Replace(settings.Templates[kind], m => values[m.Groups["key"].Value]);
        if (text.Length > 8192 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidOperationException("报幕内容超过 8192 字符或包含无效控制字符。");
        return text;
    }

    public static string Fingerprint(AnnouncementSettings settings, AnnouncementKind kind, AnnouncementContext context)
    {
        _ = Render(settings, kind, context);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { kind, context, template = settings.Templates[kind] })));
    }
}

public sealed class AnnouncementDraft
{
    public string Text { get; set; } = "";
    public string GeneratedText { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public string Label { get; private set; } = "";
    public List<SavedAnnouncementDraft> History { get; } = [];
    public bool Edited => Text != GeneratedText;

    public bool Refresh(AnnouncementSettings settings, AnnouncementKind kind, AnnouncementContext context, string label)
    {
        var fingerprint = AnnouncementTemplates.Fingerprint(settings, kind, context);
        if (fingerprint == Fingerprint) return false;
        Archive();
        GeneratedText = AnnouncementTemplates.Render(settings, kind, context);
        Text = GeneratedText; Fingerprint = fingerprint; Label = label;
        return true;
    }

    public void Invalidate()
    {
        Archive(); Text = ""; GeneratedText = ""; Fingerprint = ""; Label = "";
    }

    public void Regenerate() { Archive(); Text = GeneratedText; }

    public string TextForCopy(AnnouncementSettings settings, AnnouncementKind kind, AnnouncementContext context)
    {
        if (Fingerprint.Length == 0 || Fingerprint != AnnouncementTemplates.Fingerprint(settings, kind, context))
            throw new InvalidOperationException("报幕对应的节目或资料已变化，请核对更新后的草稿。");
        if (string.IsNullOrWhiteSpace(Text) || Text.Length > 8192 || Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("草稿为空、过长或包含无效控制字符。");
        return Text;
    }

    private void Archive()
    {
        if (!Edited || string.IsNullOrWhiteSpace(Text)) return;
        History.Add(new SavedAnnouncementDraft(Label, Text, DateTimeOffset.UtcNow));
        if (History.Count > 20) History.RemoveAt(0);
    }
}

public sealed record SavedAnnouncementDraft(string Label, string Text, DateTimeOffset SavedAtUtc);
