namespace BardStage.Core;

public sealed class CatalogState
{
    public int SchemaVersion { get; set; } = 3;
    public List<SongEntry> Songs { get; set; } = [];
    public bool SharedLibraryInitialized { get; set; }
    public List<ShowSetlist> Setlists { get; set; } = [];
    public Guid? SelectedSetlistId { get; set; }
    public List<SongRequest> Requests { get; set; } = [];
    public RequestSettings RequestSettings { get; set; } = new();
    public List<ShowSession> Sessions { get; set; } = [];
}

public sealed class RequestSettings
{
    public bool AutoArrange { get; set; }
    public QueuePlaybackMode PlaybackMode { get; set; }
    public bool IsOpen { get; set; }
    public Guid? TargetSetlistId { get; set; }
    public List<RequestChannel> Channels { get; set; } = [RequestChannel.Say, RequestChannel.Tell];
    public string Prefix { get; set; } = "点歌";
    public int MaxOutstandingPerPerson { get; set; } = 2;
    public int DuplicateCooldownSeconds { get; set; } = 30;
    public int MaxQueueSize { get; set; } = 50;
}

public enum QueuePlaybackMode { Solo, Ensemble }

public enum RequestChannel
{
    Manual,
    Say,
    Tell,
    Party,
    Shout,
    Yell,
}

public enum RequestStatus
{
    Pending,
    Deferred,
    Arranged,
    Rejected,
    Cancelled,
}

public sealed class SongRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SetlistId { get; set; }
    public string SetlistName { get; set; } = string.Empty;
    public string RequesterName { get; set; } = string.Empty;
    public string RequesterWorld { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public RequestChannel Channel { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public RequestStatus Status { get; set; }
    public Guid? SongId { get; set; }
    public Guid? SetlistEntryId { get; set; }
    public string ResolutionNote { get; set; } = string.Empty;
}

public sealed class SongEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public int TrackCount { get; set; }
    public List<string> Aliases { get; set; } = [];
    public string Arranger { get; set; } = string.Empty;
    public int PerformerCount { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset ImportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShowSetlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<SetlistEntry> Entries { get; set; } = [];
    public Guid? LockedNextEntryId { get; set; }
    public int GapSeconds { get; set; } = 3;
    public DateTimeOffset? TargetEndUtc { get; set; }
}

public sealed class SetlistEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EntryKind Kind { get; set; }
    public Guid? SongId { get; set; }
    public string Title { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public string Notes { get; set; } = string.Empty;
    public EntryStatus Status { get; set; } = EntryStatus.Queued;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public DateTimeOffset? PausedAtUtc { get; set; }
    public double PausedSeconds { get; set; }
}

public enum EntryKind
{
    Song,
    Talk,
    Break,
}

public enum EntryStatus
{
    Queued,
    InProgress,
    Completed,
    Skipped,
}

public sealed class ImportReport
{
    public int Added { get; set; }
    public int Duplicates { get; set; }
    public int Relocated { get; set; }
    public List<string> Errors { get; set; } = [];
}
