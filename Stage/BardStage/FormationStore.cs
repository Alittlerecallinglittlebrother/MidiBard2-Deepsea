using System.Text.Json;
using BardStage.Core.Rooms;

namespace BardStage;

public sealed class FormationStore(string directory)
{
    private ulong owner;
    public List<FormationPreset> Presets { get; private set; } = [];
    public string? Error { get; private set; }
    public void Load(ulong cid)
    {
        if (cid == 0 || cid == owner) return;
        owner = cid; Presets = []; Error = null;
        try
        {
            if (!File.Exists(PathName)) return;
            var values = JsonSerializer.Deserialize<List<FormationPreset>>(File.ReadAllText(PathName), RoomJson.Options) ?? [];
            if (values.Count > 50) throw new InvalidDataException("队形数量超过 50");
            foreach (var value in values) value.Validate();
            Presets = values;
        }
        catch (Exception ex) { Error = "队形文件读取失败，原文件已保留：" + ex.Message; }
    }
    public void Save()
    {
        if (owner == 0 || Error != null) throw new InvalidOperationException(Error ?? "等待角色登录");
        if (Presets.Count > 50) throw new InvalidOperationException("最多保存 50 个队形");
        foreach (var value in Presets) value.Validate();
        Directory.CreateDirectory(directory);
        var temp = PathName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Presets, RoomJson.Options));
        if (File.Exists(PathName)) File.Replace(temp, PathName, PathName + ".bak");
        else File.Move(temp, PathName);
    }
    private string PathName => Path.Combine(directory, $"formations-{owner}.json");
}
