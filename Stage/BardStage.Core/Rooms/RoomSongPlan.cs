namespace BardStage.Core.Rooms;

// A selection-scoped plan, never a path or a user's configuration file.
public sealed record RoomTrackAssignment(int Index, bool Enabled, uint Instrument, int Transpose, ulong PerformerCid);
public sealed record RoomSongPlan(Guid Id, string SongHash, long PartyId, ulong LeaderCid,
    ulong[] Members, RoomTrackAssignment[] Tracks, float Speed, bool AdaptNotes, int ToneMode)
{
    public void Validate()
    {
        if (Id == Guid.Empty || SongHash is not { Length: 64 } || !SongHash.All(Uri.IsHexDigit)
            || LeaderCid == 0 || Members is not { Length: >= 2 and <= 8 }
            || Members.Any(cid => cid == 0) || Members.Distinct().Count() != Members.Length || !Members.Contains(LeaderCid))
            throw new InvalidDataException("手动分配的小队或歌曲信息无效");
        if (Tracks is not { Length: >= 1 and <= 100 } || !float.IsFinite(Speed) || Speed is < 0.1f or > 10f
            || ToneMode is < 0 or > 4)
            throw new InvalidDataException("手动分配的轨道数量或演奏设置无效");
        for (var i = 0; i < Tracks.Length; i++)
        {
            var track = Tracks[i];
            if (track == null || track.Index != i || track.Instrument > 28 || track.Transpose is < -120 or > 120
                || (track.PerformerCid != 0 && !Members.Contains(track.PerformerCid))
                || (track.Enabled && (track.Instrument == 0 || track.PerformerCid == 0)))
                throw new InvalidDataException($"第 {i + 1} 轨缺少有效的演奏人或乐器，请分配或取消勾选");
        }
        if (!Tracks.Any(t => t.Enabled)) throw new InvalidDataException("请至少启用一条已分配的轨道");
        foreach (var group in Tracks.Where(t => t.Enabled).GroupBy(t => t.PerformerCid))
            if (group.Select(t => t.Instrument).Distinct().Count() > 1)
                throw new InvalidDataException("同一位演奏人的启用轨道不能使用不同乐器");
    }

    public void ValidateContext(long partyId, ulong leaderCid, IEnumerable<ulong> members)
    {
        Validate();
        if (PartyId != partyId || LeaderCid != leaderCid || !Members.ToHashSet().SetEquals(members))
            throw new InvalidDataException("小队成员或队长已改变，请重新分配并下发");
    }

    public RoomSongPlan Copy() => this with { Members = [.. Members], Tracks = [.. Tracks] };
}

public sealed record RoomPlanRequest(Guid Id, string SongHash);
public sealed record RoomPlanResult(Guid Id, bool Published, bool Success, string Message, RoomSongPlan? Plan = null);
