using System.Security.Cryptography;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

internal static class HandoffRuntimeChecks
{
    public static void RunUi(string scratch, string midiPath, string output)
    {
        using var f = new Fixture(scratch, midiPath);
        f.Connect(); f.AddTwoAndStart();
        RuntimeUi.RunHandoffChecks(f.A.Controller, () =>
        {
            f.Leader(2);
            f.Until(() => f.A.Controller.CanEditQueue && f.Host.Coordinator.ExecutionIssue == null
                && f.A.Controller.QueueViewPlayback == f.Host.Controller.LocalPlayback);
        }, () => f.Until(() => f.Player.IsPaused && !f.A.Controller.Room!.OperationPending
            && f.A.Controller.QueueViewPlayback == f.Host.Controller.LocalPlayback), () =>
        {
            f.Leader(3);
            f.Until(() => !f.A.Controller.CanEditQueue && f.B.Controller.CanEditQueue && f.A.Controller.QueueState.Songs.Count == 0
                && f.Host.Coordinator.ExecutionIssue == null && f.A.Controller.QueueViewPlayback == f.Host.Controller.LocalPlayback);
        }, output);
    }

    public static void Run(string scratch, string midiPath)
    {
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.Leader(2);
            f.Until(() => f.A.Controller.CanEditQueue && f.Host.Coordinator.ExecutionIssue == null);
            foreach (var sender in new[] { f.Presenter, f.A.Controller })
            {
                f.Until(() => sender.Room!.Client!.Snapshot!.Revision == f.Host.Controller.QueueRevision);
                if (!sender.QueueCommand(RoomAction.PlaybackMode, number: (int)QueuePlaybackMode.Solo))
                    throw new InvalidOperationException("mode check was not sent");
                f.Until(() => !sender.Room!.OperationPending);
                Check(sender.StatusIsError && sender.StatusMessage.Contains("原房主")
                    && f.Host.Controller.State.RequestSettings.PlaybackMode == QueuePlaybackMode.Ensemble
                    && f.A.Controller.CanEditQueue && f.Presenter.CanEditQueue,
                    "handoff rejects solo mode without locking out " + (ReferenceEquals(sender, f.Presenter) ? "presenter" : "new leader"));
            }
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect();
            Check(!f.A.Controller.CanEditQueue && !f.B.Controller.CanEditQueue
                && f.A.Controller.QueueState.Songs.Count == 0, "ordinary TLS viewers remain read-only with no shared catalog");
            f.AddTwoAndStart();
            var room = f.Host.Controller.Room!.Id;
            var playback = f.Host.Controller.LastPlaybackSignal!.PlaybackId;
            var firstEntry = f.Show.Entries[0].Id;
            f.Leader(2);
            f.Until(() => f.A.Controller.CanEditQueue && f.Host.Coordinator.ExecutionIssue == null);
            Check(f.Host.Controller.Room.Id == room && f.Presenter.Room!.Connected && f.Player.IsRunning
                && f.Player.ActiveEntryId == firstEntry && f.Host.Controller.LastPlaybackSignal!.PlaybackId == playback,
                "leadership handoff preserves room, presenter, current entry and canonical playback identity");
            Check(f.A.Backend.Adopts == 1 && f.A.Backend.Loads == 0 && f.A.Backend.Starts == 0 && f.A.Backend.Stops == 0
                && !f.Host.Controller.CanEditQueue && f.A.Controller.QueueState.Songs.Count == 2,
                "new leader adopts current playback without reload or restart and receives controller catalog");
            f.Remote(f.Presenter, RoomAction.Pause);
            f.Until(() => f.Player.IsPaused);
            f.Remote(f.Presenter, RoomAction.Start);
            f.Until(() => !f.Player.IsPaused && f.A.Backend.Resumes == 1);
            Check(f.A.Backend.Pauses == 1 && f.Host.Backend.Pauses == 0
                && f.Host.Controller.State.Sessions.Single().Attempts.Count == 1,
                "presenter pause and resume execute only on the new leader without duplicate attempts");

            foreach (var action in new[] { RoomExecutionAction.Start, RoomExecutionAction.Pause, RoomExecutionAction.Resume,
                RoomExecutionAction.Stop, RoomExecutionAction.Finish })
            {
                var command = new RoomExecutionCommand { Epoch = f.Host.Coordinator.Epoch, Action = action,
                    Hash = new string('F', 64), Mode = QueuePlaybackMode.Ensemble };
                f.Inject(command);
                Check(f.ClientResult(f.A, command.Id) is { Success: false }, "delayed wrong-song " + action + " is rejected");
            }
            var oldEpoch = f.Host.Coordinator.Epoch;
            f.Leader(3);
            f.Until(() => f.B.Controller.CanEditQueue && !f.A.Controller.CanEditQueue && f.Host.Coordinator.ExecutionIssue == null);
            Check(f.A.Controller.QueueState.Songs.Count == 0 && f.B.Backend.Adopts == 1,
                "second handoff demotes the former leader and promotes the next verified member");
            var stale = new RoomExecutionCommand { Epoch = oldEpoch, Action = RoomExecutionAction.Stop,
                Hash = f.B.Backend.Hash, Mode = QueuePlaybackMode.Ensemble };
            f.Inject(stale);
            Check(f.ClientResult(f.B, stale.Id) is { Success: false } && f.B.Backend.Stops == 0,
                "old authority epoch cannot stop the current performance");

            f.Remote(f.Presenter, RoomAction.ReceptionOwner, value: false);
            f.Remote(f.Presenter, RoomAction.Reception, value: true);
            Check(f.B.Controller.Room!.ReceptionSettings.IsOpen && !f.A.Controller.Room!.ReceptionSettings.IsOpen
                && !f.Host.Controller.Room.ReceptionSettings.IsOpen && !f.Presenter.Room!.ReceptionSettings.IsOpen,
                "captain chat reception follows the promoted viewer and stays disabled on other endpoints");
            var requests = f.Host.Controller.State.Requests.Count;
            var chat = new IncomingChatRequest(RequestChannel.Say, "Audience", "World", "First", DateTimeOffset.UtcNow, f.Show.Id);
            f.B.Controller.Room.ReceiveChat(chat);
            f.Until(() => f.Host.Controller.State.Requests.Count == requests + 1);
            Check(f.Host.Controller.State.Requests.Last().Status == RequestStatus.Arranged,
                "new leader forwards audience chat to the original authoritative queue");

            f.FinishAll();
            f.Until(() => f.B.Backend.Starts == 1 && f.Show.Entries[1].Status == EntryStatus.InProgress);
            Check(f.Host.Backend.Starts == 1 && f.B.Backend.Loads == 1 && f.B.Backend.Finishes == 1
                && f.Host.Controller.State.Sessions.Single().Attempts.Count == 2,
                "natural completion advances exactly once through the new leader backend");
            f.Remote(f.Presenter, RoomAction.Stop);
            f.Until(() => f.B.Backend.Stops == 1 && !f.Player.IsRunning && f.Show.Entries[1].Status == EntryStatus.Skipped);
            Check(f.Player.Enabled && f.Show.Entries[2].Status == EntryStatus.Queued,
                "remote stop preserves continuous preference and the remaining queue");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.A.Backend.Prove = false;
            f.Connect(); f.AddTwoAndStart();
            f.Leader(2); f.PumpFor(100);
            Check(f.Host.Coordinator.ExecutionIssue != null && f.Player.IsRunning && !f.A.Controller.CanEditQueue,
                "unverified new leader suspends scheduling while preserving continuous intent");
            var proof = f.A.Backend.LastProof!.Value;
            f.Host.Coordinator.ReceiveProof(proof.Room, proof.Challenge, 99, Fixture.PartyId);
            f.PumpFor(100);
            Check(f.Host.Coordinator.ExecutorCid == 0 && !f.A.Controller.CanEditQueue,
                "identity proof from a different real party sender cannot claim the leader CID");
            f.A.Controller.Room!.Leave(); f.PumpFor(50); f.A.Backend.Prove = true;
            f.JoinViewer(f.A);
            f.Until(() => f.A.Controller.CanEditQueue && f.Host.Coordinator.ExecutionIssue == null);
            Check(f.Show.Entries[0].Status == EntryStatus.InProgress && f.A.Backend.Loads == 0,
                "reconnected verified leader resumes the same room and adopts the existing song");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect();
            f.Host.Backend.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            f.AddTwoAndStart(waitForStart: false);
            f.Until(() => f.Host.Backend.Loads == 1);
            f.Leader(2);
            f.Until(() => f.Host.Backend.LastCancellation.IsCancellationRequested && f.A.Controller.CanEditQueue);
            f.Host.Backend.LoadGate.SetException(new IOException("old transport revoked"));
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            Check(f.Host.Backend.Starts == 0 && f.A.Backend.Loads == 1 && f.A.Backend.Starts == 1
                && f.Host.Controller.State.Sessions.Single().Attempts.Count == 1,
                "handoff during load cancels old attempt and ignores its late failure before new leader loads once");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.AddTwoAndStart();
            f.Leader(2); f.FinishAll();
            f.Until(() => f.Show.Entries[1].Status == EntryStatus.InProgress);
            Check(f.Show.Entries[0].Status == EntryStatus.Completed && f.A.Backend.Finishes == 1
                && f.Host.Controller.State.Sessions.Single().Attempts.Count == 2,
                "natural completion during adoption is reconciled once before next song");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.AddTwoAndStart(); f.Leader(2);
            f.Until(() => f.Host.Coordinator.ExecutionIssue == null && f.A.Controller.CanEditQueue);
            f.Remote(f.A.Controller, RoomAction.Skip);
            f.Until(() => f.Show.Entries[1].Status == EntryStatus.InProgress);
            Check(f.Show.Entries[0].Status == EntryStatus.Skipped && f.A.Backend.Stops == 1 && f.A.Backend.Starts == 1
                && f.Player.IsRunning, "promoted leader can skip remotely and only confirmed stop advances the queue");
            f.Leader(1);
            f.Until(() => f.Host.Coordinator.ExecutionIssue == null && f.Host.Controller.CanEditQueue && !f.A.Controller.CanEditQueue);
            f.Remote(f.Presenter, RoomAction.Stop);
            f.Until(() => f.Show.Entries[1].Status == EntryStatus.Skipped);
            Check(f.Host.Backend.Loads == 1 && f.Host.Backend.Starts == 1 && f.Host.Backend.Stops == 1,
                "original host can regain execution through adoption without recreating room or replaying song");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.AddTwoAndStart(); f.Leader(2);
            f.Until(() => f.Host.Coordinator.ExecutionIssue == null && f.A.Controller.CanEditQueue);
            f.A.Backend.FailStop = true;
            f.Remote(f.Presenter, RoomAction.Skip);
            f.Until(() => f.Player.StatusIsError);
            Check(f.Show.Entries[0].Status == EntryStatus.InProgress && f.Show.Entries[1].Status == EntryStatus.Queued
                && f.A.Backend.IsPlaying && !f.Player.IsRunning,
                "failed remote skip preserves current history and does not start another song");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.AddTwoAndStart(); f.Leader(2);
            f.Until(() => f.Host.Coordinator.ExecutionIssue == null && f.A.Controller.CanEditQueue);
            f.A.Backend.MissingSong = true; f.FinishAll();
            f.Until(() => f.Player.StatusIsError);
            Check(f.Show.Entries[1].Status == EntryStatus.Queued && f.A.Backend.Starts == 0 && !f.Player.IsRunning,
                "missing MIDI on new leader reports failure without consuming the next queue entry");
            Check(f.A.Controller.StatusIsError && f.A.Controller.StatusMessage.Contains("缺少同一份 MIDI"),
                "new leader receives the concrete local missing-MIDI error behind shared playback status");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.AddTwoAndStart(); f.A.Backend.FailAdopt = true; f.Leader(2);
            f.Until(() => f.A.Backend.Adopts > 0);
            Check(f.Show.Entries[0].Status == EntryStatus.InProgress && f.Player.IsRunning && f.A.Backend.Loads == 0,
                "unavailable current playback suspends adoption without consuming or reloading the song");
            f.A.Backend.FailAdopt = false;
            f.Until(() => f.Host.Coordinator.ExecutionIssue == null);
            f.A.Controller.Room!.Leave(); f.Until(() => f.Host.Coordinator.ExecutionIssue != null);
            f.FinishAll(); f.PumpFor(50); f.JoinViewer(f.A);
            f.Until(() => f.Show.Entries[1].Status == EntryStatus.InProgress);
            Check(f.Presenter.Room!.Connected && f.Host.Controller.State.Sessions.Single().Attempts.Count == 2
                && f.A.Backend.Loads == 1,
                "executor reconnect reconciles offline completion and resumes original queue once");
        }

