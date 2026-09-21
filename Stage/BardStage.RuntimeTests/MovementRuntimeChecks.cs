using System.Diagnostics;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;

internal static class MovementRuntimeChecks
{
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        using var f = new Fixture(output);
        await f.Connect();
        Check(!f.Host.Movement.Enabled && f.Clients.All(n => !n.Movement.Enabled), "movement defaults off on every machine");
        foreach (var node in f.Nodes) node.Movement.SetEnabled(true);
        await f.Until(() => f.Host.Movement.Reports.Count == 3 && f.Host.Movement.Reports.All(r => r.Enabled));
        f.Host.Movement.Follow();
        await f.Until(() => f.Clients.All(n => n.Movement.Running));
        Check(f.Clients.All(n => n.Driver.Target != null), "cross-computer follow reaches both local movement drivers");
        f.Clients[0].Movement.StopLocal();
        await f.Until(() => f.Nodes.All(n => !n.Movement.Running));
        Check(f.Clients[0].Driver.Target == null, "local stop survives repeated leases and cancels party movement");
        f.Clients[0].Movement.SetEnabled(true);
        await f.Until(() => f.Host.Movement.Reports.All(r => r.Enabled));
        await f.Until(() => !f.Host.Movement.Busy);
        f.Host.Movement.Follow(); await f.Until(() => f.Clients.All(n => n.Movement.Running));
        f.Host.Movement.StopAll(); await f.Until(() => f.Clients.All(n => !n.Movement.Running));
        Check(true, "leader stop halts all clients");
        f.Host.Movement.Follow(); await f.Until(() => f.Clients.All(n => n.Movement.Running));
        f.Clients[0].Movement.StopLocal(); f.Clients[0].Movement.SetEnabled(true);
        f.Clients[0].Movement.Tick();
        Check(!f.Clients[0].Movement.Running, "stop and immediate re-enable cannot replay the old receiving session");
        await f.Until(() => f.Nodes.All(n => !n.Movement.Running));
        await f.Until(() => f.Host.Movement.Reports.All(r => r.Enabled));

        var preset = f.Host.Movement.CaptureFormation("测试站位");
        preset.Slots = [new(1,"队长",0,0,0), new(2,"队员乙",-3,-3,30), new(3,"队员丙",3,-3,-30)];
        f.Host.Movement.Store.Presets.Add(preset); f.Host.Movement.Store.Save();
        var loaded = new FormationStore(f.Host.Controller.DataDirectory); loaded.Load(1);
        Check(loaded.Presets.Single().Slots.SequenceEqual(preset.Slots), "named formation persists with exact per-character offsets and facing");
        f.Host.Movement.Apply(preset);
        await f.Until(() => f.Clients.All(n => n.Driver.Target != null));
        foreach (var node in f.Nodes)
        {
            var target = FormationPreset.ToWorld(preset.Slots.Single(s => s.Cid == node.Cid), f.Members[0]);
            f.Members[(int)node.Cid - 1] = f.Members[(int)node.Cid - 1] with { Position = target.Position };
        }
        await f.Until(() => f.Host.Movement.Status == "全员已到位");
        Check(f.Clients.All(n => n.Driver.Facing != null), "formation arrival applies assigned final facing and acknowledges everyone");
        f.ResetPositions();
        f.Host.Movement.Follow(); await f.Until(() => f.Clients.All(n => n.Movement.Running));
        f.Clients[0].Driver.Issue = "正在演奏";
        await f.Until(() => f.Nodes.All(n => !n.Movement.Running));
        Check(true, "starting performance stops movement across the party");
        f.Clients[0].Driver.Issue = null;
        await f.Until(() => f.Host.Movement.Reports.All(r => r.Issue == ""));
        f.Host.Movement.Follow(); await f.Until(() => f.Clients.All(n => n.Movement.Running));
        // Stop pumping the host while keeping its TCP connection alive.
        await f.Until(() => f.Clients.All(n => !n.Movement.Running), host: false);
        Check(f.Clients.All(n => n.Driver.Target == null), "lost host heartbeat stops ground input without waiting for TCP timeout");
        f.Host.Movement.StopAll(); await f.PumpFor(0.65);
        f.ResetPositions();
        f.Host.Movement.Follow(); await f.Until(() => f.Clients.All(n => n.Movement.Running));
        f.Clients[0].Driver.Manual = true;
        await f.Until(() => f.Nodes.All(n => !n.Movement.Running));
        Check(true, "manual movement takes precedence and cannot be restarted by old command");
        f.Clients[0].Driver.Manual = false;

