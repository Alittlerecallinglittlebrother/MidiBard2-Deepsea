using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private bool showSettingsWindow = false;
    public bool SettingsWindowOpened => showSettingsWindow;
    private bool showCompensationEditWindow = false;
    private bool showInstrumentNameReferenceWindow = false;

    public void ToggleSettingsWindow()
    {
        if (showSettingsWindow)
            CloseSettingsWindow();
        else
            OpenSettingsWindow();
    }

    public void OpenSettingsWindow()
    {
        showSettingsWindow = true;
    }

    public void CloseSettingsWindow()
    {
        showSettingsWindow = false;
    }

    private void DrawSettigsWindow()
    {
        if (!showSettingsWindow) return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(350, 100) * ImGuiHelpers.GlobalScale, ImGuiHelpers.MainViewport.Size);
        ImGui.Begin("MidiBard Settings", ref showSettingsWindow);

        if (ImGui.BeginTabBar("ConfigTabs"))
        {
            if (ImGui.BeginTabItem($"{Language.setting_group_label_general_settings}##GeneralSettingsTab"))
            {
                DrawGeneralSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"{Language.setting_group_label_performance_settings}##PerformanceSettingsTab"))
            {
                DrawPerformanceSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"{Language.setting_group_label_ensemble_settings}##EnsembleSettingsTab"))
            {
                DrawEnsembleSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();


    }



}
