using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

internal static class LocalEnsembleRuntimeChecks
{
    internal static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }

    internal sealed record WorkerConfig(string Directory, string Bus, int Count, ulong Cid, bool Network = false);
    internal sealed record Proof(Guid Room, string Challenge, ulong Cid);
    internal sealed record Command(int Id, string Action, string Text = "", ulong[]? Members = null, int Value = 0);
    internal sealed record State(ulong Cid, int Process, int Ack, string Error, Guid Node, Guid Group, bool Host,
        bool Enabled, LargePhase Phase, string Status, ulong[] Discovered, ulong[] Participants, LargeReport[] Reports,
        int Loads, int Starts, double StartedAt, bool TimerDuringPause, bool Playing, string Path, LargePlan? Loaded,
        int LibraryCount, bool HasNetwork, string? ControlIssue, int Tracks, string Invite = "", ulong[]? Selectable = null);

    internal sealed class Driver(Func<LargeContext> capture, Action<Guid,string,ulong>? proof = null) : ILargeEnsembleBackend, IDisposable
    {
        private Playback? playback;
        private MonotonicPlaybackStart? timer;
        private volatile bool finished;
        public bool FramePaused, StallAtStart;
        public bool DrainOutput;
        public bool Draining => finished && DrainOutput;
        public bool TimerDuringPause;
        public int Loads, Starts;
        public double StartedAt, Target;
        public string Path = "";
        public LargePlan? Loaded;
        public string? EnableIssue => null;
        public string? PlaybackIssue => timer?.Issue;
        public bool Playing => playback?.IsRunning == true || Draining;
        public bool Finished => finished && !Draining;
        public double PositionSeconds => playback?.GetCurrentTime<MetricTimeSpan>().TotalMicroseconds / 1000000d ?? 0;
        public IReadOnlyList<string> Instruments => Enumerable.Range(0,29).Select(i => "乐器 " + i).ToArray();
        public bool SupportsScheduledStart => true;
        public double? ScheduledStartAt => timer?.FiredAt;
        public LargeContext Capture() => capture();
        public Task<LargeDraft> InspectAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new LargeDraft(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                Enumerable.Range(0,8).Select(i => new LargeTrack(i,$"轨道 {i+1}",2,0,0,true)).ToArray()));
        }
        public Task LoadAsync(string path, LargePlan plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != plan.Song.SongHash) throw new IOException("hash mismatch");
            Stop(); playback?.Dispose();
            playback = MidiFile.Read(path).GetPlayback(new PlaybackSettings
                { ClockSettings = new MidiClockSettings { CreateTickGeneratorCallback = () => new HighPrecisionTickGenerator() } });
            playback.Finished += (_,_) => finished = true;
            Loaded = plan; Path = path; Loads++; finished = false;
            return Task.CompletedTask;
        }
        public void Start()
        {
            playback!.Start(); StartedAt = MonotonicPlaybackStart.Now; Starts++;
            TimerDuringPause = FramePaused;
        }
        public void ScheduleStart(double at) => ScheduleStart(at,null);
        public void ScheduleStart(double at, Func<bool>? canStart)
        { timer?.Dispose(); timer = new(); Target = at; timer.Arm(at, Start, canStart); }
        public void PulseScheduledStart()
        { if (timer?.Issue is { } issue) throw new IOException(issue); timer?.Pulse(); if (timer?.FiredAt != null) timer.StopTimer(); }
        public void Stop() { timer?.Dispose(); timer = null; playback?.Stop(); finished = false; DrainOutput = false; }
        public void SendProof(Guid room, string challenge)
        {
            if (proof == null) throw new InvalidOperationException("local mode must not use party chat proof");
            proof(room,challenge,Capture().SelfCid);
        }
        public void Dispose() { Stop(); playback?.Dispose(); }
    }

    internal static async Task Worker(string configPath)
    {
        var config = Read<WorkerConfig>(configPath)!;
        var directory = System.IO.Path.Combine(config.Directory, config.Cid.ToString());
        using var controller = new StageController(System.IO.Path.Combine(directory,"library"));
        controller.Room = new(controller);
        var members = Membership(config.Count); uint territory = 100, world = 10;
        using var driver = new Driver(() => new(config.Cid,members,territory,world), config.Network
            ? (room,challenge,cid) => Write(System.IO.Path.Combine(directory,"proof.json"),new Proof(room,challenge,cid)) : null);
        using var large = new RoomLargeEnsemble(controller.Room,driver);
        controller.Room.LargeEnsemble = large;
        large.LocalFactory = () => new(System.IO.Path.Combine(config.Directory,"cache"), config.Bus);
        if (!config.Network) large.SetLocalMode(true);
        large.Tick();
        var enableError="";
        try { large.SetEnabled(true); }
        catch(InvalidOperationException ex) when(config.Count>LargePlan.MaxPlayers) { enableError=ex.Message; }
        using var songs = config.Network ? new RoomSongSharing(controller.Room,controller.DataDirectory,()=>large.Enabled,
            ()=>new((long)((config.Cid-1)/8)+1,config.Cid,config.Cid<=8?1UL:9UL),p=>large.VerifiedMember(p)!=0) : null;
        if (songs != null) controller.Room.Songs=songs;
        if (config.Network && config.Cid==1 && !controller.Room.Create(0)) throw new Exception("network host failed");
        var ack = 0; var error = enableError; var nextIo = 0d; var expires = MonotonicPlaybackStart.Now + 300;
        while (MonotonicPlaybackStart.Now < expires)
        {
            var now = MonotonicPlaybackStart.Now;
            driver.FramePaused = driver.StallAtStart && now >= driver.Target - .15 && now < driver.Target + .30;
            if (!driver.FramePaused)
            {
                if (config.Network && config.Cid==1)
                    for (var i=2;i<=config.Count;i++)
                        if (Read<Proof>(System.IO.Path.Combine(config.Directory,i.ToString(),"proof.json")) is { } p)
                            large.ReceiveProof(p.Room,p.Challenge,p.Cid);
                controller.Room.Tick(); large.Tick(); songs?.Tick();
            }
            if (now >= nextIo)
            {
                nextIo = now + .05;
                var command = Read<Command>(System.IO.Path.Combine(directory,"command.json"));
                if (command != null && command.Id > ack)
                {
                    error = "";
                    try
                    {
                        switch (command.Action)
                        {
                            case "join":
                                if (!controller.Room.Join(command.Text,RoomRole.Viewer)) throw new Exception("join failed");
                                break;
                            case "host": large.BecomeLocalHost(); break;
                            case "release": large.ReleaseLocalHost(); break;
                            case "select":
                                foreach (var id in command.Members!)
                                    large.SelectLocalParticipant(large.Local!.Discovered.Single(p => p.Context.SelfCid == id).Node, command.Value != 0);
                                break;
                            case "inspect": large.Inspect(command.Text); break;
                            case "manual":
                                var tracks = large.Draft!.Tracks; var selected = large.Participants.OrderBy(m => m.Cid).ToArray();
                                for (var i = 0; i < tracks.Length; i++) large.Edit(i,tracks[i] with
                                { PerformerCid = i < selected.Length ? selected[^(i+1)].Cid : 0, Instrument = (uint)(i%28+1), Enabled = i < selected.Length });
                                break;
                            case "distribute": large.Distribute(); break;
                            case "start": large.Start(); break;
                            case "stop": large.StopAll(); break;
                            case "enable": large.SetEnabled(command.Value != 0); break;
                            case "stall": driver.StallAtStart = command.Value != 0; break;
                            case "drain": driver.DrainOutput = command.Value != 0; break;
                            case "world": world = (uint)command.Value; break;
                            case "zone": territory = (uint)command.Value; break;
                            case "roster": members = Membership(command.Value); break;
                            case "network": large.SetLocalMode(false); break;
                            case "quit": return;
                            default: throw new InvalidOperationException(command.Action);
                        }
                    }
                    catch (Exception ex) { error = ex.Message; }
                    ack = command.Id;
                }
                Write(System.IO.Path.Combine(directory,"state.json"),new State(config.Cid,Environment.ProcessId,ack,error,
                    large.Local?.Node ?? Guid.Empty,large.Local?.Group ?? Guid.Empty,large.IsCaptain,large.Enabled,
                    large.Phase,large.Status,large.Local?.Discovered.Select(p => p.Context.SelfCid).ToArray() ?? [],
                    large.Participants.Select(p => p.Cid).ToArray(),large.Reports.ToArray(),driver.Loads,driver.Starts,
                    driver.StartedAt,driver.TimerDuringPause,driver.Playing,driver.Path,driver.Loaded,controller.State.Songs.Count,
                    controller.Room.IsCaptain || controller.Room.IsRemote,large.ControlIssue,large.Draft?.Tracks.Length ?? 0,
                    config.Network && controller.Room.IsCaptain ? controller.Room.Invite("127.0.0.1",controller.Room.LocalPort,RoomRole.Viewer) : "",
                    large.Local?.Discovered.Where(p => large.Local.CandidateIssue(p) == null).Select(p => p.Context.SelfCid).ToArray()));
            }
            await Task.Delay(5);
        }
    }

    internal static LargeMember[] Membership(int count) => Enumerable.Range(1,count)
        .Select(i => new LargeMember((ulong)i,$"演奏人 {i}",10,(i-1)/8)).ToArray();
    private static T? Read<T>(string path)
    {
        try { using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); return JsonSerializer.Deserialize<T>(stream); }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }
    private static void Write<T>(string path, T value)
    {
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value));
        for (var attempt=0;;attempt++)
        {
            try { File.Move(path+".tmp",path,true); return; }
            catch (Exception ex) when (attempt<20 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(2); }
        }
    }

    private sealed class Processes : IDisposable
    {
        private readonly string directory;
        private readonly List<Process> processes = [];
        private int commandId;
        private readonly Dictionary<int,State> latest = [];
        public readonly int Count;
        public State[] States
        {
            get
            {
                for (var i=1;i<=Count;i++) if (Read<State>(System.IO.Path.Combine(directory,i.ToString(),"state.json")) is { } state) latest[i]=state;
                return latest.OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
            }
        }
        public Processes(string output, int count, bool network = false)
        {
            Count = count; directory = System.IO.Path.Combine(output,"run-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var bus = "Local\\MidiBard.Test."+Guid.NewGuid().ToString("N");
            for (var i = 1; i <= count; i++)
            {
                var dir = System.IO.Path.Combine(directory,i.ToString()); Directory.CreateDirectory(dir);
                var config = System.IO.Path.Combine(dir,"config.json"); Write(config,new WorkerConfig(directory,bus,count,(ulong)i,network));
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                if (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--local-ensemble-worker"); start.ArgumentList.Add(config);
                var process = Process.Start(start)!; processes.Add(process);
                process.OutputDataReceived += (_,e) => { if (e.Data != null) File.AppendAllText(System.IO.Path.Combine(dir,"worker.log"),e.Data+"\n"); };
                process.ErrorDataReceived += (_,e) => { if (e.Data != null) File.AppendAllText(System.IO.Path.Combine(dir,"error.log"),e.Data+"\n"); };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
        }
        public async Task Until(Func<State[],bool> predicate, double seconds = 15)
        {
            var deadline = MonotonicPlaybackStart.Now + seconds;
            while (true)
            {
                var states = States;
                if (states.Length == Count && predicate(states)) return;
                if (MonotonicPlaybackStart.Now > deadline) throw new TimeoutException(string.Join("\n",states.Select(s=>$"{s.Cid}: {s.Phase}; starts={s.Starts}; {s.Status}; error={s.Error}")));
                await Task.Delay(30);
            }
        }
        public async Task<string> Send(int cid, string action, string text = "", ulong[]? members = null, int value = 0, bool failure = false)
        {
            var id = ++commandId;
            Write(System.IO.Path.Combine(directory,cid.ToString(),"command.json"),new Command(id,action,text,members,value));
            await Until(s => s[cid-1].Ack == id);
            var error = States[cid-1].Error;
            if (!failure && error != "") throw new InvalidOperationException($"{cid}/{action}: {error}");
            return error;
        }
        public async Task Bind(int host, params ulong[] others)
        {
            await Send(host,"host");
            // Own-state files and the host's IPC discovery snapshot have different
            // publication times. Wait for the actual selection precondition rather
            // than racing an arbitrary 100 ms delay after a group release.
            await Until(s => others.All(c => s[host-1].Selectable?.Contains(c) == true));
            await Send(host,"select",members:others,value:1);
            await Until(s => s[host-1].Reports.Length == others.Length+1 && others.All(c => s[c-1].Group == s[host-1].Group));
        }
        public async Task Ready(int host, string midi, bool manual = false)
        {
            await Send(host,"inspect",midi); await Until(s => s[host-1].Tracks == 8);
            if (manual) await Send(host,"manual");
            await Send(host,"distribute"); await Until(s => s[host-1].Phase == LargePhase.Ready);
        }
        public void Kill(int cid) { processes[cid-1].Kill(); processes[cid-1].WaitForExit(3000); }
        public void Dispose()
        {
            foreach (var p in processes) { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } p.Dispose(); }
        }
    }

    internal static async Task RunNetwork(string output)
    {
        Directory.CreateDirectory(output);
        var midi=System.IO.Path.Combine(output,"eight-network.mid");
        new MidiFile(Enumerable.Range(1,8).Select(i=>new TrackChunk(new SequenceTrackNameEvent($"Piano {i}"),
            new NoteOnEvent((SevenBitNumber)60,(SevenBitNumber)80),new NoteOffEvent((SevenBitNumber)60,(SevenBitNumber)0){DeltaTime=9600}))).Write(midi,true);
        using var f=new Processes(output,8,network:true);
        await f.Until(s=>s[0].Invite!="");
        var invite=f.States[0].Invite;
        for(var i=2;i<=8;i++) await f.Send(i,"join",invite);
        await f.Until(s=>s[0].Reports.Length==8 && s[0].Reports.All(r=>r.ClockReady && r.Enabled && r.Issue==""),30);
        Check(f.States.Select(s=>s.Process).Distinct().Count()==8 && f.States.All(s=>s.HasNetwork),
            "eight independent processes connect via production TLS room, not the local IPC mode");
        await f.Ready(1,midi,manual:true);
        for(var i=1;i<=8;i++) await f.Send(i,"stall",value:1);
        await f.Send(1,"start"); await f.Until(s=>s.All(x=>x.Starts==1));
        var states=f.States;
        var spread=(states.Max(s=>s.StartedAt)-states.Min(s=>s.StartedAt))*1000;
        Check(states.All(s=>s.TimerDuringPause) && spread<80,
            $"eight real MIDI engines start while framework pumps pause -150/+300 ms; spread {spread:F3} ms");
        await f.Until(s=>s.All(x=>x.Phase==LargePhase.Playing));
        Check(f.States[0].Reports.All(r=>r.ClockReady && r.TimingVersion==1 && r.StartLateMs!=null),
            "transport calibration and actual start diagnostics survive the framework pause");
        File.WriteAllText(System.IO.Path.Combine(output,"eight-starts.json"),JsonSerializer.Serialize(new
        { spreadMs=spread, realGameAudio=false, physicalComputers=1, independentProcesses=8,
          scenario="room-connected mixed multi-instance simulation; not a three-physical-PC acceptance test", states },new JsonSerializerOptions{WriteIndented=true}));
        await f.Send(8,"stop"); await f.Until(s=>s.All(x=>!x.Playing && x.Phase==LargePhase.Stopped));
        await f.Ready(1,midi); await f.Send(1,"start");
        await f.Until(s=>s.All(x=>x.Phase==LargePhase.Committed));
        await f.Send(4,"stop"); await f.Until(s=>s.All(x=>x.Phase==LargePhase.Stopped));
        await Task.Delay(5200);
        Check(f.States.All(s=>s.Starts==1),"network member cancellation revokes all eight committed timers");
        Console.WriteLine("Three physical PCs and FF14 game clients/audio have not been exercised.");
    }

    internal static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var midi = System.IO.Path.Combine(output,"local.mid");
        new MidiFile(new TrackChunk(new NoteOnEvent((SevenBitNumber)60,(SevenBitNumber)80),
            new NoteOffEvent((SevenBitNumber)60,(SevenBitNumber)0) { DeltaTime = 384 })).Write(midi,true);
        await CacheAndTimerChecks(output,midi);
        using (var f = new Processes(output,8))
        {
            await f.Until(s => s.All(x => x.Discovered.Length == 8));
            Check(f.States.Select(s => s.Process).Distinct().Count()==8 && f.States.All(s => !s.HasNetwork),
                "8 independent processes discover one another without room servers, clients or party chat");
            await f.Bind(1,Enumerable.Range(2,7).Select(i => (ulong)i).ToArray());
            await f.Ready(1,midi,manual:true);
            var plan = f.States[0].Loaded!;
            Check(f.States.All(s => s.Loaded?.Id == plan.Id && s.Loaded.CompensationVersion==1 && s.Path == f.States[0].Path && s.LibraryCount==0),
                "host-only MIDI is hash-verified in shared cache; all 8 empty libraries receive compensation and assignments");
            Check(plan.Song.Tracks[0].PerformerCid==8 && f.States.All(s => JsonSerializer.Serialize(s.Loaded)==JsonSerializer.Serialize(plan)),
                "manual reversed performer order and individual instruments reach all selected processes unchanged");
            for (var i=1;i<=8;i++) await f.Send(i,"stall",value:1);
            for (var i=1;i<=8;i++) await f.Send(i,"drain",value:1);
            await f.Send(1,"start"); await f.Until(s => s.All(x => x.Starts==1));
            var states = f.States; var spread=(states.Max(s => s.StartedAt)-states.Min(s => s.StartedAt))*1000;
            Check(states.All(s => s.TimerDuringPause) && spread < 80,
                $"real MIDI engines start while all framework pumps pause across deadline; 8-process spread {spread:F2} ms");
            await Task.Delay(2500);
            Check(f.States.All(s=>s.Phase==LargePhase.Playing),"local group waits for compensation output drain after real engine EOF");
            for (var i=1;i<=8;i++) await f.Send(i,"drain",value:0);
            await f.Until(s => s.All(x => x.Phase==LargePhase.Finished));
            Check(f.States.All(s=>!s.Playing),"natural MIDI completion is acknowledged by all local processes");
            await f.Ready(1,midi); await f.Send(1,"start");
            await f.Until(s => s.All(x => x.Phase==LargePhase.Committed));
            await f.Send(5,"stop"); await f.Until(s => s.All(x => x.Phase==LargePhase.Stopped));
            await Task.Delay(5200);
            Check(f.States.All(s => s.Starts==1),"member stop cancels every timer during committed countdown");
            await f.Ready(1,midi); await f.Send(1,"start");
            await f.Until(s => s.All(x => x.Starts==2));
            await f.Send(4,"stop"); await f.Until(s => s.All(x => !x.Playing && x.Phase==LargePhase.Stopped));
            Check(true,"member stop reaches all processes during playback and previous stop cannot poison next plan");
            await f.Ready(1,midi);
            var loads = f.States[6].Loads;
            await f.Send(7,"enable",value:0); await f.Send(7,"enable",value:1);
            await f.Until(s => s[0].Phase==LargePhase.Stopped);
            await Task.Delay(300);
            Check(f.States[6].Loads==loads && f.States[6].Starts==2,"receiver re-enable invalidates its session and cannot replay stale song/start");
            await f.Send(1,"release"); await f.Until(s=>s.All(x=>x.Group==Guid.Empty));
            await f.Send(8,"world",value:20);
            await f.Bind(1,2,3,4);
            Check((await f.Send(1,"select",members:[8],value:1,failure:true)).Contains("世界"),"wrong-world local character cannot be selected");
            await f.Send(8,"world",value:10); await f.Send(8,"zone",value:200);
            await Task.Delay(200);
            Check((await f.Send(1,"select",members:[8],value:1,failure:true)).Contains("场景"),"wrong-scene local character cannot be selected");
            await f.Send(8,"zone",value:100); await f.Send(8,"roster",value:7);
            await Task.Delay(200);
            Check((await f.Send(1,"select",members:[8],value:1,failure:true)).Contains("团队"),"different team roster cannot join local group");
            await f.Send(8,"roster",value:8); await Task.Delay(200);
            await f.Bind(5,6,7,8);
            Check((await f.Send(5,"select",members:[2],value:1,failure:true)).Contains("其他"),"second conductor cannot take an already managed character");
            await f.Ready(1,midi); await f.Ready(5,midi);
            Check(f.States[0].Loaded!.Song.Members.Length==4 && f.States[0].Loaded!.LocalTeam!.Length==8
                && f.States[4].Loaded!.Id!=f.States[0].Loaded!.Id,"two disjoint local groups retain full 8-person team identity");
            await f.Send(1,"start"); await f.Send(1,"stop"); await Task.Delay(300);
            Check(f.States.Skip(4).All(s=>s.Phase==LargePhase.Ready),"stopping one group leaves other group's plan ready");
            await f.Send(5,"start"); await f.Until(s=>s.Skip(4).All(x=>x.Phase==LargePhase.Committed));
            f.Kill(5); await f.Until(s=>s.Skip(5).All(x=>x.Phase==LargePhase.Stopped));
            await Task.Delay(5200);
            Check(f.States.Skip(5).All(s=>s.Starts==2 && s.Group==Guid.Empty),"conductor process crash expires membership and cancels countdown timers");
            await f.Send(1,"release"); await f.Send(1,"enable",value:0); await f.Send(1,"network");
            Check(f.States[0].Node==Guid.Empty && !f.States[0].Enabled,"mode switch disposes local IPC and returns to disabled network mode");
        }
        using (var f = new Processes(output,8))
        {
            await f.Until(s=>s.All(x=>x.Discovered.Length==8));
            await f.Bind(1,Enumerable.Range(2,7).Select(i=>(ulong)i).ToArray());
            await f.Ready(1,midi); await f.Send(1,"start"); await f.Until(s=>s.All(x=>x.Starts==1));
            var states=f.States; var spread=(states.Max(s=>s.StartedAt)-states.Min(s=>s.StartedAt))*1000;
            Check(states.All(s=>s.Participants.Length==8 && !s.HasNetwork) && spread<80,$"8 independent processes play over local IPC; measured engine start spread {spread:F2} ms");
            await f.Send(1,"stop"); await f.Until(s=>s.All(x=>!x.Playing));
            await f.Ready(1,midi); await f.Send(1,"start"); await f.Send(3,"zone",value:200);
            await f.Until(s=>s[0].Phase==LargePhase.Stopped);
            await Task.Delay(5200);
            Check(f.States.All(s=>s.Starts==1),"scene change during countdown stops all selected performers");
            foreach (var count in new[] { 9, 16 })
            {
                await f.Send(1,"roster",value:count);
                await f.Until(s=>s[0].ControlIssue?.Contains("8")==true);
                Check(f.States[0].Starts==1 && !f.States[0].Playing,$"{count}-member team is rejected without silently truncating its roster");
            }
        }
        foreach (var count in new[] { 9, 16 })
        {
            using var f = new Processes(output,count);
            await f.Until(s=>s.All(x=>x.Status.Contains("8") && !x.Enabled));
            var enableError=await f.Send(1,"enable",value:1,failure:true);
            var error = await f.Send(1,"host",failure:true);
            Check(enableError.Contains("8") && error!="" && f.States.All(s=>!s.Host && s.Group==Guid.Empty && s.Starts==0),
                $"{count} actual local processes cannot establish an oversized playback group");
        }
        LocalCapacityChecks();
        Console.WriteLine("Independent-process checks use real IPC/cache/clock/MIDI engine and simulated game identity/instrument output. Actual FF14 audio remains untested.");
    }

    internal static void LocalCapacityChecks()
    {
        var name="Local\\MidiBard.CapacityTest."+Guid.NewGuid().ToString("N");
        using var sender=new LocalEnsembleBus(name);
        using var receiver=new LocalEnsembleBus(name);
        var context=new LargeContext(1,Membership(8),100,10);
        var targets=Enumerable.Range(1,8).Select(i=>new LocalEnsembleTarget(i==1?sender.Node:Guid.NewGuid(),(ulong)i)).ToArray();
        var report=new LargeReport(1,Guid.NewGuid(),true,context.Key,100,10,Guid.Empty,Guid.Empty,
            false,false,false,false,true,0,0,0,"");
        var snapshot=new LocalEnsembleSnapshot(sender.Node,Environment.ProcessId,LocalEnsembleBus.Now,
            Stopwatch.Frequency,context,true,Guid.NewGuid(),true,targets,report,new());
        bool Received(LocalEnsembleSnapshot value)
        {
            Check(sender.Write(value),"local capacity wire sample is published");
            return receiver.Read()?.Any(s=>s.Node==sender.Node)==true;
        }
        bool Rejected(LocalEnsembleSnapshot value)
        {
            try { sender.Write(value); throw new InvalidOperationException("oversized local snapshot was sent"); }
            catch(InvalidDataException) { }
            // Simulate a legacy or hand-crafted sender that bypasses outbound validation.
            // Verify the reader as well as the public write boundary, not only a constant.
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var slot=(int)typeof(LocalEnsembleBus).GetField("slot",flags)!.GetValue(sender)!;
            var view=(System.IO.MemoryMappedFiles.MemoryMappedViewAccessor)typeof(LocalEnsembleBus)
                .GetField("view",flags)!.GetValue(sender)!;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(value);
            view.WriteArray(slot*64*1024+32,bytes,0,bytes.Length);
            view.Write(slot*64*1024+8,value.At);
            view.Write(slot*64*1024,bytes.Length);
            return receiver.Read()?.Any(s=>s.Node==sender.Node)==false;
        }
        Check(Received(snapshot),"eight-performer local discovery and group snapshot remains valid");
        foreach(var count in new[]{9,16})
        {
            Check(Rejected(snapshot with { Context=context with { Members=Membership(count) } }),
                $"local IPC rejects a {count}-member context before group establishment");
            Check(Rejected(snapshot with { Targets=Enumerable.Range(1,count)
                .Select(i=>new LocalEnsembleTarget(i==1?sender.Node:Guid.NewGuid(),(ulong)i)).ToArray() }),
                $"local IPC rejects {count} independently selected group targets");
            Check(Rejected(snapshot with { Frame=new() { Reports=Enumerable.Range(1,count)
                .Select(i=>report with { Cid=(ulong)i }).ToArray() } }),
                $"local IPC rejects {count} report rows without truncating the envelope");
            Check(Rejected(snapshot with { Frame=new() { LocalSessions=Enumerable.Range(1,count)
                .ToDictionary(i=>(ulong)i,_=>Guid.NewGuid()) } }),
                $"local IPC rejects {count} per-performer sessions without accepting an oversized group");
            var id=Guid.NewGuid();
            var members=Enumerable.Range(1,count).Select(i=>(ulong)i).ToArray();
            var plan=new LargePlan(id,LargeContext.RosterKey(Membership(count)),100,10,
                new(id,new string('A',64),0,1,members,
                    members.Select((cid,i)=>new RoomTrackAssignment(i,true,2,0,cid)).ToArray(),1,true,2));
            Check(Rejected(snapshot with { Frame=new() { Plan=plan } }),
                $"local IPC rejects an embedded {count}-performer song plan from a legacy sender");
        }
        Check(Received(snapshot),"rejecting oversized IPC data does not poison the next valid eight-person snapshot");
    }

    private static async Task CacheAndTimerChecks(string output,string midi)
    {
        var store=new LocalSongStore(System.IO.Path.Combine(output,"cache-check"));
        var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(midi)));
        var path=await store.Publish(midi,hash,CancellationToken.None);
        Check(await store.Receive(hash,CancellationToken.None)==path,"cache verifies MIDI content fingerprint");
        File.WriteAllText(path,"corrupted");
        try { await store.Receive(hash,CancellationToken.None); throw new Exception("accepted corruption"); }
        catch (InvalidDataException) { Check(true,"corrupted local cache is rejected"); }
        await store.Publish(midi,hash,CancellationToken.None);
        var changed=System.IO.Path.Combine(output,"changed.mid"); File.WriteAllText(changed,"changed");
        try { await store.Publish(changed,hash,CancellationToken.None); throw new Exception("accepted source change"); }
        catch (InvalidDataException) { Check(true,"source changed after inspection is rejected before playback"); }
        using (var cancel=new CancellationTokenSource(100))
        {
            try { await store.Receive(new string('A',64),cancel.Token); throw new Exception("not cancelled"); }
            catch (OperationCanceledException) { Check(true,"pending local song receive cancels without importing a playlist"); }
        }
        using var timer=new MonotonicPlaybackStart(); var starts=0;
        timer.Arm(MonotonicPlaybackStart.Now+.2,()=>Interlocked.Increment(ref starts)); timer.Cancel(); await Task.Delay(350);
        Check(starts==0,"cancelled monotonic timer never calls prepared engine");
        timer.Arm(MonotonicPlaybackStart.Now+1.7,()=>Interlocked.Increment(ref starts)); await Task.Delay(1900);
        Check(starts==0 && timer.Issue!=null,"stalled framework beyond lease fails closed instead of starting late");
    }
}
