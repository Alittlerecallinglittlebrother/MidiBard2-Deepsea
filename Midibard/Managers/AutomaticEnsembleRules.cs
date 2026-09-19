#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Core;

namespace MidiBard.Managers;

internal static class AutomaticEnsembleRules
{
    internal static IEnumerable<TrackChunk> NoteTracks(MidiFile file) =>
        file.GetTrackChunks().Where(track => track.Events.OfType<NoteOnEvent>().Any(note => note.Velocity > 0));

    private static readonly (uint Id, string[] Names)[] Instruments =
    {
        (1, new[] { "harp", "竖琴", "豎琴", "shuqin" }),
        (2, new[] { "piano", "钢琴", "鋼琴", "gangqin", "ganqin" }),
        (3, new[] { "lute", "鲁特琴", "鲁特", "魯特", "luteqin" }),
        (4, new[] { "fiddle", "pizzicato", "提琴拨弦", "提琴撥弦", "boxian" }),
        (5, new[] { "flute", "长笛", "長笛", "changdi" }),
        (6, new[] { "oboe", "双簧管", "雙簧管", "shuanghuangguan" }),
        (7, new[] { "clarinet", "单簧管", "單簧管", "danhuangguan" }),
        (8, new[] { "fife", "横笛", "橫笛", "hengdi" }),
        (9, new[] { "panpipes", "panflute", "排箫", "排簫", "paixiao" }),
        (10, new[] { "timpani", "定音鼓", "dingyingu" }),
        (11, new[] { "bongos", "bongo", "邦戈鼓", "banggegu" }),
        (12, new[] { "bassdrum", "kickdrum", "低音鼓", "大鼓", "diyingu", "dagu" }),
        (13, new[] { "snaredrum", "snare", "小军鼓", "軍鼓", "军鼓", "小鼓", "xiaojungu", "xiaogu" }),
        (14, new[] { "cymbal", "cymbals", "镲", "鑔", "钹", "鈸" }),
        (15, new[] { "trumpet", "小号", "小號", "xiaohao" }),
        (16, new[] { "trombone", "长号", "長號", "changhao" }),
        (17, new[] { "tuba", "大号", "大號", "dahao" }),
        (18, new[] { "frenchhorn", "horn", "圆号", "圓號", "yuanhao" }),
        (19, new[] { "saxophone", "sax", "萨克斯", "薩克斯", "sakesi" }),
        (20, new[] { "violin", "小提琴", "xiaotiqin" }),
        (21, new[] { "viola", "中提琴", "zhongtiqin" }),
        (22, new[] { "cello", "大提琴", "datiqin" }),
        (23, new[] { "doublebass", "contrabass", "uprightbass", "低音提琴", "diyintiqin" }),
        (24, new[] { "electricguitaroverdriven", "overdriven", "overdrive", "过载", "過載", "guozai" }),
        (25, new[] { "electricguitarclean", "guitarclean", "清音", "qingyin" }),
        (26, new[] { "electricguitarmuted", "guitarmuted", "闷音", "悶音", "menyin" }),
        (27, new[] { "electricguitarpowerchords", "powerchords", "powerchord", "重力", "zhongli" }),
        (28, new[] { "electricguitarspecial", "特殊奏法", "teshuzoufa" }),
        (24, new[] { "electricguitar", "program", "电吉他", "電吉他" }),
    };

    private static readonly (string Name, uint Id)[] Aliases = Instruments
        .SelectMany(x => x.Names.Select(name => (Name: Normalize(name), x.Id)))
        .OrderByDescending(x => x.Name.Length).ToArray();

    internal static uint? InstrumentFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = Normalize(name);
        foreach (var alias in Aliases)
            if (normalized.Contains(alias.Name, StringComparison.Ordinal)) return alias.Id;
        // A one-syllable pinyin instrument must be a token, not a substring of another name.
        if (Regex.IsMatch(name, @"(?:^|[\s\d_\-:])cha(?:$|[\s\d_\-:+])", RegexOptions.IgnoreCase)) return 14;
        return null;
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    internal static uint? InstrumentFromProgram(int? program) => program switch
    {
        >= 0 and <= 7 => 2,
        24 or 25 => 3,
        26 or 27 => 25,
        28 => 26,
        29 or 30 => 24,
        40 => 20, 41 => 21, 42 => 22, 43 => 23, 45 => 4, 46 => 1, 47 => 10,
        56 => 15, 57 => 16, 58 => 17, 60 => 18,
        >= 64 and <= 67 => 19,
        68 => 6, 71 => 7, 72 => 8, 73 => 5, 75 => 9,
        _ => null,
    };

    internal static uint ResolveInstrument(string? name, uint? saved, int? program) =>
        InstrumentFromName(name) ?? (saved is >= 1 and <= 28 ? saved : null) ?? InstrumentFromProgram(program) ?? 1;

    internal static ulong[] AssignTracks(int noteTrackCount, IReadOnlyList<ulong> partyOrder)
    {
        var result = new ulong[Math.Max(0, noteTrackCount)];
        var assigned = new HashSet<ulong>();
        for (var i = 0; i < result.Length && i < partyOrder.Count && i < 8; i++)
            if (partyOrder[i] != 0 && assigned.Add(partyOrder[i])) result[i] = partyOrder[i];
        return result;
    }

    internal static string EncodeOrder(IReadOnlyList<ulong> order) =>
        "auto=" + string.Join(",", order.Take(8).Select(cid => cid.ToString("X", CultureInfo.InvariantCulture)));

    internal static bool TryDecodeOrder(string? value, IReadOnlyCollection<ulong> currentParty, out ulong[] order)
    {
        order = Array.Empty<ulong>();
        if (value == null || !value.StartsWith("auto=", StringComparison.Ordinal) || value.Length > 141) return false;
        var parts = value[5..].Split(',');
        if (parts.Length is < 1 or > 8 || parts.Length != currentParty.Count) return false;
        var parsed = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!ulong.TryParse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed[i])
                || parsed[i] == 0 || !currentParty.Contains(parsed[i])) return false;
        if (parsed.Distinct().Count() != parsed.Length) return false;
        order = parsed;
        return true;
    }
}
