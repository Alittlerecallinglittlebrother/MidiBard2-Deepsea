#nullable enable
using System;
using System.Linq;
using System.Threading;
using BardStage.Core.Rooms;
using MidiBard.Managers.Ipc;

namespace MidiBard.Managers;

internal static class DistributedEnsembleAssignment
{
    private static readonly AsyncLocal<RoomSongPlan?> current = new();
    private static readonly AsyncLocal<bool> draft = new();
    internal static RoomSongPlan? Current => current.Value;
    internal static bool IsDraft => draft.Value;

    internal static IDisposable Begin(RoomSongPlan? plan, bool editing = false)
    {
        var previous = current.Value; var previousDraft = draft.Value;
        current.Value = plan; draft.Value = editing;
        return new Scope(() => { current.Value = previous; draft.Value = previousDraft; });
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }

    internal static MidiFileConfig CreateDraft(TrackInfo[] tracks, MidiFileConfig? saved)
    {
        var result = MidiFileConfigManager.GetMidiConfigFromTrack(tracks);
        if (saved?.Tracks.Count == tracks.Length)
        {
            result.AdaptNotes = saved.AdaptNotes; result.Speed = saved.Speed; result.ToneMode = saved.ToneMode;
            for (var i = 0; i < tracks.Length; i++)
            {
                result.Tracks[i].Enabled = saved.Tracks[i].Enabled;
                result.Tracks[i].Instrument = saved.Tracks[i].Instrument;
                result.Tracks[i].Transpose = saved.Tracks[i].Transpose;
                result.Tracks[i].AssignedCids = new(saved.Tracks[i].AssignedCids);
            }
        }
        else for (var i = 0; i < tracks.Length; i++)
            result.Tracks[i].Instrument = AutomaticEnsembleRules.ResolveInstrument(tracks[i].TrackName, null, tracks[i].InitialProgram);
        return result;
    }

    internal static RoomSongPlan Capture(Guid id, string hash, MidiFileConfig config)
    {
        var plan = new RoomSongPlan(id, hash, api.PartyList.PartyId, api.PartyList.GetPartyLeader()?.ContentId ?? 0,
            api.PartyList.Select(p => p.ContentId).ToArray(),
            config.Tracks.Select((t, i) => new RoomTrackAssignment(i, t.Enabled, t.Instrument, t.Transpose,
                MidiFileConfig.GetFirstCidInParty(t))).ToArray(), MidiBard.config.PlaySpeed, config.AdaptNotes, (int)config.ToneMode);
        plan.Validate();
        return plan;
    }

    internal static void Validate(RoomSongPlan plan)
        => plan.ValidateContext(api.PartyList.PartyId, api.PartyList.GetPartyLeader()?.ContentId ?? 0,
            api.PartyList.Select(p => p.ContentId));

    internal static MidiFileConfig Create(RoomSongPlan plan, TrackInfo[] tracks, string path)
    {
        Validate(plan);
        if (tracks.Length != plan.Tracks.Length || !string.Equals(PartySongIdentity.Hash(path), plan.SongHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("手动分配与载入的 MIDI 不一致，请重新下发");
        var result = MidiFileConfigManager.GetMidiConfigFromTrack(tracks);
        result.LeaderDistributed = true;
        result.AdaptNotes = plan.AdaptNotes; result.Speed = plan.Speed; result.ToneMode = (GuitarToneMode)plan.ToneMode;
        for (var i = 0; i < tracks.Length; i++)
        {
            var assignment = plan.Tracks[i]; var track = result.Tracks[i];
            track.Enabled = assignment.Enabled; track.Instrument = assignment.Instrument; track.Transpose = assignment.Transpose;
            track.AssignedCids = assignment.PerformerCid == 0 ? new() : new() { assignment.PerformerCid };
        }
        MidiFileConfigManager.UsingDefaultPerformer = false;
        return result;
    }
}
