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

using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

using MidiBard.Control.MidiControl.PlaybackInstance;
using MidiBard.IPC;
using MidiBard.Managers;
using MidiBard.Util;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private static bool isOthersClientsMuted = false;

    private void DrawEnsembleControlMenu()
    {
        var ensembleRunning = MidiBard.AgentMetronome.EnsembleModeRunning;
        var isEnsembleButtonsDisabled = MidiBard.CurrentPlayback == null || MidiBard.CurrentPlayback.IsSoloPlayback
            || PartyChatCommand.IsLoading || ensembleRunning || MidiBard.IsPlaying || PartyChatCommand.EnsembleLoadIssue != null;
        if (PartyChatCommand.EnsembleLoadIssue is { } issue) ImGui.TextWrapped(issue);

        ImGuiUtil.PushIconButtonSize(new Vector2(ImGuiHelpers.GlobalScale * 40, ImGui.GetFrameHeight()));
        // if (!MidiBard.config.playOnMultipleDevices || (MidiBard.config.playOnMultipleDevices && MidiBard.config.usingFileSharingServices))

        if (!ensembleRunning)
        {
            ImGui.BeginDisabled(isEnsembleButtonsDisabled);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.UserCheck, "##btnEnsembleStart", Language.ensemble_begin_ensemble_ready_check))
            {
                if (MidiBard.config.AutoAssignEnsembleTracks || MidiBard.config.UpdateInstrumentBeforeReadyCheck)
                {
                    if (MidiBard.CurrentPlayback?.MidiFileConfig is { } config && !config.LeaderDistributed)
                    {
                        IPCHandles.UpdateMidiFileConfig(config);
                    }

                    if (!MidiBard.config.playOnMultipleDevices)
                    {
                        IPCHandles.UpdateInstrument(true);
                    }
                    else if (MidiBard.config.AutoAssignEnsembleTracks)
                    {
                        PartyChatCommand.SendUpdateInstrument();
                    }
                }

                EnsembleManager.BeginEnsembleReadyCheck();
            }
            ImGui.EndDisabled();
        }
        else
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Stop, "##btnEnsembleStop", Language.ensemble_stop_ensemble))
            {
                if (!MidiBard.config.playOnMultipleDevices)
                {
                    IPCHandles.UpdateInstrument(false);
                }
                else
                {
                    PartyChatCommand.SendClose();
                }
            }
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(isEnsembleButtonsDisabled);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Guitar, "##btnUpdateInstrument", Language.ensemble_update_instruments))
        {
            if (MidiBard.CurrentPlayback?.MidiFileConfig is { } config && !config.LeaderDistributed)
            {
                IPCHandles.UpdateMidiFileConfig(config);
            }

            if (!MidiBard.config.playOnMultipleDevices)
            {
                IPCHandles.UpdateInstrument(true);
            }
            else
            {
                PartyChatCommand.SendUpdateInstrument();
            }
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (!MidiBard.config.playOnMultipleDevices)
            {
                IPCHandles.UpdateInstrument(false);
            }
            else
            {
                PartyChatCommand.SendClose();
            }
        }
        ImGui.EndDisabled();

        //-------------------

        ImGui.SameLine();
        var muteButtonText = isOthersClientsMuted ? Language.ensemble_unmute_other_clients : Language.ensemble_mute_other_clients;
        var muteButtonIcon = isOthersClientsMuted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
        if (ImGuiUtil.IconButton(muteButtonIcon, muteButtonText, muteButtonText))
        {
            // IsSndMaster => 0 = ON
            // IsSndMaster => 1 = OFF
            IPCHandles.SetOption("IsSndMaster", isOthersClientsMuted ? 0 : 1, false);
            api.GameConfig.System.Set("IsSndMaster", 0);
            isOthersClientsMuted ^= true;
        }

        //-------------------

        ImGui.SameLine();
        var muteLyricsButtonText = MidiBard.config.playLyrics ? "Disable lyrics" : "Enable lyrics";
        var muteLyricsButtonIcon = MidiBard.config.playLyrics ? FontAwesomeIcon.Microphone : FontAwesomeIcon.MicrophoneSlash;
        if (ImGuiUtil.IconButton(muteLyricsButtonIcon, "##btnMuteLyrics", muteLyricsButtonText))
        {
            MidiBard.config.playLyrics = !MidiBard.config.playLyrics;
            IPCHandles.SyncAllSettings();
        }

        //-------------------

        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.WindowMinimize, "##btnWindowMinimize", Language.ensemble_minimize_other_clients))
        {
            IPCHandles.ShowWindow(Winapi.nCmdShow.SW_MINIMIZE);
        }

        //-------------------

        if (!MidiFileConfigManager.UsingDefaultPerformer && !(MidiBard.config.playOnMultipleDevices && !MidiBard.config.usingFileSharingServices))
        {
            ImGui.SameLine();
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                IPCHandles.ShowWindow(Winapi.nCmdShow.SW_RESTORE);
            }

            //-------------------

            ImGui.SameLine();
            ImGui.Dummy(ImGuiHelpers.ScaledVector2(10));

            ImGui.SameLine();
            ImGui.BeginDisabled(isEnsembleButtonsDisabled);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.FolderOpen, "##btnOpenConfigFolder", Language.ensemble_open_midi_config_directory))
            {
                if (MidiBard.CurrentPlayback == null) return;

                var fileInfo = MidiFileConfigManager.GetMidiConfigFileInfo(MidiBard.CurrentPlayback.FilePath);
                var configDirectoryFullName = fileInfo.Directory.FullName;
                // PluginLog.Debug(fileInfo.FullName);
                // PluginLog.Debug(MidiBard.CurrentPlayback.FilePath);
                // PluginLog.Debug(configDirectoryFullName);

                Util.Extensions.OpenFolder(configDirectoryFullName);
            }
            ImGui.EndDisabled();

            //-------------------

            ImGui.SameLine();
            ImGui.BeginDisabled(isEnsembleButtonsDisabled);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Edit, "##btnOpenConfigFile", Language.ensemble_open_midi_config_file))
            {
                if (MidiBard.CurrentPlayback == null) return;

                var fileInfo = MidiFileConfigManager.GetMidiConfigFileInfo(MidiBard.CurrentPlayback.FilePath);
                // PluginLog.Debug(fileInfo.FullName);
                // PluginLog.Debug(MidiBard.CurrentPlayback.FilePath);

                Util.Extensions.OpenFile(fileInfo.FullName);
            }
            ImGui.EndDisabled();

            //-------------------

            ImGui.SameLine();
            ImGui.BeginDisabled(isEnsembleButtonsDisabled);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, "##btnDeleteConfig", Language.ensemble_delete_and_reset_current_file_config))
            {
                if (MidiBard.CurrentPlayback != null)
                {
                    MidiFileConfigManager.GetMidiConfigFileInfo(MidiBard.CurrentPlayback.FilePath).Delete();
                    MidiBard.CurrentPlayback.MidiFileConfig = MidiFileConfigManager.GetMidiConfigFromTrack(MidiBard.CurrentPlayback.TrackInfos);
                    MidiBard.CurrentPlayback.MidiFileConfig = BardPlayback.ReloadMidiFileConfig(MidiBard.CurrentPlayback.MidiFileConfig);
                    IPCHandles.UpdateInstrument(false);
                }
            }
            ImGui.EndDisabled();

            //-------------------

            ImGui.SameLine();
            ImGui.Dummy(ImGuiHelpers.ScaledVector2(10));

            ImGui.SameLine();
            ImGui.BeginDisabled(isEnsembleButtonsDisabled);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.FileExport, "##btnExportDefaultPerformer", Language.ensemble_save_default_performers))
            {
                MidiFileConfigManager.ExportToDefaultPerformer();
            }
            ImGui.EndDisabled();

            ImGuiUtil.PopIconButtonSize();
        }

        //-------------------

        ImGui.BeginDisabled(isEnsembleButtonsDisabled);
        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Redo, "##btnResetDefaultPerformer", "Reset default performer"))
        {
            MidiFileConfigManager.ResetDefaultPerformer();
        }
        ImGui.EndDisabled();

        if (MidiFileConfigManager.UsingDefaultPerformer)
        {
            ImGui.SameLine();
            ImGui.Text("[Using Default Performer]");
        }
    }
}
