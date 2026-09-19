using BardStage;
using BardStage.Core;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

internal static class AutoQueueRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Request("One", "First"); f.Request("Two", "Second"); f.Controller.Poll();
            Check(f.Show.Entries.Select(e => e.Title).SequenceEqual(new[] { "First", "Second" }), "chat requests automatically persist a FIFO queue");
            f.Player.Start(); f.Until(() => f.Show.Entries.All(e => e.Status == EntryStatus.Completed));
            Check(f.Port.Starts.SequenceEqual(new[] { "First", "Second" }) && f.Controller.State.Sessions.Single().Attempts.Count == 2,
                "real engine plays two audience requests in order and automatically records both");
            f.Request("Three", "First"); f.Until(() => f.Show.Entries.Count == 3 && f.Show.Entries.All(e => e.Status == EntryStatus.Completed));
            Check(f.Player.Enabled && f.Port.Starts.Count == 3, "empty queue keeps listening and starts the next arriving request");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Port.DurationTicks = 384;
            f.Request("One", "First"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            f.Player.Pause(); f.Controller.Poll(); f.Player.Tick();
            Check(f.Player.IsPaused && f.Player.Enabled && !f.Port.IsPlaying, "pause stops real playback and preserves continuous preference");
            f.Player.Start(); f.Controller.Poll();
            Check(f.Port.IsPlaying && f.Player.Enabled && f.Controller.State.Sessions.Single().Attempts.Count == 1, "resume continues the same performance attempt");
            f.Player.Stop(); f.Controller.Poll(); f.Player.Tick();
            Check(f.Player.Enabled && !f.Player.IsRunning && f.Show.Entries[0].Status == EntryStatus.Skipped, "stop records interruption and holds advancement without changing continuous preference");
            f.Controller.Requeue(f.Show.Entries[0].Id); f.Player.Start();
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            Check(f.Controller.State.Sessions.Single().Attempts.Count == 2, "requeue preserves the interrupted attempt and starts a new attempt");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Request("One", "First"); f.Request("Two", "Second"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            f.Player.Skip(); f.Until(() => f.Show.Entries[1].Status == EntryStatus.Completed);
            Check(f.Show.Entries[0].Status == EntryStatus.Skipped && f.Player.Enabled, "skip cancels only the current request and continues with the next");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Port.FailLoad = true; f.Request("One", "First"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Player.StatusIsError);
            Check(f.Port.Starts.Count == 0 && f.Show.Entries[0].Status == EntryStatus.Queued, "failed load leaves the request queued with no false performance record");
            f.Port.FailLoad = false; f.Player.Start(); f.Until(() => f.Show.Entries[0].Status == EntryStatus.Completed);
            Check(f.Port.Starts.Count == 1, "retry after failed load plays the request once");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Port.LoadGate = new TaskCompletionSource();
            f.Request("One", "First"); f.Controller.Poll(); f.Player.Start(); f.Pump();
            Check(f.Player.IsLoading, "async load is pending");
            f.Player.Stop(); f.Port.LoadGate.SetResult(); f.Until(() => !f.Player.IsLoading);
            Check(f.Port.Starts.Count == 0 && !f.Player.IsRunning && f.Show.Entries[0].Status == EntryStatus.Queued,
                "late load completion after cancellation never starts playback");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Request("One", "First"); f.Request("Two", "Second"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            using (var blocker = new FileStream(Path.Combine(f.Controller.DataDirectory, "catalog.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                f.Until(() => f.Controller.SyncBlocked);
                Check(f.Port.Starts.Count == 1 && f.Show.Entries[1].Status == EntryStatus.Queued, "failed completion persistence prevents the next song from playing");
            }
            f.Controller.RetryPlayback(); f.Until(() => f.Show.Entries[1].Status == EntryStatus.Completed);
            Check(f.Port.Starts.Count == 2 && f.Controller.State.Sessions.Single().Attempts.Count == 2,
                "retry saves retained completion and advances exactly once");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Controller.Change(s => s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble);
            f.Port.DelayStart = true; f.Request("One", "First"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Port.StartCalls == 1);
            Check(f.Show.Entries[0].Status == EntryStatus.Queued, "ensemble preparation does not fabricate a playback start");
            f.Port.BeginActualPlayback(); f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            f.Player.Pause(); f.Controller.Poll(); f.Player.Tick();
            Check(f.Player.IsPaused && f.Player.Enabled && !f.Port.IsPlaying, "ensemble pause stops current music and keeps continuous preference");
            f.Player.Start(); f.Until(() => f.Show.Entries[0].Status == EntryStatus.Completed);
            Check(f.Port.FinishCalls == 1, "ensemble resume finishes the same performance");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Controller.Change(s => s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble);
            f.Port.DelayStart = true; f.Request("One", "First"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Port.StartCalls == 1); f.Time = f.Time.AddSeconds(91); f.Pump();
            Check(!f.Player.IsRunning && f.Show.Entries[0].Status == EntryStatus.Queued && !f.Port.IsPlaying,
                "ensemble readiness timeout stops scheduling without consuming the request");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Controller.Change(s => s.Setlists[0].GapSeconds = 4);
            f.Request("One", "First"); f.Request("Two", "Second"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.Completed);
            f.Pump(); Check(f.Port.Starts.Count == 1, "configured song gap prevents immediate advancement");
            f.Time = f.Time.AddSeconds(5); f.Until(() => f.Show.Entries[1].Status == EntryStatus.Completed);
            Check(f.Port.Starts.Count == 2, "the next song starts after the configured gap");
        }
        using (var f = new Fixture(scratch, midiPath))
        {
            f.Port.DurationTicks = 192;
            f.Request("One", "First"); f.Request("Two", "Second"); f.Controller.Poll(); f.Player.Start();
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            f.Player.SetContinuous(false);
            Check(f.Port.IsPlaying && f.Controller.State.RequestSettings.IsOpen, "turning off continuous leaves music and reception running");
            f.Player.Pause(); f.Controller.Poll(); f.Player.Start();
            Check(!f.Player.Enabled && f.Port.IsPlaying, "resume preserves disabled continuous preference");
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.Completed);
            f.Pump();
            Check(f.Port.Starts.Count == 1 && !f.Player.IsRunning, "single playback does not advance to the second song");
            f.Controller.SetReception(false); f.Player.Start();
            f.Until(() => f.Show.Entries[1].Status == EntryStatus.InProgress);
            Check(!f.Controller.State.RequestSettings.IsOpen, "play does not reopen audience reception");
            f.Player.Stop(); f.Controller.Poll(); f.Player.SetContinuous(true); f.Pump();
            Check(!f.Player.IsRunning, "enabling continuous after stop does not start music");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly StageController Controller;
        public readonly EnginePort Port;
        public readonly AutoQueuePlayer Player;
        private TimeSpan timeOffset;
        public DateTimeOffset Time { get => DateTimeOffset.UtcNow + timeOffset; set => timeOffset = value - DateTimeOffset.UtcNow; }
        public ShowSetlist Show => Controller.QueueShow!;
        public Fixture(string scratch, string midiPath)
        {
            Controller = new StageController(Path.Combine(scratch, "auto-" + Guid.NewGuid())) { SyncAvailable = true };
            Controller.Import([midiPath]);
            while (Controller.IsBusy) { Thread.Sleep(5); Controller.Poll(); }
            Controller.Change(s =>
            {
                s.Songs[0].Title = "First"; s.Songs[0].Aliases = [];
                var copy = StageController.Clone(s.Songs[0]); copy.Id = Guid.NewGuid(); copy.Title = "Second"; copy.Sha256 = new string('2', 64);
                var copyPath = Path.Combine(scratch, "Second.mid"); File.Copy(midiPath, copyPath, true); copy.FilePath = copyPath; s.Songs.Add(copy);
                s.Setlists[0].GapSeconds = 0;
            });
            Controller.EnsureAutomaticQueue(); Controller.SetReception(true);
            Port = new EnginePort(Controller); Player = new AutoQueuePlayer(Controller, Port, () => Time); Controller.QueuePlayer = Player;
        }
        public void Request(string name, string title) => Controller.ReceiveChat(new IncomingChatRequest(RequestChannel.Say, name, "", title, DateTimeOffset.UtcNow, Show.Id));
        public void Pump() { Controller.Poll(); Player.Tick(); }
        public void Until(Func<bool> condition)
        {
            var end = DateTime.UtcNow.AddSeconds(7);
            while (!condition()) { Pump(); if (DateTime.UtcNow > end) throw new InvalidOperationException("Queue timeout: " + Player.Status + " / " + Controller.StatusMessage); Thread.Sleep(5); }
            Pump();
        }
        public void Dispose() { Player.Dispose(); Port.Dispose(); Controller.Dispose(); }
    }

    internal sealed class EnginePort : IStagePlaybackPort, IDisposable
    {
        private readonly StageController controller;
        private readonly PlaybackObserver observer;
        private Playback? playback;
        private string path = "";
        public readonly List<string> Starts = [];
        public bool FailLoad, DelayStart;
        public TaskCompletionSource? LoadGate;
        public int StartCalls, FinishCalls;
        public int DurationTicks = 64;
        public EnginePort(StageController controller) { this.controller = controller; observer = new PlaybackObserver(controller.ReceivePlayback); }
        public bool IsPlaying => playback?.IsRunning == true;
        public string? BlockReason(QueuePlaybackMode mode) => null;
        public async Task LoadAsync(string filePath, QueuePlaybackMode mode, CancellationToken cancellationToken)
        {
            if (LoadGate != null) await LoadGate.Task;
            if (FailLoad) throw new IOException("test load failure");
            playback?.Dispose(); path = filePath;
            playback = new MidiFile(new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)80),
                new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = DurationTicks }))
                { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) }.GetPlayback();
            observer.Attach(playback, path);
        }
        public void Start(QueuePlaybackMode mode) { StartCalls++; if (!DelayStart) BeginActualPlayback(); }
        public void BeginActualPlayback() { Starts.Add(controller.State.Songs.First(s => s.FilePath == path).Title); playback!.Start(); }
        public void Pause() => playback!.Stop();
        public void Resume() => playback!.Start();
        public void Finish(QueuePlaybackMode mode) => FinishCalls++;
        public void Stop(QueuePlaybackMode mode, bool keepInstruments = false) { observer.Stop(); playback?.Dispose(); playback = null; }
        public void Dispose() { observer.Dispose(); playback?.Dispose(); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
