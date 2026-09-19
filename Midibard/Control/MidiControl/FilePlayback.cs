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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl.PlaybackInstance;
using MidiBard.Util.Lyrics;

using static Dalamud.api;

namespace MidiBard.Control.MidiControl;

public static class FilePlayback
{
    private static BardPlayback GetPlaybackInstance(MidiFile midifile, string path)
    {
        PluginLog.Debug($"[LoadPlayback] -> {path} START");
        var stopwatch = Stopwatch.StartNew();
        var playback = BardPlayback.GetBardPlayback(midifile, path);
        playback.InterruptNotesOnStop = true;
        playback.TrackNotes = true;
        playback.TrackProgram = true;
        playback.Speed = MidiBard.config.PlaySpeed;
        playback.Finished += Playback_Finished;

        PluginLog.Debug($"[LoadPlayback] -> {path} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");

        if (MidiBard.config.showNowPlayingInfo)
        {
            api.ChatGui.Print(String.Format("[MidiBard 2] Now Playing: {0}", playback.DisplayName));
        }

        MidiBard.PluginIpc.MidiBardPlayingFileNamePub.SendMessage(PlaylistManager.GetPostSongName(PlaylistManager.CurrentSongIndex));
        return playback;
    }

    internal static float waitProgress = 0;
    internal static Status waitStatus = Status.notWaiting;
    private static void Playback_Finished(object sender, EventArgs e)
    {
        if (MidiBard.Stage?.OwnsPlayback(sender) == true)
        {
            if (sender is BardPlayback finished)
            {
                var index = PlaylistManager.FilePathList.FindIndex(s => string.Equals(s.FilePath, finished.FilePath, StringComparison.OrdinalIgnoreCase));
                PlaylistManager.ChangeSongPlayedStatusLocal(index, true);
            }
            return;
        }
        if (sender is BardPlayback { IsContinuityEnsemble: true } ensemblePlayback)
        {
            var index = PlaylistManager.FilePathList.FindIndex(s => string.Equals(s.FilePath, ensemblePlayback.FilePath, StringComparison.OrdinalIgnoreCase));
            PlaylistManager.ChangeSongPlayedStatusLocal(index, true);
            return;
        }
        Task.Run(() =>
        {
            try
            {
                if (MidiBard.AgentMetronome.EnsembleModeRunning)
                {
                    // Set song as played for ensemble
                    PlaylistManager.SetCurrentSongAsPlayed();
                    return;
                }
                if (!PlaylistManager.FilePathList.Any())
                    return;
                if (MidiBard.SlaveMode)
                    return;

                // Set song as played for solo
                PlaylistManager.SetCurrentSongAsPlayed();

                var fromSeconds = TimeSpan.FromSeconds(MidiBard.config.SecondsBetweenTracks);
                PerformWaiting(fromSeconds, ref waitProgress, ref waitStatus);
                if (waitStatus == Status.canceled) return;

                switch ((PlayMode)MidiBard.config.PlayMode)
                {
                    case PlayMode.Single:
                        break;
                    case PlayMode.SingleRepeat:
                        MidiBard.CurrentPlayback?.MoveToTime(new MidiTimeSpan(0));
                        MidiPlayerControl.DoPlay();
                        break;
                    case PlayMode.ListOrdered when PlaylistManager.CurrentSongIndex >= PlaylistManager.FilePathList.Count - 1:
                        break;
                    case PlayMode.ListOrdered:
                    case PlayMode.ListRepeat:
                    case PlayMode.Random:
                        MidiPlayerControl.Next(true);
                        break;
                }
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, "Unexpected exception when Playback finished.");
            }
        });
    }

    internal static async Task<bool> LoadPlayback(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MidiFile midiFile = await Task.Run(() => PlaylistManager.LoadSongFile(filePath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (midiFile == null)
        {
            // delete file if can't be loaded(likely to be deleted locally)
            //PluginLog.Debug($"[LoadPlayback] removing {index}");
            //PluginLog.Debug($"[LoadPlayback] removing {PlaylistManager.FilePathList[index].path}");
            //PlaylistManager.RemoveSync(index);
            return false;
        }

        var playback = await Task.Run(() => GetPlaybackInstance(midiFile, filePath), cancellationToken);
        if (cancellationToken.IsCancellationRequested) { playback.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        global::MidiBard.Managers.EnsembleContinuity.Cancel();
        MidiBard.CurrentPlayback?.Dispose();
        MidiBard.CurrentPlayback = playback;
        MidiBard.Stage?.Attach(playback, filePath);

        MidiBard.BardPlayDevice.ResetChannelStates();
        // TODO: refactor sync track config flow should be executed here instead of inside WaitSwitchInstrumentForSong

        try
        {
            await SwitchInstrument.WaitSwitchInstrumentForSong(Path.GetFileNameWithoutExtension(filePath));
            cancellationToken.ThrowIfCancellationRequested();
            MidiBard.Ui.RefreshPlotData();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            PluginLog.Warning(e.ToString());
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) Lrc.InitLrc(filePath);
        }

        return true;

    }

    internal static async Task<bool> LoadPlayback(string filename, Stream filePath)
    {
        MidiFile midiFile = await Task.Run(() => PlaylistManager.LoadMidiFile(filePath));

        if (midiFile == null)
        {
            // delete file if can't be loaded(likely to be deleted locally)
            //PluginLog.Debug($"[LoadPlayback] removing {index}");
            //PluginLog.Debug($"[LoadPlayback] removing {PlaylistManager.FilePathList[index].path}");
            //PlaylistManager.RemoveSync(index);
            return false;
        }

        var playback = await Task.Run(() => GetPlaybackInstance(midiFile, null));
        global::MidiBard.Managers.EnsembleContinuity.Cancel();
        MidiBard.CurrentPlayback?.Dispose();
        MidiBard.CurrentPlayback = playback;
        MidiBard.Stage?.Attach(playback, null);

        MidiBard.BardPlayDevice.ResetChannelStates();

        try
        {
            await SwitchInstrument.WaitSwitchInstrumentForSong(filename);
            MidiBard.Ui.RefreshPlotData();
        }
        catch (Exception e)
        {
            PluginLog.Warning(e.ToString());
        }

        return true;

    }

    public static bool IsWaiting => waitStatus == Status.waiting;
    public static float GetWaitWaitProgress => waitProgress;
    internal static void CancelWaiting() => waitStatus = Status.canceled;
    internal static void SkipWaiting() => waitStatus = Status.skipped;

    internal enum Status
    {
        notWaiting,
        waiting,
        skipped,
        canceled,
    }
    internal static void PerformWaiting(TimeSpan waitTime, ref float progress, ref Status status)
    {
        status = Status.waiting;
        progress = 0;
        var end = DateTime.UtcNow + waitTime;
        try
        {
            while (status == Status.waiting && progress < 1)
            {
                var remain = end - DateTime.UtcNow;
                progress = (float)(1 - remain / waitTime);
                Thread.Sleep(25);
                if (status != Status.waiting) return;
            }
        }
        finally
        {
            progress = 0;
        }

        status = Status.notWaiting;
    }
}
