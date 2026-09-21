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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface.ImGuiNotification;

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

using MidiBard.Control.MidiControl;
using MidiBard.IPC;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

using Newtonsoft.Json;

using static Dalamud.api;

namespace MidiBard;

static partial class PlaylistManager
{
    private static int pendingLoads;
    private static readonly SemaphoreSlim loadGate = new(1, 1);
    public static bool IsLoading => Volatile.Read(ref pendingLoads) > 0;
    public static bool LastLoadSucceeded { get; private set; } = true;
    public static List<SongEntry> FilePathList => CurrentContainer.SongPaths;
    private static PlaylistContainer _currentContainer;

    public static PlaylistContainer CurrentContainer
    {
        get => _currentContainer ??= LoadLastPlaylist();
        set
        {
            _currentContainer = value;
            IPCHandles.SyncPlaylist();
        }
    }

    public static int CurrentSongIndex
    {
        get => CurrentContainer.CurrentSongIndex;
        private set => CurrentContainer.CurrentSongIndex = value;
    }

    internal static PlaylistContainer LoadLastPlaylist()
    {
        var lastPlaylistFilePath = MidiBard.config.RecentUsedPlaylists.LastOrDefault();

        if (!string.IsNullOrEmpty(lastPlaylistFilePath) && File.Exists(lastPlaylistFilePath))
        {
            PluginLog.Information($"Load playlist: {lastPlaylistFilePath}");
            return PlaylistContainer.FromFile(lastPlaylistFilePath);
        }

        ImGuiUtil.AddNotification(NotificationType.Error,
            $"Latest playlist NOT exist: {lastPlaylistFilePath}, using default playlist instead!");

        var defaultPath = Path.Combine(MidiBard.config.defaultPlaylistFolder ?? api.PluginInterface.GetPluginConfigDirectory(), "DefaultPlaylist.mpl");
        PluginLog.Information($"Load Default playlist: {defaultPath}");
        return PlaylistContainer.FromFile(defaultPath, true);
    }

    internal static void SetContainerPrivate(PlaylistContainer newContainer) => _currentContainer = newContainer;

    public static void SortBy<TKey>(Func<SongEntry, TKey>? orderBy = null, bool descending = false) where TKey : IComparable
    {
        if (orderBy == null) return;

        SongEntry? currentSongItem = null;
        if (CurrentSongIndex >= 0 && CurrentSongIndex < FilePathList.Count)
        {
            currentSongItem = FilePathList[CurrentSongIndex];
        }

        CurrentContainer.SongPaths = descending
            ? CurrentContainer.SongPaths.OrderByDescending(orderBy).ToList()
            : CurrentContainer.SongPaths.OrderBy(orderBy).ToList();

        // update CurrentSongIndex after order
        if (currentSongItem != null)
        {
            CurrentSongIndex = FilePathList.IndexOf(currentSongItem);
        }

        IPCHandles.SyncPlaylist();
    }

    public static void Clear()
    {
        if (!AllowStageRemoval(FilePathList.Select(s => s.FilePath).ToArray())) return;
        FilePathList.Clear();
        CurrentSongIndex = -1;
        CurrentContainer.Save();
        IPCHandles.SyncPlaylist();
    }

    public static void RemoveSync(int songIndex)
    {
        if (!IsValidSongIndex(songIndex) || !AllowStageRemoval([FilePathList[songIndex].FilePath])) return;
        var pmdUseChatPlaylistSync = MidiBard.config.playOnMultipleDevices && MidiBard.config.useChatPlaylistSync && api.PartyList.Length > 1;
        if (pmdUseChatPlaylistSync)
        {
            PartyChatCommand.SendRemoveSong(songIndex);
            return;
        }

        RemoveLocal(songIndex);
        IPCHandles.RemoveTrackIndex(songIndex);
        CurrentContainer.Save();
    }

    public static void RemoveLocal(int songIndex)
    {
        if (!IsValidSongIndex(songIndex)) return;
        if (!AllowStageRemoval([FilePathList[songIndex].FilePath])) return;

        try
        {
            FilePathList.RemoveAt(songIndex);

            // RecalculateCurrentSongIndexAfterRemove
            if (CurrentSongIndex == -1) return;
            if (songIndex < CurrentSongIndex)
            {
                CurrentSongIndex--;
            }
            else if (songIndex == CurrentSongIndex)
            {
                if (CurrentSongIndex >= FilePathList.Count)
                {
                    CurrentSongIndex = FilePathList.Count - 1;
                }
            }

            // PluginLog.Warning($"RemoveLocal song [{songIndex}]");
        }
        catch (Exception e)
        {
            PluginLog.Error(e, $"error when removing song [{songIndex}]");
        }
    }

