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
using System.Runtime.CompilerServices;

using Dalamud.Interface.ImGuiNotification;

using MidiBard2.Util;

using Newtonsoft.Json;

using static Dalamud.api;

namespace MidiBard.Managers
{
    static class MidiFileConfigManager
    {
        public static bool UsingDefaultPerformer = false;
        public static DefaultPerformer defaultPerformer;
        public static string DefaultPerformerFileName = "MidiBardDefaultPerformer.json";
        private static readonly JsonSerializerSettings JsonSerializerSettings = new()
        {
            //TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            //TypeNameHandling = TypeNameHandling.Objects
        };

        public static void Init()
        {
            LoadDefaultPerformer();
        }

        public static void Save(this MidiFileConfig config, string path)
        {
            UsingDefaultPerformer = false;
            var fullName = GetMidiConfigFileInfo(path).FullName;

            // remove -1 element in AssignedCids added by GetFirstCidInParty before save to file
            foreach (var track in config.Tracks)
            {
                track.AssignedCids = track.AssignedCids.Where(cid => cid > 0).ToList();
            }

            File.WriteAllText(fullName, JsonConvert.SerializeObject(config, Formatting.Indented, JsonSerializerSettings));
        }

        public static FileInfo GetMidiConfigFileInfo(string songPath)
        {
            var configFileInfo = new FileInfo(Path.ChangeExtension(songPath, ".json"));
            return configFileInfo;
        }

