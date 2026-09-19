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

using Dalamud.Bindings.ImGui;
using Dalamud.Utility;

using Melanchall.DryWetMidi.Interaction;

using MidiBard.Control.MidiControl;
using MidiBard.Managers;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private static void SliderProgressBar()
    {
        MetricTimeSpan currentTime = new MetricTimeSpan(0);
        MetricTimeSpan duration = new MetricTimeSpan(0);

        if (MidiBard.CurrentPlayback == null)
        {
            float zero = 0;

            InstrumentPickerSolo();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.SliderFloat("##SetSliderProgressBar", ref zero, 0, 1, "0:00", ImGuiSliderFlags.NoInput);
            ImGuiUtil.ToolTip(Language.setting_tooltip_set_progress);

            DrawTimeLabels(currentTime, duration);
            return;
        }

        currentTime = MidiBard.CurrentPlayback.GetCurrentTime<MetricTimeSpan>();
        duration = MidiBard.CurrentPlayback.GetDuration<MetricTimeSpan>();

        float progress = Util.Extensions.SafeDivideMetricTimeSpan(currentTime, duration);

        InstrumentPickerSolo();
        ImGui.SameLine();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##SliderProgressBar", ref progress, 0, 1,
                $"{(currentTime.Hours != 0 ? currentTime.Hours + ":" : "")}{currentTime.Minutes:00}:{currentTime.Seconds:00}",
                ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoRoundToFormat))
        {
            var newTime = duration.Multiply(progress);
            MidiPlayerControl.SetTime(newTime);
            IPC.IPCHandles.SetPlaybackTime((MetricTimeSpan)newTime);
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            MidiPlayerControl.SetTime(duration.Multiply(0));
            IPC.IPCHandles.SetPlaybackTime(TimeSpan.Zero);
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_set_progress);

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

    private static void DrawTimeLabels(MetricTimeSpan current, MetricTimeSpan total)
    {
        ImGui.TextUnformatted($"{current.Hours}:{current.Minutes:00}:{current.Seconds:00}");

        string durationText = $"{total.Hours}:{total.Minutes:00}:{total.Seconds:00}";
        ImGui.SameLine(ImGuiUtil.GetWindowContentRegionWidth() - ImGui.CalcTextSize(durationText).X + ImGui.GetCursorPosX());
        ImGui.TextUnformatted(durationText);
    }

    private static void DrawInstrumentLabel()
    {
        try
        {
            var isAuto = MidiBard.PlayingGuitar && MidiBard.config.GuitarToneMode != GuitarToneMode.OverrideByTrack;
            var instrumentId = isAuto
                ? (uint)(24 + MidiBard.AgentPerformance.CurrentGroupTone)
                : MidiBard.CurrentInstrument;

            if (instrumentId == 0)
                return;

            var instrumentName = MidiBard.InstrumentSheet.GetRow(instrumentId).Instrument.ToDalamudString().TextValue;
            if (isAuto)
                instrumentName = instrumentName.Split(':', '：').First() + ": Auto";

            ImGui.SameLine((ImGuiUtil.GetWindowContentRegionWidth() - ImGui.CalcTextSize(instrumentName).X) / 2);
            ImGui.TextUnformatted(instrumentName);
        }
        catch
        {
            // ignored
        }
    }

    private static void DrawEnsembleLabel()
    {
        var ensembleText = $"{Language.text_ensemble_mode_running} {EnsembleManager.EnsembleTimer.Elapsed:mm\\:ss\\:ff}";
        ImGui.SameLine((ImGuiUtil.GetWindowContentRegionWidth() - ImGui.CalcTextSize(ensembleText).X) / 2);
        ImGui.TextColored(MidiBard.config.themeColor, ensembleText);
    }
}
