using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using BardMusicPlayer.XIVMIDI;
using BardMusicPlayer.XIVMIDI.Events;
using BardMusicPlayer.XIVMIDI.IO;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

using MidiBard.Control.MidiControl;
using MidiBard.IPC;
using MidiBard.Managers.Ipc;

namespace MidiBard;

public record BMLEntry
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Editor { get; set; } = "";
    public string Filename { get; set; } = "";
    public string PerformerSize { get; set; } = "";
}

enum BMLDownload
{
    Playback,
    ToPlaylist
}

public partial class PluginUI
{
    private bool showBMLWindow = false;
    private List<BMLEntry> _bmlsonglist = new List<BMLEntry>();
    private List<BMLEntry> _bmlcachedsonglist = new List<BMLEntry>();

    private string bmlSearchString = "";
    private bool requestRunning = false;
    private int bmlSelectedSource = 1;
    private int bmlPerfSize = 0;
    private int bmlMaxSongs { get; set; } = 0;
    private bool bmlIsLoadingMore { get; set; } = false;

    public void ToggleBMLWindow()
    {
        if (showBMLWindow)
            CloseBMLWindow();
        else
            OpenBMLWindow();
    }

    public void OpenBMLWindow()
    {
        XIVMidiApi.Instance.OnBMPSongList += Instance_OnBMPSongList;
        XIVMidiApi.Instance.OnXIVSongList += Instance_OnXIVSongList;
        XIVMidiApi.Instance.OnXIVRequestError += Instance_OnRequestError;
        showBMLWindow = true;
    }

    public void CloseBMLWindow()
    {
        XIVMidiApi.Instance.OnBMPSongList -= Instance_OnBMPSongList;
        XIVMidiApi.Instance.OnXIVSongList -= Instance_OnXIVSongList;
        XIVMidiApi.Instance.OnXIVRequestError -= Instance_OnRequestError;
        showBMLWindow = false;
    }

    private void SendRequest()
    {
        if (bmlSelectedSource == 0) //XIVMIDI
            XIVMidiApi.Instance.GetSonglist(new XIVMIDIRequestBuilder() { bandSize = bmlPerfSize });
        else //BMPAPI
            XIVMidiApi.Instance.GetSonglist(new BMPAPIRequestBuilder() { bandSize = bmlPerfSize });
        requestRunning = true;
    }

    private void DownloadSong(string filename, BMLDownload downloadType)
    {
        if (filename.Contains(" "))
            filename = Uri.EscapeUriString(filename);
        api.LogDebug(filename);
        XIVMidiApi.Instance.GetMidiFile(filename, downloadType, bmlSelectedSource == 1);
    }

    #region callback handlers
    /// <summary>
    /// Triggered when a BMPSongList was requested 
    /// </summary>
    private void Instance_OnBMPSongList(object sender, XIVMidiBMPSongsEvent e)
    {
        if (!e.DynamicLoad)
            _bmlcachedsonglist = new List<BMLEntry>();

        bmlMaxSongs = e.Songs.totalPages;
        foreach (var file in e.Songs.docs)
        {
            try
            {
                if (file.url == null)
                    continue;
                _bmlcachedsonglist.Add(new BMLEntry()
                {
                    Artist = Safe(file.artist),
                    Title = Safe(file.title),
                    Editor = Safe(file.arranger),
                    Filename = file.url,
                    PerformerSize = Safe(file.ensembleSize)
                });
            }
            catch { }
        }
        _bmlsonglist = new List<BMLEntry>(_bmlcachedsonglist);
        requestRunning = false;
        bmlIsLoadingMore = false;
    }

    /// <summary>
    /// Triggered when a XIVSongList was requested 
    /// </summary>
    private void Instance_OnXIVSongList(object sender, XIVMidiXIVSongsEvent e)
    {
        if (!e.DynamicLoad)
            _bmlcachedsonglist = new List<BMLEntry>();

        bmlMaxSongs = e.Songs.meta.total;
        foreach (var file in e.Songs.data)
        {
            try
            {
                if (file.download_url == null)
                    continue;
                _bmlcachedsonglist.Add(new BMLEntry()
                {
                    Artist = file.artist,
                    Title = file.title,
                    Editor = file.credit,
                    Filename = file.download_url,
                    PerformerSize = Misc.PerformerSize[file.bandsize]
                });
            }
            catch { }
        }
        _bmlsonglist = new List<BMLEntry>(_bmlcachedsonglist);
        requestRunning = false;
        bmlIsLoadingMore = false;
    }

