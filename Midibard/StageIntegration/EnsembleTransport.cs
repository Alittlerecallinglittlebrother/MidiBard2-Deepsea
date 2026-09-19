#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using MidiBard.Control.MidiControl;
using MidiBard.IPC;
using MidiBard.Managers.Ipc;
using MidiBard.Util;
using MidiBard.Util.Lyrics;

namespace MidiBard.StageIntegration;

internal static class EnsembleTransport
{
    private static readonly ConditionalWeakTable<Playback, Identity> identities = new();
    private sealed record Identity(string Hash);
    private static Playback? pending;
    private static long resumeAt, position, lastSent;
    private static readonly EnsembleTransportOrder order = new();
    private static long pendingParty;
    private static ulong pendingLeader;

    private static string Hash()
    {
        var playback = MidiBard.CurrentPlayback ?? throw new InvalidOperationException("没有载入歌曲");
        return identities.GetValue(playback, _ => new Identity(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(playback.FilePath))))).Hash;
    }

    internal static void Send(string action)
    {
        if (!api.PartyList.IsPartyLeader()) throw new InvalidOperationException("请由队长控制合奏");
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var at = action == "resume" ? time + 1800 : time;
        var offset = MidiBard.CurrentPlayback?.GetCurrentTime<MetricTimeSpan>().TotalMicroseconds ?? 0;
        var sequence = Math.Max(DateTime.UtcNow.Ticks, Interlocked.Read(ref lastSent) + 1);
        Interlocked.Exchange(ref lastSent, sequence);
        var values = new EnsembleTransportCommand(action, Hash(), at, offset, sequence).Encode();
        if (MidiBard.config.playOnMultipleDevices) Chat.SendMessage("/p mbtransport " + string.Join(" ", values));
        else IPCEnvelope.Create(MessageTypeCode.StageTransport, values).BroadCast();
        Receive(values);
    }

    internal static void Receive(string[] args)
    {
        try { Apply(args); }
        catch (Exception ex)
        {
            pending = null;
            api.PluginLog.Warning(ex, "Unable to apply ensemble playback command");
        }
    }

    private static void Apply(string[] args)
    {
        if (MidiBard.CurrentPlayback == null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!EnsembleTransportCommand.TryParse(args, Hash(), now,
            MidiBard.CurrentPlayback.GetDuration<MetricTimeSpan>().TotalMicroseconds, out var command)
            || !order.Accept(MidiBard.CurrentPlayback, api.PartyList.PartyId, api.PartyList.GetPartyLeader()?.ContentId ?? 0, command!.Sequence)) return;
        pending = null;
        if (command.Action == "stop") { MidiPlayerControl.Stop(); return; }
        MidiPlayerControl.Pause();
        MidiPlayerControl.SetTime(new MetricTimeSpan(command.Offset));
        if (command.Action != "resume") return;
        pending = MidiBard.CurrentPlayback; resumeAt = command.At; position = command.Offset;
        pendingParty = api.PartyList.PartyId; pendingLeader = api.PartyList.GetPartyLeader()?.ContentId ?? 0;
    }

    internal static void Tick()
    {
        if (pending == null) return;
        if (!ReferenceEquals(pending, MidiBard.CurrentPlayback) || !api.ClientState.IsLoggedIn
            || pendingParty != api.PartyList.PartyId || pendingLeader != api.PartyList.GetPartyLeader()?.ContentId)
        { pending = null; return; }
        var late = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - resumeAt;
        if (late < 0) return;
        pending = null;
        if (late > 5000) return;
        var offset = position + (long)(late * 1000 * MidiBard.CurrentPlayback!.Speed);
        if (offset >= MidiBard.CurrentPlayback.GetDuration<MetricTimeSpan>().TotalMicroseconds) return;
        MidiPlayerControl.SetTime(new MetricTimeSpan(offset));
        MidiBard.CurrentPlayback.Start();
        MidiPlayerControl._stat = MidiPlayerControl.e_stat.Playing;
        Lrc.Play();
    }
}
