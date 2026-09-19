using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using MidiBard.Managers;

using Newtonsoft.Json;

namespace MidiBard2.Util;

public static class Sanitizer
{
    private static readonly JsonSerializerSettings JsonSerializerSettings = new()
    {
    };


    /// <summary>
    /// Sanitize CIDs
    /// </summary>
    /// <param name="fileContent"></param>
    /// <param name="songPath"></param>
    /// <returns></returns>
    public static string SanitizeMidiFileConfig(string content, string songPath)
    {
        string fileContent = content;
        if (MidiFileConfigCheckInvalidJsonCID(fileContent))
        {
            var data = JsonNode.Parse(fileContent);
            if (data is not null)
            {
                foreach (var track in data["Tracks"]?.AsArray() ?? [])
                    if (track?["AssignedCids"]?.AsArray() is { } cids)
                        for (int i = 0; i < cids.Count; i++)
                            if (cids[i]?.GetValue<long>() == -1)
                                cids[i] = 0;
                fileContent = fileContent = data.ToJsonString();
            }
            MidiFileConfigManager.Save(JsonConvert.DeserializeObject<MidiFileConfig>(fileContent, JsonSerializerSettings), songPath);
        }
        return fileContent;
    }

    public static bool MidiFileConfigCheckInvalidJsonCID(string fileContent)
    {
        var data = JsonNode.Parse(fileContent);
        return data["Tracks"]!.AsArray().Any(track => track?["AssignedCids"]?.AsArray().Any(cid => cid?.GetValue<long>() == -1) ?? false);
    }
}