    public void Instance_OnMidiFile(object sender, XIVMidiFileEvent e)
    {
        BMLDownload option = (BMLDownload)e.Arguments;
        if (option == BMLDownload.ToPlaylist)
        {
            option = BMLDownload.Playback;
            if (PlaylistManager.FilePathList.Count() > 0)
            {
                string path = Path.GetDirectoryName(PlaylistManager.FilePathList.First().FilePath);
                File.WriteAllBytes(path + "/" + e.MidiData.Filename, e.MidiData.data);
                _ = PlaylistManager.AddAsync(new List<string> { path + "/" + e.MidiData.Filename }.AsEnumerable());
            }
            else
            {
                fileDialogManager.OpenFolderDialog("Open folder", (result, folderPath) =>
                {
                    if (result && Directory.Exists(folderPath))
                    {
                        File.WriteAllBytes(folderPath + "/" + e.MidiData.Filename, e.MidiData.data);
                        _ = PlaylistManager.AddAsync(new List<string> { folderPath + "/" + e.MidiData.Filename }.AsEnumerable());
                    }
                });
            }
        }
        else
        {
            if (api.PartyList.IsPartyLeader())
                IPCHandles.SendDownloadedSong(e.MidiData.Filename, e.MidiData.data);
            _ = FilePlayback.LoadPlayback(e.MidiData.Filename, new MemoryStream(e.MidiData.data));
        }
    }

    private void Instance_OnRequestError(object sender, XIVMidiApiErrorEvent e)
    {
        if (e.ErrorCode == 503)
            _bmlsonglist.Add(new BMLEntry() { Artist = "Service not available." });
        else
            _bmlsonglist.Add(new BMLEntry() { Artist = "Service error.", Title = e.Message });
    }
    #endregion

