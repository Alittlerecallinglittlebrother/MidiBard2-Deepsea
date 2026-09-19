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
using System.IO;
using System.Linq;
using System.Text;

using Dalamud.Interface.ImGuiNotification;

using MidiBard.Util;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using ProtoBuf;

using static Dalamud.api;

namespace MidiBard;

[ProtoContract]
public class PlaylistContainer
{
    [ProtoMember(1)] public string FilePathWhenLoading = null;
    [ProtoMember(2)] public List<SongEntry> SongPaths = new();
    [ProtoMember(3)] private int _currentSongIndex = -1;

    [ProtoMember(4)]
    public int CurrentSongIndex
    {
        get => _currentSongIndex;
        set => _currentSongIndex = value.Clamp(-1, SongPaths.Count - 1);
    }

    private int lastReadDurationtick = 0;
    private TimeSpan _totalDuration;
    public TimeSpan TotalDuration
    {
        get
        {
            try
            {
                var tickCount = Environment.TickCount;
                if (tickCount > lastReadDurationtick + 20)
                {
                    var d = 0L;
                    for (var i = 0; i < SongPaths.Count; i++)
                    {
                        d += SongPaths[i].SongLength.Ticks;
                    }

                    var fromTicks = TimeSpan.FromTicks(d);
                    _totalDuration = fromTicks;
                    lastReadDurationtick = tickCount;
                    return fromTicks;
                }
            }
            catch
            {
                // ignored
            }

            return _totalDuration;
        }
    }

    public SongEntry? CurrentSongEntry
    {
        get
        {
            if (CurrentSongIndex < 0)
            {
                return null;
            }

            try
            {
                return SongPaths[CurrentSongIndex];
            }
            catch
            {
                return null;
            }
        }
    }

    public string DisplayName => Path.GetFileNameWithoutExtension(FilePathWhenLoading);

    public static PlaylistContainer FromFile(string filePath, bool createIfNotExist = false)
    {
        if (File.Exists(filePath))
        {
            RecordToRecentUsed(filePath);

            if (IsJsonPlaylistFile(filePath))
            {
                try
                {
                    return FromJsonFile(filePath);
                }
                catch (Exception e)
                {
                    ImGuiUtil.AddNotification(NotificationType.Warning, $"Invalid playlist format: {e.Message}");
                    PluginLog.Warning($"Invalid playlist format: {e.Message}");
                    return null;
                }
            }

            return FromPlainTextFile(filePath);
        }

        if (!createIfNotExist) return null;

        var newContainer = new PlaylistContainer { FilePathWhenLoading = filePath };
        newContainer.Save(filePath);
        RecordToRecentUsed(filePath);
        return newContainer;
    }

    public static PlaylistContainer FromPlainTextFile(string filePath)
    {
        var container = new PlaylistContainer();
        var readLines = File.ReadAllLines(filePath, Encoding.UTF8);
        var songEntries = readLines.Select(i =>
        {
            try
            {
                var fullPath = Path.GetFullPath(i, filePath);
                return new SongEntry { FilePath = fullPath, SongLength = default, IsFilePlayed = false };
            }
            catch
            {
                return null;
            }
        }).Where(i => i is not null);

        container.SongPaths.AddRange(songEntries);
        container.FilePathWhenLoading = filePath;
        return container;
    }

    public static PlaylistContainer FromJsonFile(string filePath)
    {
        var json = File.ReadAllText(filePath, Encoding.UTF8);
        var root = JObject.Parse(json);
        var songs = root["Songs"] as JArray;

        if (songs == null)
        {
            PluginLog.Warning("No songs found in file");
            return new PlaylistContainer { FilePathWhenLoading = filePath };
        }

        var container = new PlaylistContainer
        {
            FilePathWhenLoading = filePath
        };

        foreach (var song in songs)
        {
            try
            {
                var filePathValue = song["FilePath"]?.ToString();
                var songLengthStr = song["SongLength"]?.ToString();
                var isPlayed = song["IsFilePlayed"]?.ToObject<bool>() ?? false;

                if (string.IsNullOrEmpty(filePathValue))
                    continue;

                var fullPath = Path.GetFullPath(filePathValue, filePath);
                var songLength = TimeSpan.TryParse(songLengthStr, out var ts) ? ts : TimeSpan.Zero;

                container.SongPaths.Add(new SongEntry
                {
                    FilePath = fullPath,
                    SongLength = songLength,
                    IsFilePlayed = isPlayed
                });
            }
            catch
            {
                //  ignore invalid entry
            }
        }

        // TODO: update total playlist duration
        return container;
    }

