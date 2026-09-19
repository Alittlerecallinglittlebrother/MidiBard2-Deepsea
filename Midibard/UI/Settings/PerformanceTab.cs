using System;
using System.Numerics;
using System.Text.RegularExpressions;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility;

using MidiBard.IPC;
using MidiBard.Util;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private static string[] GetToneModeToolTips()
    {
        string[] toneModeToolTips = [
             Language.tone_mode_tooltip_off,
             Language.tone_mode_tooltip_standard,
             Language.tone_mode_tooltip_simple,
             Language.tone_mode_tooltip_override_by_track,
             Language.tone_mode_tooltip_program_electric_guitar_mode,
        ];

        return toneModeToolTips;
    }

    private static string[] GetToneModeLabels()
    {
        string[] toneModeLabels = [
                Language.tone_mode_option_off,
                Language.tone_mode_option_standard,
                Language.tone_mode_option_simple,
                Language.tone_mode_option_override_by_track,
                Language.tone_mode_option_program_electric_guitar_mode,
            ];

        return toneModeLabels;
    }

    private static string[] GetAntiStackNoteLabels()
    {
        string[] antiStackNoteLabels = [
            Language.anti_stack_note_option_off,
            Language.anti_stack_note_option_keep_first_note,
            Language.anti_stack_note_option_keep_shortest_note,
            Language.anti_stack_note_option_keep_longest_note
        ];

        return antiStackNoteLabels;
    }

    private static string[] GetPostSongNameChatTargetLabels()
    {
        string[] postSongNameChatTargetLabels = {
                Language.chat_target_option_current,
                Language.chat_target_option_say,
                Language.chat_target_option_party
            };

        return postSongNameChatTargetLabels;
    }

    private void DrawPerformanceSettings()
    {
        DrawInstrumentNameReferenceWindow();

        ImGuiGroupPanel.BeginGroupPanel(Language.setting_group_label_performance_settings);

        if (ImGui.Checkbox(Language.setting_label_auto_switch_instrument_bmp, ref MidiBard.config.bmpTrackNames))
        {
            IPCHandles.SyncAllSettings();
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_auto_switch_transpose_instrument_bmp_trackname);

        ImGui.SameLine();
        // var btnNameReferencesize = ImGuiHelpers.GetButtonSize(btnNameReferenceText);
        // ImGui.SameLine(ImGui.GetWindowWidth() - 2 * ImGui.GetCursorPosX() - btnNameReferencesize.X);
        ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonInfoNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonInfoHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonInfoActive);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.InfoCircle, "btnInstrumentsNameReference", "Click to show instruments name reference"))
        {
            showInstrumentNameReferenceWindow ^= true;
        }
        ImGui.PopStyleColor(3);

        //-------------------

        ImGui.Checkbox(Language.setting_label_auto_switch_instrument_by_file_name, ref MidiBard.config.autoSwitchInstrumentBySongName);
        ImGuiUtil.ToolTip(Language.setting_tooltip_label_auto_switch_instrument_by_file_name);

        //-------------------

        ImGui.Checkbox(Language.setting_label_auto_transpose_by_file_name, ref MidiBard.config.autoTransposeBySongName);
        ImGuiUtil.ToolTip(Language.setting_tooltip_auto_transpose_by_file_name);

        //-------------------

        if (ImGui.Checkbox(Language.setting_label_auto_align_loaded_midi, ref MidiBard.config.AlignMidi))
        {
            IPCHandles.SyncAllSettings();
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_auto_align_loaded_midi);

        ImGui.SameLine();
        if (ImGuiUtil.ToggleShowHideButton("##btnUiShowAutoAlignMidi", Language.setting_label_show_hide_in_main_window, ref MidiBard.config.UiShowAutoAlignMidi))
        {
            IPCHandles.SyncAllSettings();
        }

        if (MidiBard.config.AlignMidi)
        {
            ImGui.Spacing();
            ImGui.Indent(ImGui.GetStyle().IndentSpacing * 2);
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputDouble($"Align start offset", ref MidiBard.config.AlignMidiStartOffset, 0.1f, 0.1f, $" {MidiBard.config.AlignMidiStartOffset:f2} s", ImGuiInputTextFlags.AutoSelectAll))
            {
                MidiBard.config.AlignMidiStartOffset = Math.Clamp(MidiBard.config.AlignMidiStartOffset, 0f, 10f);
                IPCHandles.SyncAllSettings();
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                MidiBard.config.AlignMidiStartOffset = 0;
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip("New song start offset, right click to reset");
            ImGui.Unindent(ImGui.GetStyle().IndentSpacing * 2);
        }

        //-------------------

        if (ImGui.Checkbox(Language.setting_label_auto_adapt_notes, ref MidiBard.config.AdaptNotesOOR))
        {
            IPCHandles.SyncAllSettings();
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_auto_adapt_notes);

        ImGui.SameLine();
        if (ImGuiUtil.ToggleShowHideButton("##btnUiShowAdaptNotesOOR", Language.setting_label_show_hide_in_main_window, ref MidiBard.config.UiShowAdaptNotesOOR))
        {
            IPCHandles.SyncAllSettings();
        }

        //-------------------

        ImGui.Text(Language.setting_label_anti_note_stack_loaded_midi);
        if (ImGuiUtil.EnumCombo("##comboAntiStackNote", ref MidiBard.config.AntiStackType, labelsOverride: GetAntiStackNoteLabels()))
        {
            IPCHandles.SyncAllSettings();
        }

        //-------------------

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted(Language.setting_label_tone_mode);
        if (ImGuiUtil.EnumCombo("##comboGuitarToneMode", ref MidiBard.config.GuitarToneMode, labelsOverride: GetToneModeLabels(), toolTips: GetToneModeToolTips()))
        {
            IPCHandles.SyncAllSettings();
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_tone_mode);

        ImGui.SameLine();
        if (ImGuiUtil.ToggleShowHideButton("##btnUiShowGuitarToneMode", Language.setting_label_show_hide_in_main_window, ref MidiBard.config.UiShowGuitarToneMode))
        {
            IPCHandles.SyncAllSettings();
        }

        //-------------------

        ImGui.TextUnformatted(Language.setting_label_set_play_speed);
        if (ImGui.InputFloat("##inputPlaySpeed", ref MidiBard.config.PlaySpeed, 0.1f, 0.5f, GetBpmString(), ImGuiInputTextFlags.AutoSelectAll))
        {
            SetSpeed();
        }
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            MidiBard.config.PlaySpeed = 1;
            SetSpeed();
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_set_speed);

        ImGui.SameLine();
        if (ImGuiUtil.ToggleShowHideButton("##btnUiShowPlaySpeed", Language.setting_label_show_hide_in_main_window, ref MidiBard.config.UiShowPlaySpeed))
        {
            IPCHandles.SyncAllSettings();
        }

        //-------------------

        // SameLine(ImGuiUtil.GetWindowContentRegionWidth() / 2f);
        // SetNextItemWidth(itemWidth);
        ImGui.TextUnformatted(Language.setting_label_global_transpose);
        if (ImGui.InputInt("##inputGlobalTranspose", ref MidiBard.config.TransposeGlobal, 12))
        {
            MidiBard.config.SetTransposeGlobal(MidiBard.config.TransposeGlobal);
            IPC.IPCHandles.GlobalTranspose(MidiBard.config.TransposeGlobal);
        }
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            MidiBard.config.SetTransposeGlobal(0);
            IPC.IPCHandles.GlobalTranspose(MidiBard.config.TransposeGlobal);
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_transpose_all);

        ImGui.SameLine();
        if (ImGuiUtil.ToggleShowHideButton("##btnUiShowTransposeGlobal", Language.setting_label_show_hide_in_main_window, ref MidiBard.config.UiShowTransposeGlobal))
        {
            IPCHandles.SyncAllSettings();
        }

        //-------------------

        // var itemWidth = ImGuiHelpers.GlobalScale * 100;
        // SetNextItemWidth(itemWidth);
        ImGui.TextUnformatted(Language.setting_label_delay_between_songs);
        if (ImGui.InputFloat("##inputSongDelay", ref MidiBard.config.SecondsBetweenTracks, 0.5f, 0.5f, $" {MidiBard.config.SecondsBetweenTracks:f2} s", ImGuiInputTextFlags.AutoSelectAll))
        {
            MidiBard.config.SecondsBetweenTracks = Math.Max(0, MidiBard.config.SecondsBetweenTracks);
            IPCHandles.SyncAllSettings();
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            MidiBard.config.SecondsBetweenTracks = 3;
            IPCHandles.SyncAllSettings();
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_song_delay);

        //-------------------

        ImGuiGroupPanel.EndGroupPanel();

        ImGui.Spacing();

        DrawPostSongOptions();

        ImGui.Spacing();
        ImGui.Spacing();
    }

    private void DrawInstrumentNameReferenceWindow()
    {
        if (!showInstrumentNameReferenceWindow) return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(250, 100) * ImGuiHelpers.GlobalScale, ImGuiHelpers.MainViewport.Size);
        if (ImGui.Begin("Track Name References For Auto-Switch Instruments", ref showInstrumentNameReferenceWindow))
        {
            if (ImGui.BeginTable("ins", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("##InstrumentImage", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Track Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var instrument in MidiBard.Instruments)
                {
                    if (instrument.Row.RowId == 0) continue;
                    ImGui.TableNextColumn();
                    // Image(instrument.IconTextureWrap.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetFrameHeight()));
                    ImGui.Image(instrument.IconTextureWrap.GetWrapOrEmpty().Handle, new Vector2(40, 40));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGuiUtil.TextCopyable(SanitizeIntrumentName(instrument.FFXIVDisplayName));
                    ImGuiUtil.ToolTip("Click to copy the name");
                }
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }

    private void DrawPostSongOptions()
    {
        if (ImGui.CollapsingHeader(Language.post_song_to_chat, ImGuiTreeNodeFlags.NoAutoOpenOnLog))
        {
            ImGui.Spacing();
            ImGui.Indent();
            // var available = ImGui.GetContentRegionAvail();
            // ImGui.SetNextItemWidth(available.X);

            if (ImGui.Checkbox(Language.auto_send_song_name_to_chat_on_play, ref MidiBard.config.autoPostSongName))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.ToolTip("Check this if you want to auto send song name to chat on play");

            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.TextUnformatted(Language.select_chat_to_send_song_name);
            if (ImGuiUtil.EnumCombo($"##comboPostSongNameChatTarget", ref MidiBard.config.SongNameChatTarget, labelsOverride: GetPostSongNameChatTargetLabels()))
            {
                IPCHandles.SyncAllSettings();
            }

            // --------- Capture Regex ----------

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted(Language.song_name_regex_and_output_format);
            ImGui.Spacing();

            ImGui.BeginGroup();
            ImGui.TextUnformatted(Language.capture_regex);
            ImGui.SetNextItemWidth(250f);
            if (ImGui.InputTextWithHint("##PostSongNameChatCaptureRegex", "", ref MidiBard.config.postSongNameCaptureRegex, 1000))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.HelpMarker("""
            Use this to capture information from file name to post into chat

                Example file naming pattern:
                    Artist - Song Name.mid
                    Taylor Swift - Shake It Off.mid
                Regex:
                    ^(.*?) - (.*?)

                This captures 2 groups:
                $1 => artist name (Taylor Swift)
                $2 => song name (Shake It Off)

            All files must follow same pattern to it work, if you have variations you need add these variations to the expression to it work properly
                Example:
                    Taylor Swift - Shake It Off (trio).mid
                    Taylor Swift - Shake It Off (duo).mid
                    Taylor Swift - Shake It Off (quartet).mid

                Regex need to be adjusted to:
                    ^(.*?) - (.*?)(?:\(.*)?$

            This will capture artist and song name and ignore anything after first parentesis (

            The easiest way to build this expression is to ask an AI, send your song naming pattern with examples and ask it to generate a regex to capture the parts you want.
            """);
            ImGui.EndGroup();

            ImGui.SameLine();

            // --------- Output Format ----------

            ImGui.BeginGroup();
            ImGui.TextUnformatted(Language.output_format);
            ImGui.SetNextItemWidth(250f);
            if (ImGui.InputTextWithHint("##PostSongNameChatOutputFormat", "♪ Artist: $1 - Song: $2 ♪", ref MidiBard.config.postSongNameCaptureOutputFormat, 1000))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.HelpMarker("""
            Define the output format for the captured information from the file name.
            Captured parts are represented by $1 $2 $3 etc and this is where song info will be placed and you may insert any text between it

            Examples:
                Format:  $1 - $2
                Display: Artist - Song Name

                Format:  Now playing: ♪ $1 - $2 ♪
                Display: Now playing: ♪ Artist - Song Name ♪

                Format:  $2
                Display: Song Name
            """);
            ImGui.EndGroup();

            ImGui.Spacing();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.Asterisk, "##CopyRegexExample", "Copy regex example for pattern: Arist - Song Name"))
            {
                ImGui.SetClipboardText("^(.*?) - (.*?)");
                ImGuiUtil.AddNotification(NotificationType.Info, "Copied to clipboard");
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.Comment, "CopyAIPromptExample", "Copy AI prompt example"))
            {
                ImGui.SetClipboardText("""
                Provide only the regular expression (no explanation) that captures the artist and the song name into separate groups, following this pattern:
                    Artist - Song Name
                    Examples:
                    Queen - Bohemian Rhapsody
                    Nirvana - Smells Like Teen Spirit
                    Adele - Rolling in the Deep
                    Michael Jackson - Billie Jean
                    Coldplay - Viva La Vida
                In some cases, the song name may be followed by extra information in parentheses (e.g., "(solo)", "(quartet)", "(octet) 2008"). This extra part should be ignored completely—it must not be included in the song name group.

                Examples to ignore the extra info:
                    The Beatles - Let It Be (solo)
                    Radiohead - Creep (quartet)
                    Beyoncé - Halo (octet) 2008
                """);
                ImGuiUtil.AddNotification(NotificationType.Info, "Copied to clipboard");
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.Bookmark, "##RegexTestWebsite", "Open regex test website"))
            {
                Util.Extensions.OpenUrl("https://regex101.com/");
            }

            ImGui.Spacing();

            //-------------------

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.TextUnformatted(Language.sanitize_song_name);
            ImGui.Spacing();

            // --------- Find ----------
            ImGui.BeginGroup();
            ImGui.TextUnformatted(Language.find);
            ImGui.SetNextItemWidth(250f);
            if (ImGui.InputTextWithHint("##postSongNameFindRegex", "", ref MidiBard.config.postSongNameFindRegex, 1000))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.HelpMarker("""
            Enter expression to replace unwanted characters

            Example file name:
                Taylor_Swift - Shake_It_Off

            Find all underscore:
                _
            """);
            ImGui.EndGroup();

            ImGui.SameLine();

            // --------- Replace By ----------
            ImGui.BeginGroup();
            ImGui.TextUnformatted(Language.replace_by);
            ImGui.SetNextItemWidth(250f);
            if (ImGui.InputTextWithHint("##postSongNameReplacement", "", ref MidiBard.config.postSongNameReplacement, 1000))
            {
                IPCHandles.SyncAllSettings();
            }
            ImGuiUtil.HelpMarker("""
            Example:
                Replace all found characters by another one like blank space

            Result in:
                Taylor Swift - Shake It Off
            """);
            ImGui.EndGroup();

            ImGui.Spacing();

            ImGui.Unindent();
        }
    }

    private static string SanitizeIntrumentName(string input)
    {
        return Regex.Replace(input, "[^a-zA-Z]", "");
    }
}
