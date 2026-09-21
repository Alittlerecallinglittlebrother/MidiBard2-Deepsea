using BardStage;
using BardStage.Core.Rooms;

internal static class ManualAssignmentRuntimeChecks
{
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        using var f = new Fixture(output);
        await f.Connect();
        var plan = f.Plan();
        await f.Host.Sharing.PublishAsync(plan, CancellationToken.None);
        var receive = f.Clients.Select(c => c.Sharing.ReceiveAsync(plan.Id, plan.SongHash, CancellationToken.None)).ToArray();
        await f.Until(() => receive.All(t => t.IsCompleted));
        foreach (var task in receive)
        {
            var copy = await task;
            Check(copy.Tracks.SequenceEqual(plan.Tracks) && copy.Members.SequenceEqual(plan.Members)
                && copy.Speed == plan.Speed, "TLS carries exact manual performer, instrument, transpose and speed");
        }
        var revision = plan with { Id = Guid.NewGuid(), Tracks = [new(0, true, 22, -12, 2), new(1, true, 2, 0, 1)] };
        await f.Host.Sharing.PublishAsync(revision, CancellationToken.None);
        var stale = f.Clients[0].Sharing.ReceiveAsync(plan.Id, plan.SongHash, CancellationToken.None);
        await f.Until(() => stale.IsCompleted); await Fails(stale, "old assignment revision cannot be reused for the same MIDI");
        var fresh = f.Clients[0].Sharing.ReceiveAsync(revision.Id, revision.SongHash, CancellationToken.None);
        await f.Until(() => fresh.IsCompleted);
        Check((await fresh).Tracks[0].Instrument == 22, "same cached song still fetches the new assignment");

        f.AllowProof = false;
        var unverified = f.Clients[1].Sharing.ReceiveAsync(revision.Id, revision.SongHash, CancellationToken.None);
        for (var i = 0; i < 20; i++) { f.Pump(); await Task.Delay(10); }
        Check(!unverified.IsCompleted, "unverified room viewer receives no assignment snapshot");
        f.AllowProof = true;
        await f.Until(() => unverified.IsCompleted); await unverified;

        // Bypass the friendly API to check server-side authorization too.
        var forged = f.Plan();
        f.Clients[0].Room.Client!.SendPlanPacket(new() { Type = "planPublish", RoomId = f.Host.Room.Id, SongPlan = forged });
        for (var i = 0; i < 20; i++) { f.Pump(); await Task.Delay(10); }
        var retained = f.Clients[1].Sharing.ReceiveAsync(revision.Id, revision.SongHash, CancellationToken.None);
        await f.Until(() => retained.IsCompleted); await retained;
        Check(true, "ordinary viewer cannot replace the verified leader assignment");

        f.Leader = 2;
        f.Pump();
        var handedOff = f.Plan();
        var publishing = f.Clients[0].Sharing.PublishAsync(handedOff, CancellationToken.None);
        await f.Until(() => publishing.IsCompleted); await publishing;
        var delivered = f.Clients[1].Sharing.ReceiveAsync(handedOff.Id, handedOff.SongHash, CancellationToken.None);
        await f.Until(() => delivered.IsCompleted);
        Check((await delivered).LeaderCid == 2, "new verified game leader publishes through the existing room host");
        var hostCopy = await f.Host.Sharing.ReceiveAsync(handedOff.Id, handedOff.SongHash, CancellationToken.None);
        Check(hostCopy.Id == handedOff.Id, "former leader on the host receives the new leader assignment");

        using var cancellation = new CancellationTokenSource();
        var cancelled = f.Clients[1].Sharing.ReceiveAsync(handedOff.Id, handedOff.SongHash, cancellation.Token);
        cancellation.Cancel(); await Fails(cancelled, "cancelled assignment cannot complete on a late TLS response");
        var changing = f.Clients[1].Sharing.ReceiveAsync(handedOff.Id, handedOff.SongHash, CancellationToken.None);
        f.Leader = 1; f.Pump(); await Fails(changing, "leadership change cancels pending assignment retrieval");
        var newest = f.Plan();
        await f.Host.Sharing.PublishAsync(newest, CancellationToken.None);
        var disconnected = f.Clients[1].Sharing.ReceiveAsync(newest.Id, newest.SongHash, CancellationToken.None);
        f.Clients[1].Room.Leave(); f.Pump(); await Fails(disconnected, "room disconnect cancels pending assignment retrieval");
        Console.WriteLine("MANUAL ASSIGNMENT TLS CHECKS COMPLETE");
    }

    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }
    private static async Task Fails(Task task, string label)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected rejection: " + label); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or InvalidDataException)
        { Check(true, label); }
    }
    private sealed class Node : IDisposable
    {
        public StageController Controller { get; }
        public StageRoom Room => Controller.Room!;
        public RoomAssignmentSharing Sharing { get; }
        public Node(string path, ulong cid, Fixture fixture)
        {
            Controller = new(Path.Combine(path, cid.ToString()));
            Controller.Room = new(Controller);
            Sharing = new(Room, () => true, () => new(7, cid, fixture.Leader), () => [1, 2, 3],
                peer => fixture.AllowProof ? fixture.Identities.GetValueOrDefault(peer) : 0);
        }
        public void Dispose() { Sharing.Dispose(); Controller.Dispose(); }
    }
    private sealed class Fixture : IDisposable
    {
        public ulong Leader = 1;
        public bool AllowProof = true;
        public readonly Dictionary<RoomPeer, ulong> Identities = [];
        public Node Host { get; }
        public Node[] Clients { get; }
        public Fixture(string path)
        {
            Host = new(path, 1, this); Check(Host.Room.Create(0), "manual room created");
            Clients = [new(path, 2, this), new(path, 3, this)];
        }
        public RoomSongPlan Plan() => new(Guid.NewGuid(), new string('A', 64), 7, Leader, [1, 2, 3],
            [new(0, true, 20, 12, 2), new(1, true, 2, 0, 1)], 1.2f, true, 0);
        public async Task Connect()
        {
            for (var i = 0; i < Clients.Length; i++)
            {
                Check(Clients[i].Room.Join(Host.Room.Invite("127.0.0.1", Host.Room.LocalPort, RoomRole.Viewer), RoomRole.Viewer), "manual viewer joined");
                await Until(() => Clients[i].Room.Connected);
                var peer = Host.Room.Server!.Participants.Single(p => !Identities.ContainsKey(p.Peer)).Peer;
                Identities[peer] = (ulong)i + 2;
            }
        }
        public void Pump(bool serve = true)
        {
            Host.Room.Tick(); if (serve) Host.Sharing.Tick();
            foreach (var node in Clients) { node.Room.Tick(); node.Sharing.Tick(); }
        }
        public async Task Until(Func<bool> condition, bool serve = true)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Pump(serve); if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(5); }
        }
        public void Dispose() { foreach (var node in Clients) node.Dispose(); Host.Dispose(); }
    }
}
