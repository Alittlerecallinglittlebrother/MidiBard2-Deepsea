#nullable enable
using System;
using System.Linq;
using System.Threading;
using MidiBard.Managers.Ipc;

namespace MidiBard.Managers;

internal static class AutomaticEnsembleAssignment
{
    private static readonly AsyncLocal<bool> SoloLoad = new();
    private sealed record PartyOrder(ulong LeaderCid, long PartyId, ulong[] Members);
    private static PartyOrder? currentOrder;

    internal static bool IsEnabled => MidiBard.config.AutoAssignEnsembleTracks && !SoloLoad.Value;
    internal static bool IsSoloLoad => SoloLoad.Value;

    internal static IDisposable BeginSoloLoad()
    {
        var previous = SoloLoad.Value;
        SoloLoad.Value = true;
        return new SoloLoadScope(previous);
    }

    private sealed class SoloLoadScope(bool previous) : IDisposable
    {
        public void Dispose() => SoloLoad.Value = previous;
    }

    internal static string CaptureOrderToken()
    {
        if (!IsEnabled || !api.PartyList.IsPartyLeader()) return "";
        var order = PartyWatcher.GetDisplayedMemberCIDs();
        var token = AutomaticEnsembleRules.EncodeOrder(order);
        return AcceptOrderToken(token, api.Player.ContentId) ? token : "";
    }

    internal static bool AcceptOrderToken(string? token, ulong sender)
    {
        var currentLeader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        var party = api.PartyList.Select(p => p.ContentId).Where(cid => cid != 0).ToArray();
        if (sender == 0 || sender != currentLeader
            || !AutomaticEnsembleRules.TryDecodeOrder(token, party, out var order)) return false;
        Volatile.Write(ref currentOrder, new PartyOrder(currentLeader, api.PartyList.PartyId, order));
        return true;
    }

    internal static ulong[] GetCurrentOrder()
    {
        var currentLeader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
        if (currentLeader == api.Player.ContentId) CaptureOrderToken();
        var snapshot = Volatile.Read(ref currentOrder);
        var party = api.PartyList.Select(p => p.ContentId).Where(cid => cid != 0).ToArray();
        if (snapshot == null || currentLeader == 0 || currentLeader != snapshot.LeaderCid || snapshot.PartyId != api.PartyList.PartyId
            || !party.ToHashSet().SetEquals(snapshot.Members)) return Array.Empty<ulong>();
        return snapshot.Members.ToArray();
    }

    internal static MidiFileConfig Create(TrackInfo[] tracks, MidiFileConfig? saved)
    {
        var result = MidiFileConfigManager.GetMidiConfigFromTrack(tracks);
        var order = GetCurrentOrder();
        if (order.Length == 0) throw new InvalidOperationException("未取得队长的小队显示顺序，请由队长重新选曲，并确认全队使用新版插件");
        var assignments = AutomaticEnsembleRules.AssignTracks(tracks.Length, order);
        result.AutomaticallyAssigned = true;
        if (saved?.Tracks.Count == tracks.Length)
        {
            result.AdaptNotes = saved.AdaptNotes;
            result.ToneMode = saved.ToneMode;
            result.Speed = saved.Speed;
        }
        for (var i = 0; i < tracks.Length; i++)
        {
            var existing = saved?.Tracks.Count == tracks.Length ? saved.Tracks[i] : null;
            var track = result.Tracks[i];
            track.Instrument = AutomaticEnsembleRules.ResolveInstrument(tracks[i].TrackName,
                existing?.Instrument, tracks[i].InitialProgram);
            track.Transpose = existing?.Transpose ?? tracks[i].TransposeFromTrackName;
            track.Enabled = assignments[i] != 0;
            track.AssignedCids = assignments[i] == 0 ? new() : new() { assignments[i] };
        }
        MidiFileConfigManager.UsingDefaultPerformer = false;
        return result;
    }
}
