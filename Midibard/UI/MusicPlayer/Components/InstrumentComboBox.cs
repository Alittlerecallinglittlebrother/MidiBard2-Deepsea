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

using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private static void InstrumentComboBox()
    {
        UIcurrentInstrument = MidiBard.CurrentInstrument;
        if (MidiBard.PlayingGuitar)
        {
            UIcurrentInstrument = (uint)(MidiBard.AgentPerformance.CurrentGroupTone + MidiBard.guitarGroup[0]);
        }

        if (ImGui.BeginCombo(Language.setting_label_select_instrument, MidiBard.InstrumentStrings[UIcurrentInstrument], ImGuiComboFlags.HeightLarge))
        {
            ImGui.GetWindowDrawList().ChannelsSplit(2);
            for (uint i = 0; i < MidiBard.Instruments.Length; i++)
            {
                var instrument = MidiBard.Instruments[i];
                ImGui.GetWindowDrawList().ChannelsSetCurrent(1);
                ImGui.Image(instrument.IconTextureWrap.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetTextLineHeightWithSpacing()));

                ImGui.SameLine();
                ImGui.GetWindowDrawList().ChannelsSetCurrent(0);
                ImGui.AlignTextToFramePadding();

                if (ImGui.Selectable($"{instrument.InstrumentString}##{i}", UIcurrentInstrument == i, ImGuiSelectableFlags.SpanAllColumns))
                {
                    UIcurrentInstrument = i;
                    SwitchInstrument.SwitchToContinue(i);
                }
            }

            ImGui.GetWindowDrawList().ChannelsMerge();
            ImGui.EndCombo();
        }

        // if (ImGui.Combo("Instrument".Localize(), ref UIcurrentInstrument, MidiBard.InstrumentStrings,
        //        MidiBard.InstrumentStrings.Length, 20))
        // {
        //    SwitchInstrument.SwitchToContinue((uint)UIcurrentInstrument);
        // }

        ImGuiUtil.ToolTip(Language.setting_tooltip_select_instrument);

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            SwitchInstrument.SwitchToContinue(0);
            MidiPlayerControl.Pause();
        }
    }
}