        f.Leader = 2; await f.PumpFor(0.7);
        await f.Until(() => f.Clients[0].Movement.Reports.Count == 3);
        f.Clients[0].Movement.Follow();
        await f.Until(() => f.Host.Movement.Running && f.Clients[1].Movement.Running);
        Check(true, "new verified party leader controls movement through original host");
        f.Clients[0].Movement.StopAll(); await f.Until(() => f.Nodes.All(n => !n.Movement.Running));
        await f.Until(() => !f.Clients[0].Movement.Busy);

        f.Clients[1].Room.Client!.SendMovement("movementSubmit", new() { Plan = f.Plan(2) });
        await f.PumpFor(0.7);
        Check(f.Nodes.All(n => !n.Movement.Running), "ordinary verified party member cannot forge leader movement");

        f.Clients[0].Movement.Follow(); await f.Until(() => f.Host.Movement.Running && f.Clients[1].Movement.Running);
        f.Clients[1].Driver.Scene = new(101, 10);
        await f.Until(() => f.Nodes.All(n => !n.Movement.Running));
        Check(true, "scene transition stops old movement plan");
        f.Clients[1].Driver.Scene = new(100, 10); await f.PumpFor(0.7);
        f.Clients[1].Movement.SetEnabled(false); await f.PumpFor(0.7);
        f.Clients[0].Movement.Follow(); await f.PumpFor(0.7);
        Check(f.Nodes.All(n => !n.Movement.Running) && f.Clients[0].Movement.Status.Contains("开启"),
            "disabled member blocks dispatch instead of silently skipping them");
        f.Clients[1].Movement.SetEnabled(true); await f.PumpFor(0.7);
        f.Clients[0].Movement.Follow();
        await f.Until(() => !f.Clients[0].Movement.Busy, host: false);
        Check(f.Clients[0].Movement.Status.Contains("已取消"), "submission timeout cancels the leader command and rotates its session");
        await f.PumpFor(0.7);
        Check(f.Nodes.All(n => !n.Movement.Running), "delayed submission cannot restart movement after timed-out cancellation");
        f.Clients[0].Movement.Follow(); await f.Until(() => f.Host.Movement.Running && f.Clients[1].Movement.Running);
        f.Clients[1].Room.Leave(); f.Pump();
        Check(!f.Clients[1].Movement.Running && f.Clients[1].Driver.Target == null, "room disconnect immediately releases local movement");
        using var legacyHost = new StageController(Path.Combine(output, "legacy-" + Guid.NewGuid().ToString("N")));
        legacyHost.Room = new(legacyHost); legacyHost.Room.Create(0);
        using var newClient = new StageController(Path.Combine(output, "new-client-" + Guid.NewGuid().ToString("N")));
        newClient.Room = new(newClient);
        using var newMovement = new RoomMovementCoordinator(newClient.Room,
            new Driver(() => new(7, 2, 2, new(100, 10), f.Members)), _ => 0, newClient.DataDirectory);
        newClient.Room.Join(legacyHost.Room.Invite("127.0.0.1", legacyHost.Room.LocalPort, RoomRole.Viewer));
        var legacyWatch = Stopwatch.StartNew();
        while (legacyWatch.Elapsed.TotalSeconds < 1)
        { legacyHost.Room.Tick(); newClient.Room.Tick(); newMovement.Tick(); await Task.Delay(5); }
        Check(newClient.Room.Connected && !legacyHost.Room.Server!.TryMovement(out _)
            && newMovement.ControlIssue!.Contains("升级"), "capability negotiation keeps new clients compatible with rooms lacking movement");
        Console.WriteLine("MOVEMENT TLS CHECKS COMPLETE");
    }
    internal static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }

    internal sealed class Driver(Func<MovementSnapshot> capture) : IRoomMovementBackend
    {
        public MovementPosition? Target;
        public float? Facing;
        public string? Issue;
        public bool Manual;
        public MovementScene Scene = new(100, 10);
        public MovementSnapshot Capture() => capture() with { BlockReason = Issue, ManualInput = Manual, Scene = Scene };
        public string? Enable() => null;
        public void Disable() => Target = null;
        public void Drive(MovementPosition p, float d) => Target = p;
        public void Face(float a) => Facing = a;
        public void Stop() => Target = null;
        public void Dispose() => Target = null;
    }
    private sealed class Node : IDisposable
    {
        public ulong Cid { get; }
        public StageController Controller { get; }
        public StageRoom Room => Controller.Room!;
        public Driver Driver { get; }
        public RoomMovementCoordinator Movement { get; }
        public Node(string path, ulong cid, Fixture f)
        {
            Cid = cid; Controller = new(Path.Combine(path, Guid.NewGuid().ToString("N")));
            Controller.Room = new(Controller);
            Driver = new(() => new(7, cid, f.Leader, new(100,10), f.Members.ToArray()));
            Movement = new(Room, Driver, p => f.Identities.GetValueOrDefault(p), Controller.DataDirectory);
            Room.Movement = Movement;
        }
        public void Dispose() { Movement.Dispose(); Controller.Dispose(); }
    }
    private sealed class Fixture : IDisposable
    {
        public ulong Leader = 1;
        public MovementMember[] Members = [];
        public readonly Dictionary<RoomPeer, ulong> Identities = [];
        public Node Host { get; }
        public Node[] Clients { get; }
        public IEnumerable<Node> Nodes => new[] { Host }.Concat(Clients);
        public Fixture(string path)
        {
            ResetPositions(); Host = new(path, 1, this); Check(Host.Room.Create(0), "movement room created");
            Clients = [new(path, 2, this), new(path, 3, this)];
        }
        public void ResetPositions() => Members = [new(1,"队长",new(0,0,10),0,true),
            new(2,"队员乙",new(-3,0,0),0,true), new(3,"队员丙",new(3,0,0),0,true)];
        public MovementPlan Plan(ulong leader) => new(Guid.NewGuid(),7,leader,new(100,10),[1,2,3],MovementAction.Follow,
            Members.Where(m => m.Cid != leader).Select(m => new MovementTarget(m.Cid,m.Position,0)).ToArray());
        public async Task Connect()
        {
            foreach (var node in Clients)
            {
                node.Room.Join(Host.Room.Invite("127.0.0.1", Host.Room.LocalPort, RoomRole.Viewer));
                await Until(() => node.Room.Connected);
                Identities[Host.Room.Server!.Participants.Single(p => !Identities.ContainsKey(p.Peer)).Peer] = node.Cid;
            }
        }
        public void Pump(bool host = true)
        {
            if (host) { Host.Room.Tick(); Host.Movement.Tick(); }
            foreach (var node in Clients) { node.Room.Tick(); node.Movement.Tick(); }
        }
        public async Task Until(Func<bool> predicate, bool host = true)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate()) { Pump(host); if (watch.Elapsed.TotalSeconds > 8) throw new TimeoutException(
                string.Join(" | ",Nodes.Select(n=>$"{n.Cid}:{n.Movement.Status},busy={n.Movement.Busy},run={n.Movement.Running}"))); await Task.Delay(5); }
        }
        public async Task PumpFor(double seconds)
        { var watch = Stopwatch.StartNew(); while (watch.Elapsed.TotalSeconds < seconds) { Pump(); await Task.Delay(5); } }
        public void Dispose() { foreach (var n in Clients) n.Dispose(); Host.Dispose(); }
    }
}
