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

using Melanchall.DryWetMidi.Interaction;

using MidiBard.Control.MidiControl;

namespace MidiBard;

public partial class PluginUI
{
    private static void ProgressBar()
    {
        MetricTimeSpan currentTime = new MetricTimeSpan(0);
        MetricTimeSpan duration = new MetricTimeSpan(0);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, FilePlayback.IsWaiting ? Style.Colors.White : MidiBard.config.themeColor);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, MidiBard.config.themeColorDark);

        if (MidiBard.CurrentPlayback == null)
        {
            // ImGui.SetNextItemWidth(-1);
            ImGui.ProgressBar(0, new Vector2(-1, 3));

            DrawTimeLabels(currentTime, duration);
            ImGui.PopStyleColor(2);
            return;
        }

        currentTime = MidiBard.CurrentPlayback.GetCurrentTime<MetricTimeSpan>();
        duration = MidiBard.CurrentPlayback.GetDuration<MetricTimeSpan>();
        var progress = MidiBard.CurrentPlayback.GetPlaybackProgress();
        ImGui.ProgressBar(progress, new Vector2(-1, 3));

        ImGui.PopStyleColor(2);

        DrawTimeLabels(currentTime, duration);

        if (MidiBard.AgentMetronome.EnsembleModeRunning)
        {
            DrawEnsembleLabel();
        }
        else
        {
            DrawInstrumentLabel();
        }

    }
}