    internal static void CalculateCurrentSongIndexAfterReorder(int songIndex, int targetIndex)
    {
        // if the item has been moved to a position before the current song index entire playlist shift one position
        if (CurrentSongIndex == -1) return;
        if (songIndex == CurrentSongIndex)
        {
            CurrentSongIndex = targetIndex;
        }
        else if (songIndex < CurrentSongIndex && targetIndex >= CurrentSongIndex)
        {
            CurrentSongIndex--;
        }
        else if (songIndex > CurrentSongIndex && targetIndex <= CurrentSongIndex)
        {
            CurrentSongIndex++;
        }
    }

    public static void MoveSongToIndexSync(int songIndex, int targetIndex)
    {
        var pmdUseChatPlaylistSync = MidiBard.config.playOnMultipleDevices && MidiBard.config.useChatPlaylistSync && api.PartyList.Length > 1;
        if (pmdUseChatPlaylistSync)
        {
            PartyChatCommand.SendChangeSongOrder(songIndex, targetIndex);
            return;
        }

        MoveSongToIndexLocal(songIndex, targetIndex);
        IPCHandles.MoveSongToIndex(songIndex, targetIndex);
        CurrentContainer.Save();
    }

    public static void MoveSongToIndexLocal(int songIndex, int targetIndex)
    {

        if (!IsValidSongIndex(songIndex)) return;
        if (songIndex == targetIndex) return;

        // clamp index
        targetIndex = Math.Clamp(targetIndex, 0, FilePathList.Count);

        var songItem = FilePathList[songIndex];
        FilePathList.RemoveAt(songIndex);

        FilePathList.Insert(targetIndex, songItem);

        CalculateCurrentSongIndexAfterReorder(songIndex, targetIndex);
        // PluginLog.Warning($"MoveSongToIndexLocal {FilePathList[targetIndex].FileName} {songIndex} => {targetIndex}");
    }

    public static void SetCurrentSongAsPlayed()
    {
        if (MidiBard.CurrentPlayback != null)
        {
            var progress = MidiBard.CurrentPlayback.GetPlaybackProgress();
            // Mark song as played
            var playedThresholdPercent = 0.85;
            if (progress >= playedThresholdPercent)
            {
                ChangeSongPlayedStatusLocal(CurrentSongIndex, true);
            }
        }
    }

    public static void ChangeSongPlayedStatusSync(int songIndex, bool isFilePlayed)
    {
        if (!IsValidSongIndex(songIndex)) return;

        ChangeSongPlayedStatusLocal(songIndex, isFilePlayed);
        IPCHandles.ChangeSongPlayedStatus(songIndex, isFilePlayed);
        // required if changing the playlist file structure to save the status in the file
        // CurrentContainer.Save();
    }

    public static void ChangeSongPlayedStatusLocal(int songIndex, bool isSongPlayed)
    {
        if (!IsValidSongIndex(songIndex)) return;
        var fileItem = FilePathList.ElementAtOrDefault(songIndex);
        if (fileItem != null)
        {
            fileItem.IsFilePlayed = isSongPlayed;
            // TODO:
            // trigger a interface update for playlist redraw
            // if you have filter show only unplayed songs and mark one as played it wont reload the list
        }
    }

    public static void ResetAllSongsPlayedStatusSync()
    {
        ResetAllSongsPlayedStatusLocal();
        IPCHandles.ResetAllSongsPlayedStatus();
    }

    public static void ResetAllSongsPlayedStatusLocal()
    {
        if (FilePathList.Count == 0) return;

        foreach (var fileItem in FilePathList.Where(item => item.IsFilePlayed))
        {
            fileItem.IsFilePlayed = false;
        }
        CurrentContainer.Save();
    }

    internal static readonly ReadingSettings readingSettings = new ReadingSettings
    {
        NoHeaderChunkPolicy = NoHeaderChunkPolicy.Ignore,
        NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.ReadValid,
        InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
        InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
        MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
        UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
        ExtraTrackChunkPolicy = ExtraTrackChunkPolicy.Read,
        UnknownChunkIdPolicy = UnknownChunkIdPolicy.ReadAsUnknownChunk,
        SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOff,
        TextEncoding = MidiBard.config.uiLang == 1
            ? Encoding.GetEncoding("gb18030")
            : Encoding.Default,
        InvalidSystemCommonEventParameterValuePolicy = InvalidSystemCommonEventParameterValuePolicy.SnapToLimits
    };

