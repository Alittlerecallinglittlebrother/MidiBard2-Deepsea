using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;

internal static class RoomRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        using (var f = new Fixture(scratch, midiPath))
        {
            var original = File.ReadAllBytes(Path.Combine(f.Presenter.DataDirectory, "catalog.json"));
            f.Connect();
            Check(f.Presenter.QueueState.Songs.Count == 2 && f.Presenter.State.Songs.Count == 0, "presenter sees captain catalog without replacing local data");
            Check(f.Presenter.QueueState.Songs.All(s => s.FilePath == "" && s.Sha256 == "") && f.Presenter.QueueState.Sessions.Count == 0,
                "room snapshot excludes local paths, hashes and session archive");
            f.Presenter.Change(s => s.Setlists.Clear()); f.Presenter.Import([midiPath]);
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Presenter.DataDirectory, "catalog.json"))) && !f.Presenter.IsBusy,
                "presenter mode blocks leftover local editors and imports");
            f.Remote(RoomAction.Add, song: f.First);
            f.Remote(RoomAction.Add, song: f.Second);
            var firstEntry = f.Show.Entries[0].Id; var secondEntry = f.Show.Entries[1].Id;
            f.Remote(RoomAction.MoveUp, entry: secondEntry);
            Check(f.Show.Entries[0].Id == secondEntry && f.Presenter.QueueViewShow!.Entries[0].Id == secondEntry,
                "presenter add and reorder are acknowledged with the new queue");
            f.Remote(RoomAction.PlayNext, entry: firstEntry);
            Check(StageOperations.Next(f.Show)?.Id == firstEntry, "presenter can select the next song independently of queue order");
            f.Remote(RoomAction.Remove, entry: secondEntry);
            Check(f.Show.Entries.Single(e => e.Id == secondEntry).Status == EntryStatus.Skipped, "presenter removes a waiting song");
            f.Remote(RoomAction.Requeue, entry: secondEntry);
            Check(f.Show.Entries.Last().Id == secondEntry && f.Show.Entries.Last().Status == EntryStatus.Queued, "presenter can restore a removed song");
            f.Captain.QueueCommand(RoomAction.Remove, entry: secondEntry);
            f.Until(() => f.Presenter.QueueViewShow!.Entries.Last().Status == EntryStatus.Skipped);
            Check(f.Presenter.QueueViewShow!.Entries.Last().Id == secondEntry, "captain retains editing rights and publishes local changes");

            var duplicate = new RoomCommand { Action = RoomAction.Add, SongId = f.Second };
            f.Send(duplicate); var count = f.Show.Entries.Count;
            f.Send(duplicate);
            Check(f.Show.Entries.Count == count, "retransmitted operation ID never adds a song twice");
            // Freeze the presenter at the old revision, then edit locally before processing its packet.
            f.Captain.QueueCommand(RoomAction.Add, song: f.First);
            count = f.Show.Entries.Count;
            Check(f.Presenter.Room!.Send(new() { Action = RoomAction.Add, SongId = f.Second }), "stale command is sent for conflict check");
            f.Until(() => !f.Presenter.Room.OperationPending);
            Check(f.Show.Entries.Count == count && f.Presenter.StatusIsError && f.Presenter.StatusMessage.Contains("已更新"),
                "concurrent stale edit is rejected without overwriting captain changes");

            f.Send(new() { Action = RoomAction.Settings, Settings = new() { Prefix = null! } }, false);
            Check(f.Captain.State.RequestSettings.Prefix == "点歌", "malformed authenticated settings do not corrupt state or crash the pump");
            f.Send(new() { Action = RoomAction.Chat, Chat = new(null!, "", "First", RequestChannel.Say) }, false);
            f.Remote(RoomAction.Reception, value: true);
            var requestCount = f.Captain.State.Requests.Count;
            var chat = new IncomingChatRequest(RequestChannel.Say, "Audience", "World", "First", DateTimeOffset.UtcNow, f.Show.Id);
            f.Presenter.Room!.ReceiveChat(chat with { SetlistId = Guid.NewGuid() });
            Check(f.Presenter.StatusIsError && f.Captain.State.Requests.Count == requestCount,
                "buffered chat from a previous local queue cannot enter the remote room");
            f.Captain.Room!.ReceiveChat(chat); f.Presenter.Room!.ReceiveChat(chat);
            f.Until(() => f.Captain.State.Requests.Count == requestCount + 1);
            Check(f.Captain.State.Requests.Last().Status == RequestStatus.Arranged && !f.Captain.Room.ReceptionSettings.IsOpen,
                "only presenter receives chat and audience requests automatically enter the captain queue");
            f.Until(() => f.Presenter.QueueState.Requests.Count == f.Captain.State.Requests.Count);
            f.Send(new() { Action = RoomAction.Chat, Chat = new("Another audience", "", "Unknown song", RequestChannel.Say) });
            var unresolved = f.Captain.State.Requests.Last().Id;
            f.Send(new() { Action = RoomAction.Resolve, RequestId = unresolved, SongId = f.Second });
            Check(f.Captain.State.Requests.Last().Status == RequestStatus.Arranged, "presenter resolves an unmatched audience request into captain queue");
            f.Remote(RoomAction.ReceptionOwner, value: false);
            Check(!f.Presenter.Room.ReceptionSettings.IsOpen && f.Captain.Room.ReceptionSettings.IsOpen,
                "both controllers can switch the sole chat receiver");

            f.Captain.Room.Leave();
            f.Until(() => !f.Presenter.Room.Connected);
            Check(!f.Presenter.CanEditQueue && !f.Presenter.QueueCommand(RoomAction.Skip), "disconnected presenter cannot issue playback commands");
            f.Presenter.Room.Leave();
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Presenter.DataDirectory, "catalog.json"))),
                "leaving restores unchanged local catalog");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.Port.DurationTicks = 768;
            f.Remote(RoomAction.Add, song: f.First); f.Remote(RoomAction.Add, song: f.Second);
            f.Remote(RoomAction.PlayNext, entry: f.Show.Entries[1].Id);
            f.Remote(RoomAction.Start);
            f.Until(() => f.Show.Entries[1].Status == EntryStatus.InProgress);
            var activeOrder = f.Show.Entries.Select(e => e.Id).ToArray();
            f.Send(new() { Action = RoomAction.MoveTo, EntryId = activeOrder[1], TargetEntryId = activeOrder[0] }, false);
            f.Send(new() { Action = RoomAction.MoveTo, EntryId = activeOrder[0], TargetEntryId = activeOrder[1] }, false);
            Check(f.Show.Entries.Select(e => e.Id).SequenceEqual(activeOrder), "remote drag cannot move or target the performing entry");
            Check(f.Port.Starts.SequenceEqual(new[] { "Second" }) && f.Presenter.QueuePlayer == null,
                "remote start runs only on captain and honors explicit next song");
            f.Remote(RoomAction.Pause);
            f.Until(() => f.Player.IsPaused && f.Presenter.QueueViewPlayback.Paused);
            f.Remote(RoomAction.Start); f.Until(() => f.Port.IsPlaying);
            Check(f.Player.Enabled && !f.Player.IsPaused, "remote resume applies playback notifications before scheduler tick");
            f.Remote(RoomAction.Skip);
            f.Until(() => f.Port.Starts.Count == 2);
            Check(f.Show.Entries[1].Status == EntryStatus.Skipped && f.Port.Starts[1] == "First", "remote skip advances the captain real MIDI engine once");
            f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            f.Remote(RoomAction.Stop);
            f.Until(() => !f.Port.IsPlaying && !f.Player.IsRunning);
            Check(f.Captain.State.Sessions.Single().Attempts.Count == 2, "remote pause/resume/stop preserve performance attempt history");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect(); f.Port.DelayStart = true;
            f.Remote(RoomAction.PlaybackMode, number: (int)QueuePlaybackMode.Ensemble);
            f.Remote(RoomAction.Add, song: f.First); f.Remote(RoomAction.Start);
            f.Until(() => f.Port.StartCalls == 1);
            Check(f.Show.Entries[0].Status == EntryStatus.Queued, "remote ensemble start waits for the captain readiness flow");
            f.Port.BeginActualPlayback(); f.Until(() => f.Show.Entries[0].Status == EntryStatus.InProgress);
            f.Remote(RoomAction.Pause); f.Until(() => f.Player.IsPaused);
            Check(f.Player.Enabled && !f.Port.IsPlaying, "remote ensemble pause stops music while preserving continuous preference");
            f.Remote(RoomAction.Start); f.Until(() => f.Show.Entries[0].Status == EntryStatus.Completed);
            Check(f.Port.FinishCalls == 1, "remote ensemble resume finishes the same song");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            f.Connect();
            var completed = (Dictionary<Guid, RoomResult>)typeof(StageRoom).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Captain.Room)!;
            for (var i = 0; i < 4096; i++) { var id = Guid.NewGuid(); completed[id] = new(id, true, "ok"); }
            f.Presenter.QueueCommand(RoomAction.Add, song: f.First);
            f.Until(() => f.Captain.StatusMessage.Contains("记录已满"));
            Check(f.Show.Entries.Count == 0, "idempotency capacity is checked before applying any mutation");
        }

        using (var f = new Fixture(scratch, midiPath))
        {
            Check(f.Captain.Room!.Create(0), "captain starts isolated cross-process room");
            var invitationFile = Path.Combine(scratch, "process-room-invite.txt");
            File.WriteAllText(invitationFile, f.Captain.Room.Invite("127.0.0.1", f.Captain.Room.LocalPort));
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--room-presenter-check"); start.ArgumentList.Add(invitationFile);
                using var child = Process.Start(start)!;
                f.Until(() => child.HasExited, 15);
                Check(child.ExitCode == 0 && f.Show.Entries.Count == 1, "separate presenter process authenticates, views catalog and edits captain queue");
            }
            finally { File.Delete(invitationFile); }
        }
    }

    public static void RunPresenterProcess(string invitationFile)
    {
        using var client = new RoomClient(RoomInvite.Decode(File.ReadAllText(invitationFile)));
        Wait(() => client.Connected);
        var snapshot = client.Snapshot!;
        if (snapshot.Catalog.Songs.Any(s => s.FilePath.Length > 0)) throw new InvalidOperationException("private paths leaked");
        var command = new RoomCommand { Action = RoomAction.Add, RoomId = snapshot.RoomId, Revision = snapshot.Revision, SongId = snapshot.Catalog.Songs[0].Id };
        if (!client.Send(command)) throw new InvalidOperationException("send failed");
        RoomResult result = null!; Wait(() => client.TryResult(out result));
        if (!result.Success || client.Snapshot!.Catalog.Setlists[0].Entries.Count != 1) throw new InvalidOperationException("acknowledgement failed");
        client.Dispose(); client.Completion.GetAwaiter().GetResult();
        static void Wait(Func<bool> done)
        { var deadline = DateTime.UtcNow.AddSeconds(10); while (!done()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); Thread.Sleep(5); } }
    }

    internal sealed class Fixture : IDisposable
    {
        public readonly StageController Captain, Presenter;
        public readonly AutoQueueRuntimeChecks.EnginePort Port;
        public readonly AutoQueuePlayer Player;
        public ShowSetlist Show => Captain.QueueShow!;
        public Guid First => Captain.State.Songs[0].Id;
        public Guid Second => Captain.State.Songs[1].Id;
        public Fixture(string scratch, string midiPath)
        {
            Captain = new StageController(Path.Combine(scratch, "captain-" + Guid.NewGuid())) { SyncAvailable = true };
            Presenter = new StageController(Path.Combine(scratch, "presenter-" + Guid.NewGuid()));
            Captain.Import([midiPath]); while (Captain.IsBusy) { Captain.Poll(); Thread.Sleep(5); }
            Captain.Change(s =>
            {
                s.Songs[0].Title = "First"; s.Songs[0].Aliases = [];
                var second = StageController.Clone(s.Songs[0]); second.Id = Guid.NewGuid(); second.Title = "Second"; second.Sha256 = new string('D', 64);
                second.FilePath = Path.Combine(Captain.DataDirectory, "Second.mid"); File.Copy(midiPath, second.FilePath); s.Songs.Add(second);
                s.Setlists[0].GapSeconds = 0;
            });
            Captain.EnsureAutomaticQueue();
            Port = new(Captain); Player = new(Captain, Port); Captain.QueuePlayer = Player;
            Captain.Room = new(Captain); Presenter.Room = new(Presenter);
        }
        public void Connect()
        {
            if (!Captain.Room!.Create(0) || !Presenter.Room!.Join(Captain.Room.Invite("127.0.0.1", Captain.Room.LocalPort))) throw new InvalidOperationException("create/join failed");
            Until(() => Presenter.Room!.Connected);
        }
        public void Remote(RoomAction action, Guid? entry = null, Guid? song = null, bool value = false, int number = 0)
        {
            Until(() => Presenter.Room!.Connected && !Presenter.Room.OperationPending && Presenter.QueueViewShow != null);
            // Let snapshots from automatic playback catch up before emulating a user click.
            Until(() => Presenter.QueueViewPlayback == Captain.LocalPlayback && Presenter.QueueState.Setlists[0].Entries.Count == Show.Entries.Count);
            if (!Presenter.QueueCommand(action, entry: entry, song: song, value: value, number: number)) throw new InvalidOperationException(Presenter.StatusMessage);
            Until(() => !Presenter.Room!.OperationPending);
            if (Presenter.StatusIsError) throw new InvalidOperationException(action + ": " + Presenter.StatusMessage);
        }
        public void Send(RoomCommand command, bool success = true)
        {
            if (!Presenter.Room!.Send(command)) throw new InvalidOperationException("send failed");
            if (command.Action == RoomAction.Chat)
            {
                var pending = (Dictionary<Guid, (RoomCommand Command, DateTimeOffset Sent)>)typeof(StageRoom)
                    .GetField("pendingChat", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Presenter.Room)!;
                Until(() => !pending.ContainsKey(command.Id));
            }
            else Until(() => !Presenter.Room.OperationPending);
            if (Presenter.StatusIsError == success) throw new InvalidOperationException(Presenter.StatusMessage);
        }
        public void Pump() { Captain.Poll(); Captain.Room!.Tick(); Player.Tick(); Presenter.Poll(); Presenter.Room!.Tick(); }
        public void Until(Func<bool> done, int seconds = 8)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!done()) { Pump(); if (DateTime.UtcNow > end) throw new TimeoutException(Captain.StatusMessage + " / " + Presenter.StatusMessage + " / " + Player.Status); Thread.Sleep(5); }
        }
        public void Dispose()
        {
            var transports = new[] { Captain.Room?.TransportCompletion, Presenter.Room?.TransportCompletion }.OfType<Task>().ToArray();
            Player.Dispose(); Port.Dispose(); Presenter.Dispose(); Captain.Dispose();
            Task.WhenAll(transports).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
