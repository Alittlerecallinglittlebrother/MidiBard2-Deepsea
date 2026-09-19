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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;
using MidiBard.Util.MidiPreprocessor;

using static Dalamud.api;

namespace MidiBard.Control.MidiControl.PlaybackInstance;

internal sealed class BardPlayback : Playback
{
    internal MidiFileConfig MidiFileConfig { get; set; }
    internal bool IsSoloPlayback { get; init; }
    internal bool IsContinuityEnsemble { get; set; }
    internal MidiFile MidiFile { get; init; }
    internal string FilePath { get; init; }
    internal TrackChunk[] TrackChunks { get; init; }
    internal TrackInfo[] TrackInfos { get; init; }
    internal string DisplayName { get; init; }
    private static ulong[] Cids = new ulong[100];
    public static MidiFileConfig ReloadMidiFileConfig(MidiFileConfig midiFileConfig) => MidiFileConfigManager.LoadDefaultPerformer(midiFileConfig, ref Cids);

    public static BardPlayback GetBardPlayback(MidiFile file, string filePath)
    {
        PreparePlaybackData(file, out var tempoMap, out var trackChunks, out var trackInfos, out var timedEventWithMetadata);
        MidiFileConfig midiFileConfig = ResolveMidiConfig(filePath, trackChunks, trackInfos);

        return new BardPlayback(timedEventWithMetadata, tempoMap)
        {
            MidiFile = file,
            FilePath = filePath,
            TrackChunks = trackChunks,
            TrackInfos = trackInfos,
            MidiFileConfig = midiFileConfig,
            IsSoloPlayback = AutomaticEnsembleAssignment.IsSoloLoad,
            DisplayName = Path.GetFileNameWithoutExtension(filePath)
        };
    }

    private static MidiFileConfig ResolveMidiConfig(string filePath, TrackChunk[] trackChunks, TrackInfo[] trackInfos)
    {
        if (AutomaticEnsembleAssignment.IsSoloLoad)
        {
            if (MidiBard.CurrentPlayback?.MidiFileConfig?.AutomaticallyAssigned == true
                || !MidiBard.config.TrackStatus.Take(trackInfos.Length).Any(t => t.Enabled))
            {
                MidiBard.config.ResetTrackStatus();
                if (trackInfos.Length > 0) MidiBard.config.TrackStatus[0].Enabled = true;
                MidiBard.config.SoloedTrack = null;
            }
            return null;
        }
        if (api.PartyList.IsInParty() && AutomaticEnsembleAssignment.IsEnabled)
            return AutomaticEnsembleAssignment.Create(trackInfos, MidiFileConfigManager.GetMidiConfigFromFile(filePath));

        // dont use midiFileConfi or Default Performer when not in a party
        var ignoreDefaultPerformer = api.PartyList.IsInParty() && MidiBard.config.lockTracks;
        if (!api.PartyList.IsInParty() || ignoreDefaultPerformer)
        {
            PluginLog.Debug($"[LoadPlayback] using config TrackStatus");
            return null;
        }

        var midiConfigFromTrack = MidiFileConfigManager.GetMidiConfigFromTrack(trackInfos);

        // use midi specific json config
        // TODO: improve json config file changes detection
        // compare tracks name / order to decide what props to reset
        var midiFileConfig = MidiFileConfigManager.GetMidiConfigFromFile(filePath);
        var isMidiTracksEqualJsonConfigFileTracks = midiFileConfig?.Tracks.Count == trackChunks.Length;
        var useMidiJsonFileConfig = midiFileConfig is not null && isMidiTracksEqualJsonConfigFileTracks;
        if (useMidiJsonFileConfig)
        {
            PluginLog.Debug($"[LoadPlayback] using json midi file config");
            return LoadMidiConfigFromJson(midiFileConfig);
        }

        // PMD
        if (MidiBard.config.playOnMultipleDevices)
        {
            if (MidiBard.config.usingFileSharingServices)
            {
                PluginLog.Debug($"[LoadPlayback] using shared default performer");
                return LoadMidiConfigFromDefaultPerformer(midiConfigFromTrack);
            }

            PluginLog.Debug($"[LoadPlayback] PMD using config TrackStatus");
            return LoadMidiConfigFromTrackStatus(midiConfigFromTrack);
        }

        // default performer
        var defaultPerformerTrackMapping = MidiFileConfigManager.defaultPerformer?.TrackMappingDict ?? new();
        var useDefaultPerformer = defaultPerformerTrackMapping.Count > 0;
        if (useDefaultPerformer)
        {
            PluginLog.Debug($"[LoadPlayback] using default performer");
            return LoadMidiConfigFromDefaultPerformer(midiConfigFromTrack);
        }

        // if in a party but no default perform or midi json file use config.TrackStatus
        // for solo bards while in party or ensemble with PMD to not lose the assigned tracks
        PluginLog.Debug($"[LoadPlayback] using config TrackStatus");
        return LoadMidiConfigFromTrackStatus(midiConfigFromTrack);
    }