        using (var f = new Fixture(scratch, midiPath) { RealEngine = true })
        {
            f.Connect(); f.AddTwoAndStart(); var first = f.Player.ActiveEntryId;
            f.Leader(2); f.Until(() => f.A.Controller.CanEditQueue && f.Host.Coordinator.ExecutionIssue == null);
            f.Remote(f.Presenter, RoomAction.Pause); f.Until(() => f.Player.IsPaused);
            Check(f.Nodes.All(n => !n.Backend.IsPlaying) && f.Player.ActiveEntryId == first,
                "real DryWetMidi playback pauses on all endpoints after TLS leadership handoff");
            f.Remote(f.Presenter, RoomAction.Start);
            f.Until(() => f.Show.Entries.All(e => e.Status == EntryStatus.Completed), 10);
            f.Until(() => f.A.Backend.Finishes == 2);
            Check(f.Host.Backend.Loads == 1 && f.A.Backend.Loads == 1 && f.A.Backend.Starts == 1
                && f.Host.Controller.State.Sessions.Single().Attempts.Count == 2,
                "real DryWetMidi finish after adoption advances through remote load and start with exactly two attempts");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const long PartyId = 987;
        public readonly Node Host, A, B;
        public readonly StageController Presenter;
        public readonly AutoQueuePlayer Player;
        public bool RealEngine;
        public ShowSetlist Show => Host.Controller.QueueShow!;
        public Fixture(string scratch, string midiPath)
        {
            Host = new(this, scratch, 1); A = new(this, scratch, 2); B = new(this, scratch, 3);
            Presenter = new(Path.Combine(scratch, "handoff-presenter-" + Guid.NewGuid())); Presenter.Room = new(Presenter);
            Host.Controller.Import([midiPath]);
            while (Host.Controller.IsBusy) { Host.Controller.Poll(); Thread.Sleep(5); }
            Host.Controller.EnsureAutomaticQueue();
            Host.Controller.Change(s =>
            {
                s.Songs[0].Title = "First"; s.Songs[0].Aliases = [];
                var second = StageController.Clone(s.Songs[0]); second.Id = Guid.NewGuid(); second.Title = "Second";
                second.FilePath = Path.Combine(Host.Controller.DataDirectory, "Second.mid");
                File.WriteAllBytes(second.FilePath, File.ReadAllBytes(midiPath).Concat(new byte[] { 0 }).ToArray());
                second.Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second.FilePath)));
                s.Songs.Add(second); s.Setlists[0].GapSeconds = 0;
                s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble;
            });
            Player = new(Host.Controller, Host.Coordinator); Host.Controller.QueuePlayer = Player;
        }
        public void Connect()
        {
            if (!Host.Controller.Room!.Create(0) || !Presenter.Room!.Join(Host.Controller.Room.Invite("127.0.0.1", Host.Controller.Room.LocalPort)))
                throw new InvalidOperationException("handoff room connect failed");
            JoinViewer(A); JoinViewer(B);
            Until(() => Presenter.Room.Connected && A.Controller.Room!.Connected && B.Controller.Room!.Connected
                && A.Backend.LastProof != null && B.Backend.LastProof != null && Host.Coordinator.ExecutionIssue == null);
            PumpFor(30);
        }
        public void JoinViewer(Node node)
        {
            if (!node.Controller.Room!.Join(Host.Controller.Room!.Invite("127.0.0.1", Host.Controller.Room.LocalPort, RoomRole.Viewer)))
                throw new InvalidOperationException(node.Controller.StatusMessage);
        }
        public void Leader(ulong cid)
        { foreach (var node in Nodes) node.Backend.Party = node.Backend.Party with { LeaderCid = cid }; }
        public IEnumerable<Node> Nodes => new[] { Host, A, B };
        public void AddTwoAndStart(bool waitForStart = true)
        {
            Remote(Presenter, RoomAction.Add, Host.Controller.State.Songs[0].Id);
            Remote(Presenter, RoomAction.Add, Host.Controller.State.Songs[1].Id);
            Remote(Presenter, RoomAction.Start);
            if (waitForStart) Until(() => Show.Entries[0].Status == EntryStatus.InProgress);
        }
        public void Remote(StageController sender, RoomAction action, Guid? song = null, bool value = false)
        {
            Until(() => sender.Room!.Connected && !sender.Room.OperationPending
                && sender.Room.Client!.Snapshot!.Revision == Host.Controller.QueueRevision);
            if (!sender.QueueCommand(action, song: song, value: value)) throw new InvalidOperationException(sender.StatusMessage);
            Until(() => !sender.Room!.OperationPending);
            if (sender.StatusIsError) throw new InvalidOperationException(action + ": " + sender.StatusMessage);
        }
        public void Inject(RoomExecutionCommand command)
        {
            var peer = Host.Controller.Room!.Server!.Participants.Single(p => Host.Controller.Room.Server.IsExecutor(p.Peer)).Peer;
            peer.Send(new RoomPacket { Type = "execute", RoomId = Host.Controller.Room.Id, Execution = command });
            var target = Nodes.Single(n => n.Backend.Party.SelfCid == n.Backend.Party.LeaderCid);
            Until(() => ClientResult(target, command.Id) != null);
        }
        public RoomExecutionResult? ClientResult(Node node, Guid id)
        {
            var results = (Dictionary<Guid, RoomExecutionResult>)typeof(RoomPlaybackCoordinator)
                .GetField("clientResults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(node.Coordinator)!;
            return results.GetValueOrDefault(id);
        }
        public void FinishAll() { foreach (var node in Nodes) node.Backend.Emit(PlaybackSignalKind.Finished); }
        public void Pump()
        {
            Host.Coordinator.Tick(); Host.Controller.Poll(); Host.Controller.Room!.Tick(); Player.Tick();
            foreach (var node in new[] { A, B }) { node.Coordinator.Tick(); node.Controller.Poll(); node.Controller.Room!.Tick(); }
            Presenter.Poll(); Presenter.Room!.Tick();
        }
        public void PumpFor(int milliseconds)
        { var end = DateTime.UtcNow.AddMilliseconds(milliseconds); do { Pump(); Thread.Sleep(5); } while (DateTime.UtcNow < end); }
        public void Until(Func<bool> done, int seconds = 8)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                Pump();
                if (DateTime.UtcNow > end) throw new TimeoutException(Host.Controller.StatusMessage + " / " + Presenter.StatusMessage
                    + " / " + Player.Status + " / " + Host.Coordinator.ExecutionIssue);
                Thread.Sleep(5);
            }
        }
        public void Dispose()
        {
            var tasks = Nodes.Select(n => n.Controller.Room?.TransportCompletion).Append(Presenter.Room?.TransportCompletion).OfType<Task>().ToArray();
            Player.Dispose(); foreach (var node in Nodes) { node.Backend.Dispose(); node.Coordinator.Dispose(); node.Controller.Dispose(); }
            Presenter.Dispose(); Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    private sealed class Node
    {
        public readonly StageController Controller;
        public readonly Backend Backend;
        public readonly RoomPlaybackCoordinator Coordinator;
        public Node(Fixture fixture, string scratch, ulong cid)
        {
            Controller = new(Path.Combine(scratch, "handoff-" + cid + "-" + Guid.NewGuid())) { SyncAvailable = true };
            Controller.Room = new(Controller); Backend = new(fixture, cid); Coordinator = new(Controller, Controller.Room, Backend);
            Backend.Coordinator = Coordinator;
        }
    }

    private sealed class Backend(Fixture fixture, ulong cid) : IRoomEnsembleBackend, IDisposable
    {
        public RoomPartyState Party { get; set; } = new(Fixture.PartyId, cid, 1);
        public RoomPlaybackCoordinator Coordinator = null!;
        public int Loads, Starts, Pauses, Resumes, Stops, Finishes, Adopts;
        public bool Prove = true, FailStop, MissingSong, FailAdopt;
        public TaskCompletionSource? LoadGate;
        public CancellationToken LastCancellation;
        public (Guid Room, string Challenge)? LastProof;
        public string Path = "", Hash = "";
        private Guid playback;
        private long sequence;
        private Playback? engine;
        private PlaybackObserver? observer;
        private bool playing;
        public bool IsPlaying => fixture.RealEngine ? engine?.IsRunning == true : playing;
        public string? BlockReason(QueuePlaybackMode mode) => Party.SelfCid == Party.LeaderCid ? null : "not leader";
        public string Fingerprint(string filePath) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
        public string? ResolveSong(string hash) => MissingSong ? null
            : fixture.Host.Controller.State.Songs.FirstOrDefault(s => Fingerprint(s.FilePath) == hash)?.FilePath;
        public void SendIdentityProof(Guid roomId, string challenge)
        {
            LastProof = (roomId, challenge);
            if (Prove) fixture.Host.Coordinator.ReceiveProof(roomId, challenge, Party.SelfCid, Party.PartyId);
        }
        public bool Adopt(string hash) { Adopts++; return !FailAdopt && Hash == hash; }
        public void Revoke() { }
        public Task LoadAsync(string filePath, QueuePlaybackMode mode, CancellationToken cancellationToken)
        {
            Loads++; LastCancellation = cancellationToken;
            if (LoadGate != null) return LoadGate.Task;
            foreach (var node in fixture.Nodes)
            {
                node.Backend.Path = filePath; node.Backend.Hash = Fingerprint(filePath); node.Backend.playback = Guid.NewGuid();
                if (fixture.RealEngine)
                {
                    node.Backend.observer ??= new(node.Coordinator.Receive);
                    node.Backend.engine?.Dispose();
                    node.Backend.engine = new MidiFile(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)80),
                        new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 384 }))
                        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) }.GetPlayback();
                    node.Backend.observer.Attach(node.Backend.engine, filePath);
                }
                else node.Backend.Emit(PlaybackSignalKind.Loaded);
            }
            return Task.CompletedTask;
        }
        public void Start(QueuePlaybackMode mode) { Starts++; Broadcast(PlaybackSignalKind.Started); }
        public void Pause() { Pauses++; Broadcast(PlaybackSignalKind.Paused); }
        public void Resume() { Resumes++; Broadcast(PlaybackSignalKind.Resumed); }
        public void Finish(QueuePlaybackMode mode) { Finishes++; }
        public void Stop(QueuePlaybackMode mode, bool keepInstruments = false)
        { if (FailStop) throw new InvalidOperationException("simulated stop failure"); Stops++; Broadcast(PlaybackSignalKind.Stopped); }
        private void Broadcast(PlaybackSignalKind kind)
        {
            foreach (var node in fixture.Nodes)
            {
                if (!fixture.RealEngine) node.Backend.Emit(kind);
                else if (kind is PlaybackSignalKind.Started or PlaybackSignalKind.Resumed) node.Backend.engine!.Start();
                else if (kind == PlaybackSignalKind.Paused) node.Backend.engine!.Stop();
                else if (kind == PlaybackSignalKind.Stopped) node.Backend.observer!.Stop();
            }
        }
        public void Emit(PlaybackSignalKind kind)
        {
            playing = kind is PlaybackSignalKind.Started or PlaybackSignalKind.Resumed;
            Coordinator.Receive(new(playback, ++sequence, Path, kind, DateTimeOffset.UtcNow));
        }
        public void Dispose() { observer?.Dispose(); engine?.Dispose(); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