        public static MidiFileConfig? GetMidiConfigFromFile(string songPath)
        {
            // BML stream midi
            if (songPath == null)
                return null;

            var configFile = GetMidiConfigFileInfo(songPath);
            MidiFileConfig config = null;
            if (!configFile.Exists) return null;
            string fileContent = "";
            try
            {
                using (FileStream fs = File.Open(configFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    StreamReader sr = new StreamReader(fs);
                    fileContent = sr.ReadToEnd();
                    // remove unwanted -1 from cid
                    fileContent = Sanitizer.SanitizeMidiFileConfig(fileContent, songPath);
                    config = JsonConvert.DeserializeObject<MidiFileConfig>(fileContent, JsonSerializerSettings);
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e.ToString());
            }
            return config;
        }

        public static MidiFileConfig GetMidiConfigFromTrack(IEnumerable<TrackInfo> trackInfos)
        {
            return new()
            {
                Tracks = trackInfos.Select(i => new DbTrack
                {
                    Index = i.Index,
                    Name = i.TrackName,
                    Instrument = i.InstrumentIDFromTrackName ?? 0,
                    Transpose = i.TransposeFromTrackName,
                }).ToList(),
                AdaptNotes = MidiBard.config.AdaptNotesOOR,
                ToneMode = MidiBard.config.GuitarToneMode,
                Speed = 1,
            };
        }

        internal static void SetDefaultPerformerFolder(string path)
        {
            MidiBard.config.defaultPerformerFolder = path;
            LoadDefaultPerformer();
        }

        internal static void ResetDefaultPerformer()
        {
            PluginLog.Debug("Reseting Default Performer...");
            defaultPerformer = new DefaultPerformer();

            var folder = EnsureValidFolder(ref MidiBard.config.defaultPerformerFolder);
            var defaultPerformerFilePath = Path.Combine(folder, DefaultPerformerFileName);

            if (!File.Exists(defaultPerformerFilePath))
            {
                defaultPerformerFilePath = FallbackToDefaultFolder();
            }

            var defaultPerformerFileInfo = new FileInfo(defaultPerformerFilePath);
            var serializedContents = JsonConvert.SerializeObject(defaultPerformer, Formatting.Indented);
            File.WriteAllText(defaultPerformerFileInfo.FullName, serializedContents);
            PluginLog.Debug($"{defaultPerformerFileInfo.FullName} Saved");
            ImGuiUtil.AddNotification(NotificationType.Info, $"Default performer reseted");
        }

        internal static DefaultPerformer LoadDefaultPerformer()
        {
            PluginLog.Debug("Loading Default Performer...");

            var folder = EnsureValidFolder(ref MidiBard.config.defaultPerformerFolder);
            var path = Path.Combine(folder, DefaultPerformerFileName);

            if (!File.Exists(path))
            {
                PluginLog.Warning($"Default Performer not found at {path}, creating...");
                if (!SaveDefaultPerformer())
                {
                    path = FallbackToDefaultFolder();
                }
            }

            try
            {
                var json = File.ReadAllText(path);
                defaultPerformer = JsonConvert.DeserializeObject<DefaultPerformer>(json, JsonSerializerSettings);
            }
            catch (Exception e)
            {
                PluginLog.Error($"Failed to load Default Performer: {e}");
            }

            return defaultPerformer;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        public static MidiFileConfig LoadDefaultPerformer(MidiFileConfig midiFileConfig, ref ulong[] Cids)
        {
            PluginLog.Debug("Loading Default Performer from MidiFileConfig...");
            UsingDefaultPerformer = true;
            var trackMapping = defaultPerformer?.TrackMappingDict ?? new();
            Cids = new ulong[100];

            var partyMembers = api.PartyList.ToList();

            foreach (var member in partyMembers)
            {
                if (member?.ContentId > 0 && trackMapping.TryGetValue(member.ContentId, out var trackIndices))
                {
                    foreach (var trackIdx in trackIndices)
                        Cids[trackIdx] = member.ContentId;
                }
            }

            for (int i = 0; i < midiFileConfig.Tracks.Count; i++)
            {
                try
                {
                    var track = midiFileConfig.Tracks[i];
                    if (MidiFileConfig.GetFirstCidInParty(track) <= 0 && Cids[i] > 0)
                    {
                        if (!track.AssignedCids.Contains(Cids[i]))
                            track.AssignedCids.Insert(0, Cids[i]);
                    }
                }
                catch (Exception e)
                {
                    PluginLog.Warning($"Track {i}: {e.Message}");
                }
            }

            return midiFileConfig;
        }

        private static string EnsureValidFolder(ref string folder)
        {
            if (Directory.Exists(folder)) return folder;

            PluginLog.Warning($"Default Performer folder does not exist. Creating at: {folder}");
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception e)
            {
                PluginLog.Error($"Invalid folder path: {folder}, using fallback. {e.Message}");
                ImGuiUtil.AddNotification(NotificationType.Error, $"Invalid Default Performer folder. Using default instead.");
                folder = api.PluginInterface.ConfigDirectory.FullName;
            }

            return folder;
        }

        private static string FallbackToDefaultFolder()
        {
            var fallbackFolder = api.PluginInterface.ConfigDirectory.FullName;
            var fallbackPath = Path.Combine(fallbackFolder, DefaultPerformerFileName);

            ImGuiUtil.AddNotification(NotificationType.Error, "Failed to save Default Performer, using fallback folder.");
            SaveDefaultPerformer();

            return fallbackPath;
        }

        static bool SaveDefaultPerformer()
        {
            if (defaultPerformer == null)
            {
                defaultPerformer = new DefaultPerformer();
            }

            try
            {
                var trackMappingFileInfo = GetDefaultPerformerFileInfo();
                var directory = trackMappingFileInfo.Directory;
                if (directory != null && !directory.Exists)
                {
                    PluginLog.Warning($"Directory {directory.FullName} does not exist. Creating...");
                    Directory.CreateDirectory(directory.FullName);
                }

                if (trackMappingFileInfo != null)
                {
                    var serializedContents = JsonConvert.SerializeObject(defaultPerformer, Formatting.Indented);
                    File.WriteAllText(trackMappingFileInfo.FullName, serializedContents);
                    PluginLog.Warning($"{trackMappingFileInfo.FullName} Saved");
                }
            }
            catch (Exception e)
            {
                PluginLog.Error($"Failed to save Default Performer: {e}");
                return false;
            }

            return true;
        }

        static FileInfo GetDefaultPerformerFileInfo()
        {
            if (string.IsNullOrEmpty(MidiBard.config.defaultPerformerFolder))
            {
                PluginLog.Warning("Default Performer folder is not set. Using fallback folder.");
                MidiBard.config.defaultPerformerFolder = api.PluginInterface.ConfigDirectory.FullName;
            }

            return new FileInfo(Path.Combine(MidiBard.config.defaultPerformerFolder, DefaultPerformerFileName));
        }

        public static void ExportToDefaultPerformer()
        {
            if (MidiBard.CurrentPlayback?.MidiFileConfig == null)
            {
                ImGuiUtil.AddNotification(NotificationType.Error, "Please choose a song first!");
                return;
            }

            var midiFileConfig = MidiBard.CurrentPlayback?.MidiFileConfig;
            Dictionary<ulong, List<int>> trackDict = new Dictionary<ulong, List<int>>();
            List<ulong> existingCidInConfig = new List<ulong>();
            foreach (var cur in midiFileConfig.Tracks)
            {
                foreach (var curCid in cur.AssignedCids)
                {
                    if (!trackDict.ContainsKey(curCid))
                    {
                        trackDict.Add(curCid, new List<int>());
                    }

                    trackDict[curCid].Add(cur.Index);

                    if (!existingCidInConfig.Contains(curCid))
                    {
                        existingCidInConfig.Add(curCid);
                    }
                }
            }

            foreach (var pair in trackDict)
            {
                if (!defaultPerformer.TrackMappingDict.ContainsKey(pair.Key))
                {
                    defaultPerformer.TrackMappingDict.Add(pair.Key, pair.Value);
                }
                else
                {
                    defaultPerformer.TrackMappingDict[pair.Key] = pair.Value;
                }
            }

            // scan for those in the party but not in config anymore, remove them from Default Performer
            var partyList = api.PartyList.ToArray();
            List<ulong> toRemove = new List<ulong>();
            foreach (var cur in partyList)
            {
                if (!existingCidInConfig.Contains(cur.ContentId))
                {
                    toRemove.Add(cur.ContentId);
                }
            }

            foreach (var cur in toRemove)
            {
                if (defaultPerformer.TrackMappingDict.ContainsKey(cur))
                {
                    defaultPerformer.TrackMappingDict.Remove(cur);
                }
            }

            bool succeed = SaveDefaultPerformer();
            if (succeed)
            {
                UsingDefaultPerformer = true;
                ImGuiUtil.AddNotification(NotificationType.Success, "Default Performer Exported.");
                GetMidiConfigFileInfo(MidiBard.CurrentPlayback.FilePath).Delete();
                if (!MidiBard.config.playOnMultipleDevices)
                {
                    IPC.IPCHandles.UpdateDefaultPerformer();
                }
            }
            else
            {
                ImGuiUtil.AddNotification(NotificationType.Error, "Fail to Export Default Performer!");
            }
        }
    }

