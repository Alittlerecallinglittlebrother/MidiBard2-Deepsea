using System.Security.Cryptography;
using BardStage;
using BardStage.Core.Rooms;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

internal static class SongSyncRuntimeChecks
{
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        using (var f = new Fixture(output, 7))
        {
            await f.Connect();
            var (path, hash) = f.Song("空曲库七端", 230000);
            f.Host.Sharing.Offer(path, hash);
            var jobs = f.Clients.Select(c => c.Sharing.ReceiveAsync(hash, CancellationToken.None)).ToArray();
            await f.Until(() => jobs.All(t => t.IsCompleted));
            var results = await Task.WhenAll(jobs);
            Check(results.Distinct().Count() == 7 && results.All(p => p != null && Fingerprint(p) == hash),
                "seven independent empty caches receive identical MIDI over real TLS");
            Check(f.Clients.All(c => c.Controller.State.Songs.Count == 0 && c.Controller.Room!.RemoteState.Songs.Count == 0),
                "transfer never imports songs or exposes the private catalog to viewers");
            foreach (var result in results) _ = MidiFile.Read(result!);
            Check(results.All(p => Path.GetFileName(p) == "空曲库七端.mid"), "validated cache retains the song title and parses as MIDI");
            f.Host.Enabled = false;
            var reused = await f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            Check(reused == results[0], "verified content cache can be reused without another network download");
            Check(!Directory.EnumerateFiles(f.Directory, "*.part", SearchOption.AllDirectories).Any(), "completed transfers leave no partial files");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (path, hash) = f.Song("身份验证", 160000);
            f.Host.Sharing.Offer(path, hash); f.AllowData = false;
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            for (var i = 0; i < 30; i++) { f.Pump(); await Task.Delay(5); }
            Check(!job.IsCompleted && f.Clients[0].Controller.Room!.Connected, "unproven room viewers wait without receiving MIDI");
            f.AllowData = true;
            await f.Until(() => job.IsCompleted); await job;
            Check(job.Result != null, "transfer resumes after party identity approval");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (path, hash) = f.Song("关闭开关", 64);
            f.Host.Enabled = false; f.Host.Sharing.Offer(path, hash);
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            await f.Until(() => job.IsCompleted);
            await Failed(job, "disabled sender refuses file requests");
            f.Clients[0].Enabled = false;
            Check(await f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None) == null, "disabled receiver does not request files");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (path, hash) = f.Song("取消", 250000);
            f.Host.Sharing.Offer(path, hash); f.AllowData = false;
            using var cancel = new CancellationTokenSource();
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, cancel.Token);
            await f.Until(() => f.Host.Controller.Room!.Server!.Participants.Count == 1);
            cancel.Cancel();
            await Failed(job, "canceled selection cannot publish cache or continue loading");
            Check(f.Clients[0].Sharing.ResolveCached(hash) == null, "canceled transfer has no accepted cache");
        }
        foreach (var change in new[] { "leader", "disconnect", "supersede" })
        {
            using var f = new Fixture(output, 1);
            await f.Connect();
            var (path, hash) = f.Song(change, 150000);
            f.Host.Sharing.Offer(path, hash); f.AllowData = false;
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            for (var i = 0; i < 15; i++) { f.Pump(); await Task.Delay(5); }
            var generation = f.Clients[0].Controller.Room!.ConnectionGeneration;
            if (change == "leader")
            {
                f.Host.Party = f.Host.Party with { LeaderCid = 2 };
                f.Clients[0].Party = f.Clients[0].Party with { LeaderCid = 2 };
            }
            else if (change == "disconnect") f.Host.Controller.Room!.Server!.Participants[0].Peer.Dispose();
            else
            {
                var next = f.Song("next", 200);
                f.Host.Sharing.Offer(next.Path, next.Hash);
            }
            await f.Until(() => job.IsCompleted);
            await Failed(job, change + " rejects stale transfer");
            if (change == "disconnect")
            {
                f.AllowData = true;
                await f.Until(() => f.Clients[0].Controller.Room!.Connected && f.Clients[0].Controller.Room!.ConnectionGeneration > generation);
                var retry = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
                await f.Until(() => retry.IsCompleted); await retry;
                Check(retry.Result != null, "explicit re-selection works after a fresh TLS connection");
            }
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (_, hash) = f.Song("digest", 200);
            var wrong = File.ReadAllBytes(f.Song("wrong", 200).Path);
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            (RoomPeer Peer, RoomSongRequest Request) request = default;
            await f.Until(() => f.Host.Controller.Room!.Server!.TrySongRequest(out request), false);
            request.Peer!.Send(new() { Type = "songChunk", RoomId = f.Host.Controller.Room!.Id,
                SongChunk = new(request.Request!.Id, hash, 0, wrong.Length, "../escape.mid", wrong) });
            await f.Until(() => job.IsCompleted, false);
            await Failed(job, "SHA256 mismatch is rejected before cache publication");
            Check(f.Clients[0].Sharing.ResolveCached(hash) == null, "mismatched MIDI is never accepted");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (_, hash) = f.Song("bad-size", 100);
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            (RoomPeer Peer, RoomSongRequest Request) request = default;
            await f.Until(() => f.Host.Controller.Room!.Server!.TrySongRequest(out request), false);
            request.Peer!.Send(new() { Type = "songChunk", RoomId = f.Host.Controller.Room!.Id,
                SongChunk = new(request.Request!.Id, hash, 0, RoomSongSharing.MaxSongBytes + 1, "bad.mid", [1, 2]) });
            await f.Until(() => job.IsCompleted, false);
            await Failed(job, "oversized or malformed chunks are refused");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (path, hash) = f.Song("源文件改变", 200);
            f.Host.Sharing.Offer(path, hash);
            File.AppendAllText(path, "changed");
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            await f.Until(() => job.IsCompleted);
            await Failed(job, "source modified after selection cannot be served under the old hash");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (path, hash) = f.Song("路径隔离", 100);
            var bytes = File.ReadAllBytes(path);
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            (RoomPeer Peer, RoomSongRequest Request) request = default;
            await f.Until(() => f.Host.Controller.Room!.Server!.TrySongRequest(out request), false);
            request.Peer!.Send(new() { Type = "songChunk", RoomId = f.Host.Controller.Room!.Id,
                SongChunk = new(request.Request!.Id, hash, 0, bytes.Length, "../../../../escape.mid", bytes) });
            await f.Until(() => job.IsCompleted, false);
            var saved = await job;
            Check(saved != null && saved.StartsWith(Path.Combine(f.Clients[0].Controller.DataDirectory, "SongCache", hash),
                StringComparison.OrdinalIgnoreCase) && Path.GetFileName(saved) == "escape.mid",
                "remote filenames cannot escape the content hash cache directory");
        }
        using (var f = new Fixture(output, 1))
        {
            await f.Connect();
            var (path, hash) = f.Song("慢端", 2400000);
            f.Host.Sharing.Offer(path, hash);
            var job = f.Clients[0].Sharing.ReceiveAsync(hash, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!job.IsCompleted && DateTime.UtcNow < deadline)
            {
                f.Pump();
                await Task.Delay(40);
            }
            var saved = await job.WaitAsync(TimeSpan.FromSeconds(1));
            Check(saved != null && Fingerprint(saved) == hash && f.Clients[0].Controller.Room!.Connected,
                "slow framework pump downloads multiple MiB without flooding or disconnecting room control");
        }
        Console.WriteLine("SONG SYNC CHECKS COMPLETE");
    }

    private static async Task Failed(Task<string?> task, string message)
    {
        try { await task; throw new Exception("Expected transfer failure: " + message); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { Check(true, message); }
    }
    private static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; }
        public Node Host { get; }
        public Node[] Clients { get; }
        public bool AllowData = true;
        public Fixture(string output, int count)
        {
            Directory = Path.Combine(output, "song-sync-" + Guid.NewGuid().ToString("N"));
            Host = new(Directory, 1, _ => AllowData);
            Check(Host.Controller.Room!.Create(0), "room created");
            Clients = Enumerable.Range(2, count).Select(i => new Node(Directory, (ulong)i, _ => false)).ToArray();
        }
        public async Task Connect()
        {
            var invite = Host.Controller.Room!.Invite("127.0.0.1", Host.Controller.Room.LocalPort, RoomRole.Viewer);
            foreach (var client in Clients) Check(client.Controller.Room!.Join(invite, RoomRole.Viewer), "viewer joined");
            await Until(() => Clients.All(c => c.Controller.Room!.Connected));
        }
        public (string Path, string Hash) Song(string title, int payload)
        {
            var path = Path.Combine(Directory, title + ".mid");
            new MidiFile(new TrackChunk(new TextEvent(new string('x', payload) + title),
                new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)80),
                new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 96 })).Write(path);
            return (path, Fingerprint(path));
        }
        public void Pump(bool serve = true)
        {
            Host.Controller.Room!.Tick(); if (serve) Host.Sharing.Tick();
            foreach (var node in Clients) { node.Controller.Room!.Tick(); node.Sharing.Tick(); }
        }
        public async Task Until(Func<bool> condition, bool serve = true)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                Pump(serve); if (DateTime.UtcNow > deadline) throw new TimeoutException(string.Join("; ", Clients.Select(c => c.Sharing.Status)));
                await Task.Delay(5);
            }
        }
        public void Dispose()
        {
            foreach (var node in Clients.Append(Host)) { node.Sharing.Dispose(); node.Controller.Dispose(); }
        }
    }
    private sealed class Node
    {
        public StageController Controller { get; }
        public RoomSongSharing Sharing { get; }
        public RoomPartyState Party;
        public bool Enabled = true;
        public Node(string directory, ulong cid, Func<RoomPeer, bool> authorized)
        {
            Controller = new(Path.Combine(directory, cid.ToString()));
            Controller.Room = new(Controller);
            Party = new(100, cid, 1);
            Sharing = new(Controller.Room, Controller.DataDirectory, () => Enabled, () => Party, authorized);
            Controller.Room.Songs = Sharing;
        }
    }
}