    private static bool IsJsonPlaylistFile(string filePath)
    {
        try
        {
            var firstNonEmptyLine = File.ReadLines(filePath, Encoding.UTF8)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();

            return firstNonEmptyLine != null && firstNonEmptyLine.StartsWith("{");
        }
        catch
        {
            return false;
        }
    }

    private PlaylistContainer() { }

    private static void RecordToRecentUsed(string filePath)
    {
        var usedPlaylists = MidiBard.config.RecentUsedPlaylists;
        if (usedPlaylists.Contains(filePath))
        {
            usedPlaylists.Remove(filePath);
        }

        usedPlaylists.Add(filePath);

        const int maxRecentRecordSize = 30;
        if (usedPlaylists.Count > maxRecentRecordSize)
        {
            usedPlaylists.RemoveRange(0, usedPlaylists.Count - maxRecentRecordSize);
        }
    }

    public void Save()
    {
        Save(FilePathWhenLoading, this);
    }

    public void Save(string filePath)
    {
        Save(filePath, this);
    }

    public static void Save(string filePath, PlaylistContainer obj)
    {
        try
        {
            obj.SaveStrict(filePath);
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, "Error when saving playlist");
        }
    }

    internal void SaveStrict(string filePath = null)
    {
            filePath ??= FilePathWhenLoading;
            if (string.IsNullOrWhiteSpace(filePath)) throw new InvalidOperationException("当前播放列表没有保存路径，请先保存播放列表。");

            var playlistJson = new PlaylistJson
            {
                PlaylistName = "Midibard playlist",
                PlaylistTotalDuration = TotalDuration,
                Songs = SongPaths.Select(song => new SongEntry
                {
                    FilePath = Path.GetRelativePath(filePath, song.FilePath),
                    SongLength = song.SongLength,
                    IsFilePlayed = song.IsFilePlayed
                }).ToList()
            };

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            };

            var json = JsonConvert.SerializeObject(playlistJson, settings);
            var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json, Encoding.UTF8);
                if (File.Exists(filePath)) File.Replace(temporary, filePath, filePath + ".bak", true);
                else File.Move(temporary, filePath);
                RecordToRecentUsed(filePath);
                FilePathWhenLoading = filePath;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void ExportToCsv(string filePath)
    {
        ExportToCsv(filePath, this);
    }

    public static void ExportToCsv(string filePath, PlaylistContainer obj)
    {
        try
        {
            RecordToRecentUsed(filePath);
            obj.FilePathWhenLoading = filePath;

            var sb = new StringBuilder();

            // header
            sb.AppendLine("Song;Duration");
            sb.AppendLine($"Midibard playlist;{Util.Extensions.GetDurationString(obj.TotalDuration)}");

            // song list
            foreach (var song in obj.SongPaths)
            {
                var songName = PlaylistManager.ExtractSongName(
                    song.FileName,
                    MidiBard.config.postSongNameCaptureRegex,
                    MidiBard.config.postSongNameCaptureOutputFormat,
                    MidiBard.config.postSongNameFindRegex,
                    MidiBard.config.postSongNameReplacement);

                sb.AppendLine($"{songName};{song.SongLengthFormated}");
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, "Error when saving playlist as CSV");
        }
    }
}

[ProtoContract]
public class SongEntry
{
    [ProtoMember(1)] public string FilePath;
    // [JsonConverter(typeof(BaseNumberConverter))]
    [ProtoMember(2)] public TimeSpan SongLength;
    [ProtoMember(3)] public bool IsFilePlayed;
    [JsonIgnore] private string _name;
    [JsonIgnore] public string FileName => _name ??= Path.GetFileNameWithoutExtension(FilePath);
    [JsonIgnore] public string FileDirectory => Path.GetDirectoryName(FilePath);
    [JsonIgnore] public string SongLengthFormated => $"{(SongLength.Hours != 0 ? SongLength.Hours + ":" : "")}{SongLength.Minutes:00}:{SongLength.Seconds:00}";
    [JsonIgnore] public string LrcPath => Path.ChangeExtension(FilePath, "lrc");
}

public class PlaylistJson
{
    public string PlaylistName { get; set; }
    public TimeSpan PlaylistTotalDuration { get; set; }
    public List<SongEntry> Songs { get; set; } = new();
}