    private void DrawBMLWindow()
    {
        if (!showBMLWindow) return;

        ImGui.SetNextWindowSize(new(ImGui.GetWindowSize().Y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(ImGui.GetWindowPos() - new Vector2(2, 0), ImGuiCond.FirstUseEver, new Vector2(1, 0));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Style.Components.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Style.Components.WindowBg);

        if (ImGui.Begin("BML Browser", ref showBMLWindow))
        {
            DrawContent();
        }

        ImGui.PopStyleColor(2);
        ImGui.End();

        void DrawContent()
        {
            DrawBMLSearch();

            ImGui.Spacing();
            ImGui.Spacing();
            if (requestRunning)
                ImGuiUtil.DrawColoredBanner(Style.Colors.Violet, "Loading...");

            ImGui.Spacing();
            DrawBMLTable();
        }
    }

    private void DrawBMLSearch()
    {
        ImGui.Text("^Midi Source");
        if (ImGui.BeginCombo("##midisource_combo", Misc.Sources[bmlSelectedSource]))
        {
            for (int n = 0; n < Misc.Sources.Count; n++)
            {
                bool is_selected = (bmlSelectedSource == n);
                if (ImGui.Selectable(Misc.Sources[n], is_selected))
                    bmlSelectedSource = n;
                if (is_selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.Spacing();
        #region Search Stuff
        if (ImGui.InputTextWithHint("##searchplaylist", "Type to search", ref bmlSearchString, 255, ImGuiInputTextFlags.AutoSelectAll |
                                                                                                    ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (bmlSelectedSource == 0) //XIVMIDI
            {
                XIVMidiApi.Instance.GetSonglist(new XIVMIDIRequestBuilder()
                {
                    Search = bmlSearchString,
                    bandSize = bmlPerfSize
                }, false);
            }
            else if (bmlSelectedSource == 1)
            {
                XIVMidiApi.Instance.GetSonglist(new BMPAPIRequestBuilder()
                {
                    Search = bmlSearchString,
                    bandSize = bmlPerfSize
                }, false);
            }
        }
        ImGui.SameLine();

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Filter, "##searchFilterBtn", "Advanced Search"))
        {
            ImGui.OpenPopup("AdvancedSearchPopup");
        }
        if (ImGui.BeginPopup("AdvancedSearchPopup"))
        {
            ImGui.TextDisabled("Advanced Search");
            ImGui.Separator();

            var search = Misc.DecodeSearch(bmlSearchString);
            bmlSearchString = search["search"];
            string _searchMain = search["search"];
            string _searchArtist = search["artist"];
            string _searchEditor = search["editor"];

            ImGui.Text("Search:");
            ImGui.InputText("##filterSearch", ref _searchMain, 128);

            ImGui.Text("Artist:");
            ImGui.InputText("##filterArtist", ref _searchArtist, 128);

            ImGui.Text("Editor:");
            ImGui.InputText("##filterEditor", ref _searchEditor, 128);

            bmlSearchString = _searchMain;
            bmlSearchString = bmlSearchString + ";a:" + _searchArtist;
            bmlSearchString = bmlSearchString + ";e:" + _searchEditor;
            ImGui.Spacing();

            ImGui.Separator();
            if (ImGui.Button("Search"))
            {
                if (bmlSelectedSource == 0) //XIVMIDI
                {
                    XIVMidiApi.Instance.GetSonglist(new XIVMIDIRequestBuilder()
                    {
                        Search = bmlSearchString,
                        bandSize = bmlPerfSize
                    }, false);
                }
                else if (bmlSelectedSource == 1)
                {
                    XIVMidiApi.Instance.GetSonglist(new BMPAPIRequestBuilder()
                    {
                        Search = bmlSearchString,
                        bandSize = bmlPerfSize
                    }, false);
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        #endregion

        ImGui.Spacing();
        ImGui.Text("Perfomer size");
        if (ImGui.BeginCombo("##combo", Misc.PerformerSize[bmlPerfSize]))
        {
            for (int n = 0; n < Misc.PerformerSize.Count; n++)
            {
                bool is_selected = (bmlPerfSize == n);
                if (ImGui.Selectable(Misc.PerformerSize[n], is_selected))
                    bmlPerfSize = n;
                if (is_selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonSuccessNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonSuccessHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonSuccessActive);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Sync, "##getList", "Load Song List"))
        {
            SendRequest();
        }
        ImGui.PopStyleColor(3);

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonDangerNormal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonDangerHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonDangerActive);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, "##cancelRequests", "Cancel"))
        {
            //XIVMIDI.Instance.CancelDownloads();
        }
        ImGui.PopStyleColor(3);
    }

    private void DrawBMLTable()
    {
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX |
            ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInnerV;

        var tableColumnCount = 6;
        if (ImGui.BeginTable($"##BMLTableHead", tableColumnCount, tableFlags))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Artist", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Performer Size", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Options", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            ImGui.EndTable();
        }

        if (ImGui.BeginChild("bmlchild"))
        {
            if (ImGui.BeginTable("##BMLTable", tableColumnCount, tableFlags))
            {
                ImGui.TableSetupColumn("##songNumberColumn", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##artistColumn", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##titleColumn", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##editorColumn", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##performerSizeColumn", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##optionsColumn", ImGuiTableColumnFlags.WidthFixed);

                ImGuiListClipperPtr clipper;
                unsafe
                {
                    clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());
                }

                clipper.Begin(_bmlsonglist.Count());

                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        if (i >= _bmlsonglist.Count()) break;
                        ImGui.PushID(i);

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text($"{i + 1:0000}");

                        ImGui.TableNextColumn();
                        ImGui.Selectable(_bmlsonglist.ElementAt(i).Artist, false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowItemOverlap);
                        if (ImGui.IsItemHovered())
                        {
                            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            {
                                PartyChatCommand.SendDownloadSong(bmlSelectedSource == 0 ? "XIVMIDI" : "BMP", Uri.EscapeUriString(_bmlsonglist.ElementAt(i).Filename));
                                DownloadSong(_bmlsonglist.ElementAt(i).Filename, BMLDownload.Playback);
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text(_bmlsonglist.ElementAt(i).Title);
                        // DrawBMLlistContextMenu(i);

                        ImGui.TableNextColumn();
                        ImGui.Text(_bmlsonglist.ElementAt(i).Editor);

                        ImGui.TableNextColumn();
                        ImGui.Text(_bmlsonglist.ElementAt(i).PerformerSize);

                        ImGui.TableNextColumn();
                        if (ImGuiUtil.IconButton(FontAwesomeIcon.Download, $"##importBmlSong_{i}", "Add to playlist"))
                            DownloadSong(_bmlsonglist.ElementAt(i).Filename, BMLDownload.ToPlaylist);

                        ImGui.OpenPopupOnItemClick($"ContextMenuImportBmlSong", ImGuiPopupFlags.MouseButtonRight);
                        if (ImGui.BeginPopup("ContextMenuImportBmlSong"))
                        {
                            if (ImGui.MenuItem("Copy download URL"))
                            {
                                //var songUrl = BMLDownloadUrl + Uri.EscapeDataString(_bmlsonglist.ElementAt(i).Filename);
                                //ImGui.SetClipboardText(songUrl);
                            }
                            ImGui.EndPopup();
                        }

                        ImGui.SameLine();
                        if (ImGuiUtil.IconButton(FontAwesomeIcon.Play, $"##loadBmlSong_{i}", "Load to playback"))
                        {
                            PartyChatCommand.SendDownloadSong(bmlSelectedSource == 0 ? "XIVMIDI" : "BMP", Uri.EscapeUriString(_bmlsonglist.ElementAt(i).Filename));
                            DownloadSong(_bmlsonglist.ElementAt(i).Filename, BMLDownload.Playback);
                        }
                        ImGui.PopID();
                    }
                }

                clipper.End();
                ImGui.EndTable();
            }

            float scrollY = ImGui.GetScrollY();
            float maxScrollY = ImGui.GetScrollMaxY();
            if ((maxScrollY > 0 && (maxScrollY - scrollY) < 100f) && !bmlIsLoadingMore)
            {
                if ((bmlSelectedSource == 0) && (_bmlsonglist.Count >= bmlMaxSongs))
                    return;
                if ((bmlSelectedSource == 1) && (_bmlsonglist.Count / 100 >= bmlMaxSongs))
                    return;


                bmlIsLoadingMore = true;
                if (bmlSelectedSource == 0) //XIVMIDI
                {
                    XIVMidiApi.Instance.GetSonglist(new XIVMIDIRequestBuilder()
                    {
                        Search = bmlSearchString,
                        bandSize = bmlPerfSize
                    }, true);
                }
                else if (bmlSelectedSource == 1)
                {
                    XIVMidiApi.Instance.GetSonglist(new BMPAPIRequestBuilder()
                    {
                        Search = bmlSearchString,
                        bandSize = bmlPerfSize
                    }, true);
                }
            }
        }
        ImGui.EndChild();

        // void DrawBMLlistContextMenu(int i)
        // {
        //     ImGui.OpenPopupOnItemClick($"##bmllistRightClickMenu", ImGuiPopupFlags.MouseButtonRight);
        //     ImGui.PushStyleColor(ImGuiCol.Border, Style.Components.TooltipBorderColor);
        //     ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);

        //     if (ImGui.BeginPopup($"##bmllistRightClickMenu"))
        //     {
        //         // menu title
        //         ImGui.PushStyleColor(ImGuiCol.Button, Style.Components.ButtonInfoNormal);
        //         ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.Components.ButtonInfoNormal);
        //         ImGui.PushStyleColor(ImGuiCol.ButtonActive, Style.Components.ButtonInfoNormal);
        //         float fullWidth = ImGui.GetContentRegionAvail().X;
        //         ImGui.Button($"({i + 1}) {_bmlsonglist.ElementAt(i).Artist} - {_bmlsonglist.ElementAt(i).Title}", new Vector2(fullWidth, 0));
        //         ImGui.PopStyleColor(3);
        //         ImGui.Separator();

        //         if (ImGui.MenuItem("Add to playlist"))
        //         {
        //             this._downloadType = BMLDownload.ToPlaylist;
        //             XIVMIDI.Instance.AddToQueue(new GetRequest()
        //             {
        //                 Url = BMLDownloadUrl + Uri.EscapeUriString(_bmlsonglist.ElementAt(i).Filename),
        //                 Host = "xivmidi.com",
        //                 Accept = "audio/midi",
        //                 Requester = Requester.DOWNLOAD
        //             });
        //         }

        //         ImGui.EndPopup();
        //     }
        //     ImGui.PopStyleVar();
        //     ImGui.PopStyleColor();
        // }
    }

    static string Safe(string s) => s == null ? "" : s.Replace("\0", "").Trim();
}
