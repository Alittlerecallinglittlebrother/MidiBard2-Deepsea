// Copyright (C) 2022 akira0245
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see https://github.com/akira0245/MidiBard/blob/master/LICENSE.
//
// This code is written by akira0245 and was originally used in the MidiBard project. Any usage of this code must prominently credit the author, akira0245, and indicate that it was originally used in the MidiBard project.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Melanchall.DryWetMidi.Interaction;
using MidiBard.Managers;

namespace MidiBard;

public record TrackInfo
{
    //  var (programTrackChunk, programTrackInfo) =
    //  CurrentTracks.FirstOrDefault(i => Regex.IsMatch(i.trackInfo.TrackName, @"^Program:.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase));

    public string[] TrackNameEventsText { get; init; }
    public string[] ProgramChangeEventsText { get; init; }
    public int NoteCount { get; init; }
    public Note LowestNote { get; init; }
    public Note HighestNote { get; init; }
    public MetricTimeSpan DurationMetric { get; init; }
    public long DurationMidi { get; init; }
    public bool IsProgramControlled { get; init; }
    public string TrackName { get; init; }
    public int Index { get; set; }
    public bool IsProgramElectricGuitar { get; set; }
    public int? InitialProgram { get; init; }

    public ref bool IsEnabled => ref MidiBard.config.TrackStatus[Index].Enabled;
    public bool IsPlaying => MidiBard.config.SoloedTrack is int t ? t == Index : IsEnabled;

    public int TransposeFromTrackName => GetTransposeByName(TrackName);
    public uint? InstrumentIDFromTrackName => GetInstrumentIDByName(TrackName);
    public uint? GuitarToneFromTrackName => GetInstrumentIDByName(TrackName) - 24;

    public override string ToString()
    {
        return $"{TrackName} / {NoteCount} notes / {LowestNote}-{HighestNote}";
    }

    public string ToLongString()
    {
        var trackInfo = $"""
        Track name:
            {TrackName}
        Note count:
            {NoteCount}
        Note Range:
            {LowestNote}-{HighestNote}
        ProgramChange events:
            {string.Join("\n ", ProgramChangeEventsText.Distinct())}
        Duration:
            {DurationMetric}
        """;
        return trackInfo;
    }

    public static uint? GetInstrumentIDByName(string trackName)
    {
        return AutomaticEnsembleRules.InstrumentFromName(trackName);
    }

    public static int GetTransposeByName(string trackName)
    {
        RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Multiline;
        string sanitizedTrackName = Regex.Replace(trackName, @"(\s+|:)", "", options).ToLowerInvariant();
        string octavePattern = $@"(?:(\+|-)(?:\s+)?(\d))";
        Regex expression = new Regex(octavePattern, options);
        var matches = expression.Matches(sanitizedTrackName);

        int octave = 0;

        foreach (Match match in matches)
        {
            GroupCollection groups = match.Groups;
            string plusMinusSign = groups[1].Value.ToString();
            bool isParsable = Int32.TryParse(groups[2].Value, out octave);
            octave = (plusMinusSign == "-" ? -octave : octave) * 12;
        }
        // PluginLog.Debug("Transpose octave: " + octave);

        return octave;
    }
}
