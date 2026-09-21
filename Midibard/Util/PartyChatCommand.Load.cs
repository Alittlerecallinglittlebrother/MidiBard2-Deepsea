#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BardStage.Core.Rooms;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using MidiBard.Control.MidiControl;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using MidiBard.Util;

namespace MidiBard;

internal static partial class PartyChatCommand
{
    // Stage supplies these callbacks when the optional encrypted room transfer
    // is enabled.  They deliberately remain nullable so normal PMD operation
    // still behaves exactly as before when no room is connected.
    internal static Func<Guid, string, CancellationToken, Task<string?>>? SongTransferRequest { get; set; }
    internal static Action<Guid, string, string>? SongTransferSource { get; set; }
    internal static Func<string, CancellationToken, Task<bool>>? ExternalSongLoader { get; set; }
    internal static Func<string, string?>? TransferredSongResolver { get; set; }

    internal static Task<string?> RequestTransferredSong(Guid request, string hash, CancellationToken token)
        => SongTransferRequest?.Invoke(request, hash, token) ?? Task.FromResult<string?>(null);

    internal static string? ResolveTransferredSong(string hash) => TransferredSongResolver?.Invoke(hash);

    private static PartySongLoad? pendingLoad;
    private static CancellationTokenSource? activeLoad;
    private static readonly object loadGate = new();
    private static long loadParty;
    private static ulong loadLeader;
    private static readonly HashSet<Guid> seenLoads = new();
    private static object? readyPlayback;
    private static long readyParty;
    private static ulong readyLeader;
    private static string readyMembers = "", loadMembers = "";
    private static string MemberKey => string.Join(",", api.PartyList.Select(p => p.ContentId).OrderBy(p => p));
    internal static string? EnsembleLoadIssue => !MidiBard.config.EnableCrossComputerSongSync || !MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 ? null
        : activeLoad != null ? "正在等待全员接收并载入 MIDI"
        : readyPlayback == null || !ReferenceEquals(readyPlayback, MidiBard.CurrentPlayback)
            || !IsPartyContext(readyParty, readyLeader) || readyMembers != MemberKey || ManualAssignmentChanged
            ? ManualDistributionMode ? "请完成手动分配并下发，等待全员载入成功后再开始"
                : "请由当前队长重新选曲，等待全员载入成功后再开始" : null;

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

    private static bool IsCurrentLoad() { lock (loadGate) return IsPartyContext(loadParty, loadLeader) && loadMembers == MemberKey; }

    internal static void Tick()
    {
        lock (loadGate) { if (activeLoad != null && !IsCurrentLoad()) activeLoad.Cancel(); }
    }

    internal static void CancelLoad() { lock (loadGate) activeLoad?.Cancel(); }

    private static async Task ObserveLoad(Task<bool> load)
    {
        try { await load; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ManualDistributionStatus = ex.Message; api.PluginLog.Warning(ex, "Party song load failed"); api.ChatGui.PrintError("[MidiBard] " + ex.Message); }
    }

