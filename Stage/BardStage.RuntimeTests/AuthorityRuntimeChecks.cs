using System.Text.Json;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;

internal static class AuthorityRuntimeChecks
{
    public static void RunStandalone(string output)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "authority-check-" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);
        var midiPath = Path.Combine(scratch, "authority.mid");
        new Melanchall.DryWetMidi.Core.MidiFile(new Melanchall.DryWetMidi.Core.TrackChunk(
            new Melanchall.DryWetMidi.Core.NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)80),
            new Melanchall.DryWetMidi.Core.NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 1920 })).Write(midiPath);
        Run(scratch, midiPath);
        RuntimeUi.RunAuthorityChecks(midiPath, output);
    }

    public static void Run(string scratch, string midiPath)
    {
        using (var f = new Fixture(scratch, midiPath))
        {
            Check(f.Controller.CanEditQueue, "current local leader can edit the ensemble queue");
            f.Leader = false;
            var original = JsonSerializer.Serialize(f.Controller.State);
            var entry = f.Controller.QueueShow!.Entries[0].Id;
            foreach (var action in new[] { RoomAction.Add, RoomAction.Remove, RoomAction.MoveUp, RoomAction.Start,
                RoomAction.Pause, RoomAction.Stop, RoomAction.Skip, RoomAction.Continuous, RoomAction.Reception })
                Check(!f.Controller.QueueCommand(action, entry: entry, song: f.Controller.State.Songs[0].Id, value: true),
                    "former leader is rejected for " + action);
            Check(!f.Controller.CanEditQueue && original == JsonSerializer.Serialize(f.Controller.State),
                "leader change immediately makes local ensemble queue read-only without changing records");
            f.Player.Start(); f.Player.SetContinuous(false); f.Player.Tick();
            Check(!f.Player.IsRunning && f.Player.Enabled && f.Port.Loads == 0,
                "direct playback calls cannot bypass local authority or change continuous preference");
            f.Leader = true;
            Check(f.Controller.CanEditQueue && f.Controller.QueueCommand(RoomAction.Add, song: f.Controller.State.Songs[0].Id),
                "new local leader gains queue controls immediately without recreating controller");
            f.Player.Tick();
            Check(f.Port.Loads == 0, "regaining leadership never restarts the revoked scheduler by itself");
            f.Leader = false;
            Check(f.Controller.QueueCommand(RoomAction.PlaybackMode, number: (int)QueuePlaybackMode.Solo) && f.Controller.CanEditQueue,
                "local follower can leave ensemble mode for independent solo playback");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Port.LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Player.Start(); f.Player.Tick();
            Check(f.Player.IsLoading && f.Port.Loads == 1, "authority test starts one pending load");
            var original = JsonSerializer.Serialize(f.Controller.State);
            f.Leader = false; f.Player.Tick();
            Check(f.Port.LastCancellation.IsCancellationRequested && !f.Player.IsRunning && f.Player.ActiveEntryId == null,
                "loss of leadership cancels pending load and releases scheduler ownership");
            f.Port.LoadGate.SetResult(); f.Leader = true; f.Player.Tick(); f.Player.Tick();
            Check(!f.Player.IsLoading && f.Port.Starts == 0 && f.Port.Stops == 0 && f.Port.Finishes == 0
                && original == JsonSerializer.Serialize(f.Controller.State),
                "late completed load cannot start or stop playback after a leader round trip");
            f.Player.Start(); f.Player.Tick(); f.Player.Tick();
            Check(f.Port.Loads == 2 && f.Port.Starts == 1, "explicit restart after regaining authority loads a fresh attempt");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Player.Start(); f.Player.Tick(); f.Player.Tick();
            Check(f.Port.Starts == 1 && f.Player.ActiveEntryId != null, "ensemble preparation is pending before authority loss");
            var original = JsonSerializer.Serialize(f.Controller.State);
            f.Leader = false; f.Player.Tick(); f.Player.Stop(); f.Player.Pause(); f.Player.Skip();
            Check(f.Port.Stops == 0 && f.Port.Pauses == 0 && f.Port.Finishes == 0 && !f.Player.IsRunning
                && original == JsonSerializer.Serialize(f.Controller.State),
                "revoking preparation sends no transport command and consumes no queued song");
        }

        using (var f = new RoomRuntimeChecks.Fixture(scratch, midiPath))
        {
            var leader = true;
            f.Captain.LocalEnsembleControlIssue = () => leader ? null : Fixture.Reason;
            f.Captain.Change(s => s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble);
            f.Port.DurationTicks = 192;
            f.Captain.QueueCommand(RoomAction.Add, song: f.First);
            f.Captain.QueueCommand(RoomAction.Add, song: f.Second);
            f.Player.Start(); f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            var original = JsonSerializer.Serialize(f.Captain.State);
            leader = false; f.Player.Tick();
            Check(f.Port.IsPlaying && original == JsonSerializer.Serialize(f.Captain.State),
                "leadership loss leaves current real MIDI playback and performance history unchanged");
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.Completed);
            Check(f.Port.Starts.Count == 1 && f.Port.FinishCalls == 0 && f.Show.Entries[1].Status == EntryStatus.Queued,
                "old leader records natural completion but never advances or sends ensemble finish");
        }

        using (var f = new RoomRuntimeChecks.Fixture(scratch, midiPath))
        {
            var leader = true;
            f.Captain.LocalEnsembleControlIssue = () => leader ? null : Fixture.Reason;
            f.Captain.Change(s => s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble);
            f.Port.DurationTicks = 1920;
            f.Captain.QueueCommand(RoomAction.Add, song: f.First);
            f.Captain.QueueCommand(RoomAction.Add, song: f.Second);
            f.Player.Start(); f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            var current = f.Player.ActiveEntryId;
            leader = false; f.Player.Tick(); leader = true; f.Player.Tick();
            Check(!f.Player.IsRunning && f.Player.ActiveEntryId == current && f.Port.IsPlaying,
                "restored leader retains current playing entry without restarting the scheduler");
            f.Player.Pause(); f.Captain.Poll(); f.Player.Tick();
            Check(f.Player.IsPaused && !f.Port.IsPlaying, "restored leader can pause the same current song");
            leader = false; f.Player.Tick(); leader = true; f.Player.Tick();
            Check(f.Player.IsPaused && !f.Player.IsRunning && !f.Port.IsPlaying,
                "restoring leadership while paused never resumes playback automatically");
            f.Player.Start(); f.Captain.Poll(); f.Player.Tick();
            Check(f.Port.IsPlaying && f.Player.ActiveEntryId == current && f.Captain.State.Sessions.Single().Attempts.Count == 1,
                "restored leader explicitly resumes the existing performance attempt");
            leader = false; f.Player.Tick(); leader = true; f.Player.Tick();
            f.Player.Stop(); f.Captain.Poll(); f.Player.Tick();
            Check(!f.Port.IsPlaying && !f.Player.IsRunning && f.Player.Enabled && f.Show.Entries[0].Status == EntryStatus.Skipped
                && f.Show.Entries[1].Status == EntryStatus.Queued && f.Port.Starts.Count == 1,
                "restored leader stops current playback without advancing or losing continuous preference");
        }

        using (var f = new RoomRuntimeChecks.Fixture(scratch, midiPath))
        {
            var hostAllowed = true;
            f.Captain.Room!.CanHost = () => hostAllowed ? null : Fixture.Reason;
            f.Presenter.LocalEnsembleControlIssue = () => Fixture.Reason;
            f.Connect();
            Check(f.Presenter.CanEditQueue, "presenter rights do not depend on presenter's local party leadership");
            f.Remote(RoomAction.Add, song: f.First);
            hostAllowed = false; f.Captain.Room.Tick(true);
            f.Until(() => f.Presenter.QueueViewPlayback.Status.Contains(Fixture.Reason));
            Check(!f.Captain.CanEditQueue && f.Captain.Room.ConnectionStatus.Contains(Fixture.Reason)
                && !f.Captain.Room.ReceptionSettings.IsOpen, "former host publishes suspended control and stops chat reception");
            var original = JsonSerializer.Serialize(f.Captain.State);
            f.Send(new() { Action = RoomAction.Add, SongId = f.Second }, false);
            f.Send(new() { Action = RoomAction.Start }, false);
            f.Send(new() { Action = RoomAction.Chat, Chat = new("Audience", "World", "First", RequestChannel.Say) }, false);
            Check(original == JsonSerializer.Serialize(f.Captain.State) && !f.Player.IsRunning
                && f.Presenter.StatusMessage.Contains(Fixture.Reason),
                "server rechecks authority for remote edits, playback and chat with an explicit rejection");
            hostAllowed = true; f.Captain.Room.Tick(true);
            f.Until(() => f.Presenter.QueueViewPlayback == f.Captain.LocalPlayback);
            f.Remote(RoomAction.Add, song: f.Second);
            Check(f.Show.Entries.Count == 2 && f.Captain.CanEditQueue,
                "restored original host resumes room edits without dropping the queue or connection");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Reason = "当前小队队长已变更，合奏队列只读";
        public readonly StageController Controller;
        public readonly Port Port = new();
        public readonly AutoQueuePlayer Player;
        public bool Leader = true;

        public Fixture(string scratch, string midiPath)
        {
            Controller = new StageController(Path.Combine(scratch, "authority-" + Guid.NewGuid())) { SyncAvailable = true };
            Controller.Import([midiPath]);
            while (Controller.IsBusy) { Controller.Poll(); Thread.Sleep(5); }
            if (Controller.State.Songs.Count != 1) throw new InvalidOperationException(Controller.StatusMessage);
            Controller.EnsureAutomaticQueue();
            Controller.Change(s =>
            {
                s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble;
                s.RequestSettings.IsOpen = true;
                s.Setlists[0].GapSeconds = 0;
                SetlistOperations.AddSong(s.Setlists[0], s.Songs[0]);
            });
            Controller.LocalEnsembleControlIssue = () => Leader ? null : Reason;
            Player = new AutoQueuePlayer(Controller, Port);
            Controller.QueuePlayer = Player;
        }

        public void Dispose() { Player.Dispose(); Controller.Dispose(); }
    }

    private sealed class Port : IStagePlaybackPort
    {
        public TaskCompletionSource? LoadGate;
        public CancellationToken LastCancellation;
        public int Loads, Starts, Stops, Pauses, Finishes;
        public bool IsPlaying => false;
        public string? BlockReason(QueuePlaybackMode mode) => null;
        public Task LoadAsync(string path, QueuePlaybackMode mode, CancellationToken cancellationToken)
        { Loads++; LastCancellation = cancellationToken; return LoadGate?.Task ?? Task.CompletedTask; }
        public void Start(QueuePlaybackMode mode) => Starts++;
        public void Pause() => Pauses++;
        public void Resume() => throw new InvalidOperationException("Unexpected resume");
        public void Finish(QueuePlaybackMode mode) => Finishes++;
        public void Stop(QueuePlaybackMode mode, bool keepInstruments = false) => Stops++;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
