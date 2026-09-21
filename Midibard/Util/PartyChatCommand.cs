using System;
using System.Collections.Generic;
using System.Linq;

using BardMusicPlayer.XIVMIDI;

using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;

using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

namespace MidiBard;

internal static partial class PartyChatCommand
{
    private static readonly Dictionary<string, Action<string[]>> CommandHandlers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["playonmultipledevices"] = HandlePlayOnMultipleDevices,
            ["pmd"] = HandlePlayOnMultipleDevices,
            ["switchto"] = HandleSwitchTo,
            ["mbmanual"] = args =>
            {
                if (MidiBard.config.playOnMultipleDevices && api.PartyList.Length >= 2
                    && args.Length >= 1 && int.TryParse(args[0], out var index))
                    HandleSelectionRequest(args, index - 1, manual: true);
            },
            ["usechatplaylistsync"] = HandleSendUseChatPlaylistSync,
            ["playlistremove"] = HandleRemoveSong,
            ["playlistmove"] = HandleChangeSongOrder,
            ["reloadplaylist"] = HandleReloadPlaylist,
            ["updatedefaultperformer"] = HandleUpdateDefaultPerformer,
            ["updateinstrument"] = HandleUpdateInstrument,
            ["close"] = HandleClose,
            ["speed"] = HandleChangeSpeed,
            ["transpose"] = HandleSetGlobalTranspose,
            ["downloadsong"] = HandleDownloadSong,
            ["mbtransport"] = StageIntegration.EnsembleTransport.Receive,
            ["mbloadresult"] = _ => { },
            ["mbloadcancel"] = args => { if (args.Length == 1 && Guid.TryParseExact(args[0], "N", out var id) && id == receivingRequest) CancelLoad(); },
            ["mbstageproof"] = _ => { },
        };

    internal static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.Party)
            return;

        var messageString = SeString.Parse(message.OriginalMessage.Data.Span).TextValue;
        if (!CommandHandlers.Keys.Any(cmd => messageString.StartsWith(cmd, StringComparison.OrdinalIgnoreCase)))
            return;

        string[] parts = messageString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return;

        string cmd = parts[0].ToLower();
        string[] args = parts.Skip(1).ToArray();
        if (cmd == "mbstageproof")
        {
            ReceiveStageProof(args, SenderCid(message));
            return;
        }
        if (cmd == "mbloadresult")
        {
            if (args.Length == 2 && Guid.TryParseExact(args[0], "N", out var id) && IsCurrentLoad()
                && api.PartyList.IsPartyLeader()) pendingLoad?.Receive(id, SenderCid(message), args[1]);
            return;
        }

        var orderToken = args.FirstOrDefault(arg => arg.StartsWith("auto=", StringComparison.Ordinal));
        if (cmd is "switchto" or "mbmanual" or "updateinstrument" or "mbtransport" or "close" or "mbloadcancel")
        {
            var leader = api.PartyList.GetPartyLeader();
            if (leader == null || SenderCid(message) != leader.ContentId) return;
            if (orderToken != null && (cmd is "switchto" or "updateinstrument")
                && !AutomaticEnsembleAssignment.AcceptOrderToken(orderToken, leader.ContentId)) return;
        }

        // api.PluginLog.Warning($"OnChatMessage [{cmd}] ({args.JoinString(", ")})");

        if (CommandHandlers.TryGetValue(cmd, out var action))
        {
            action.Invoke(args);
        }
    }

    internal static void SendPlayOnMultipleDevices(bool isOn)
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        var str = isOn ? "on" : "off";
        Chat.SendMessage($"/p pmd {str}");
    }

    private static void HandlePlayOnMultipleDevices(string[] args)
    {
        if (args.Length < 1)
            return;

        var value = args[0].ToLower();
        if (value == "on")
            MidiBard.config.playOnMultipleDevices = true;
        else if (value == "off")
            MidiBard.config.playOnMultipleDevices = false;
    }

    // -------------------------

    internal static void SendUseChatPlaylistSync(bool isOn)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
        {
            return;
        }

        var str = isOn ? "on" : "off";
        Chat.SendMessage($"/p usechatplaylistsync {str}");
    }

    private static void HandleSendUseChatPlaylistSync(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices) return;

        if (args.Length < 1)
            return;

        var value = args[0].ToLower();
        if (value == "on")
            MidiBard.config.useChatPlaylistSync = true;
        else if (value == "off")
            MidiBard.config.useChatPlaylistSync = false;
    }

    // -------------------------

    internal static void SendSwitchTo(int songIndex)
    {
        _ = ObserveLoad(ManualDistributionMode
            ? PrepareManualSongAsync(songIndex, System.Threading.CancellationToken.None)
            : SwitchToAsync(songIndex, System.Threading.CancellationToken.None));
    }

    private static void HandleSwitchTo(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (int.TryParse(args[0], out int songIndex))
        {
            if (HandleSelectionRequest(args, songIndex - 1)) return;
            MidiPlayerControl.StopLrc();
            _ = LoadPartyPlayback(songIndex - 1);
            MidiBard.Ui.OpenMainWindow();
        }
    }

    private static async System.Threading.Tasks.Task LoadPartyPlayback(int index)
    {
        if (index < 0 || index >= PlaylistManager.FilePathList.Count) return;
        var path = PlaylistManager.FilePathList[index].FilePath;
        var success = false;
        try { success = await PlaylistManager.LoadPlayback(index); }
        catch (Exception ex) { api.PluginLog.Warning(ex, "Party song load failed"); }
        finally { if (!success) api.ChatGui.PrintError("[MidiBard] 合奏歌曲载入失败"); }
    }

    // -------------------------

    internal static void SendRemoveSong(int songIndex)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p playlistremove {songIndex + 1}");
    }

    private static void HandleRemoveSong(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (int.TryParse(args[0], out int songIndex))
        {
            PlaylistManager.RemoveLocal(songIndex - 1);
        }
    }

    // -------------------------

    internal static void SendChangeSongOrder(int songIndex, int targetIndex)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p playlistmove {songIndex + 1} {targetIndex + 1}");
    }

    private static void HandleChangeSongOrder(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || args.Length < 2)
            return;

        if (int.TryParse(args[0], out int fromIndex) && int.TryParse(args[1], out int toIndex))
        {
            PlaylistManager.MoveSongToIndexLocal(fromIndex - 1, toIndex - 1);
        }
    }

    // -------------------------

    internal static void SendChangeSpeed(float speed)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p speed {speed}");
    }

    private static void HandleChangeSpeed(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (float.TryParse(args[0], out float speed))
        {
            MidiBard.config.PlaySpeed = Math.Max(0.1f, speed);
        }
    }

    // -------------------------

    internal static void SendSetGlobalTranspose(int transpose)
    {
        if (!MidiBard.config.playOnMultipleDevices || !MidiBard.config.useChatPlaylistSync || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
        {
            return;
        }

        Chat.SendMessage($"/p transpose {transpose}");
    }

    private static void HandleSetGlobalTranspose(string[] args)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || args.Length < 1)
            return;

        if (int.TryParse(args[0], out int transpose))
        {
            MidiBard.config.SetTransposeGlobal(transpose);
        }
    }

    // -------------------------

    internal static void SendClose()
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage("/p close");
    }

    private static void HandleClose(string[] args)
    {
        MidiPlayerControl.Stop();
        SwitchInstrument.SwitchToAsync(0);
    }

    // -------------------------

    internal static void SendReloadPlaylist()
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage($"/p reloadplaylist");
    }

    private static void HandleReloadPlaylist(string[] args)
    {
        PlaylistManager.CurrentContainer = PlaylistManager.LoadLastPlaylist();
    }

    // -------------------------

    internal static void SendUpdateDefaultPerformer()
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        Chat.SendMessage($"/p updatedefaultperformer");
    }

    private static void HandleUpdateDefaultPerformer(string[] args)
    {
        MidiFileConfigManager.LoadDefaultPerformer();
    }

    // -------------------------

    internal static void SendUpdateInstrument()
    {
        if (api.PartyList.Length < 2)
        {
            return;
        }

        var order = MidiBard.CurrentPlayback?.MidiFileConfig?.LeaderDistributed == true
            ? "" : AutomaticEnsembleAssignment.CaptureOrderToken();
        Chat.SendMessage($"/p updateinstrument{(order.Length > 0 ? " " + order : "")}");
    }

    private static void HandleUpdateInstrument(string[] args)
    {
        if (MidiBard.CurrentPlayback == null)
        {
            return;
        }

        if (args.Any(arg => arg.StartsWith("auto=", StringComparison.Ordinal)) && AutomaticEnsembleAssignment.IsEnabled
            && MidiBard.CurrentPlayback.MidiFileConfig?.LeaderDistributed != true)
            MidiBard.CurrentPlayback.MidiFileConfig = AutomaticEnsembleAssignment.Create(
                MidiBard.CurrentPlayback.TrackInfos, MidiFileConfigManager.GetMidiConfigFromFile(MidiBard.CurrentPlayback.FilePath));

        MidiBard.CurrentPlayback.SyncTrackStatusWithMidiFileConfig();
        uint instrumentId = MidiBard.CurrentPlayback.GetInstrumentId();

        SwitchInstrument.SwitchToContinue(instrumentId);
    }

    // -------------------------

    internal static void SendDownloadSong(string source, string url)
    {
        if (!api.PartyList.IsPartyLeader() || !MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2)
            return;
        Chat.SendMessage($"/p downloadsong {source} {url}");
    }

    private static void HandleDownloadSong(string[] args)
    {
        if (!args[0].IsNullOrEmpty() && !args[1].IsNullOrEmpty())
        {
            api.LogDebug("download " + args[1] + " " + args[0]);
            if (args[0].StartsWith("BMP"))
                api.LogDebug("BMP");
            else
                api.LogDebug("XIV");
            XIVMidiApi.Instance.GetMidiFile(args[1], BMLDownload.Playback, args[0].StartsWith("BMP"));
        }
    }
}

