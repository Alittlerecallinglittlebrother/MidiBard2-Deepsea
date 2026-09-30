using System.Security.Cryptography;

namespace BardStage.Core.Rooms;

/// <summary>Content-addressed copies, shared by local processes without sharing personal playlists.</summary>
public sealed class LocalSongStore(string directory)
{
    private string SongPath(string hash)
    {
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("歌曲指纹无效");
        return Path.Combine(directory, hash.ToUpperInvariant() + ".mid");
    }
    public async Task<string> Publish(string source, string hash, CancellationToken token)
    {
        var target = SongPath(hash);
        Directory.CreateDirectory(directory);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                if (input.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("本机歌曲大小须为 1 字节～16 MB");
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await input.CopyToAsync(output, token);
            }
            if (!await Matches(temp, hash, token)) throw new InvalidDataException("歌曲读取后被修改，请重新读取轨道");
            token.ThrowIfCancellationRequested();
            if (File.Exists(target) && await Matches(target, hash, token)) return target;
            try { File.Move(temp, target, false); }
            catch (IOException) when (File.Exists(target))
            {
                // Another local conductor may have published identical content meanwhile.
                if (await Matches(target, hash, token)) return target;
                File.Move(temp, target, true);
            }
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<string> Receive(string hash, CancellationToken token)
    {
        var path = SongPath(hash);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                if (!await Matches(path, hash, token)) throw new InvalidDataException("本机歌曲缓存校验失败，请由主控重新下发");
                return path;
            }
            await Task.Delay(50, token);
        }
    }
    private static async Task<bool> Matches(string path, string hash, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return stream.Length is > 0 and <= 16 * 1024 * 1024
            && Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
