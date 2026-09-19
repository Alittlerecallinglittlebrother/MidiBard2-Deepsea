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

using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

using MidiBard.Managers;

namespace MidiBard;

public partial class PluginUI
{
    private static bool InstrumentPicker(string label, ref uint instrumentId)
    {
        bool changed = false;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().FramePadding.Y));

        uint undefinedInstrumentTexture = 60042;

        var icon = instrumentId == 0
            ? TextureManager.Get(undefinedInstrumentTexture).GetWrapOrEmpty().Handle
            : MidiBard.Instruments[instrumentId].IconTextureWrap.GetWrapOrEmpty().Handle;

        var iconSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());
        ImGui.Image(icon, iconSize);

        if (ImGui.IsItemHovered())
            ImGuiUtil.ToolTip(MidiBard.Instruments[instrumentId].InstrumentString);

        ImGui.OpenPopupOnItemClick($"instrument{label}", ImGuiPopupFlags.MouseButtonLeft);

        if (ImGui.BeginPopup($"instrument{label}"))
        {
            HashSet<uint> InstrumentGroupBreaks = new() { 4, 9, 14, 19, 23 };

            for (uint i = 1; i < MidiBard.Instruments.Length; i++)
            {
                ImGui.Image(MidiBard.Instruments[i].IconTextureWrap.GetWrapOrEmpty().Handle, ImGuiHelpers.ScaledVector2(40, 40));

                if (ImGui.IsItemClicked())
                {
                    instrumentId = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }

                if (ImGui.IsItemHovered())
                    ImGuiUtil.ToolTip(MidiBard.Instruments[i].InstrumentString);

                if (!InstrumentGroupBreaks.Contains(i))
                    ImGui.SameLine();
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            instrumentId = 0;
            changed = true;
        }

        return changed;
    }
}
