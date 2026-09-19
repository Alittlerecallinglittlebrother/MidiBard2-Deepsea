using System.Text.Json;
using System.Text.Json.Serialization;

namespace BardStage.Core;

public sealed class AnnouncementStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "announcements.json");

    public AnnouncementSettings Load()
    {
        if (!File.Exists(FilePath)) return AnnouncementTemplates.Defaults();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count(p => p.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)) != 1
                || !document.RootElement.EnumerateObject().Any(p => p.Name.Equals("templates", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("报幕文件缺少版本或模板。");
            var settings = document.RootElement.Deserialize<AnnouncementSettings>(Options) ?? throw new InvalidDataException("报幕模板为空。");
            AnnouncementTemplates.Validate(settings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"报幕模板未载入，原文件已保留：{ex.Message}", ex);
        }
    }

    public void Save(AnnouncementSettings settings)
    {
        AnnouncementTemplates.Validate(settings);
        if (File.Exists(FilePath)) _ = Load();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
                stream.Write(bytes); stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", true);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
