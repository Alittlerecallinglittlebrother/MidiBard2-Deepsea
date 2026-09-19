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
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;

using MidiBard.Control.MidiControl;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private int songTargetIndexInputValue = 1;

    private void DrawPlaylist()
    {
        if (MidiBard.config.UseStandalonePlaylistWindow)
        {
            ImGui.SetNextWindowSize(new(ImGui.GetWindowSize().Y), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(ImGui.GetWindowPos() - new Vector2(2, 0), ImGuiCond.FirstUseEver, new Vector2(1, 0));
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Style.Components.WindowBg);
            ImGui.PushStyleColor(ImGuiCol.TitleBg, Style.Components.WindowBg);
            /*if (ImGui.Begin(
                    Language.window_title_standalone_playlist +
                    $" ({PlaylistManager.FilePathList.Count})" +
                    (PlaylistManager.CurrentContainer.TotalDuration > TimeSpan.Zero ? $" Duration: {Extensions.GetDurationString(PlaylistManager.CurrentContainer.TotalDuration)}" : "") +
                    $"###MidibardPlaylist",
                    ref MidiBard.config.UseStandalonePlaylistWindow, ImGuiWindowFlags.NoDocking))*/
            if (ImGui.Begin(
                    Language.window_title_standalone_playlist +
                    $" ({PlaylistManager.FilePathList?.Count ?? 0})" +
                    (PlaylistManager.CurrentContainer?.TotalDuration > TimeSpan.Zero
                        ? $" Duration: {Extensions.GetDurationString(PlaylistManager.CurrentContainer.TotalDuration)}"
                        : "") +
                    $"###MidibardPlaylist",
                    ref MidiBard.config.UseStandalonePlaylistWindow, ImGuiWindowFlags.NoDocking))
            {
                DrawContent();
            }

            ImGui.PopStyleColor(2);
            ImGui.End();
        }
        else
        {
            if (!MidiBard.config.miniPlayer)
            {
                DrawContent();
                ImGui.Spacing();
            }
        }

        void DrawContent()
        {
            DrawPlaylistMenu();

            if (!PlaylistManager.FilePathList.Any())
            {
                if (ImGui.Button(Language.text_playlist_is_empty, new Vector2(-1, ImGui.GetFrameHeight())))
                {
                    RunImportFileTask();
                }
            }
            else
            {
                DrawPlaylistTable();
            }
        }
    }

    private void DrawPlaylistTable()
    {
        bool beginChild;
        if (MidiBard.config.UseStandalonePlaylistWindow)
        {
            beginChild = ImGui.BeginChild("playlistchild");
        }
        else
        {
            var minSongsToDisplay = 15;
            var maxSogsToDisplay = PlaylistManager.FilePathList.Count; // +2 for table header row
            beginChild = ImGui.BeginChild("playlistchild",
                new Vector2(x: -1, y: ImGui.GetTextLineHeightWithSpacing() * Math.Min(minSongsToDisplay, maxSogsToDisplay)));
        }

        if (beginChild)
        {
            var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX |
                ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV;

            if (ImGui.BeginTable("##PlaylistTable", 4, tableFlags, ImGui.GetWindowSize()))
            {
                ImGui.TableSetupColumn("##songNumberColumn", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##deleteColumn", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort);
                ImGui.TableSetupColumn("##durationColumn", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##fileNameColumn", ImGuiTableColumnFlags.WidthStretch);
                // ImGui.TableHeadersRow();
                // ↑ ↓

                // table header sort
                // ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                // ImGui.TableNextColumn();
                // ImGui.TableNextColumn();

                // ImGui.TableNextColumn();
                // var songDurationSortIcon = songDurationSortDirectionDesc ? FontAwesomeIcon.SortAmountUpAlt : FontAwesomeIcon.SortAmountDownAlt;
                // if (ImGuiUtil.IconButton(songDurationSortIcon, "##playlistTableDurationSort"))
                // {
                //     PlaylistManager.SortBy((song) => song.SongLength, descending: !songDurationSortDirectionDesc);
                //     songDurationSortDirectionDesc = !songDurationSortDirectionDesc;
                //     RefreshPlaylistSearchResult();
                // }
                // ImGuiUtil.ToolTip("Sort");

                // ImGui.TableNextColumn();
                // var songNameSortIcon = songNameSortDirectionDesc ? FontAwesomeIcon.SortAmountUpAlt : FontAwesomeIcon.SortAmountDownAlt;
                // if (ImGuiUtil.IconButton(songNameSortIcon, "##playlistTableFileNameSort"))
                // {
                //     PlaylistManager.SortBy((song) => song.FileName, descending: !songNameSortDirectionDesc);
                //     songNameSortDirectionDesc = !songNameSortDirectionDesc;
                //     RefreshPlaylistSearchResult();
                // }
                // ImGuiUtil.ToolTip("Sort");

                var isFiltered = MidiBard.config.enableSearching &&
                  (!string.IsNullOrEmpty(PlaylistSearchString) ||
                  MidiBard.config.SearchFilterPlayedOption != FilterPlayedSongOptions.ShowAll);

                var itemCount = isFiltered ? searchedPlaylistIndexs.Count : PlaylistManager.FilePathList.Count;

                bool lockMultipleDevicesOptions = MidiBard.config.playOnMultipleDevices
                                            && MidiBard.config.useChatPlaylistSync
                                            && api.PartyList.IsInParty()
                                            && !api.PartyList.IsPartyLeader();

                ImGuiListClipperPtr clipper;
                unsafe
                {
                    clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());
                }

                clipper.Begin(itemCount);

                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        if (i >= itemCount) break;

                        int realIndex = isFiltered ? searchedPlaylistIndexs[i] : i;

                        if (realIndex >= PlaylistManager.FilePathList.Count) continue;

                        DrawPlayListEntry(realIndex, lockMultipleDevicesOptions);
                    }
                }

                if (playlistScrollToCurrentSong)
                {
                    playlistScrollToCurrentSong = false;
                    int targetIndex = isFiltered
                        ? searchedPlaylistIndexs.FindIndex(i1 => i1 == PlaylistManager.CurrentSongIndex)
                        : PlaylistManager.CurrentSongIndex;

                    if (targetIndex >= 0 && targetIndex < itemCount)
                    {
                        var scrollY = targetIndex * clipper.ItemsHeight;
                        ImGui.SetScrollY(scrollY);
                    }
                }

                clipper.End();
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
    }

    private void DrawPlayListEntry(int i, bool lockMultipleDevicesOptions)
    {
        var isInvalidSongIndex = i < 0 || i >= PlaylistManager.FilePathList.Count;
        if (isInvalidSongIndex) return;

        var entry = PlaylistManager.FilePathList[i];

        ImGui.PushID(i);

        ImGui.TableNextRow();
        // ImGui.TableSetColumnIndex(0);
        ImGui.TableNextColumn();
        DrawPlaylistItemSelectable(i);

        ImGui.TableNextColumn();
        ImGui.BeginDisabled(lockMultipleDevicesOptions);
        DrawPlaylistDeleteButton();
        ImGui.EndDisabled();

        ImGui.TableNextColumn();
        DrawPlaylistTrackDuration();

        ImGui.TableNextColumn();
        DrawPlaylistTrackName();

        ImGui.PopID();

        void DrawPlaylistItemSelectable(int i)
        {
            if (ImGui.Selectable($"{i + 1:000}##playlistItem", PlaylistManager.CurrentSongIndex == i,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowItemOverlap))
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (!MidiBard.AgentMetronome.EnsembleModeRunning)
                    {
                        if (MidiBard.config.playOnMultipleDevices && api.PartyList.Length > 1)
                        {
                            PartyChatCommand.SendSwitchTo(i);
                        }
                        else
                        {
                            MidiPlayerControl.StopLrc();
                            PlaylistManager.LoadPlayback(i);
                        }
                    }
                }
            }

            DrawPlaylistContextMenu(i);

            // Drag & Drop
            ImGui.BeginDisabled(lockMultipleDevicesOptions);
            if (ImGui.BeginDragDropSource())
            {
                unsafe
                {
                    ImGui.SetDragDropPayload("DND_PLAYLIST_ITEM", new ReadOnlySpan<byte>(&i, sizeof(int)), ImGuiCond.None);
                    ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonInfoActive);
                    ImGui.Button($"({i + 1}) {PlaylistManager.FilePathList[i].FileName}");
                    ImGui.PopStyleColor();
                }
                // PluginLog.Debug($"Drag start [{i}]: {PlaylistManager.FilePathList[i].FileName}");
                ImGui.EndDragDropSource();
            }

            ImGui.PushStyleColor(ImGuiCol.DragDropTarget, Style.Components.DragDropTarget);
            if (ImGui.BeginDragDropTarget())
            {
                ImGuiPayloadPtr dragDropPayload = ImGui.AcceptDragDropPayload("DND_PLAYLIST_ITEM");

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
                            PlaylistManager.MoveSongToIndexSync(originalIndex, targetIndex);
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }
            ImGui.PopStyleColor();
            ImGui.EndDisabled();
        }

        void DrawPlaylistContextMenu(int i)
        {
            // if (IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            // if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            // {
            //     ImGui.OpenPopup("##playlistRightClickMenu");
            // }
            ImGui.OpenPopupOnItemClick($"##playlistRightClickMenu", ImGuiPopupFlags.MouseButtonRight);
            ImGui.PushStyleColor(ImGuiCol.Border, Style.Components.TooltipBorderColor);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
            if (ImGui.BeginPopup($"##playlistRightClickMenu"))
            {
                var song = PlaylistManager.FilePathList[i];
                var isFilePlayed = song.IsFilePlayed;

                // menu title
                ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonInfoNormal);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonInfoNormal);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonInfoNormal);
                float fullWidth = ImGui.GetContentRegionAvail().X;
                ImGui.Button($"({i + 1}) {PlaylistManager.FilePathList[i].FileName}", new Vector2(fullWidth, 0));
                ImGui.PopStyleColor(3);

                // close btn
                // ImGui.SameLine();
                // ImGui.Dummy(Vector2.Zero);
                // ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10);
                // ImGui.SetItemAllowOverlap();
                // ImGui.PushStyleColor(ImGuiCol.Button, Style.Colors.Red);
                // ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Colors.Red);
                // ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Colors.Red);
                // if (ImGui.Button(" X "))
                // {
                //     ImGui.CloseCurrentPopup();
                // }
                // ImGui.PopStyleColor(3);
                // ImGuiUtil.ToolTip(Language.menu_label_close);

                ImGui.Separator();

                //-------------------

                // if (ImGui.Selectable(Language.menu_label_toggle_song_played_status))
                // {
                //     PlaylistManager.ChangeSongPlayedStatusSync(i, !isFilePlayed);
                //     ImGui.CloseCurrentPopup();
                // }

                if (ImGui.MenuItem(Language.menu_label_toggle_song_played_status))
                {
                    PlaylistManager.ChangeSongPlayedStatusSync(i, !isFilePlayed);
                }

                //-------------------

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.MenuItem(Language.menu_label_send_song_name_to_chat))
                {
                    PlaylistManager.SendSongToChat(i);
                }

                if (ImGui.MenuItem(Language.menu_label_copy_song_name))
                {
                    var songName = PlaylistManager.GetPostSongName(i);
                    ImGui.SetClipboardText($"{songName}");
                    ImGuiUtil.AddNotification(NotificationType.Info, Language.text_song_name_copied_to_clipboard);
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                //-------------------

                if (ImGui.MenuItem("Recalculate song duration"))
                {
                    PlaylistManager.CalculateSongDuration(i);
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                //-------------------

                ImGui.BeginDisabled(lockMultipleDevicesOptions);
                if (ImGui.MenuItem(Language.menu_label_move_song_to_top))
                {
                    PlaylistManager.MoveSongToIndexSync(i, 0);
                }

                if (ImGui.MenuItem(Language.menu_label_move_song_to_bottom))
                {
                    PlaylistManager.MoveSongToIndexSync(i, PlaylistManager.FilePathList.Count - 1);
                }

                ImGui.Spacing();

                ImGui.TextUnformatted(Language.menu_label_move_song_to_position);
                ImGui.SetNextItemWidth(150);
                if (ImGui.InputInt("##btnMoveSongToIndex", ref songTargetIndexInputValue, 1, 10, default, ImGuiInputTextFlags.AutoSelectAll))
                {
                    if (songTargetIndexInputValue <= 0)
                        songTargetIndexInputValue = 1;
                }

                // var btnChangeSize = ImGuiHelpers.GetButtonSize(btnChangeText);
                // ImGui.SameLine(ImGui.GetWindowWidth() - 2 * ImGui.GetCursorPosX() - btnChangeSize.X);
                ImGui.SameLine();
                if (ImGui.Button("Move"))
                {
                    PlaylistManager.MoveSongToIndexSync(i, songTargetIndexInputValue - 1);
                    // TODO: scroll to index after move
                }
                ImGui.EndDisabled();

                //-------------------

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.MenuItem("Edit lyric"))
                {
                    if (PlaylistManager.FilePathList.TryGetValue(i, out var entry))
                    {
                        LrcEditor.Instance.LoadLrcToEditor(LrcEditor.GetLrcFromSongEntry(entry));
                        LrcEditor.Instance.Show();
                    }
                }

                //-------------------

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.MenuItem(Language.menu_item_open_in_file_explorer))
                {
                    if (PlaylistManager.FilePathList.TryGetValue(i, out var entry))
                    {
                        Extensions.OpenFileLocation(entry.FilePath);
                    }
                }

                //-------------------

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                ImGui.BeginDisabled(lockMultipleDevicesOptions);
                if (ImGui.MenuItem(Language.menu_label_remove_song_from_playlist))
                {
                    PlaylistManager.RemoveSync(i);
                }
                ImGui.EndDisabled();

                //-------------------

                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        void DrawPlaylistDeleteButton()
        {
            // fix button background for light color themes
            ImGui.PushStyleColor(ImGuiCol.Button, Style.Colors.Transparent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Colors.Transparent);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Colors.Transparent);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, $"##btnDeletePlaylistSong##{i}"))
            {
                PlaylistManager.RemoveSync(i);
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            // PushFont(UiBuilder.IconFont);
            // PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5f, 5f));
            // if (Button($"{FontAwesomeIcon.TrashAlt.ToIconString()}##{i}",
            //         new Vector2(GetTextLineHeight(), GetTextLineHeight())))
            // {
            //     PlaylistManager.RemoveSync(i);
            // }
            // PopStyleVar();
            // PopFont();
        }

        void DrawPlaylistTrackDuration()
        {
            ImGui.TextUnformatted($"{entry.SongLengthFormated}");
        }

        void DrawPlaylistTrackName()
        {
            var displayName = entry.FileName;
            // ImGui.TextColored(textColor, displayName);
            if (entry.IsFilePlayed)
                ImGui.PushStyleColor(ImGuiCol.Text, MidiBard.config.playedSongColor);

            ImGui.TextUnformatted(displayName);

            if (entry.IsFilePlayed)
                ImGui.PopStyleColor();

            var songTooltipText = $"{displayName}\n\n{entry.FileDirectory}";
            ImGuiUtil.ToolTip(songTooltipText + "\n\n(Drag to reorder - right click for more options)");
        }
    }

    // private void DrawPlaylistSelector()
    // {
    //     ImGui.SetNextWindowPos(ImGui.GetWindowPos() + new Vector2(ImGui.GetWindowWidth(), 0), ImGuiCond.Always);
    //     ImGui.SetNextWindowSize(new Vector2(ImGuiHelpers.GlobalScale * 150, GetWindowHeight()));
    //     if (ImGui.Begin("playlists",
    //            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoMove |
    //            ImGuiWindowFlags.NoFocusOnAppearing))
    //     {
    //         try
    //         {
    //             bool sync = false;
    //             var container = PlaylistContainerManager.Container;
    //             var playlistEntries = container.Entries;
    //             if (ImGui.BeginListBox("##playlistListbox", new Vector2(-1, ImGuiUtil.GetWindowContentRegionHeight() - 2 * GetFrameHeightWithSpacing())))
    //             {
    //                 for (int i = 0; i < playlistEntries.Count; i++)
    //                 {
    //                     var playlist = playlistEntries[i];
    //                     if (ImGui.Selectable($"{playlist.Name} ({playlist.PathList.Count})##{i}",
    //                             PlaylistContainerManager.CurrentPlaylistIndex == i))
    //                     {
    //                         PlaylistContainerManager.CurrentPlaylistIndex = i;
    //                     }
    //                 }

    //                 ImGui.EndListBox();
    //             }
    //             ImGui.SetNextItemWidth(-1);
    //             if (ImGui.InputText($"##currentPlaylistName", ref container.CurrentPlaylist.Name, 128, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
    //             {
    //                 sync = true;
    //             }

    //             if (ImGuiUtil.IconButton(FontAwesomeIcon.File, "new", Language.icon_button_tooltip_new_playlist))
    //             {
    //                 playlistEntries.Add(new PlaylistEntry() { Name = Language.icon_button_tooltip_new_playlist });
    //                 sync = true;
    //             }

    //             ImGui.SameLine();
    //             if (ImGui.IconButton(FontAwesomeIcon.Copy, "clone", Language.icon_button_tooltip_clone_current_playlist))
    //             {
    //                 playlistEntries.Insert(container.CurrentListIndex, container.CurrentPlaylist.Clone());
    //                 sync = true;
    //             }
    //             ImGui.SameLine();
    //             if (ImGui.IconButton(FontAwesomeIcon.Download, "saveas", Language.icon_button_tooltip_save_search_as_playlist))
    //             {
    //                 try
    //                 {
    //                     var c = new PlaylistEntry();
    //                     c.Name = PlaylistSearchString;
    //                     RefreshPlaylistSearchResult();
    //                     c.PathList = MidiBard.Ui.searchedPlaylistIndexs.Select(i => PlaylistManager.FilePathList[i]).ToList();
    //                     playlistEntries.Add(c);
    //                     sync = true;
    //                 }
    //                 catch (Exception e)
    //                 {
    //                     PluginLog.Warning(e, "error when try saving current search result as new playlist");
    //                 }
    //             }
    //             ImGui.SameLine();
    //             if (ImGuiUtil.IconButton(FontAwesomeIcon.Save, "save", Language.icon_button_tooltip_save_and_sync_playlist))
    //             {
    //                 container.Save();
    //                 sync = true;
    //             }

    //             ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.GetFrameHeightWithSpacing());
    //             if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, "deleteCurrentPlist", Language.icon_button_tooltip_delete_current_playlist))
    //             {
    //             }
    //             if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
    //             {
    //                 playlistEntries.Remove(container.CurrentPlaylist);
    //                 sync = true;
    //             }

    //             if (sync)
    //             {
    //                 IPCHandles.SyncPlaylist();
    //             }
    //         }
    //         catch (Exception e)
    //         {
    //             PluginLog.Error(e, "error when draw playlist popup");
    //         }

    //         ImGui.End();
    //     }
    // }
}
