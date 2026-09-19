#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using MidiBard.Control.MidiControl;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

namespace MidiBard;

internal static partial class PartyChatCommand
{
    private static PartySongLoad? pendingLoad;
    private static CancellationTokenSource? activeLoad;
    private static readonly object loadGate = new();
    private static long loadParty;
    private static ulong loadLeader;
    private static readonly HashSet<Guid> seenLoads = new();

    private static ulong SenderCid(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        var original = SeString.Parse(message.OriginalSender.Data.Span);
        var player = original.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player != null)
            return api.PartyList.FirstOrDefault(p => p.Name.ToString() == player.PlayerName && p.World.RowId == player.World.RowId)?.ContentId ?? 0;
        // Same-world/self messages can lack a player link. Trust only an exact,
        // unambiguous party name from the unmodified game sender.
        var matches = api.PartyList.Where(p => p.Name.ToString() == original.TextValue).ToArray();
        return matches.Length == 1 ? matches[0].ContentId : 0;
    }

    private static bool IsPartyContext(long party, ulong leader) => api.ClientState.IsLoggedIn && party == api.PartyList.PartyId
        && leader != 0 && leader == api.PartyList.GetPartyLeader()?.ContentId;

    private static bool IsCurrentLoad() { lock (loadGate) return IsPartyContext(loadParty, loadLeader); }

    internal static void Tick()
    {
        lock (loadGate) { if (activeLoad != null && !IsCurrentLoad()) activeLoad.Cancel(); }
    }

    internal static void CancelLoad() { lock (loadGate) activeLoad?.Cancel(); }

    private static async Task ObserveLoad(Task<bool> load)
    {
        try { await load; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { api.PluginLog.Warning(ex, "Party song load failed"); api.ChatGui.PrintError("[MidiBard] " + ex.Message); }
    }

    internal static async Task<bool> SwitchToAsync(int index, CancellationToken cancellationToken)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
            throw new InvalidOperationException("请由当前小队队长选择合奏歌曲");
        if (index < 0 || index >= PlaylistManager.FilePathList.Count) throw new InvalidOperationException("歌曲不在播放列表中");
        var order = AutomaticEnsembleAssignment.CaptureOrderToken();
        if (AutomaticEnsembleAssignment.IsEnabled && order.Length == 0)
            throw new InvalidOperationException("尚未取得小队显示顺序，请等待小队列表更新后重试");
        var hash = PartySongIdentity.Hash(PlaylistManager.FilePathList[index].FilePath);
        var party = api.PartyList.PartyId;
        var leader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        var cancellation = BeginLoad(cancellationToken);
        var token = cancellation.Token;
        var request = new PartySongLoad(api.PartyList.Where(p => p.ContentId != api.Player.ContentId)
            .Select(p => (p.ContentId, p.Name.ToString())));
        pendingLoad = request;
        RememberLoad(request.Id);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Chat.SendMessage($"/p switchto {index + 1} load={request.Id:N} song={hash}{(order.Length > 0 ? " " + order : "")}",
                () => !token.IsCancellationRequested && IsPartyContext(party, leader));
            // The sender's chat echo is not a reliable execution callback.
            MidiPlayerControl.StopLrc();
            var loaded = await PlaylistManager.LoadPlayback(index, false, false, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) throw new InvalidOperationException("队长端未能载入 MIDI 文件，请检查文件和自动分配设置");
            try { await request.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token); }
            catch (TimeoutException)
            { throw new InvalidOperationException($"等待队员载入超时：{request.WaitingFor}。请确认全队更新到 3.2.5.14，并开启多设备演奏"); }
            return true;
        }
        finally
        {
            if (ReferenceEquals(pendingLoad, request)) pendingLoad = null;
            EndLoad(cancellation);
            if (request.Completion.IsFaulted) _ = request.Completion.Exception;
        }
    }

    private static CancellationTokenSource BeginLoad(CancellationToken token)
    {
        lock (loadGate)
        {
            activeLoad?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            activeLoad = cancellation;
            loadParty = api.PartyList.PartyId;
            loadLeader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
            return cancellation;
        }
    }

    private static void EndLoad(CancellationTokenSource cancellation)
    {
        lock (loadGate)
        {
            if (ReferenceEquals(activeLoad, cancellation)) activeLoad = null;
            cancellation.Dispose();
        }
    }

    private static bool RememberLoad(Guid id)
    {
        lock (loadGate)
        {
            if (seenLoads.Contains(id)) return false;
            if (seenLoads.Count >= 256) seenLoads.Clear();
            return seenLoads.Add(id);
        }
    }

    private static bool HandleSelectionRequest(string[] args, int preferred)
    {
        var requestText = args.FirstOrDefault(a => a.StartsWith("load=", StringComparison.Ordinal));
        if (requestText == null) return false;
        var hash = args.FirstOrDefault(a => a.StartsWith("song=", StringComparison.Ordinal));
        if (Guid.TryParseExact(requestText[5..], "N", out var id) && hash != null && RememberLoad(id))
            _ = ReceiveSelection(preferred, id, hash[5..]);
        return true;
    }

    private static async Task ReceiveSelection(int preferred, Guid request, string hash)
    {
        var party = api.PartyList.PartyId;
        var leader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        var cancellation = BeginLoad(CancellationToken.None);
        var token = cancellation.Token;
        try
        {
            var index = PartySongIdentity.Resolve(PlaylistManager.FilePathList.Select(s => s.FilePath).ToArray(), preferred, hash);
            var result = "missing";
            if (index >= 0)
            {
                MidiPlayerControl.StopLrc();
                result = await PlaylistManager.LoadPlayback(index, false, false, cancellation.Token) ? "ok" : "failed";
                MidiBard.Ui.OpenMainWindow();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (result != "ok") api.ChatGui.PrintError(result == "missing"
                ? "[MidiBard] 曲库中没有队长选择的同一份 MIDI 文件，请同步曲库后重试"
                : "[MidiBard] 合奏歌曲载入失败，请检查 MIDI 文件及自动分配设置");
            Chat.SendMessage($"/p mbloadresult {request:N} {result}", () => !token.IsCancellationRequested && IsPartyContext(party, leader));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            api.PluginLog.Warning(ex, "Party song load failed");
            Chat.SendMessage($"/p mbloadresult {request:N} failed", () => !token.IsCancellationRequested && IsPartyContext(party, leader));
        }
        finally { EndLoad(cancellation); }
    }
}
