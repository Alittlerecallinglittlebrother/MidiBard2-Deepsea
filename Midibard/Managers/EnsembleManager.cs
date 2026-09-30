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
using System.Linq;

using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;

using Midibard.Playlib;

using MidiBard.Control.MidiControl;

using static Dalamud.api;

namespace MidiBard.Managers;

internal partial class EnsembleManager : IDisposable
{
    //public SyncHelper(out List<(byte[] notes, byte[] tones)> sendNotes, out List<(byte[] notes, byte[] tones)> recvNotes)
    //{
    //    sendNotes = new List<(byte[] notes, byte[] tones)>();
    //    recvNotes = new List<(byte[] notes, byte[] tones)>();
    //}

    //private delegate IntPtr sub_140C87B40(IntPtr agentMetronome, byte beat);
    //   private Hook<sub_140C87B40> UpdateMetronomeHook;

    private delegate long sub_1410F4EC0(IntPtr a1, IntPtr a2);
    private readonly Hook<sub_1410F4EC0> NetworkEnsembleHook;
    internal EnsembleManager()
    {
        //UpdateMetronomeHook = new Hook<sub_140C87B40>(Offsets.UpdateMetronome, HandleUpdateMetronome);
        //UpdateMetronomeHook.Enable();

        NetworkEnsembleHook = api.GameInteropProvider.HookFromAddress<sub_1410F4EC0>(Offsets.NetworkEnsembleStart, (a1, a2) =>
        {
            if (MidiBard.config.MonitorOnEnsemble && !StageIntegration.MidiBardLargeEnsembleBackend.Active) StartEnsemble();
            return NetworkEnsembleHook.Original(a1, a2);
        });

        NetworkEnsembleHook.Enable();

        EnsembleStopped += () => EnsembleTimer.Reset();
    }

    internal static List<TimeSpan> EnsembleRecvTime { get; } = new();

    internal static unsafe void BeginEnsembleReadyCheck()
    {
        if (StageIntegration.MidiBardLargeEnsembleBackend.Active) { api.ChatGui.PrintError("[MidiBard] 请在“多人合奏”页面预约开演"); return; }
        if (PartyChatCommand.EnsembleLoadIssue is { } issue) { api.ChatGui.PrintError("[MidiBard] " + issue); return; }
        var ensembleRunning = MidiBard.AgentMetronome.EnsembleModeRunning;
        if (!ensembleRunning)
        {
            if (MidiBard.AgentPerformance.InPerformanceMode && !MidiBard.AgentMetronome.Struct->AgentInterface.IsAgentActive())
            {
                MidiBard.AgentMetronome.Struct->AgentInterface.Show();
            }

            Playlib.BeginReadyCheck();
            Playlib.ConfirmBeginReadyCheck();
        }
    }

    internal static unsafe void StopEnsemble()
    {
        Playlib.BeginReadyCheck();
        Playlib.SendAction("SelectYesno", 3, 0);
    }

    //private unsafe IntPtr HandleUpdateMetronome(IntPtr agentMetronome, byte currentBeat)
    //{
    //    var original = UpdateMetronomeHook.Original(agentMetronome, currentBeat);
    //    try
    //    {
    //        if (MidiBard.config.MonitorOnEnsemble)
    //        {
    //            var metronome = ((AgentMetronome.AgentMetronomeStruct*)agentMetronome);
    //            var beatsPerBar = metronome->MetronomeBeatsPerBar;
    //            var barElapsed = metronome->MetronomeBeatsElapsed;
    //            var ensembleRunning = metronome->EnsembleModeRunning;
    //               PluginLog.Verbose($"[Metronome] {barElapsed} {currentBeat}/{beatsPerBar}");

    //               if (barElapsed == -2 && currentBeat == 0 && ensembleRunning != 0)
    //               {
    //                   PluginLog.Warning($"Prepare: ensemble: {ensembleRunning}");
    //                   StartEnsemble();
    //               }
    //           }
    //    }
    //    catch (Exception e)
    //    {
    //        PluginLog.Error(e, $"error in {nameof(UpdateMetronomeHook)}");
    //    }

    //    return original;
    //}

    private static void StartEnsemble()
    {
        EnsembleRecvTime.Clear();
        EnsemblePrepare?.Invoke();

        //if playback is null, cancel ensemble mode.
        if (MidiBard.CurrentPlayback == null)
        {
            if (MidiBard.config.SyncClients)
            {
                StopEnsemble();
                ImGuiUtil.AddNotification(NotificationType.Error, "Please load a song before starting ensemble!");
                IPC.IPCHandles.ErrPlaybackNull(api.Player.CharacterName);
            }
        }
        else
        {
            EnsembleTimer.Restart();
            MidiBard.CurrentPlayback.Stop();
            MidiBard.CurrentPlayback.MoveToStart();

            try
            {
                MidiPlayerControl.DoPlay(true);
                PluginLog.Warning($"Start ensemble: sw: {EnsembleTimer.Elapsed.TotalMilliseconds}ms");
                EnsembleStart?.Invoke();
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "error EnsembleStart");
            }
        }
    }

    internal static void InvokeEnsembleStop() => EnsembleStopped?.Invoke();

    public static event Action EnsembleStart;

    public static event Action EnsemblePrepare;

    public static event Action EnsembleStopped;

    public static readonly Stopwatch EnsembleTimer = new Stopwatch();
    public static bool EnsembleRunning => EnsembleTimer.IsRunning;

    public void Dispose()
    {
        NetworkEnsembleHook?.Dispose();
        //UpdateMetronomeHook?.Dispose();
    }
}
