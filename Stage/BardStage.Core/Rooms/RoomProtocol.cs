using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BardStage.Core.Rooms;

public enum RoomAction { Add, MoveUp, MoveDown, PlayNext, Remove, Requeue, Resolve, Reject, Reception, Settings, PlaybackMode, Start, Pause, Stop, Skip, Chat, ReceptionOwner, DeleteFinished, ClearFinished, Continuous, MoveTo }
public enum RoomRole { Presenter, Viewer }
public enum RoomExecutionAction { Adopt, Load, Start, Pause, Resume, Finish, Stop, Cancel }

public sealed class RoomCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RoomId { get; set; }
    public long Revision { get; set; }
    public long AuthorityEpoch { get; set; }
    public RoomAction Action { get; set; }
    public Guid? EntryId { get; set; }
    public Guid? TargetEntryId { get; set; }
    public Guid? SongId { get; set; }
    public Guid? RequestId { get; set; }
    public bool Value { get; set; }
    public int Number { get; set; }
    public RequestSettings? Settings { get; set; }
    public RoomChat? Chat { get; set; }
}

public sealed record RoomChat(string Name, string World, string Query, RequestChannel Channel);
public sealed record RoomResult(Guid Id, bool Success, string Message);
public sealed record RoomPlayback(bool Enabled, bool Loading, bool Paused, bool Playing, Guid? ActiveEntryId, string Status);
public sealed record RoomProofChallenge(string Challenge, long PartyId);
public sealed record RoomExecutionLease(long Epoch, long PartyId, ulong LeaderCid, bool Granted);
public sealed record RoomExecutionSignal(long Epoch, Guid PlaybackId, long Sequence, string Hash, string Kind, DateTimeOffset AtUtc);
public sealed class RoomExecutionCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Epoch { get; set; }
    public RoomExecutionAction Action { get; set; }
    public string Hash { get; set; } = "";
    public QueuePlaybackMode Mode { get; set; }
    public bool KeepInstruments { get; set; }
}
public sealed record RoomExecutionResult(Guid Id, long Epoch, bool Success, string Message, RoomExecutionSignal? State = null);

public sealed class RoomSnapshot
{
    public Guid RoomId { get; set; }
    public long Revision { get; set; }
    public CatalogState Catalog { get; set; } = new();
    public RoomPlayback Playback { get; set; } = new(false, false, false, false, null, "自动演奏未开始");
    public bool PresenterReceivesChat { get; set; }
    public bool CanControl { get; set; }
    public long AuthorityEpoch { get; set; }
    public ulong ExecutorCid { get; set; }
    public string ExecutionStatus { get; set; } = "";

    internal RoomSnapshot ForController() => new()
    {
        RoomId = RoomId, Revision = Revision, Catalog = Catalog, Playback = Playback,
        PresenterReceivesChat = PresenterReceivesChat, CanControl = true,
        AuthorityEpoch = AuthorityEpoch, ExecutorCid = ExecutorCid, ExecutionStatus = ExecutionStatus,
    };

    internal RoomSnapshot ForViewer() => new()
    {
        RoomId = RoomId, Revision = Revision, Playback = Playback,
        AuthorityEpoch = AuthorityEpoch, ExecutorCid = ExecutorCid, ExecutionStatus = ExecutionStatus,
        Catalog = new CatalogState
        {
            SelectedSetlistId = Catalog.RequestSettings.TargetSetlistId,
            RequestSettings = new() { TargetSetlistId = Catalog.RequestSettings.TargetSetlistId, PlaybackMode = Catalog.RequestSettings.PlaybackMode },
            Setlists = Catalog.Setlists.Where(s => s.Id == Catalog.RequestSettings.TargetSetlistId).Select(s => new ShowSetlist
            {
                Id = s.Id, Name = s.Name, GapSeconds = s.GapSeconds, LockedNextEntryId = s.LockedNextEntryId,
                Entries = s.Entries.Where(e => e.Status is EntryStatus.Queued or EntryStatus.InProgress).Select(e => new SetlistEntry
                {
                    Id = e.Id, Kind = e.Kind, SongId = e.SongId, Title = e.Title, Status = e.Status,
                    DurationSeconds = e.DurationSeconds, PlaybackSpeed = e.PlaybackSpeed,
                    StartedAtUtc = e.StartedAtUtc, PausedAtUtc = e.PausedAtUtc, PausedSeconds = e.PausedSeconds,
                }).ToList(),
            }).ToList(),
        },
    };
}

public sealed class RoomPacket
{
    public int Version { get; set; } = 1;
    public string Type { get; set; } = "";
    public string? Key { get; set; }
    public Guid RoomId { get; set; }
    public RoomRole Role { get; set; }
    public RoomCommand? Command { get; set; }
    public RoomSnapshot? Snapshot { get; set; }
    public RoomResult? Result { get; set; }
    public RoomProofChallenge? Proof { get; set; }
    public RoomExecutionLease? Lease { get; set; }
    public RoomExecutionCommand? Execution { get; set; }
    public RoomExecutionResult? ExecutionResult { get; set; }
    public RoomExecutionSignal? Signal { get; set; }
}

public sealed record RoomInvite(string Host, int Port, string Fingerprint, string Key, Guid RoomId, RoomRole Role = RoomRole.Presenter)
{
    public string Encode()
    {
        Validate();
        return "mbroom1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, RoomJson.Options));
    }

    public static RoomInvite Decode(string text)
    {
        text = text.Trim();
        if (text.Length > 4096 || !text.StartsWith("mbroom1.", StringComparison.Ordinal)) throw new InvalidOperationException("邀请口令格式无效");
        try
        {
            var invite = JsonSerializer.Deserialize<RoomInvite>(Convert.FromBase64String(text[8..]), RoomJson.Options)
                ?? throw new InvalidOperationException("邀请口令为空");
            invite.Validate();
            return invite;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { throw new InvalidOperationException("邀请口令损坏，请重新复制完整口令"); }
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Role)) throw new InvalidOperationException("邀请口令中的角色无效");
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Uri.CheckHostName(Host) == UriHostNameType.Unknown
            || Port is < 1 or > 65535 || RoomId == Guid.Empty) throw new InvalidOperationException("公网节点地址或端口无效");
        if (Fingerprint == null || Key == null || Fingerprint.Length != 64 || Key.Length != 64
            || !Fingerprint.All(Uri.IsHexDigit) || !Key.All(Uri.IsHexDigit)) throw new InvalidOperationException("邀请口令中的房间凭据无效");
    }
}

public static class RoomJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
