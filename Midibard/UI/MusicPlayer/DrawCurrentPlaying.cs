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
using System.Numerics;

using Dalamud.Bindings.ImGui;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    static bool playlistScrollToCurrentSong = false;
    private static void DrawCurrentPlaying()
    {
        if (MidiBard.CurrentPlayback != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MidiBard.config.themeColor * new Vector4(1, 1, 1, 1.3f));
            ImGui.TextUnformatted(MidiBard.CurrentPlayback.DisplayName);
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (ImGui.IsItemClicked())
            {
                playlistScrollToCurrentSong = true;
            }
            ImGuiUtil.ToolTip("Click to scroll to current playing song in playlist");
        }
        else
        {
            var totalDuration = PlaylistManager.CurrentContainer.TotalDuration;
            var durationString = totalDuration == TimeSpan.Zero
                ? ""
                : $"Duration: {Util.Extensions.GetDurationString(totalDuration)}";

            var totalSongs = PlaylistManager.FilePathList.Count;
            var tracksText = string.Format(Language.text_tracks_in_playlist, totalSongs);
            ImGui.TextUnformatted($"{tracksText}");

            ImGui.SameLine(ImGuiUtil.GetWindowContentRegionWidth() - ImGui.CalcTextSize(durationString).X + ImGui.GetCursorPosX());
            ImGui.TextUnformatted($"{durationString}");
        }
    }
}
