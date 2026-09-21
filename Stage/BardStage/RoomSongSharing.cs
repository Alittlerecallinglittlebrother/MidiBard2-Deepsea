using System.Security.Cryptography;
using BardStage.Core.Rooms;

namespace BardStage;

/// <summary>
/// Pulls one bounded chunk at a time over the room's pinned TLS connection.
/// Network data can select only the currently offered content hash, never a path.
/// Framework Tick owns network progress; disk work runs outside the game thread.
/// </summary>
public sealed class RoomSongSharing : IDisposable
{
    public const int ChunkBytes = 64 * 1024;
    public const int MaxSongBytes = 16 * 1024 * 1024;
    private readonly StageRoom room;
    private readonly Func<bool> enabled;
    private readonly Func<RoomPartyState> party;
    private readonly Func<RoomPeer, bool> authorized;
    private readonly string cache;
    private readonly object gate = new();
    private readonly Dictionary<RoomPeer, PendingRequest> requests = [];
    private Source? source;
    private Download? download;
    private bool disposed;
    public string Status { get; private set; } = "当前歌曲分发未开始";

    public RoomSongSharing(StageRoom room, string directory, Func<bool> enabled,
        Func<RoomPartyState> party, Func<RoomPeer, bool> authorized)
    {
        this.room = room; this.enabled = enabled; this.party = party; this.authorized = authorized;
        cache = Path.Combine(directory, "SongCache");
    }

    public void Offer(string path, string hash)
    {
        lock (gate)
        {
            if (disposed || !enabled() || !room.IsCaptain || !ValidHash(hash)) return;
            ClearSource();
            source = new(path, hash, room.Id, party());
            Status = "当前歌曲已可供队员接收";
        }
    }