    internal class MidiFileConfig
    {
        //public string FileName;
        //public string FilePath { get; set; }
        //public int Transpose { get; set; }
        public List<DbTrack> Tracks = new List<DbTrack>();
        //public DbChannel[] Channels = Enumerable.Repeat(new DbChannel(), 16).ToArray();
        //public List<int> TrackToDuplicate = new List<int>();
        public GuitarToneMode ToneMode = GuitarToneMode.Off;
        public bool AdaptNotes = true;
        public float Speed = 1;
        // Included in IPC snapshots; automatic assignments are only saved after a manual edit.
        public bool AutomaticallyAssigned;
        // Only the current playback uses a received plan; never overwrite the receiver's sidecar.
        [Newtonsoft.Json.JsonIgnore]
        public bool LeaderDistributed;

        internal static bool IsCidOnTrack(ulong cid, DbTrack track)
        {
            // main cid
            if (track.AssignedCids.Contains(cid))
                return true;

            // linked
            return MidiBard.config.EnsembleMemberConfigs
                .Where(cfg => track.AssignedCids.Contains(cfg.Cid))
                .Any(cfg => cfg.LinkedEnsembleMembers.Any(link => link.Cid == cid));

            // return false;
        }

        internal static ulong GetFirstCidInParty(DbTrack track)
        {
            // main CIDs
            var mainCid = track.AssignedCids
                .FirstOrDefault(cid => api.PartyList.Any(p => p.ContentId == cid));

            if (mainCid != 0)
            {
                // api.PluginLog.Warning($"GetFirstCidInParty main ({mainCid}): {track.Name}");
                return mainCid;
            }

            // linked CIDs
            var linkedCid = MidiBard.config.EnsembleMemberConfigs
                .Where(cfg => track.AssignedCids.Contains(cfg.Cid))
                .SelectMany(cfg => cfg.LinkedEnsembleMembers)
                .Select(link => link.Cid)
                .FirstOrDefault(cid => api.PartyList.Any(p => p.ContentId == cid));

            if (linkedCid != 0)
            {
                // api.PluginLog.Warning($"GetFirstCidInParty linked ({linkedCid}): {track.Name}");
                return linkedCid;
            }

            return 0;
        }
    }

    internal class DbTrack
    {
        public int Index;
        public bool Enabled = true;
        public string Name;
        public int Transpose;
        public uint Instrument;
        public List<ulong> AssignedCids = new List<ulong>();
    }

    // internal class DbChannel
    // {
    //     public int Transpose;
    //     public int Instrument;
    //     public List<long> AssignedCids = new List<long>();
    // }

    internal class DefaultPerformer
    {
        public Dictionary<ulong, List<int>> TrackMappingDict = new Dictionary<ulong, List<int>>(); // AssignedCids - List of Track Indexes
    }
}