    private static MidiFileConfig LoadMidiConfigFromJson(MidiFileConfig midiFileConfig)
    {
        MidiFileConfigManager.UsingDefaultPerformer = false;
        for (int i = 0; i < midiFileConfig.Tracks.Count; i++)
            Cids[i] = MidiFileConfig.GetFirstCidInParty(midiFileConfig.Tracks[i]);

        return midiFileConfig;
    }

    /*
        // merge json config with default performer
        private static MidiFileConfig LoadMidiConfigFromJson(MidiFileConfig midiFileConfig)
        {
            var useDefaultPerformerMerge = true;
            MidiFileConfigManager.UsingDefaultPerformer = false;

            for (int i = 0; i < midiFileConfig.Tracks.Count; i++)
                Cids[i] = MidiFileConfig.GetFirstCidInParty(midiFileConfig.Tracks[i]);

            // use only json data
            if (!useDefaultPerformerMerge)
                return midiFileConfig;

            // merge default performer with json
            var defaultPerformerFallback = LoadMidiConfigFromDefaultPerformer(midiFileConfig.JsonClone());
            // bool changed = false;

            for (int i = 0; i < defaultPerformerFallback.Tracks.Count; i++)
            {
                var cid = MidiFileConfig.GetFirstCidInParty(defaultPerformerFallback.Tracks[i]);

                if (!Cids.Contains(cid))
                {
                    midiFileConfig.Tracks[i].AssignedCids.Add(cid);
                    // changed = true;
                }
            }

            // if (changed)
            // {
            //     try
            //     {
            //         midiFileConfig.Save(filePath);
            //     }
            //     catch
            //     {
            //         // ignored
            //     }
            // }

            return midiFileConfig;
        }
        */

    private static MidiFileConfig LoadMidiConfigFromTrackStatus(MidiFileConfig midiConfigFromTrack)
    {
        MidiFileConfigManager.UsingDefaultPerformer = false;
        Cids = new ulong[100];

        var bardCid = api.Player.ContentId;
        for (int i = 0; i < midiConfigFromTrack.Tracks.Count; i++)
        {
            if (MidiBard.config.TrackStatus[i].Enabled)
            {
                midiConfigFromTrack.Tracks[i].Enabled = true;
                midiConfigFromTrack.Tracks[i].AssignedCids.Add(bardCid);
                Cids[i] = bardCid;
            }
        }

        return midiConfigFromTrack;
    }

    private static MidiFileConfig LoadMidiConfigFromDefaultPerformer(MidiFileConfig midiConfigFromTrack)
    {
        // PMD
        if (MidiBard.config.usingFileSharingServices)
            MidiFileConfigManager.LoadDefaultPerformer();

        return MidiFileConfigManager.LoadDefaultPerformer(midiConfigFromTrack, ref Cids);
    }

    private BardPlayback(IEnumerable<TimedEventWithMetadata> timedObjects, TempoMap tempoMap)
    : base(timedObjects, tempoMap, new PlaybackSettings { ClockSettings = new MidiClockSettings { CreateTickGeneratorCallback = () => new HighPrecisionTickGenerator() } })
    {
    }

    protected override bool TryPlayEvent(MidiEvent midiEvent, object metadata)
    {
        // Place your logic here
        // Return true if event played (sent to plug-in); false otherwise
        MidiBard.BardPlayDevice.SendEventWithMetadata(midiEvent, metadata);
        return true;
    }

    private static void PreparePlaybackData(MidiFile file, out TempoMap tempoMap, out TrackChunk[] trackChunks, out TrackInfo[] trackInfos, out TimedEventWithMetadata[] timedEventWithMetadata)
    {
        if (MidiBard.config.AntiStackType != AntiStackType.Off)
            file = MidiPreprocessor.RemoveStackedNotes(file, MidiBard.config.AntiStackType);
        if (MidiBard.config.AlignMidi)
            file = MidiPreprocessor.RealignMidiFile(file, MidiBard.config.AlignMidiStartOffset);

        tempoMap = TryGetTempoMap(file);
        var map = tempoMap;
        trackChunks = MidiPreprocessor.ProcessTracks(GetNoteTracks(file).ToArray(), map);
        trackInfos = trackChunks.Select((chunk, index) => GetTrackInfos(chunk, index, map)).ToArray();
        timedEventWithMetadata = GetTimedEventWithMetadata(trackChunks).ToArray();
    }