    internal static async Task AddAsync(IEnumerable<string> filePaths)
    {
        var success = 0;
        var sw = Stopwatch.StartNew();

        await Task.Run(() =>
        {
            foreach (var (file, path) in CheckValidFiles(filePaths))
            {
                try
                {
                    var songLength = file.GetDurationTimeSpan();
                    FilePathList.Add(new SongEntry { FilePath = path, SongLength = songLength ?? TimeSpan.Zero, IsFilePlayed = false });

                    success++;
                }
                catch (Exception e)
                {
                    PluginLog.Warning(e, "error when getting duration");
                }
            }

            CalculateDurationAll();
        });

        IPCHandles.SyncPlaylist();
        CurrentContainer.Save();
        PluginLog.Information($"File import all complete in {sw.Elapsed.TotalMilliseconds} ms! success: {success}");
    }

    internal static void CalculateDurationAll()
    {
        var parallelQuery = PlaylistManager.FilePathList.AsParallel();
        parallelQuery.ForAll(i =>
        {
            if (i.SongLength == default)
            {
                try
                {
                    i.SongLength = PlaylistManager.LoadSongFile(i.FilePath).GetDuration<MetricTimeSpan>();
                }
                catch (Exception e)
                {
                    PluginLog.Warning(e, $"error when getting {i.FilePath} duration");
                }
            }
        });
    }

