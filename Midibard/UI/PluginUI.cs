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
using Dalamud.Bindings.ImPlot;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Utility;

using MidiBard.Managers.Ipc;
using MidiBard.Util;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private bool showMainWindow = false;
    public bool MainWindowOpened => showMainWindow;
    private readonly ThemeManager themeManager = new ThemeManager(MidiBard.config.CurrentTheme);
    private readonly FileDialogService fileDialogService = new FileDialogService(MidiBard.config.PinnedImportFolders);
    private FileDialogManager fileDialogManager => fileDialogService.DialogManager;

    public PluginUI()
    {
        ImPlot.SetImGuiContext(ImGui.GetCurrentContext());
        var _context = ImPlot.CreateContext();
        ImPlot.SetCurrentContext(_context);
    }

    public void ToggleMainWindow()
    {
        if (showMainWindow)
            CloseMainWindow();
        else
            OpenMainWindow();
    }

    public void OpenMainWindow()
    {
        showMainWindow = true;
    }

    public void CloseMainWindow()
    {
        showMainWindow = false;
    }

    public unsafe void Draw()
    {
        fileDialogManager.Draw();

        // TODO: find a better way to apply the theme without interfering with other plugins
        themeManager.PushThemeStyles();

        if (showMainWindow)
        {
            DrawMainPluginWindow();
            DrawTrackVisualizerWindow();
            DrawCompensationEditWindow();
            DrawEnsembleWindow();
            DrawBMLWindow();
            LrcEditor.Instance.Draw();
            ImGuiUtil.IconButtonSize.Clear();
        }

        DrawSettigsWindow();
        themeManager.PopThemeStyles();

#if DEBUG
        DrawDebugWindow();
#endif
    }

    private void DrawMainPluginWindow()
    {
        var listeningForEvents = InputDeviceManager.IsListeningForEvents;
        // var ensemblePreparing = AgentMetronome.MetronomeBeatsElapsed < 0;
        try
        {
            var ensembleRunning = MidiBard.AgentMetronome.EnsembleModeRunning || Managers.EnsembleContinuity.IsActive;
            var playerName = api.Player.CharacterName;
            var playerWorld = api.Player.HomeWorld.ValueNullable?.Name.ToDalamudString().TextValue ?? "";
            var playerInfo = MidiBard.config.hidePlayerInformationFromUi ? "" : $"{playerName}@{playerWorld}";
            var title = $"midibard2-深海回响特供版 v{MidiBard.VersionString}";
            var name = title + " ###MIDIBARD";
            var windowFlags = MidiBard.config.miniPlayer ? ImGuiWindowFlags.NoDecoration : ImGuiWindowFlags.None;

            ImGui.SetNextWindowPos(new Vector2(100, 100), ImGuiCond.FirstUseEver);
            var width = System.Math.Max(ImGuiHelpers.GlobalScale * 357, ImGui.CalcTextSize(title).X + 104 * ImGuiHelpers.GlobalScale);
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, float.MaxValue));
            if (ImGui.Begin(name, ref showMainWindow, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | windowFlags))
            {
                if (!string.IsNullOrEmpty(playerInfo)) ImGui.TextUnformatted(playerInfo);
                var icon = MidiBard.config.miniPlayer ? FontAwesomeIcon.ExpandAlt : FontAwesomeIcon.CompressAlt;
                if (ImGuiUtil.AddHeaderIcon("headerIconMinimode", icon.ToIconString(), Language.icon_button_tooltip_mini_player))
                {
                    MidiBard.config.miniPlayer ^= true;
                }

                if (listeningForEvents)
                {
                    ImGuiUtil.DrawColoredBanner(Style.Colors.Violet, Language.text_listening_midi_device + InputDeviceManager.CurrentInputDevice.DeviceName());
                }

                DrawPlaylist();

                DrawCurrentPlaying();
                if (Managers.EnsembleContinuity.IsActive) ImGui.TextWrapped(Managers.EnsembleContinuity.Status);

                ImGui.Spacing();

                if (!MidiBard.config.miniPlayer)
                {
                    SliderProgressBar();
                }
                else
                {
                    ProgressBar();
                }

                ImGui.Spacing();

                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, ImGuiHelpers.ScaledVector2(4, 4));
                ImGuiUtil.PushIconButtonSize(ImGuiHelpers.ScaledVector2(45.5f, 25));
                {
                    DrawButtonPlayPause(disabled: ensembleRunning);
                    DrawButtonStop();
                    DrawButtonFastForward(disabled: ensembleRunning);
                    DrawButtonPlayMode(disabled: ensembleRunning);
                    DrawButtonShowSettingsWindow();
                    DrawButtonVisualization();
                    DrawButtonShowEnsembleWindow(disabled: !api.PartyList.IsPartyLeader());
                    if (!api.PartyList.IsPartyLeader())
                    {
                        ShowEnsembleWindow = false;
                    }
                }
                ImGuiUtil.PopIconButtonSize();
                ImGui.PopStyleVar();

                if (!MidiBard.config.miniPlayer)
                {
                    ImGui.Separator();
                    DrawTrackSelection();
                    DrawMusicControlPanel();
                    DrawFooter();
                    ImGui.Separator();
                    if (ImGuiUtil.IconButton(FontAwesomeIcon.ClipboardList, "##stageManagement", "自动点歌")) MidiBard.Stage?.Open();
                    ImGui.SameLine(); ImGui.TextUnformatted("自动点歌");
                    ImGui.SameLine();
                    if (ImGui.Button("联系作者##contactAuthor")) Util.Extensions.OpenUrl(BardStage.StageController.AuthorWebsite);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(BardStage.StageController.AuthorWebsite);
                }
            }
        }
        finally
        {
            ImGui.End();
        }
    }
}
