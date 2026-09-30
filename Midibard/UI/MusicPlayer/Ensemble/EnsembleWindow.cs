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
using Dalamud.Interface.Utility;

using MidiBard.IPC;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private bool ShowEnsembleWindow;
    internal void OpenEnsembleWindow() => ShowEnsembleWindow = true;
#nullable enable
    internal static Action<string, Vector2, Vector2>? EnsembleItemBounds { get; set; }
#nullable restore

    private void DrawEnsembleWindow()
    {
        if (!ShowEnsembleWindow) return;
        var canControl = api.PartyList.Length >= 2 && api.PartyList.IsPartyLeader()
            && !StageIntegration.MidiBardLargeEnsembleBackend.Active;

        // ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        // ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, ImGui.GetStyle().ItemSpacing.Y));
        // ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Style.Components.WindowBg);
        // ImGui.PushStyleColor(ImGuiCol.TitleBg, Style.Components.WindowBg);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding * 1.2f);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.Y));

        if (ImGui.Begin(Language.window_title_ensemble_panel + "###ensembleWindow", ref ShowEnsembleWindow))
        {
            ImGui.TextWrapped(api.PartyList.Length < 2
                ? "尚未组队：可以提前设置。分配演奏人、全员取出乐器和准备确认需要先组成小队。"
                : StageIntegration.MidiBardLargeEnsembleBackend.Active ? "多人合奏已启用，请在演出助手的“多人合奏”页下发与开演。"
                : canControl ? "小队队长：选曲，检查分配，全员取出乐器，再准备合奏。"
                : "小队成员：等待队长选曲。收到队长的自动分配后，本机会按本曲安排取出乐器。无需开启自己的自动分配开关。");
            EnsembleItemBounds?.Invoke("ensembleRole", ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            if (ImGui.CollapsingHeader("本机接收设置（首次使用请检查）", api.PartyList.Length < 2 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
            ImGui.BeginDisabled(StageIntegration.MidiBardLargeEnsembleBackend.Active || PartyChatCommand.IsLoading
                || MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning);
            var settingsChanged = ImGui.Checkbox("接收同机控制", ref MidiBard.config.SyncClients);
            settingsChanged |= ImGui.Checkbox("接收跨电脑小队控制（PMD）", ref MidiBard.config.playOnMultipleDevices);
            settingsChanged |= ImGui.Checkbox("跟随游戏合奏准备确认", ref MidiBard.config.MonitorOnEnsemble);
            if (settingsChanged) MidiBard.SaveConfig();
            ImGui.EndDisabled();
            if (!MidiBard.config.SyncClients && !MidiBard.config.playOnMultipleDevices)
                ImGui.TextWrapped("尚未开启控制接收：队员需要开启与队长相同的控制方式，才能接收选曲和配器。");
            if (!MidiBard.config.MonitorOnEnsemble)
                ImGui.TextWrapped("跟随准备确认已关闭：本机不会随游戏小队合奏自动开始。");
            }
            ImGui.Separator();
            // fixed header
            // float headerStartY = ImGui.GetCursorPosY();
            var toolbarHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
            if (canControl && PartyChatCommand.EnsembleLoadIssue is { } loadIssue)
                toolbarHeight += ImGui.CalcTextSize(loadIssue, false, Math.Max(1, ImGui.GetContentRegionAvail().X)).Y + ImGui.GetStyle().ItemSpacing.Y;
            ImGui.BeginChild("##EnsembleControlMenuFixedHeight", new Vector2(-1, toolbarHeight), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.BeginDisabled(!canControl);
            DrawEnsembleControlMenu();
            ImGui.EndDisabled();
            ImGui.EndChild();

            ImGui.Separator();

            var playback = MidiBard.CurrentPlayback;
            var loading = PartyChatCommand.IsLoading;
            ImGui.BeginDisabled(loading || playback?.IsSoloPlayback == true || MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning);
            if (ImGui.Checkbox("我作为队长时自动分配乐器与演奏人", ref MidiBard.config.AutoAssignEnsembleTracks))
            {
                try
                {
                    PartyChatCommand.InvalidateAssignment();
                    if (canControl && playback is { } loaded)
                    {
                        if (MidiBard.config.AutoAssignEnsembleTracks)
                            loaded.MidiFileConfig = AutomaticEnsembleAssignment.Create(loaded.TrackInfos,
                                MidiFileConfigManager.GetMidiConfigFromFile(loaded.FilePath));
                        else
                            loaded.MidiFileConfig = DistributedEnsembleAssignment.CreateDraft(loaded.TrackInfos,
                                loaded.MidiFileConfig ?? MidiFileConfigManager.GetMidiConfigFromFile(loaded.FilePath));
                        loaded.SyncTrackStatusWithMidiFileConfig();
                        if (!MidiBard.config.playOnMultipleDevices && loaded.MidiFileConfig is { } currentConfig)
                            IPCHandles.UpdateMidiFileConfig(currentConfig);
                    }
                }
                catch (Exception ex) { api.ChatGui.PrintError("[MidiBard] " + ex.Message); }
                MidiBard.SaveConfig();
            }
            EnsembleItemBounds?.Invoke("ensembleAutoAssign", ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            ImGui.EndDisabled();
            ImGui.TextWrapped("自动分配决定自己选曲时的行为；队员接收队长自动分配时，本曲跟随队长，本机默认设置保留。");
            if (!loading && playback?.MidiFileConfig is { AutomaticallyAssigned: true } assigned)
            {
                var tracks = assigned.Tracks;
                ImGui.TextDisabled($"{tracks.Count(t => t.Enabled)} / {tracks.Count} 轨已分配");
            }

            ImGui.BeginChild("##EnsembleScrollableContent", new Vector2(-1, 0), false, ImGuiWindowFlags.HorizontalScrollbar);

            if (canControl && PartyChatCommand.ManualDistributionMode)
            {
                ImGui.TextWrapped("手动分配：指定每轨的演奏人、乐器和移调，再统一下发。队员会使用本次分配，无需调整自己的自动分配开关。");
                ImGui.BeginDisabled(!canControl || loading || playback?.MidiFileConfig == null || playback.IsSoloPlayback
                    || MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning);
                if (ImGui.Button("下发歌曲与手动分配", new Vector2(-1, 0))) PartyChatCommand.SendManualAssignment();
                EnsembleItemBounds?.Invoke("manualDistribute", ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                ImGui.EndDisabled();
                if (!string.IsNullOrEmpty(PartyChatCommand.ManualDistributionStatus))
                    ImGui.TextWrapped(PartyChatCommand.ManualDistributionStatus);
                ImGui.Separator();
            }

            if (loading)
            {
                ImGui.TextUnformatted("正在载入曲目...");
            }
            else if (!MidiBard.config.AutoAssignEnsembleTracks && MidiBard.config.playOnMultipleDevices
                && playback?.MidiFileConfig?.AutomaticallyAssigned != true && playback?.MidiFileConfig?.LeaderDistributed != true
                && !MidiBard.config.usingFileSharingServices && !PartyChatCommand.ManualDistributionMode)
            {
                ImGui.TextWrapped("手动分配需要开启跨电脑歌曲同步并连接房间；未使用歌曲同步时，请在各队员端自行选择轨道。");
            }
            else if (playback == null)
            {
                if (ImGui.Button(Language.ensemble_select_a_song_from_playlist, new Vector2(-1, ImGui.GetFrameHeight())))
                {
                    //try
                    //{
                    //    FilePlayback.LoadPlayback(new Random().Next(0, PlaylistManager.FilePathList.Count));
                    //}
                    //catch (Exception e)
                    //{
                    //    //
                    //}
                }
            }
            else if (playback.MidiFileConfig is not { } fileConfig)
            {
                ImGui.TextUnformatted(playback.IsSoloPlayback ? "当前曲目：单人演奏" : "当前曲目未加载合奏配置");
            }
            else
            {
                try
                {
                    var changed = false;

                    // use ensemble members config to define party selectbox order
                    var partyList = api.PartyList.Select(partyMember => partyMember.GetPartyMemberData()).ToList();

                    var cidToIndexMap = MidiBard.config.EnsembleMemberConfigs
                        .Select((config, index) => new { config.Cid, Index = index })
                        .ToDictionary(item => item.Cid, item => item.Index);

                    var orderedPartyList = partyList
                        .OrderBy(partyMember => cidToIndexMap.ContainsKey(partyMember.Cid)
                                                ? cidToIndexMap[partyMember.Cid]
                                                : int.MaxValue)
                        .ToList();

                    orderedPartyList.Insert(0, (Cid: 0, Name: "", World: ""));

                    var partyNamesList = orderedPartyList
                        .Select(partyMember => partyMember.Cid != 0 ? $"{partyMember.Name}·{partyMember.World}" : "")
                        .ToArray();

                    ImGui.BeginDisabled(!canControl || fileConfig.AutomaticallyAssigned || loading || MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning);
                    if (ImGui.BeginTable("fileConfig.Tracks", 4, ImGuiTableFlags.SizingFixedFit))
                    {
                        ImGui.TableSetupColumn("轨道", ImGuiTableColumnFlags.WidthStretch, 1);
                        ImGui.TableSetupColumn("乐器", ImGuiTableColumnFlags.WidthFixed);
                        ImGui.TableSetupColumn("移调", ImGuiTableColumnFlags.WidthFixed);
                        ImGui.TableSetupColumn("演奏人", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                        ImGui.TableHeadersRow();

                        var id = 125687;
                        foreach (var dbTrack in fileConfig.Tracks)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.PushID(id++);
                            // ImGui.PushStyleColor(ImGuiCol.Text, dbTrack.Enabled ? Style.Components.Text : Style.Components.TextDisabled);
                            ImGui.PushStyleColor(ImGuiCol.Text, dbTrack.Enabled ? ThemeManager.CurrentTheme.Text : ThemeManager.CurrentTheme.TextDisabled);
                            //var colUprLeft = dbTrack.Enabled ? Style.Colors.Orange : Style.Colors.Violet;
                            //var pMin = GetWindowPos() + GetCursorPos();
                            //var pMax = GetWindowPos() + GetCursorPos() + new Vector2(GetWindowContentRegionWidth(), GetFrameHeight());
                            //GetWindowDrawList().AddRectFilledMultiColor(pMin, pMax, colUprLeft, 0, 0, colUprLeft);
                            ImGui.AlignTextToFramePadding();
                            changed |= ImGui.Checkbox($"{dbTrack.Index + 1:00} {dbTrack.Name}", ref dbTrack.Enabled);

                            ImGui.TableNextColumn(); //1
                            changed |= InstrumentPicker($"##ensembleInstrumentPicker", ref dbTrack.Instrument);

                            ImGui.TableNextColumn(); //2
                            ImGui.SetNextItemWidth(ImGui.GetFrameHeight() * 3.3f);
                            changed |= ImGuiUtil.InputIntWithReset($"##ensembleTransposeTrack", ref dbTrack.Transpose, 12, () => 0);

                            ImGui.TableNextColumn(); //3
                            ImGui.SetNextItemWidth(-1);

                            var firstMidiFileCid = MidiFileConfig.GetFirstCidInParty(dbTrack);
                            var selectedIdx = firstMidiFileCid == 0 ? 0 : orderedPartyList.FindIndex(i => i.Cid != 0 && i.Cid == firstMidiFileCid);

                            if (ImGui.Combo("##partymemberSelect", ref selectedIdx, partyNamesList, partyNamesList.Length))
                            {
                                if (selectedIdx >= 1)
                                {
                                    var currentCid = orderedPartyList[selectedIdx].Cid;
                                    if (firstMidiFileCid > 0 && currentCid != firstMidiFileCid)
                                    {
                                        // character changed, delete the old one
                                        dbTrack.AssignedCids.Remove(firstMidiFileCid);
                                        changed = true;
                                    }

                                    if (currentCid > 0)
                                    {
                                        // add character
                                        if (!dbTrack.AssignedCids.Contains(currentCid))
                                        {
                                            dbTrack.AssignedCids.Insert(0, currentCid);
                                            changed = true;
                                        }
                                    }
                                }
                                else
                                {
                                    // choose empty, remove all the characters in the same party
                                    foreach (var member in api.PartyList)
                                    {
                                        if (dbTrack.AssignedCids.Contains(member.ContentId))
                                        {
                                            dbTrack.AssignedCids.Remove(member.ContentId);
                                        }
                                    }

                                    changed = true;
                                }
                            }

                            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            {
                                // choose empty, remove all the characters in the same party
                                foreach (var member in api.PartyList)
                                {
                                    if (dbTrack.AssignedCids.Contains(member.ContentId))
                                    {
                                        dbTrack.AssignedCids.Remove(member.ContentId);
                                    }
                                }
                                changed = true;
                            }

                            ImGuiUtil.ToolTip(Language.ensemble_combo_tooltip_assign_track_character);

                            ImGui.PopStyleColor();

                            ImGui.PopID();
                        }

                        ImGui.EndTable();
                    }
                    ImGui.EndDisabled();

                    if (changed)
                    {
                        PartyChatCommand.InvalidateAssignment();
                        fileConfig.LeaderDistributed = false;
                        playback.SyncTrackStatusWithMidiFileConfig();
                        fileConfig.Save(playback.FilePath);
                        if (!PartyChatCommand.ManualDistributionMode) IPCHandles.UpdateMidiFileConfig(fileConfig);
                    }
                }
                catch (Exception e)
                {
                    ImGui.TextUnformatted(e.ToString());
                }
            }

#if DEBUG
            try
            {
                foreach (var partyMember in api.PartyList)
                {
                    ImGui.TextUnformatted($"{partyMember.Name} {partyMember.ContentId:X} {partyMember.EntityId:X} {partyMember.Address.ToInt64():X}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"C##{partyMember.ContentId}"))
                    {
                        ImGui.SetClipboardText(partyMember.Address.ToInt64().ToString("X"));
                    }
                }
            }
            catch (Exception e)
            {
                ImGui.TextUnformatted(e.ToString());
            }
#endif

            ImGui.EndChild();
        }

        ImGui.End(); // ##EnsembleScrollableContent

        // ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }
}
