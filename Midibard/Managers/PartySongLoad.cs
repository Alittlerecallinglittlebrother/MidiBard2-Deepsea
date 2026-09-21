#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MidiBard.Managers;

// Request identities keep local chat echoes and late member replies out of a newer load.
internal sealed class PartySongLoad
{
    private readonly Dictionary<ulong, string> pending;
    private readonly object gate = new();
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Guid Id { get; } = Guid.NewGuid();
    internal Task<bool> Completion => completion.Task;
    internal string WaitingFor { get { lock (gate) return string.Join("、", pending.Values); } }

    internal PartySongLoad(IEnumerable<(ulong Id, string Name)> members)
    {
        pending = members.ToDictionary(m => m.Id, m => m.Name);
        if (pending.Count == 0) completion.TrySetResult(true);
    }

    internal void Receive(Guid request, ulong member, string result)
    {
        lock (gate)
        {
            if (request != Id || !pending.TryGetValue(member, out var name) || completion.Task.IsCompleted) return;
            if (result == "ok")
            {
                pending.Remove(member);
                if (pending.Count == 0) completion.TrySetResult(true);
            }
            else if (result is "missing" or "failed")
                completion.TrySetException(new InvalidOperationException(result == "missing"
                    ? $"{name} 缺少相同的 MIDI 文件，请检查歌曲同步开关和演出房间连接，或手动导入"
                    : $"{name} 未能载入歌曲，请检查该队员的 MidiBard 提示"));
        }
    }
}

internal static class PartySongIdentity
{
    private sealed record Cached(long Length, DateTime Modified, string Hash);
    private static readonly ConcurrentDictionary<string, Cached> cache = new(StringComparer.OrdinalIgnoreCase);

    internal static string Hash(string path)
    {
        var file = new FileInfo(path);
        if (cache.TryGetValue(file.FullName, out var known) && known.Length == file.Length && known.Modified == file.LastWriteTimeUtc)
            return known.Hash;
        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (cache.Count >= 4096) cache.Clear();
        cache[file.FullName] = new(file.Length, file.LastWriteTimeUtc, hash);
        return hash;
    }

    internal static int Resolve(IReadOnlyList<string> paths, int preferred, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return -1;
        bool Matches(int index)
        {
            try { return Hash(paths[index]).Equals(hash, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
        if (preferred >= 0 && preferred < paths.Count && Matches(preferred)) return preferred;
        for (var i = 0; i < paths.Count; i++) if (i != preferred && Matches(i)) return i;
        return -1;
    }
}