    public string? ResolveCached(string hash)
    {
        if (!enabled() || !ValidHash(hash)) return null;
        var directory = Path.Combine(cache, hash.ToUpperInvariant());
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.mid"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length <= MaxSongBytes && Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public async Task<string?> ReceiveAsync(string hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidHash(hash)) throw new InvalidDataException("歌曲指纹无效");
        if (!enabled()) return null;
        var cached = await Task.Run(() => ResolveCached(hash), token);
        token.ThrowIfCancellationRequested();
        if (cached != null) return cached;
        Download item;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(RoomSongSharing));
            if (!room.IsViewer || !room.Connected)
                throw new InvalidOperationException("请先使用队员查看邀请码加入主控的演出房间");
            CancelDownload("已选择另一首歌曲");
            item = new(hash, room.Id, room.ConnectionGeneration, party(), token);
            download = item;
            Status = "正在接收队长选择的 MIDI";
            RequestChunk(item);
        }
        try { return await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(90), token); }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(download, item)) download = null;
                item.Cancellation.Cancel(); item.Buffer.Dispose();
                if (item.Save != null)
                    _ = item.Save.ContinueWith(t => { _ = t.Exception; item.Cancellation.Dispose(); }, TaskScheduler.Default);
                else item.Cancellation.Dispose();
            }
        }
    }

    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            var context = party();
            if (source != null && (!enabled() || !room.IsCaptain || source.RoomId != room.Id || source.Party != context))
                ClearSource();
            if (download is { } active && (!enabled() || !room.Connected || active.RoomId != room.Id
                || active.Generation != room.ConnectionGeneration || active.Party != context || active.Cancellation.IsCancellationRequested))
                CancelDownload("房间连接、小队或队长已改变，请重新选曲");
            for (var i = 0; i < 16 && room.TrySongRequest(out var peer, out var request); i++)
                requests[peer] = new(request, DateTimeOffset.UtcNow.AddSeconds(40));
            ServeRequests();
            while (room.TrySongPacket(out var packet)) ReceivePacket(packet);
            if (download is { Save.IsCompleted: true } completed)
            {
                try
                {
                    completed.Cancellation.Token.ThrowIfCancellationRequested();
                    var path = completed.Save.GetAwaiter().GetResult();
                    Status = "MIDI 已校验并缓存";
                    completed.Completion.TrySetResult(path);
                }
                catch (Exception ex) { FailDownload(ex.Message); }
            }
        }
    }

    private void ServeRequests()
    {
        foreach (var (peer, pending) in requests.ToArray())
        {
            if (!peer.IsAlive) { requests.Remove(peer); continue; }
            var request = pending.Request;
            RoomPacket? response = null;
            string? error = null;
            if (!enabled() || source == null || !source.Hash.Equals(request.Hash, StringComparison.OrdinalIgnoreCase))
                error = "主控尚未开放这首 MIDI，请开启歌曲分发并重新选曲";
            else if (!authorized(peer))
            {
                if (pending.Deadline > DateTimeOffset.UtcNow) continue;
                error = "未能确认小队身份，请重新加入房间";
            }
            else
            {
                var current = source;
                current.Read ??= Task.Run(() => ReadSource(current.Path, current.Hash, current.Cancellation.Token));
                if (!current.Read.IsCompleted) continue;
                try
                {
                    var data = current.Read.GetAwaiter().GetResult();
                    var offset = checked(request.Index * ChunkBytes);
                    if (offset >= data.Length) throw new InvalidDataException("分片编号无效");
                    var chunk = data.AsSpan(offset, Math.Min(ChunkBytes, data.Length - offset)).ToArray();
                    response = new() { Type = "songChunk", RoomId = room.Id,
                        SongChunk = new(request.Id, request.Hash, request.Index, data.Length, SafeName(source.Path), chunk) };
                }
                catch (Exception) { error = "MIDI 无法分发：文件已改变、格式无效或超过 16 MiB"; }
            }
            response ??= new() { Type = "songResult", RoomId = room.Id, SongResult = new(request.Id, request.Hash, false, error!) };
            if (peer.TrySend(response)) requests.Remove(peer);
        }
    }

    private void RequestChunk(Download item)
    {
        if (!room.SendSongRequest(new(item.Id, item.Hash, item.Index)))
            FailDownload("歌曲请求未发送，请重新连接演出房间");
    }

    private void ReceivePacket(RoomPacket packet)
    {
        var item = download;
        if (item == null || item.Completion.Task.IsCompleted || item.Save != null) return;
        if (packet.SongResult is { } result && result.Id == item.Id)
        { FailDownload(result.Message); return; }
        if (packet.SongChunk is not { } chunk || chunk.Id != item.Id) return;
        try
        {
            if (!string.Equals(chunk.Hash, item.Hash, StringComparison.OrdinalIgnoreCase) || chunk.Index != item.Index
                || chunk.Length is < 14 or > MaxSongBytes || chunk.Data == null
                || chunk.Data.Length != Math.Min(ChunkBytes, chunk.Length - item.Index * ChunkBytes)
                || (item.Length != 0 && (item.Length != chunk.Length || item.FileName != chunk.FileName)))
                throw new InvalidDataException("MIDI 分片不完整或长度不一致");
            item.Length = chunk.Length; item.FileName = chunk.FileName;
            item.Buffer.Write(chunk.Data); item.Index++;
            Status = $"正在接收 MIDI：{item.Buffer.Length * 100 / item.Length}%";
            if (item.Buffer.Length == item.Length)
            {
                var bytes = item.Buffer.ToArray();
                item.Save = Task.Run(() => SaveVerified(bytes, item.Hash, item.FileName, item.Cancellation.Token));
            }
            else RequestChunk(item);
        }
        catch (Exception ex) { FailDownload(ex.Message); }
    }

    private string SaveVerified(byte[] bytes, string hash, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateMidi(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MIDI SHA256 校验失败，请重新选曲");
        var directory = Path.Combine(cache, hash.ToUpperInvariant());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SafeName(name));
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadSource(string path, string hash, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 14 or > MaxSongBytes) throw new InvalidDataException();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
        ValidateMidi(bytes);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        return bytes;
    }

    private static void ValidateMidi(byte[] bytes)
    {
        if (bytes.Length < 14 || !bytes.AsSpan(0, 4).SequenceEqual("MThd"u8)
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4)) != 6)
            throw new InvalidDataException("只支持标准 .mid / .midi 文件");
    }

    private static string SafeName(string name)
    {
        // Always use one sanitized leaf name under the SHA256 directory.
        var leaf = Path.GetFileNameWithoutExtension(name ?? "");
        leaf = new string(leaf.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).Take(120).ToArray()).Trim().TrimEnd('.');
        if (leaf.Length == 0 || leaf is "." or "..") leaf = "song";
        if (System.Text.RegularExpressions.Regex.IsMatch(leaf, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            leaf = "_" + leaf;
        return leaf + ".mid";
    }

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private void FailDownload(string reason)
    {
        Status = "歌曲接收未完成：" + reason;
        download?.Completion.TrySetException(new InvalidOperationException(Status));
    }
    private void CancelDownload(string reason)
    {
        if (download == null) return;
        download.Cancellation.Cancel(); Status = reason;
        download.Completion.TrySetCanceled();
    }
    private void ClearSource()
    {
        var old = source; source = null;
        if (old == null) return;
        old.Cancellation.Cancel();
        if (old.Read != null) _ = old.Read.ContinueWith(t => { _ = t.Exception; old.Cancellation.Dispose(); }, TaskScheduler.Default);
        else old.Cancellation.Dispose();
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; ClearSource(); CancelDownload("已离开歌曲分发"); requests.Clear();
        }
    }

    private sealed record PendingRequest(RoomSongRequest Request, DateTimeOffset Deadline);
    private sealed class Source(string path, string hash, Guid roomId, RoomPartyState party)
    {
        public string Path { get; } = path;
        public string Hash { get; } = hash;
        public Guid RoomId { get; } = roomId;
        public RoomPartyState Party { get; } = party;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<byte[]>? Read;
    }
    private sealed class Download(string hash, Guid roomId, int generation, RoomPartyState party, CancellationToken token)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Hash { get; } = hash;
        public Guid RoomId { get; } = roomId;
        public int Generation { get; } = generation;
        public RoomPartyState Party { get; } = party;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Buffer { get; } = new();
        public Task<string>? Save;
        public int Index, Length;
        public string FileName = "";
    }
}
