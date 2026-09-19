#nullable enable
using System;
using System.Globalization;

namespace MidiBard.StageIntegration;

internal sealed record EnsembleTransportCommand(string Action, string Hash, long At, long Offset, long Sequence)
{
    internal string[] Encode() => new[] { Action, Hash, At.ToString(CultureInfo.InvariantCulture),
        Offset.ToString(CultureInfo.InvariantCulture), Sequence.ToString(CultureInfo.InvariantCulture) };

    internal static bool TryParse(string[]? args, string hash, long now, long duration, out EnsembleTransportCommand? command)
    {
        command = null;
        if (args == null || args.Length != 5 || args[0] is not ("pause" or "resume" or "stop") || args[1] != hash
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || at < now - 5000 || at > now + 5000 || offset < 0 || offset > duration || sequence <= 0) return false;
        command = new(args[0], args[1], at, offset, sequence);
        return true;
    }
}

internal sealed class EnsembleTransportOrder
{
    private object? playback;
    private long party, lastSequence;
    private ulong leader;

    internal bool Accept(object currentPlayback, long currentParty, ulong currentLeader, long sequence)
    {
        if (currentLeader == 0 || sequence <= 0) return false;
        if (!ReferenceEquals(playback, currentPlayback) || party != currentParty || leader != currentLeader)
        {
            playback = currentPlayback; party = currentParty; leader = currentLeader; lastSequence = 0;
        }
        if (sequence <= lastSequence) return false;
        lastSequence = sequence;
        return true;
    }
}
