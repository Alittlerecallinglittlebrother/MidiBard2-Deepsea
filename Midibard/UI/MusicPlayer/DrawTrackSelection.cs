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
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

using MidiBard.Control.CharacterControl;
using MidiBard.Util;

using MidiBard2.Resources;

using static Dalamud.api;

namespace MidiBard;

public partial class PluginUI
{
    readonly uint[] toneColors = new uint[]
    {
        0xee_6666bb,
        0xee_bbbb66,
        0xee_66bb66,
        0xee_66bbbb,
        0xee_bb6666
    };

    readonly string[] toneStrings = new string[]
    {
        "I", "II", "III", "IV", "V",
    };

    private void DrawTrackSelection()
    {
        if (MidiBard.CurrentPlayback?.TrackInfos?.Any() == true)
        {
            if (ImGui.BeginChild("TrackTrunkSelection",
                    new Vector2(
                        ImGuiUtil.GetWindowContentRegionWidth() - 1,
                        Math.Min(MidiBard.CurrentPlayback.TrackInfos.Length, 8.5f) * ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y),
                    false, ImGuiWindowFlags.NoDecoration))
            {
                DrawTrackSelectionList();
                ImGui.EndChild();
            }

            ImGui.Separator();
        }
    }

    void DrawTrackSelectionList()
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, Style.Colors.Black);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2 * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemSpacing.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.6f, 0));

        if (MidiBard.PlayingGuitar && MidiBard.config.GuitarToneMode == GuitarToneMode.OverrideByTrack)
        {
            ImGui.Columns(2);
            ImGui.SetColumnWidth(0, ImGuiUtil.GetWindowContentRegionWidth() - 6 * (2 * ImGuiHelpers.GlobalScale) - 5 * (ImGui.GetFrameHeight() * 0.8f));
        }

        bool soloing = MidiBard.config.SoloedTrack is not null;
        int? soloingTrack = MidiBard.config.SoloedTrack;

        try
        {
            for (int i = 0; i < MidiBard.CurrentPlayback.TrackInfos.Length; i++)
            {
                DrawTrackLine(i, soloing, soloingTrack);
            }
        }
        catch (Exception e)
        {
            PluginLog.Error(e, "error when drawing tracks");
        }

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor();
    }

    void DrawTrackLine(int i, bool soloing, int? soloingTrack)
    {
        try
        {
            ImGui.PushID($"tracks{i}");
            ImGui.SetCursorPosX(0);

            var isEnabled = MidiBard.config.TrackStatus[i].Enabled;
            var isSolo = soloingTrack == i;
            var textColor = isEnabled ? ThemeManager.CurrentTheme.Text : ThemeManager.CurrentTheme.TextDisabled;
            var checkmarkColor = isEnabled ? ThemeManager.CurrentTheme.CheckMark : ThemeManager.CurrentTheme.TextDisabled;
            if (soloing) textColor = isSolo ? MidiBard.config.themeColor : ThemeManager.CurrentTheme.TextDisabled;

            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, checkmarkColor);

            if (ImGui.Checkbox("##trackCheckbox", ref MidiBard.config.TrackStatus[i].Enabled))
                JudgeSwitchInstrument();

            ImGui.SameLine(); ImGui.Dummy(Vector2.Zero); ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFrameHeightWithSpacing() * 3);
            ImGui.InputInt($"##TransposeByTrack", ref MidiBard.config.TrackStatus[i].Transpose, 12);

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                MidiBard.config.TrackStatus[i].Transpose = 0;

            ImGui.SameLine(); ImGui.Dummy(Vector2.Zero); ImGui.SameLine();
            ImGui.TextUnformatted((isSolo ? "[Solo]" : $"[{i + 1:00}]") + $" {MidiBard.CurrentPlayback.TrackInfos[i]}");

            if (ImGui.IsItemClicked())
            {
                MidiBard.config.TrackStatus[i].Enabled ^= true;
                JudgeSwitchInstrument();
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                HandleSoloTrackClick(i, isSolo);
            }

            ImGuiUtil.ToolTip(MidiBard.CurrentPlayback.TrackInfos[i].ToLongString() + "\n\n" + Language.window_tooltip_track_selection);

            if (MidiBard.PlayingGuitar && MidiBard.config.GuitarToneMode == GuitarToneMode.OverrideByTrack)
            {
                ImGui.NextColumn();
                for (int toneId = 0; toneId < 5; toneId++)
                {
                    if (toneId != 0) ImGui.SameLine();
                    DrawToneSelectButton(toneId, ref MidiBard.config.TrackStatus[i].Tone);
                }
                ImGui.NextColumn();
            }
        }
        catch (Exception e)
        {
            PluginLog.Error(e.ToString());
        }
        finally
        {
            ImGui.PopStyleColor(2);
            ImGui.PopID();
        }
    }

    void HandleSoloTrackClick(int index, bool wasSolo)
    {
        MidiBard.config.SoloedTrack = wasSolo ? null : index;

        if (!wasSolo)
            Chat.SendMessage("/echo [MidiBard 2] Track SOLO mode actived <se.9>");

        if (MidiBard.config.bmpTrackNames && !MidiBard.IsPlaying &&
            MidiBard.config.SoloedTrack is int solo &&
            MidiBard.config.TrackStatus[solo].Enabled &&
            MidiBard.CurrentPlayback.TrackInfos[solo].InstrumentIDFromTrackName is uint inst)
        {
            SwitchInstrument.SwitchToAsync(inst);
        }
    }

    bool DrawToneSelectButton(int toneID, ref int selected)
    {
        var buttonSize = new Vector2(ImGui.GetFrameHeight() * 0.8f, ImGui.GetFrameHeight());
        var toneColor = toneColors[toneID];
        var toneName = toneStrings[toneID];
        var isToneSelected = selected == toneID;
        var ret = false;
        if (isToneSelected)
            ImGui.PushStyleColor(ImGuiCol.Button, toneColor);

        ImGui.PushStyleColor(ImGuiCol.ButtonActive, toneColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, toneColor);
        if (ImGui.Button($"{toneName}##toneSwitchButton", buttonSize))
        {
            selected = toneID;
            ret = true;
        }
        ImGui.PopStyleColor(2);

        if (isToneSelected)
            ImGui.PopStyleColor();

        return ret;
    }

    private void JudgeSwitchInstrument()
    {
        if (MidiBard.config.bmpTrackNames && !MidiBard.IsPlaying)
        {
            var firstEnabledTrack = MidiBard.CurrentPlayback.TrackInfos.FirstOrDefault(trackInfo => trackInfo.IsEnabled);
            if (firstEnabledTrack?.InstrumentIDFromTrackName != null)
            {
                SwitchInstrument.SwitchToAsync((uint)firstEnabledTrack.InstrumentIDFromTrackName);
            }
            else
            {
                SwitchInstrument.SwitchToAsync(0);
            }
        }
    }
}
