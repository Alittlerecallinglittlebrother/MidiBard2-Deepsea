using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

using MidiBard.IPC;
using MidiBard.Util;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private readonly string[] uilangStrings = Enum.GetNames<MidiBard.CultureCode>();

    private static string[] GetThemeLabels()
    {
        string[] themeLabels = [
                Language.theme_default,
                Language.theme_dark,
                Language.theme_modern_dark,
                Language.theme_light,
                Language.theme_ocean_fishing,
                Language.theme_deepblue,
                Language.theme_catnip,
                Language.theme_chocobo,
                Language.theme_dracula,
                Language.theme_neon,
                Language.theme_purple,
                Language.theme_wine,
                Language.theme_barbie_pink,
                Language.theme_cotton_candy,
                Language.theme_tropical,
                Language.theme_sunset,
                Language.theme_orange
            ];

        return themeLabels;
    }

    private void DrawGeneralSettings()
    {
        ImGuiGroupPanel.BeginGroupPanel(Language.setting_group_label_general_settings);
        {
            if (ImGui.Checkbox(Language.setting_label_auto_open_on_startup, ref MidiBard.config.AutoOpenOnStartup))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip(Language.setting_label_auto_open_on_startup);

            //-------------------

            if (ImGui.Checkbox(Language.setting_label_auto_open_when_performing, ref MidiBard.config.AutoOpenPlayerWhenPerforming))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip(Language.setting_label_auto_open_when_performing);

            if (ImGui.Checkbox(Language.setting_label_auto_close_when_performing, ref MidiBard.config.AutoClosePlayerWhenPerforming))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip(Language.setting_label_auto_close_when_performing);

            //-------------------

            if (ImGui.Checkbox(Language.setting_label_show_now_playing_info, ref MidiBard.config.showNowPlayingInfo))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip(Language.setting_label_show_now_playing_info);

            //-------------------

            if (ImGui.Checkbox(Language.setting_label_hide_player_information_from_ui, ref MidiBard.config.hidePlayerInformationFromUi))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip(Language.setting_label_hide_player_information_from_ui);

            //-------------------

            if (ImGui.Checkbox(Language.w32_file_dialog, ref MidiBard.config.useLegacyFileDialog))
            {
                IPCHandles.SyncAllSettings();
            }

            //-------------------

            //Checkbox(Low_latency_mode, ref MidiBard.config.LowLatencyMode);
            //ImGuiUtil.ToolTip(low_latency_mode_tooltip);

            //ImGui.Checkbox(checkbox_auto_restart_listening, ref MidiBard.config.autoRestoreListening);
            //ImGuiUtil.ToolTip(checkbox_auto_restart_listening_tooltip);

            //ImGui.SameLine(ImGuiUtil.GetWindowContentRegionWidth() / 2);
            //ImGui.Checkbox("Auto listening new device".Localize(), ref MidiBard.config.autoStartNewListening);
            //ImGuiUtil.ToolTip("Auto start listening new midi input device when idle.".Localize());
            //ImGuiUtil.ColorPickerButton(1000, label_theme_color, ref MidiBard.config.themeColor,
            //    ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.AlphaBar);
            //if (ImGui.ColorEdit4("Theme color".Localize(), ref MidiBard.config.themeColor,
            //    ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoInputs))
            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.TextUnformatted(Language.setting_label_theme_color);
            ImGui.Spacing();
            ImGui.ColorEdit4("##settingLabelThemeColor", ref MidiBard.config.themeColor,
                ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.AlphaBar);

            ImGui.SameLine();
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Undo, "##btnResetUIColor", "Reset"))
            {
                MidiBard.config.themeColor = Style.Colors.Lavender;
                IPCHandles.SyncAllSettings();
            }

            //-------------------

            ImGui.Spacing();
            ImGui.TextUnformatted(Language.setting_label_played_song_highlight_color);
            ImGui.Spacing();
            ImGui.ColorEdit4("##settingLabelPlayedSongHighlightColor", ref MidiBard.config.playedSongColor, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoLabel);
            ImGui.SameLine();
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Undo, "##btnResetSongHighlightColor", "Reset"))
            {
                MidiBard.config.playedSongColor = Style.Colors.Cyan;
                IPCHandles.SyncAllSettings();
            }

            //-------------------

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted(Language.setting_label_theme);
            if (ImGuiUtil.EnumCombo($"##comboThemeVariantType", ref MidiBard.config.CurrentTheme, labelsOverride: GetThemeLabels()))
            {
                ThemeManager.SetTheme(MidiBard.config.CurrentTheme);
                IPCHandles.SyncAllSettings();
            }

            //-------------------

            ImGui.Spacing();
            ImGui.TextUnformatted(Language.setting_label_select_ui_language);
            if (ImGui.Combo($"##settingUiLang", ref MidiBard.config.uiLang, uilangStrings, uilangStrings.Length))
            {
                MidiBard.ConfigureLanguage(MidiBard.GetCultureCodeString((MidiBard.CultureCode)MidiBard.config.uiLang));
            }

            //-------------------

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.Spacing();

            if (ImGui.Button(Language.open_plugin_folder))
            {
                Util.Extensions.OpenFolder(api.PluginInterface.ConfigDirectory.FullName);
            }

            ImGui.Spacing();
            ImGui.Spacing();
        }

        ImGuiGroupPanel.EndGroupPanel();

        ImGui.Spacing();
        ImGui.Spacing();

        DrawPinnedImportFoldersSettings();
    }

    private void DrawPinnedImportFoldersSettings()
    {
        if (ImGui.CollapsingHeader(Language.favorite_import_folders, ImGuiTreeNodeFlags.NoAutoOpenOnLog))
        {
            ImGui.Indent();
            ImGui.Spacing();
            ImGui.Spacing();

            if (ImGui.Button(Language.add_folder))
            {
                AddCustomPinnedFolderImGui();
            }
            ImGuiUtil.HelpMarker("Add favorite folders to be displayed in the import folders and files dialog (Drag to reorder)");

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.BeginTable("##PinnedImportFoldersTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX |
            ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Folder", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Options", ImGuiTableColumnFlags.WidthFixed);

                for (int i = 0; i < MidiBard.config.PinnedImportFolders.Count; i++)
                {
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted($"{i + 1:000}");

                    ImGui.TableNextColumn();
                    ImGui.Selectable($"{MidiBard.config.PinnedImportFolders[i]}");

                    if (ImGui.BeginDragDropSource())
                    {
                        unsafe
                        {
                            ImGui.SetDragDropPayload("DND_PINNED_IMPORT_FOLDERS", new ReadOnlySpan<byte>(&i, sizeof(int)), ImGuiCond.None);
                            ImGui.Button($"({i + 1}) {MidiBard.config.PinnedImportFolders[i]}");
                        }

                        // PluginLog.Warning($"Drag start [{i}]: {MidiBard.config.PinnedImportFolders[i]}");
                        ImGui.EndDragDropSource();
                    }

                    ImGui.PushStyleColor(ImGuiCol.DragDropTarget, Style.Components.DragDropTarget);
                    if (ImGui.BeginDragDropTarget())
                    {
                        ImGuiPayloadPtr dragDropPayload = ImGui.AcceptDragDropPayload("DND_PINNED_IMPORT_FOLDERS");

                        bool isDropping = false;
                        unsafe
                        {
                            isDropping = !dragDropPayload.IsNull;
                        }

                        if (isDropping && dragDropPayload.IsDelivery())
                        {
                            unsafe
                            {
                                int originalIndex = *(int*)dragDropPayload.Data;

                                int offset = i - originalIndex;
                                if (offset != 0 && originalIndex + offset >= 0)
                                {
                                    int targetIndex = originalIndex + offset;
                                    // PluginLog.Warning($"Drag end [{i}]: [{originalIndex}, {targetIndex}] {offset}");
                                    MidiBard.config.PinnedImportFolders.MoveItemToIndex(originalIndex, targetIndex);
                                    MidiBard.SaveConfig();
                                    IPCHandles.SyncAllSettings();
                                    fileDialogService.OverwriteCustomPinnedFolders(MidiBard.config.PinnedImportFolders);
                                }
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    if (ImGuiUtil.IconButton(FontAwesomeIcon.FolderOpen, $"##OpenPinnedFolder_{i}", "Open"))
                    {
                        Util.Extensions.OpenFolder(MidiBard.config.PinnedImportFolders[i]);
                    }

                    ImGui.SameLine();

                    if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, $"##RemovePinnedFolder_{i}", "Remove"))
                    {
                        MidiBard.config.PinnedImportFolders.SafeRemoveAt(i);
                        fileDialogService.OverwriteCustomPinnedFolders(MidiBard.config.PinnedImportFolders);
                        MidiBard.SaveConfig();
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                ImGui.Unindent();
            }
        }
    }

    private void AddCustomPinnedFolderImGui()
    {
        fileDialogManager.OpenFolderDialog("Select pinned folder", (result, filePath) =>
        {
            if (result)
            {
                MidiBard.config.PinnedImportFolders.Add(filePath);
                MidiBard.SaveConfig();
                IPCHandles.SyncAllSettings();
                fileDialogService.OverwriteCustomPinnedFolders(MidiBard.config.PinnedImportFolders);
            }
        }, MidiBard.config.lastOpenedFolderPath);
    }
}