    internal static Task<bool> SwitchToAsync(int index, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= PlaylistManager.FilePathList.Count) throw new InvalidOperationException("歌曲不在播放列表中");
        return SwitchToPathAsync(PlaylistManager.FilePathList[index].FilePath, index, cancellationToken);
    }

    private static async Task<bool> SwitchToPathCoreAsync(string path, int index, CancellationToken cancellationToken, MidiFileConfig? manual = null)
    {
        if (!MidiBard.config.playOnMultipleDevices || api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader())
            throw new InvalidOperationException("请由当前小队队长选择合奏歌曲");
        if (manual != null && (MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning))
            throw new InvalidOperationException("请先停止合奏再下发手动分配");
        var order = manual == null ? AutomaticEnsembleAssignment.CaptureOrderToken() : "";
        if (manual == null && AutomaticEnsembleAssignment.IsEnabled && order.Length == 0)
            throw new InvalidOperationException("尚未取得小队显示顺序，请等待小队列表更新后重试");
        var hash = PartySongIdentity.Hash(path);
        var party = api.PartyList.PartyId;
        var leader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        var cancellation = BeginLoad(cancellationToken);
        var token = cancellation.Token;
        var request = new PartySongLoad(api.PartyList.Where(p => p.ContentId != api.Player.ContentId)
            .Select(p => (p.ContentId, p.Name.ToString())));
        pendingLoad = request;
        RememberLoad(request.Id);
        var complete = false;
        try
        {
            readyPlayback = null;
            readyManualSignature = null;
            SongTransferSource?.Invoke(request.Id, hash, path);
            RoomSongPlan? plan = null;
            if (manual != null)
            {
                plan = DistributedEnsembleAssignment.Capture(request.Id, hash, manual);
                ManualDistributionStatus = "正在下发歌曲与手动分配，等待全员载入";
                if (ManualPlanPublisher == null) throw new InvalidOperationException("手动分配服务未就绪，请重新加载插件");
                await ManualPlanPublisher(plan, token);
                token.ThrowIfCancellationRequested();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            Chat.SendMessage($"/p {(manual != null ? "mbmanual" : "switchto")} {Math.Max(1, index + 1)} load={request.Id:N} song={hash}{(order.Length > 0 ? " " + order : "")}",
                () => !token.IsCancellationRequested && IsPartyContext(party, leader));
            // The sender's chat echo is not a reliable execution callback.
            MidiPlayerControl.StopLrc();
            using var assignment = DistributedEnsembleAssignment.Begin(plan);
            var loaded = index >= 0 ? await PlaylistManager.LoadPlayback(index, false, false, token)
                : await PlaylistManager.LoadExternalPlayback(path, token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!loaded) throw new InvalidOperationException("队长端未能载入 MIDI 文件，请检查文件和自动分配设置");
            try { await request.Completion.WaitAsync(TimeSpan.FromSeconds(manual != null ? 180 : MidiBard.config.EnableCrossComputerSongSync ? 120 : 30), token); }
            catch (TimeoutException)
            { throw new InvalidOperationException($"等待队员载入超时：{request.WaitingFor}。请确认全队使用相同版本、开启多设备演奏，并检查演出房间连接和歌曲同步开关"); }
            token.ThrowIfCancellationRequested();
            if (!IsCurrentLoad()) throw new OperationCanceledException("小队已改变");
            readyPlayback = MidiBard.CurrentPlayback; readyParty = party; readyLeader = leader; readyMembers = MemberKey;
            readyManualSignature = manual != null ? AssignmentSignature(MidiBard.CurrentPlayback?.MidiFileConfig) : null;
            complete = true;
            if (manual != null) ManualDistributionStatus = "全员已载入歌曲与手动分配，可以准备合奏";
            return true;
        }
        finally
        {
            if (!complete) Chat.SendMessage($"/p mbloadcancel {request.Id:N}", () => IsPartyContext(party, leader));
            if (manual != null && !complete)
                ManualDistributionStatus = "手动分配未完成，请检查队员提示后重新下发";
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
            loadMembers = MemberKey;
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

    private static bool HandleSelectionRequest(string[] args, int preferred, bool manual = false)
    {
        var requestText = args.FirstOrDefault(a => a.StartsWith("load=", StringComparison.Ordinal));
        if (requestText == null) return false;
        var hash = args.FirstOrDefault(a => a.StartsWith("song=", StringComparison.Ordinal));
        if (Guid.TryParseExact(requestText[5..], "N", out var id) && hash != null && RememberLoad(id))
            _ = ReceiveSelection(preferred, id, hash[5..], manual);
        return true;
    }

    private static Guid receivingRequest;
    private static async Task ReceiveSelection(int preferred, Guid request, string hash, bool manual = false)
    {
        var party = api.PartyList.PartyId;
        var leader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        var cancellation = BeginLoad(CancellationToken.None);
        cancellation.CancelAfter(TimeSpan.FromSeconds(manual ? 160 : 110));
        receivingRequest = request;
        var token = cancellation.Token;
        try
        {
            RoomSongPlan? plan = null;
            if (manual)
            {
                if (!MidiBard.config.EnableCrossComputerSongSync || ManualPlanReceiver == null)
                    throw new InvalidOperationException("请开启跨电脑歌曲同步并使用 3.2.5.23 或更新版本，接收队长的手动分配");
                plan = await ManualPlanReceiver(request, hash, token);
                token.ThrowIfCancellationRequested();
                DistributedEnsembleAssignment.Validate(plan);
                if (plan.Id != request || !string.Equals(plan.SongHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("手动分配与选曲不一致");
            }
            using var assignment = DistributedEnsembleAssignment.Begin(plan);
            var index = PartySongIdentity.Resolve(PlaylistManager.FilePathList.Select(s => s.FilePath).ToArray(), preferred, hash);
            var result = "missing";
            if (index >= 0)
            {
                MidiPlayerControl.StopLrc();
                result = await PlaylistManager.LoadPlayback(index, false, false, cancellation.Token) ? "ok" : "failed";
                MidiBard.Ui.OpenMainWindow();
            }
            else if (MidiBard.config.EnableCrossComputerSongSync && ExternalSongLoader is { } externalLoader)
            {
                var path = await RequestTransferredSong(request, hash, cancellation.Token);
                if (path != null)
                {
                    token.ThrowIfCancellationRequested();
                    MidiPlayerControl.StopLrc();
                    result = await externalLoader(path, token) ? "ok" : "failed";
                    MidiBard.Ui.OpenMainWindow();
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (result != "ok") api.ChatGui.PrintError(result == "missing"
                ? "[MidiBard] 缺少队长选择的 MIDI；请开启歌曲同步并加入演出房间，或手动导入文件"
                : "[MidiBard] 合奏歌曲载入失败，请检查 MIDI 文件及自动分配设置");
            Chat.SendMessage($"/p mbloadresult {request:N} {result}", () => !token.IsCancellationRequested && IsPartyContext(party, leader));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            api.PluginLog.Warning(ex, "Party song load failed");
            api.ChatGui.PrintError("[MidiBard] " + ex.Message);
            Chat.SendMessage($"/p mbloadresult {request:N} failed", () => !token.IsCancellationRequested && IsPartyContext(party, leader));
        }
        finally { EndLoad(cancellation); }
    }
}
