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

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private bool showTrackVisualizerWindow = false;
    private bool _resetPlotWindowPosition = false;
    private bool setNextLimit;
    // private readonly double timeWindow = 10;
    //private uint[] ChannelColorPalette = Enumerable.Range(0, 16).Select(i => ImGui.ColorConvertFloat4ToU32(HSVToRGB(i / 16f, 0.75f, 1))).ToArray();

    public void ToggleTrackVisualizerWindow()
    {
        if (showTrackVisualizerWindow)
            CloseTrackVisualizerWindow();
        else
            OpenTrackVisualizerWindow();
    }

    public void OpenTrackVisualizerWindow()
    {
        showTrackVisualizerWindow = true;
    }

    public void CloseTrackVisualizerWindow()
    {
        showTrackVisualizerWindow = false;
    }

    private void DrawTrackVisualizerWindow()
    {
        if (!showTrackVisualizerWindow) return;

        ImGui.PushStyleColor(ImGuiCol.TitleBg, Style.Components.FrameBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Style.Components.FrameBg);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, -Vector2.One);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.SetNextWindowSize(ImGuiHelpers.ScaledVector2(640, 480), ImGuiCond.FirstUseEver);

        if (_resetPlotWindowPosition)
        {
            ImGui.SetNextWindowPos(new Vector2(100), ImGuiCond.Always);
            ImGui.SetNextWindowSize(ImGuiHelpers.ScaledVector2(640, 480), ImGuiCond.Always);
            _resetPlotWindowPosition = false;
        }

        if (ImGui.Begin(Language.window_title_visualizor + "###midibardMidiPlot", ref showTrackVisualizerWindow, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.PopStyleVar();
            var icon = MidiBard.config.LockPlot ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
            if (ImGuiUtil.AddHeaderIcon("lockPlot", icon.ToIconString(), Language.icon_button_tooltip_visualizer_follow_playback_tooltip))
            {
                MidiBard.config.LockPlot ^= true;
            }

            DrawMidiPlot();
        }
        else
        {
            ImGui.PopStyleVar();
        }

        ImGui.End();
        ImGui.PopStyleColor(2);
    }
}
