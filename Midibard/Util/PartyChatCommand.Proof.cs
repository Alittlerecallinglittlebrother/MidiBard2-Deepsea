#nullable enable
using System;
using System.Linq;
using MidiBard.Util;

namespace MidiBard;

internal static partial class PartyChatCommand
{
    internal static event Action<Guid, string, ulong, long>? StageProof;

    internal static void SendStageProof(Guid roomId, string challenge)
    {
        if (roomId == Guid.Empty || !ValidChallenge(challenge) || !api.ClientState.IsLoggedIn || api.PartyList.Length < 2) return;
        var party = api.PartyList.PartyId;
        var self = api.Player.ContentId;
        Chat.SendMessage($"/p mbstageproof {roomId:N} {challenge}",
            () => api.ClientState.IsLoggedIn && api.PartyList.PartyId == party && api.Player.ContentId == self && api.PartyList.Length >= 2);
    }

    private static bool ValidChallenge(string challenge) => challenge.Length == 32 && challenge.All(Uri.IsHexDigit);

    private static void ReceiveStageProof(string[] args, ulong sender)
    {
        if (sender == 0 || !api.ClientState.IsLoggedIn || api.PartyList.Length < 2 || args.Length != 2
            || !Guid.TryParseExact(args[0], "N", out var roomId) || roomId == Guid.Empty || !ValidChallenge(args[1])) return;
        StageProof?.Invoke(roomId, args[1], sender, api.PartyList.PartyId);
    }
}
