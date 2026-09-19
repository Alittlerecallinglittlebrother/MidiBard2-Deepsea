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
using System.Numerics;

using Dalamud.Configuration;

using MidiBard.Managers;
using MidiBard.Util;

using Newtonsoft.Json;

namespace MidiBard;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    [JsonIgnore]
    public TrackStatus[] TrackStatus = Enumerable.Range(0, 100).Select(_ => new TrackStatus()).ToArray();

    //public ChannelStatus[] ChannelStatus = Enumerable.Repeat(new ChannelStatus(), 16).ToArray();
    public List<EnsembleMemberConfig> EnsembleMemberConfigs { get; set; } = new();

    public List<string> RecentUsedPlaylists { get; set; } = new List<string>();

    // folder / file dialogs
    public List<string> PinnedImportFolders { get; set; } = new List<string>();
    public string lastOpenedFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string defaultPerformerFolder = api.PluginInterface.ConfigDirectory.FullName;
    public string defaultPlaylistFolder = api.PluginInterface.ConfigDirectory.FullName;
    public bool useLegacyFileDialog = false;

    // playback config
    public float PlaySpeed = 1f;
    public float SecondsBetweenTracks = 3;
    public int PlayMode = 0;
    public int TransposeGlobal = 0;
    public bool AdaptNotesOOR = true;
    public bool AlignMidi = false;
    public double AlignMidiStartOffset = 0;
    public AntiStackType AntiStackType = AntiStackType.Off;
    public bool LowLatencyMode => false;
    public bool MonitorOnEnsemble = true;
    public bool AutoOpenPlayerWhenPerforming = true;
    public bool AutoClosePlayerWhenPerforming = false;
    public int? SoloedTrack = null;
    public bool lazyNoteRelease = true;
    public string lastUsedMidiDeviceName = "";
    public bool autoRestoreListening = false;
    public bool autoSwitchInstrumentBySongName = true;
    public bool autoTransposeBySongName = true;
    public bool bmpTrackNames = true;
    public bool StopPlayingWhenEnsembleEnds = true;
    public bool SyncClients = true;
    public bool AutoSetOffAFKSwitchingTime = true;
    public float EnsembleIndicatorDelay = -4;
    public bool UseEnsembleIndicator = false;
    public bool UpdateInstrumentBeforeReadyCheck;
    public bool AutoAssignEnsembleTracks = true;
    public GuitarToneMode GuitarToneMode = GuitarToneMode.Off;
    public CompensationModes CompensationMode = CompensationModes.ByInstrumentNote;
    public int[] ManualInstrumentCompensation = EnsembleManager.GetCompensationAver();
    //public bool TrimChords = false;
    //public int TrimTo = 1;
    //public bool autoSwitchInstrumentByTrackName = false;
    //public bool autoTransposeByTrackName = false;
    //public bool autoStartNewListening = false;

    // Play on multiple devices
    public bool playOnMultipleDevices = false;
    public bool useChatPlaylistSync = false;
    public bool usingFileSharingServices = true;
    public bool lockTracks = false;

    // Lyrics
    public bool playLyrics = true;
    public ChatType LyricsChatTarget = ChatType.Current;

    // Post Song
    public bool autoPostSongName = false;
    public ChatType SongNameChatTarget = ChatType.Current;
    public string postSongNameCaptureRegex = "";
    public string postSongNameCaptureOutputFormat = "";
    public string postSongNameFindRegex = "";
    public string postSongNameReplacement = "";

    // Theme
    public Vector4 themeColor = new Vector4(0.65882355f, 0.65882355f, 1f, 1f);
    public Vector4 themeColorDark => themeColor * new Vector4(0.25f, 0.25f, 0.25f, 1);
    public Vector4 themeColorTransparent => themeColor * new Vector4(1, 1, 1, 0.33f);
    public Vector4 playedSongColor = new Vector4(0.0f, 0.9804f, 1.0f, 1.0f);
    public ThemeVariant CurrentTheme = ThemeVariant.Default;

    // search
    public bool enableSearching = false;
    public bool SearchUseRegex;
    public FilterPlayedSongOptions SearchFilterPlayedOption = FilterPlayedSongOptions.ShowAll;

    // UI
    public bool AutoOpenOnStartup = false;
    public bool UseStandalonePlaylistWindow = false;
    public bool hidePlayerInformationFromUi = false;
    public int playlistSizeY = 10;
    public bool miniPlayer = false;
    public bool LockPlot = false;
    public int uiLang = api.PluginInterface.UiLanguage == "zh" ? 1 : 0;
    // show / hide items
    public bool UiShowGuitarToneMode = false;
    public bool UiShowPlaySpeed = false;
    public bool UiShowTransposeGlobal = false;
    public bool UiShowAdaptNotesOOR = false;
    public bool UiShowAutoAlignMidi = false;
    public bool showNowPlayingInfo = true;

    //[JsonIgnore] public bool OverrideGuitarTones => GuitarToneMode == GuitarToneMode.Override;

    public void ToggleSearchFilterPlayedOption()
    {
        var totalOptions = Enum.GetValues(typeof(FilterPlayedSongOptions)).Length;
        SearchFilterPlayedOption = (FilterPlayedSongOptions)(((int)SearchFilterPlayedOption + 1) % totalOptions);
    }

    public void AddEnsembleMemberConfig(EnsembleMemberConfig newConfig)
    {
        var existing = EnsembleMemberConfigs.FirstOrDefault(p => p.Cid == newConfig.Cid);
        if (existing == null)
        {
            EnsembleMemberConfigs.Add(newConfig);
        }
    }

    public void LinkEnsembleMember(ulong sourceCid, ulong targetCid)
    {
        var source = EnsembleMemberConfigs.FirstOrDefault(x => x.Cid == sourceCid);
        var target = EnsembleMemberConfigs.FirstOrDefault(x => x.Cid == targetCid);

        if (source == null || target == null || source == target)
            return;

        // Move member
        target.LinkedEnsembleMembers.Add(new EnsembleMember
        {
            Cid = source.Cid,
            Name = source.Name
        });

        EnsembleMemberConfigs.Remove(source);
    }

    public void UnlinkEnsembleMember(ulong parentCid, ulong linkedCid)
    {
        var parent = EnsembleMemberConfigs.FirstOrDefault(x => x.Cid == parentCid);

        if (parent == null)
            return;

        if (parent.LinkedEnsembleMembers == null || parent.LinkedEnsembleMembers.Count == 0)
            return;

        var linked = parent.LinkedEnsembleMembers
            .FirstOrDefault(x => x.Cid == linkedCid);

        if (linked == null)
            return;

        parent.LinkedEnsembleMembers.Remove(linked);

        // Add back to main list only if not already there
        if (!EnsembleMemberConfigs.Any(x => x.Cid == linked.Cid))
        {
            EnsembleMemberConfigs.Add(new EnsembleMemberConfig
            {
                Cid = linked.Cid,
                Name = linked.Name,
                TrackAssignmentRegex = "",
                LinkedEnsembleMembers = new List<EnsembleMember>()
            });
        }
    }

    public void ResetTrackStatus()
    {
        TrackStatus = Enumerable.Repeat(new TrackStatus(), 100).ToArray().JsonSerialize().JsonDeserialize<TrackStatus[]>();
    }

    public string GetChatCommand(ChatType chatType)
    {
        return chatType switch
        {
            ChatType.Current => string.Empty,
            ChatType.Say => "/s ",
            ChatType.Party => "/p ",
            _ => string.Empty
        };
    }

    public void SetTransposeGlobal(int transpose)
    {
        bool isDrumTrackPlaying = false;
        if (MidiBard.CurrentPlayback?.TrackInfos?.Length > 0)
        {
            foreach (var trackInfo in MidiBard.CurrentPlayback?.TrackInfos)
            {
                var insID = trackInfo.InstrumentIDFromTrackName;
                if (trackInfo.IsEnabled && insID >= 10 && insID <= 14)
                {
                    isDrumTrackPlaying = true;
                    break;
                }
            }
        }

        if (isDrumTrackPlaying)
        {
            TransposeGlobal = 0;
            return;
        }

        TransposeGlobal = transpose;
    }
}

