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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

using MidiBard.Util;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private Regex PlaylistSearchRegex = null;
    private string PlaylistSearchString = "";
    private readonly List<int> searchedPlaylistIndexs = new();
    private bool RegexError;
    private string RegexErrorMessage = "";
    private bool songDurationSortDirectionDesc = true;
    private bool songNameSortDirectionDesc = true;

    private void DrawPlaylistSearchBar()
    {
        var regexError = MidiBard.config.SearchUseRegex && RegexError;

        if (regexError)
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Lerp(Style.Components.FrameBg, Style.Colors.Red, 0.5f));

        // ImGui.SetNextItemWidth(-1);
        float iconButtonWidth = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.X * 2;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int totalButtons = 3;
        float totalButtonsWidth = iconButtonWidth * totalButtons + spacing * totalButtons;
        float inputWidth = ImGui.GetContentRegionAvail().X - totalButtonsWidth;
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputTextWithHint("##searchplaylist", MidiBard.config.SearchUseRegex ? "Enter regex to search" : Language.hint_search_textbox, ref PlaylistSearchString, 255, ImGuiInputTextFlags.AutoSelectAll))
        {
            RefreshPlaylistSearchResult();
        }

        if (regexError)
        {
            if (ImGui.IsItemFocused())
            {
                ImGui.SetNextWindowPos(ImGui.GetItemRectMin() + new Vector2(0, ImGui.GetFrameHeightWithSpacing()));
                if (ImGui.Begin("##tooltipRegexError", ImGuiWindowFlags.Tooltip | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextUnformatted(RegexErrorMessage);
                }
                ImGui.End();
            }
            ImGui.PopStyleColor();
        }

        // DrawClearButton();

        DrawUseRegexButton();

        DrawFilterPlayedSongsButton();

        DrawSortPlaylistButton();
    }

    private void DrawUseRegexButton()
    {
        Vector4? color = MidiBard.config.SearchUseRegex ? MidiBard.config.themeColor : null;
        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.StarOfLife, "buttonUseRegex", "Use regex", color))
        {
            MidiBard.config.SearchUseRegex = !MidiBard.config.SearchUseRegex;
            RefreshPlaylistSearchResult();
        }
    }

    // private void DrawClearButton()
    // {
    //     ImGui.SameLine();
    //     if (ImGuiUtil.IconButton(FontAwesomeIcon.Times))
    //     {
    //         PlaylistSearchString = string.Empty;
    //         RefreshPlaylistSearchResult();
    //     }
    //     ImGuiUtil.ToolTip("Clear");
    // }

    private void DrawFilterPlayedSongsButton()
    {
        (var filterPlayedSongsIcon, Vector4? filterPlayedSongsIconColor, string filterPlayedSongsTooltip) = MidiBard.config.SearchFilterPlayedOption switch
        {
            FilterPlayedSongOptions.ShowAll => (FontAwesomeIcon.Music, null, "Show all songs"),
            FilterPlayedSongOptions.ShowPlayed => (FontAwesomeIcon.Tasks, MidiBard.config.playedSongColor, "Filter played songs"),
            FilterPlayedSongOptions.ShowUnPlayed => (FontAwesomeIcon.ListUl, null, "Filter unplayed songs"),
            _ => (FontAwesomeIcon.Music, (Vector4?)null, "Show all songs")
        };

        ImGui.SameLine();
        if (ImGuiUtil.IconButton(filterPlayedSongsIcon, "##btnFilterPlayedSongs", filterPlayedSongsTooltip, filterPlayedSongsIconColor))
        {
            MidiBard.config.ToggleSearchFilterPlayedOption();
            RefreshPlaylistSearchResult();
        }
    }

    private void DrawSortPlaylistButton()
    {
        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.SortAmountDown, "##btnSortPlaylist", "Sort"))
        {
            ImGui.OpenPopup("SortPlaylistContextMenu");
        }

        if (ImGui.BeginPopup("SortPlaylistContextMenu"))
        {
            if (ImGui.MenuItem("Sort by name"))
            {
                PlaylistManager.SortBy((song) => song.FileName, descending: !songNameSortDirectionDesc);
                songNameSortDirectionDesc = !songNameSortDirectionDesc;
                RefreshPlaylistSearchResult();
            }

            if (ImGui.MenuItem("Sort by duration"))
            {
                PlaylistManager.SortBy((song) => song.SongLength, descending: !songDurationSortDirectionDesc);
                songDurationSortDirectionDesc = !songDurationSortDirectionDesc;
                RefreshPlaylistSearchResult();
            }

            ImGui.EndPopup();
        }
    }

    private void RefreshPlaylistSearchResult()
    {
        searchedPlaylistIndexs.Clear();

        try
        {
            PlaylistSearchRegex = new Regex(PlaylistSearchString, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            RegexError = false;
        }
        catch (Exception e)
        {
            RegexErrorMessage = e.Message;
            RegexError = true;
        }

        searchedPlaylistIndexs.AddRange(
            PlaylistManager.FilePathList
            .Select((item, index) => new { Index = index, item.FileName, item.IsFilePlayed })
            .Where((item) =>
            {
                var showPlayedSongsFilterResult = MidiBard.config.SearchFilterPlayedOption switch
                {
                    FilterPlayedSongOptions.ShowAll => item.IsFilePlayed == true || item.IsFilePlayed == false,
                    FilterPlayedSongOptions.ShowPlayed => item.IsFilePlayed == true,
                    FilterPlayedSongOptions.ShowUnPlayed => item.IsFilePlayed == false,
                    _ => item.IsFilePlayed == true || item.IsFilePlayed == false
                };

                var isRegexSearch = MidiBard.config.SearchUseRegex && !RegexError && PlaylistSearchRegex != null;
                var textSearchResult = isRegexSearch ? PlaylistSearchRegex.IsMatch(item.FileName) : item.FileName.ContainsIgnoreCase(PlaylistSearchString);

                return showPlayedSongsFilterResult && textSearchResult;
            })
            .Select(item => item.Index)
            .ToList()
        );
    }
}
