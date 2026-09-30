using System.Diagnostics;
using System.Security.Cryptography;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Common;

internal static class LargeEnsembleRuntimeChecks
{
    public static void Check(bool value,string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        using var f = new Fixture(output);
        Check(f.Nodes.All(n=>!n.Large.Enabled),"all eight receivers default off");
        f.Host.Large.SetEnabled(true); f.Host.Tick();
        Check(f.Host.Room.Create(0),"explicit large mode creates an eight-performer TLS room");
        f.Host.Tick();
        f.AllowProof=false;
        foreach (var n in f.Clients)
        {
            n.Large.SetEnabled(true);
            Check(n.Room.Join(f.Host.Room.Invite("127.0.0.1",f.Host.Room.LocalPort,RoomRole.Viewer),RoomRole.Viewer),$"performer {n.Driver.Cid} joins");
            await f.Until(()=>n.Room.Connected);
        }
        await f.PumpFor(.7);
        Check(f.Host.Large.Reports.Count==1,"room invitations alone cannot impersonate game alliance identities");
        f.AllowProof=true;
        foreach(var n in f.Clients) { n.Large.SetEnabled(false); n.Large.SetEnabled(true); }
        await f.Ready();
        Check(f.Host.Room.ViewerCount == 7 && f.Host.Large.Reports.Count == 8,"host plus seven independently authenticated performers");
        using (var overflow = new RoomClient(RoomInvite.Decode(f.Host.Room.Invite("127.0.0.1",f.Host.Room.LocalPort,RoomRole.Viewer))))
        {
            await f.PumpFor(.5);
            Check(!overflow.Connected && f.Host.Room.ViewerCount==7,"ninth performer is rejected at the transport capacity");
        }
        f.Host.Large.Inspect(f.Midi); await f.Until(()=>f.Host.Large.Draft != null);
        Check(f.Host.Large.Draft!.Tracks.Select(t=>t.PerformerCid).Distinct().Count()==8,"automatic assignment covers eight different performers");
        Check(f.Host.Large.UseInstrumentCompensation,"large instrument compensation defaults on");
        f.Clients[6].Driver.CompensationVersion=0;
        await f.Until(()=>f.Host.Large.Reports.Any(r=>r.Cid==8 && r.CompensationVersion==0));
        try { f.Host.Large.Distribute(); throw new Exception("old receiver was accepted for compensation"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("3.2.5.27"),"old receiver cannot silently play without requested compensation"); }
        f.Host.Large.SetInstrumentCompensation(false);
        await f.Distribute();
        Check(f.Nodes.All(n=>n.Driver.Loaded?.CompensationVersion==0),"explicit off is distributed unchanged, including to a legacy-capability receiver");
        f.Host.Large.SetInstrumentCompensation(true);
        Check(f.Host.Large.Phase==LargePhase.Stopped,"changing compensation invalidates a previously loaded plan");
        f.Clients[6].Driver.CompensationVersion=1;
        await f.Until(()=>f.Host.Large.Reports.All(r=>r.CompensationVersion==1));
        f.Host.Large.Edit(7,f.Host.Large.Draft.Tracks[7] with { Instrument=20,Transpose=-12 });
        await f.Distribute();
        Check(f.Nodes.All(n=>n.Driver.Loaded?.CompensationVersion==1),"all eight independent receivers receive the same compensation version");
        Check(f.Nodes.All(n=>n.Driver.Loaded?.Song.Tracks[7].Instrument==20 && n.Driver.Loaded.Song.Tracks[7].Transpose==-12),
            "manual instrument and transpose arrive unchanged at every client");
        Check(f.Clients.All(n=>n.Driver.Path != f.Midi && File.Exists(n.Driver.Path)),"all seven empty libraries download the MIDI into separate verified caches");
        Check(f.Clients.All(n=>n.Controller.State.Songs.Count==0),"follower personal libraries remain empty");
        f.Host.Large.Start(); await f.Until(()=>f.Nodes.All(n=>n.Driver.Starts==1));
        var starts=f.Nodes.Select(n=>n.Driver.RealStart).ToArray();
        Check(starts.Max()-starts.Min()<.080,$"eight independent clock origins start within {(starts.Max()-starts.Min())*1000:F1} ms on local TLS fixture");
        await f.PumpFor(.4);
        f.Clients[6].Large.StopAll(); await f.Until(()=>f.Nodes.All(n=>!n.Driver.Playing));
        Check(true,"any member can stop the group without replaying the same song");
        await f.PumpFor(.4);
        f.Host.Large.StopAll(); await f.Ready(); await f.Distribute();
        f.Host.Large.Start();
        f.Clients[0].Large.SetEnabled(false);
        await f.Until(()=>f.Host.Large.Phase==LargePhase.Stopped);
        await f.PumpFor(.5);
        Check(f.Nodes.All(n=>n.Driver.Starts==1),"disabling one receiver during countdown cancels every scheduled start");
        f.Clients[0].Large.SetEnabled(true); f.Host.Large.StopAll(); await f.Ready();
        await f.Distribute(); f.Host.Large.Start();
        await f.Until(()=>f.Nodes.All(n=>n.Driver.Starts==2));
        f.Clients[3].Driver.Drift=.6;
        await f.Until(()=>f.Nodes.All(n=>!n.Driver.Playing));
        Check(true,"excessive local playback drift stops all performers");
        f.Clients[3].Driver.Drift=0; f.Host.Large.StopAll(); await f.Ready();
        await f.Distribute(); f.Host.Large.Start();
        await f.Until(()=>f.Nodes.All(n=>n.Driver.Starts==3));
        await f.Until(()=>f.Clients.All(n=>!n.Driver.Playing),host:false);
        Check(true,"host heartbeat loss stops receivers even while TLS remains connected");
        f.Host.Large.StopAll(); await f.Ready();
        f.Clients[5].Driver.LoadFailure="测试：乐器准备失败";
        f.Host.Large.Distribute(); await f.Until(()=>f.Host.Large.Phase==LargePhase.Stopped);
        Check(f.Nodes.All(n=>n.Driver.Starts==3),"instrument/load failure prevents another group start");
        f.Clients[5].Driver.LoadFailure=null; f.Host.Large.StopAll(); await f.Ready();
        await f.Distribute();
        foreach(var node in f.Nodes) { node.Driver.Finished=false; }
        f.Host.Large.Start(); await f.Until(()=>f.Nodes.All(n=>n.Driver.Starts==4));
        foreach(var node in f.Nodes) { node.Driver.Draining=true; node.Driver.Drift=-.7; }
        await f.PumpFor(.5);
        Check(f.Nodes.All(n=>n.Large.Phase==LargePhase.Playing),"compensation tail drain is not mistaken for a stalled or drifted MIDI engine");
        foreach(var node in f.Nodes) { node.Driver.Stop(); node.Driver.Finished=true; node.Driver.Drift=0; }
        await f.Until(()=>f.Host.Large.Phase==LargePhase.Finished);
        Check(true,"normal end is acknowledged without starting another personal-library song");
        f.Host.Large.StopAll(); await f.Ready();
        await f.Distribute(); f.Host.Large.Start();
        await f.Until(()=>f.Nodes.All(n=>n.Driver.Starts==5));
        f.Host.Large.SetEnabled(false);
        await f.Until(()=>f.Clients.All(n=>!n.Driver.Playing));
        Check(true,"host disabling large mode revokes playback on all receivers");
        f.Host.Large.SetEnabled(true); f.Host.Large.StopAll(); await f.Ready();
        await f.Distribute(); f.Host.Large.Start();
        f.Members=f.Members[..7];
        await f.PumpFor(.5);
        Check(f.Nodes.All(n=>!n.Driver.Playing) && f.Host.Large.Phase==LargePhase.Stopped,"team roster change invalidates all pending playback");
        f.Members=Fixture.Membership(9); await f.PumpFor(.2);
        Check(f.Host.Large.ControlIssue?.Contains("8") == true,"nine-member team is rejected without truncating its roster");
        f.Members=Fixture.Membership(16); await f.PumpFor(.2);
        Check(f.Host.Large.ControlIssue?.Contains("8") == true && f.Nodes.All(n=>!n.Driver.Playing),
            "sixteen-member team is rejected before dispatch and cannot restart previous playback");
        Console.WriteLine("Actual FFXIV alliance/audio/native playback is not exercised by this fixture.");
    }
    internal sealed class Driver(ulong cid,Func<LargeContext> capture,Action<Guid,string,ulong> proof,Func<double>? now=null) : ILargeEnsembleBackend
    {
        private MonotonicPlaybackStart? timer;
        private double timerOffset;
        public bool SupportsScheduledStart => true;
        public double? ScheduledStartAt => timer?.FiredAt is { } at ? at + timerOffset : null;
        public void ScheduleStart(double at) => ScheduleStart(at, null);
        public void ScheduleStart(double at, Func<bool>? canStart)
        {
            timer?.Dispose(); timer = new(); timerOffset = clock() - TransportClock.Now;
            timer.Arm(at - timerOffset, Start, canStart);
        }
        public void PulseScheduledStart()
        {
            if (timer?.Issue is { } issue) throw new InvalidOperationException(issue);
            timer?.Pulse(); if (timer?.FiredAt != null) timer.StopTimer();
        }
        public ulong Cid=cid;
        private readonly Func<double> clock=now ?? (()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency);
        public string? EnableIssue => null;
        public string? PlaybackIssue { get; set; }
        public string? LoadFailure;
        public bool Playing { get; private set; }
        public bool Finished { get; set; }
        public bool Draining { get; set; }
        public int CompensationVersion { get; set; } = 1;
        public double Drift;
        public int Starts;
        public double RealStart;
        public string Path="";
        public LargePlan? Loaded;
        public double PositionSeconds => TransportClock.Now-RealStart+Drift;
        public IReadOnlyList<string> Instruments => Enumerable.Range(0,29).Select(i=>i==0?"未分配":$"乐器 {i}").ToArray();
        public LargeContext Capture()=>capture();
        public Task<LargeDraft> InspectAsync(string path,CancellationToken token)
        {
            using var file=File.OpenRead(path); var hash=Convert.ToHexString(SHA256.HashData(file));
            return Task.FromResult(new LargeDraft(path,hash,Enumerable.Range(0,8).Select(i=>new LargeTrack(i,$"轨道 {i+1}",2,0,0,true)).ToArray()));
        }
        public Task LoadAsync(string path,LargePlan plan,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); using var file=File.OpenRead(path);
            if(LoadFailure != null) return Task.FromException(new InvalidOperationException(LoadFailure));
            if (Convert.ToHexString(SHA256.HashData(file))!=plan.Song.SongHash) throw new InvalidOperationException("hash mismatch");
            Loaded=plan; Path=path; Finished=false; return Task.CompletedTask;
        }
        public void Start() { Playing=true; Finished=false; Starts++; RealStart=TransportClock.Now; }
        public void Stop() { timer?.Dispose(); timer=null; Playing=false; Draining=false; }
        public void SendProof(Guid room,string challenge)=>proof(room,challenge,Cid);
    }
    internal sealed class Node : IDisposable
    {
        public StageController Controller;
        public StageRoom Room=>Controller.Room!;
        public Driver Driver;
        public RoomLargeEnsemble Large;
        public RoomSongSharing Songs;
        public Node(string directory,ulong cid,Fixture f)
        {
            Controller=new(System.IO.Path.Combine(directory,cid.ToString())); Controller.Room=new(Controller);
            Func<double> clock = () => TransportClock.Now + cid * 123.0;
            Driver=new(cid,()=>new(cid,f.Members,100,10),(r,c,id)=> { if(f.AllowProof) f.Host.Large.ReceiveProof(r,c,id); },clock);
            Large=new(Room,Driver,clock);
            Room.LargeEnsemble=Large;
            Songs=new(Room,Controller.DataDirectory,()=>Large.Enabled,()=>new((long)((cid-1)/8)+1,cid,cid<=8?1UL:9UL),p=>Large.VerifiedMember(p)!=0);
            Room.Songs=Songs;
        }
        public void Tick() { Room.Tick(); Large.Tick(); Songs.Tick(); }
        public void Dispose() { Large.Dispose(); Songs.Dispose(); Controller.Dispose(); }
    }
    internal sealed class Fixture : IDisposable
    {
        public static LargeMember[] Membership(int count=8)=>Enumerable.Range(1,count).Select(i=>new LargeMember((ulong)i,$"演奏人 {i}",10,(i-1)/8)).ToArray();
        public LargeMember[] Members=Membership();
        public bool AllowProof=true;
        public Node Host;
        public Node[] Clients;
        public IEnumerable<Node> Nodes=>Clients.Prepend(Host);
        public string Midi;
        public Fixture(string output)
        {
            Directory.CreateDirectory(output);
            Midi=System.IO.Path.Combine(output,"eight.mid");
            new MidiFile(Enumerable.Range(1,8).Select(i=>new TrackChunk(new SequenceTrackNameEvent($"Piano {i}"),
                new NoteOnEvent((SevenBitNumber)60,(SevenBitNumber)80),new NoteOffEvent((SevenBitNumber)60,(SevenBitNumber)0){DeltaTime=19200}))).Write(Midi,true);
            Host=new(output,1,this); Clients=Enumerable.Range(2,7).Select(i=>new Node(output,(ulong)i,this)).ToArray();
        }
        public async Task Ready()
        {
            await PumpFor(.25);
            await Until(()=>Host.Large.Reports.Count==8 && Host.Large.Reports.All(r=>r.Enabled && r.ClockReady && r.Issue==""));
        }
        public async Task Distribute()
        { await Ready(); Host.Large.Distribute(); await Until(()=>Host.Large.Phase==LargePhase.Ready); }
        public void Pump(bool host=true)
        { if(host) Host.Tick(); foreach(var node in Clients) node.Tick(); }
        public async Task PumpFor(double seconds,bool host=true)
        { var timer=Stopwatch.StartNew(); while(timer.Elapsed.TotalSeconds<seconds) { Pump(host); await Task.Delay(5); } }
        public async Task Until(Func<bool> condition,bool host=true)
        {
            var timer=Stopwatch.StartNew();
            while(!condition())
            {
                Pump(host); if(timer.Elapsed.TotalSeconds>15)
                    throw new TimeoutException($"Host: {Host.Large.Status}\n"+string.Join("\n",Nodes.Select(n=>$"{n.Driver.Cid}: {n.Large.Phase} {n.Large.Status}")));
                await Task.Delay(5);
            }
        }
        public void Dispose() { foreach(var node in Nodes) node.Dispose(); }
    }
}