    internal static void CalculateSongDuration(int songIndex)
    {
        if (!IsValidSongIndex(songIndex)) return;

        try
        {
            // if file doesnt exits remove it from playlist
            if (!File.Exists(FilePathList[songIndex].FilePath))
            {
                RemoveSync(songIndex);
                ImGuiUtil.AddNotification(NotificationType.Warning, $"The song file no longer exists and has been removed from the playlist");
                return;
            }

            FilePathList[songIndex].SongLength = PlaylistManager.LoadSongFile(FilePathList[songIndex].FilePath).GetDuration<MetricTimeSpan>();
            CurrentContainer.Save();
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, $"error when getting {FilePathList[songIndex].FilePath} duration");
        }
    }

    internal static bool IsValidSongIndex(int songIndex)
    {
        var isEmptyList = FilePathList == null || FilePathList.Count == 0;
        var isInvalidIndex = songIndex < 0 || songIndex >= FilePathList.Count;

        if (isEmptyList || isInvalidIndex)
            return false;

        return true;
    }

    private static IEnumerable<(MidiFile, string)> CheckValidFiles(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            MidiFile file = null;

            file = LoadSongFile(path);
            if (file is not null) yield return (file, path);
        }
    }

    internal static MidiFile LoadSongFile(string path)
    {
        if (Path.GetExtension(path).Equals(".mid") || Path.GetExtension(path).Equals(".midi"))
            return LoadMidiFile(path);
        else if (Path.GetExtension(path).Equals(".mmsong"))
            return LoadMMSongFile(path);
        return null;
    }

    private static MidiFile LoadMidiFile(string filePath)
    {
        PluginLog.Debug($"[LoadMidiFile] -> {filePath} START");
        MidiFile loaded = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(filePath))
            {
                PluginLog.Warning($"File not exist! path: {filePath}");
                return null;
            }

            using (var f = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                loaded = MidiFile.Read(f, readingSettings);
            }

            PluginLog.Debug($"[LoadMidiFile] -> {filePath} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Failed to load file at {0}", filePath);
        }

        return loaded;
    }

    public static MidiFile LoadMidiFile(Stream midi)
    {
        PluginLog.Debug($"[LoadMidiFile] -> START");
        MidiFile loaded = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (midi == null)
            {
                PluginLog.Warning($"Stream was empty");
                return null;
            }

            loaded = MidiFile.Read(midi, readingSettings);

            PluginLog.Debug($"[LoadMidiFile] -> OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Failed to load from stream.");
        }

        return loaded;
    }

    internal static async Task<bool> LoadExternalPlayback(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref pendingLoads);
        var entered = false;
        try
        {
            await loadGate.WaitAsync(cancellationToken);
            entered = true;
            LastLoadSucceeded = false;
            cancellationToken.ThrowIfCancellationRequested();
            // The cache is not a playlist. No import, IPC playlist broadcast or save.
            CurrentSongIndex = -1;
            return LastLoadSucceeded = await FilePlayback.LoadPlayback(path, cancellationToken);
        }
        finally
        {
            if (entered) loadGate.Release();
            Interlocked.Decrement(ref pendingLoads);
        }
    }

    public static async Task<bool> LoadPlayback(int? index = null, bool startPlaying = false, bool sync = true, CancellationToken cancellationToken = default)
    {
        // if (index < 0 || index >= FilePathList.Count)
        // {
        //    PluginLog.Warning($"LoadPlaybackIndex: invalid playlist index {index}");
        //    return false;
        // }

        Interlocked.Increment(ref pendingLoads);
        var entered = false;
        try
        {
            await loadGate.WaitAsync(cancellationToken);
            entered = true;
            LastLoadSucceeded = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (index is int songIndex) CurrentSongIndex = songIndex;
            if (sync) IPCHandles.LoadPlayback(CurrentSongIndex);
            if (await LoadPlaybackPrivate(cancellationToken))
            {
                LastLoadSucceeded = true;
                if (startPlaying) MidiPlayerControl.DoPlay();
                return true;
            }
            return false;
        }
        finally
        {
            if (entered) loadGate.Release();
            Interlocked.Decrement(ref pendingLoads);
        }
    }

    public static string ExtractSongName(string input, string capturePattern, string capturedOutputReplacement, string findPattern, string replacement)
    {
        if (string.IsNullOrEmpty(capturePattern) || string.IsNullOrEmpty(capturedOutputReplacement))
            return input;

        try
        {
            return Regex.Replace(input, capturePattern, match =>
            {
                string result = capturedOutputReplacement;

                // replace matching groups
                for (int i = match.Groups.Count - 1; i >= 1; i--)
                {
                    result = result.Replace($"${i}", match.Groups[i].Value);
                }

                // remove any group not found
                result = Regex.Replace(result, @"\$\d+", "");

                // sanitize result using the provided pattern
                if (!string.IsNullOrEmpty(findPattern))
                {
                    result = Regex.Replace(result, findPattern, replacement);
                }

                return result;
            });
        }
        catch
        {
            // ignored
            return input;
        }
    }

    public static string GetPostSongName(int songIndex)
    {
        if (!IsValidSongIndex(songIndex))
        {
            return string.Empty;
        }

        var songName = ExtractSongName(
            FilePathList[songIndex].FileName,
            MidiBard.config.postSongNameCaptureRegex,
            MidiBard.config.postSongNameCaptureOutputFormat,
            MidiBard.config.postSongNameFindRegex,
            MidiBard.config.postSongNameReplacement);

        return songName;
    }

    public static void SendSongToChat(int songIndex)
    {
        if (api.PartyList.IsInParty() && !api.PartyList.IsPartyLeader()) return;
        if (!IsValidSongIndex(songIndex)) return;

        // prevent send again after pausing song
        if (MidiPlayerControl._stat != MidiPlayerControl.e_stat.Paused)
        {
            var songName = GetPostSongName(songIndex);
            if (songName == "") return;

            var chatComand = MidiBard.config.GetChatCommand(MidiBard.config.SongNameChatTarget);

            var chatText = $"{chatComand}{songName}";
            Chat.SendMessage(chatText);
        }
    }

    private static async Task<bool> LoadPlaybackPrivate(CancellationToken cancellationToken)
    {
        try
        {
            var songEntry = FilePathList[CurrentSongIndex];
            return await FilePlayback.LoadPlayback(songEntry.FilePath, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            PluginLog.Warning(e.ToString());
            return false;
        }
    }

    private static MidiFile LoadMMSongFile(string filePath)
    {
        PluginLog.Debug($"[LoadMMSongFile] -> {filePath} START");
        MidiFile midiFile = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(filePath))
            {
                PluginLog.Warning($"File not exist! path: {filePath}");
                return null;
            }

            Dictionary<int, string> instr = new Dictionary<int, string>()
                {
                    { 0, "NONE" },
                    { 1, "Harp" },
                    { 2, "Piano" },
                    { 3, "Lute" },
                    { 4, "Fiddle" },
                    { 5, "Flute" },
                    { 6, "Oboe" },
                    { 7, "Clarinet" },
                    { 8, "Fife" },
                    { 9, "Panpipes" },
                    { 10, "Timpani" },
                    { 11, "Bongo" },
                    { 12, "BassDrum" },
                    { 13, "SnareDrum" },
                    { 14, "Cymbal" },
                    { 15, "Trumpet" },
                    { 16, "Trombone" },
                    { 17, "Tuba" },
                    { 18, "Horn" },
                    { 19, "Saxophone" },
                    { 20, "Violin" },
                    { 21, "Viola" },
                    { 22, "Cello" },
                    { 23, "DoubleBass" },
                    { 24, "ElectricGuitarOverdriven" },
                    { 25, "ElectricGuitarClean" },
                    { 26, "ElectricGuitarMuted" },
                    { 27, "ElectricGuitarPowerChords" },
                    { 28, "ElectricGuitarSpecial" }
                };

            Util.MMSongContainer songContainer = null;

            FileInfo fileToDecompress = new FileInfo(filePath);
            using (FileStream originalFileStream = fileToDecompress.OpenRead())
            {
                string currentFileName = fileToDecompress.FullName;
                string newFileName = currentFileName.Remove(currentFileName.Length - fileToDecompress.Extension.Length);
                using (GZipStream decompressionStream = new GZipStream(originalFileStream, CompressionMode.Decompress))
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        decompressionStream.CopyTo(memoryStream);
                        memoryStream.Position = 0;
                        var data = "";
                        using (var reader = new StreamReader(memoryStream, System.Text.Encoding.ASCII))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                data += line;
                            }
                        }
                        memoryStream.Close();
                        decompressionStream.Close();
                        songContainer = JsonConvert.DeserializeObject<Util.MMSongContainer>(data);
                    }
                }
            }

            midiFile = new MidiFile();
            foreach (Util.MMSong msong in songContainer.songs)
            {
                if (msong.bards.Count() == 0)
                    continue;
                else
                {
                    foreach (var bard in msong.bards)
                    {
                        var thisTrack = new TrackChunk(new SequenceTrackNameEvent(instr[bard.instrument]));
                        using (var manager = thisTrack.ManageTimedEvents())
                        {
                            TimedObjectsCollection<TimedEvent> timedEvents = manager.Objects;
                            int last = 0;
                            foreach (var note in bard.sequence)
                            {
                                if (note.Value == 254)
                                {
                                    var pitched = last + 24;
                                    timedEvents.Add(new TimedEvent(new NoteOffEvent((SevenBitNumber)pitched, (SevenBitNumber)127), note.Key));
                                }
                                else
                                {
                                    var pitched = (SevenBitNumber)note.Value + 24;
                                    timedEvents.Add(new TimedEvent(new NoteOnEvent((SevenBitNumber)pitched, (SevenBitNumber)127), note.Key));
                                    last = note.Value;
                                }
                            }
                        }
                        midiFile.Chunks.Add(thisTrack);
                    }
                    ;
                    break; //Only the first song for now
                }
            }
            midiFile.ReplaceTempoMap(TempoMap.Create(Tempo.FromBeatsPerMinute(25)));
            PluginLog.Debug($"[LoadMMSongFile] -> {filePath} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Failed to load file at {0}", filePath);
        }

        return midiFile;
    }

    // public static void MoveSongByStepsLocalSync(int songIndex, int moveBy)
    // {
    //     MoveSongByStepsLocal(songIndex, moveBy);
    //     IPCHandles.MoveSongBySteps(songIndex, moveBy);
    //     CurrentContainer.Save();
    // }

    // public static void MoveSongByStepsLocal(int songIndex, int moveBy)
    // {
    //     var isEmptyList = FilePathList == null || FilePathList.Count == 0;
    //     var isInvalidIndex = songIndex < 0 || songIndex >= FilePathList.Count;

    //     if (isEmptyList || isInvalidIndex)
    //         return;

    //     int targetIndex = songIndex + moveBy;
    //     targetIndex = Math.Clamp(targetIndex, 0, FilePathList.Count);

    //     if (targetIndex == songIndex)
    //         return;

    //     var item = FilePathList[songIndex];
    //     FilePathList.RemoveAt(songIndex);

    //     targetIndex = Math.Clamp(targetIndex, 0, FilePathList.Count);

    //     FilePathList.Insert(targetIndex, item);

    //     CalculateCurrentSongIndexAfterReorder(songIndex, targetIndex);
    //     // PluginLog.Debug($"MoveSongByStepsLocal {FilePathList[targetIndex].FileName} [{songIndex}, {targetIndex}]");
    // }
}