    public float GetPlaybackProgress()
    {
        var currentTime = GetCurrentTime<MetricTimeSpan>();
        var duration = GetDuration<MetricTimeSpan>();
        float progress = Util.Extensions.SafeDivideMetricTimeSpan(currentTime, duration);
        return progress;
    }

    private static TempoMap TryGetTempoMap(MidiFile midiFile)
    {
        try
        {
            return midiFile.GetTempoMap();
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, "[LoadPlayback] error when getting file TempoMap, using default TempoMap instead.");
            return TempoMap.Default;
        }
    }

    private static IEnumerable<TrackChunk> GetNoteTracks(MidiFile midifile)
    {
        try
        {
            return AutomaticEnsembleRules.NoteTracks(midifile);
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, $"[LoadPlayback] error when parsing tracks, falling back to generated NoteEvent playback.");
            try
            {
                PluginLog.Debug($"[LoadPlayback] file.Chunks.Count {midifile.Chunks.Count}");
                var trackChunks = midifile.GetTrackChunks().ToArray();
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.Count {trackChunks.Length}");
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.First {trackChunks.First()}");
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.Events.Count {trackChunks.First().Events.Count}");
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.Events.OfType<NoteEvent>.Count {trackChunks.First().Events.OfType<NoteEvent>().Count()}");

                return AutomaticEnsembleRules.NoteTracks(midifile)
                    .Select((i) =>
                    {
                        var noteEvents = i.Events.Where(midiEvent => midiEvent is NoteEvent or ProgramChangeEvent or TextEvent);
                        return new TrackChunk(noteEvents);
                    });
            }
            catch (Exception exception2)
            {
                PluginLog.Error(exception2, "[LoadPlayback] still errors? check your file");
                throw;
            }
        }
    }

    private static TrackInfo GetTrackInfos(TrackChunk i, int index, TempoMap tempoMap)
    {
        var notes = i.GetNotes();
        var eventsCollection = i.Events;
        var TrackNameEventsText = eventsCollection.OfType<SequenceTrackNameEvent>().Select(j => j.Text.Replace("\0", string.Empty).Trim()).Distinct().ToArray();
        var TrackName = TrackNameEventsText.FirstOrDefault() ?? "Untitled";
        var IsProgramControlled = Regex.IsMatch(TrackName, @"^Program:.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var timedNoteOffEvent = notes.LastOrDefault()?.GetTimedNoteOffEvent();

        return new TrackInfo
        {
            //TextEventsText = eventsCollection.OfType<TextEvent>().Select(j => j.Text.Replace("\0", string.Empty).Trim()).Distinct().ToArray(),
            ProgramChangeEventsText = eventsCollection.OfType<ProgramChangeEvent>().Select(j => $"channel {j.Channel}, {j.GetGMProgramName()}").Distinct().ToArray(),
            InitialProgram = eventsCollection.OfType<ProgramChangeEvent>().Select(j => (int?)j.ProgramNumber).FirstOrDefault(),
            TrackNameEventsText = TrackNameEventsText,
            HighestNote = notes.MaxElement(j => (int)j.NoteNumber),
            LowestNote = notes.MinElement(j => (int)j.NoteNumber),
            NoteCount = notes.Count,
            DurationMetric = timedNoteOffEvent?.TimeAs<MetricTimeSpan>(tempoMap) ?? new MetricTimeSpan(),
            DurationMidi = timedNoteOffEvent?.Time ?? 0,
            TrackName = TrackName,
            IsProgramControlled = IsProgramControlled,
            Index = index,
            IsProgramElectricGuitar = TrackName.ToLower().Replace(":", "").StartsWith("programelectricguitar")
            //Channels = i.Events.OfType<ProgramChangeEvent>().Select(j => j.Channel).Distinct().Union(notes.Select(note => note.Channel).Distinct()).ToArray()
        };
    }

    private readonly Dictionary<long, Dictionary<SevenBitNumber, int>> timeEventsDictionary =
        new Dictionary<long, Dictionary<SevenBitNumber, int>>();

    internal static List<Dictionary<long, Dictionary<int, int>>> TrimDict;

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static IEnumerable<TimedEventWithMetadata> GetTimedEventWithMetadata(IEnumerable<TrackChunk> tracks)
    {
        var timedEvents = tracks
            .SelectMany((track, index) => track.GetTimedEvents()
                    .Where(i => i.Event.EventType is not MidiEventType.ControlChange and not MidiEventType.PitchBend and not MidiEventType.UnknownMeta)
                    .Select(timedEvent => new TimedEventWithMetadata(timedEvent.Event, timedEvent.Time, GetMetadataForEvent(timedEvent.Event, timedEvent.Time, index))))
            .OrderBy(e => e.Time)
            .ThenBy(i => ((BardPlayDevice.MidiPlaybackMetaData)i.Metadata).EventValue);
        return timedEvents;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    static BardPlayDevice.MidiPlaybackMetaData GetMetadataForEvent(MidiEvent midiEvent, long time, int trackIndex)
    {
        var compareValue = midiEvent switch
        {
            //order chords so they always play from low to high
            NoteEvent noteEvent => noteEvent.NoteNumber,
            //order program change events so they always get processed before notes
            ProgramChangeEvent => -2,
            //keep other unimportant events order
            _ => -1
        };
        return new BardPlayDevice.MidiPlaybackMetaData(trackIndex, time, compareValue);
    }

    public uint GetInstrumentId()
    {
        // find instrument from config file
        uint? configInstrumentId = MidiFileConfig?.Tracks?
            .FirstOrDefault(t => t.Enabled && (MidiFileConfig.AutomaticallyAssigned
                ? t.AssignedCids.Contains(api.Player.ContentId) : MidiFileConfig.IsCidOnTrack(api.Player.ContentId, t)))
            ?.Instrument;

        if (MidiFileConfig?.AutomaticallyAssigned == true) return configInstrumentId ?? 0;

        // find instrument from first enabled track
        uint? trackInstrumentId = TrackInfos?
            .FirstOrDefault(i => i.IsEnabled)
            ?.InstrumentIDFromTrackName;

        uint defaultInstrumentId = 0;
        return (configInstrumentId ?? trackInstrumentId) ?? defaultInstrumentId;
    }

    internal void ApplyTransposeToTracks()
    {
        foreach (var trackInfo in TrackInfos)
        {
            var transposePerTrack = trackInfo.TransposeFromTrackName;
            if (transposePerTrack != 0)
            {
                PluginLog.Information($"applying transpose {transposePerTrack:+#;-#;0} for track [{trackInfo.Index + 1}] {trackInfo.TrackName}");
            }

            MidiBard.config.TrackStatus[trackInfo.Index].Transpose = transposePerTrack;
        }

        MidiBard.config.TransposeGlobal = 0;
    }

    internal void UpdateGuitarToneByConfig()
    {
        var playback = MidiBard.CurrentPlayback;
        if (playback == null) return;

        foreach (var (trackInfo, index) in playback.TrackInfos.Select((info, i) => (info, i)))
        {
            var trackInstrumentId = trackInfo.InstrumentIDFromTrackName;
            if (trackInstrumentId is uint instrumentId && MidiBard.Instruments[instrumentId].IsGuitar)
            {
                MidiBard.config.TrackStatus[index].Tone = MidiBard.Instruments[instrumentId].GuitarTone;
            }
        }
    }

    internal void SyncTrackStatusWithMidiFileConfig()
    {
        if (MidiFileConfig == null)
            return;

        // if (MidiFileConfig == null || MidiFileConfigManager.UsingDefaultPerformer)
        //     return;

        var tracks = MidiFileConfig.Tracks;
        MidiBard.config.ResetTrackStatus();
        PluginLog.Debug($"[LoadPlayback] SyncTrackStatusWithMidiFileConfig");
        for (var trackIndex = 0; trackIndex < MidiFileConfig.Tracks.Count; trackIndex++)
        {
            try
            {
                var isBardAssignedToTrack = MidiFileConfig.AutomaticallyAssigned
                    ? tracks[trackIndex].AssignedCids.Contains(api.Player.ContentId)
                    : MidiFileConfig.GetFirstCidInParty(tracks[trackIndex]) == api.Player.ContentId;
                MidiBard.config.TrackStatus[trackIndex].Enabled = tracks[trackIndex].Enabled && isBardAssignedToTrack;
                MidiBard.config.TrackStatus[trackIndex].Transpose = tracks[trackIndex].Transpose;
                MidiBard.config.TrackStatus[trackIndex].Tone = InstrumentHelper.GetGuitarTone(tracks[trackIndex].Instrument);
                MidiBard.config.SoloedTrack = null;
            }
            catch (Exception e)
            {
                PluginLog.Error(e, $"error when updating track {trackIndex}");
            }
        }
    }
}
