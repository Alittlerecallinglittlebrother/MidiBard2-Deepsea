using System.Diagnostics;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;

internal static class ViewerRuntimeChecks
{
    public static void Run(string scratch, string midiPath)
    {
        using var f = new RoomRuntimeChecks.Fixture(scratch, midiPath);
        f.Connect(); f.Remote(RoomAction.Add, song: f.First); f.Remote(RoomAction.Add, song: f.Second);
        using var viewer = new StageController(Path.Combine(scratch, "viewer-" + Guid.NewGuid()));
        using var secondViewer = new StageController(Path.Combine(scratch, "viewer-" + Guid.NewGuid()));
        var follower = new FollowerPort();
        using var player = new AutoQueuePlayer(viewer, follower); viewer.QueuePlayer = player;
        viewer.Room = new(viewer); secondViewer.Room = new(secondViewer);
        var before = File.ReadAllBytes(Path.Combine(viewer.DataDirectory, "catalog.json"));
        var invitation = f.Captain.Room!.Invite("127.0.0.1", f.Captain.Room.LocalPort, RoomRole.Viewer);
        Check(!viewer.Room.Join(invitation, RoomRole.Presenter) && !viewer.Room.IsRemote, "viewer invitation cannot be used through presenter role selection");
        Check(viewer.Room.Join(invitation, RoomRole.Viewer) && secondViewer.Room.Join(invitation), "viewers join while native ensemble playback is already running");
        Until(() => viewer.Room.Connected && secondViewer.Room.Connected);
        viewer.Room.Tick();
        Check(viewer.StatusMessage == "已连接队长，节目单只读同步中" && !viewer.StatusIsError,
            "viewer connected footer replaces the joining message");
        Check(f.Captain.Room.ViewerCount == 2 && f.Captain.Room.HasPresenter && viewer.Room.IsViewer && !viewer.Room.IsPresenter,
            "multiple viewing controllers coexist with the presenter");
        Check(viewer.QueueViewShow!.Entries.Count == 2 && viewer.QueueState.Songs.Count == 0 && viewer.QueueState.Requests.Count == 0,
            "viewers receive only the active program, not the full library or audience history");

        f.Remote(RoomAction.PlayNext, entry: f.Show.Entries[1].Id);
        Until(() => viewer.QueueViewShow!.LockedNextEntryId == f.Show.LockedNextEntryId && secondViewer.QueueViewShow!.LockedNextEntryId == f.Show.LockedNextEntryId);
        Check(StageOperations.RunOrder(viewer.QueueViewShow!)[0].Title == "Second", "viewer run order follows explicit next-song selection");
        f.Remote(RoomAction.Remove, entry: f.Show.Entries[0].Id);
        Until(() => viewer.QueueViewShow!.Entries.Count == 1 && secondViewer.QueueViewShow!.Entries.Count == 1);
        Check(viewer.QueueViewShow!.Entries[0].Title == "Second", "presenter cancellation disappears from every viewer pending list");
        f.Captain.QueueCommand(RoomAction.Add, song: f.First);
        Until(() => viewer.QueueViewShow!.Entries.Count == 2 && secondViewer.QueueViewShow!.Entries.Count == 2);
        Check(viewer.QueueViewShow!.Entries.Last().Title == "First", "captain edits also reach every viewer");

        foreach (var action in Enum.GetValues<RoomAction>())
            Check(!viewer.QueueCommand(action, entry: f.Show.Entries.Last().Id, song: f.First), "viewer rejects local control entry point: " + action);
        viewer.Room.ReceiveChat(new(RequestChannel.Say, "Audience", "", "First", DateTimeOffset.UtcNow, f.Show.Id));
        player.Start(); viewer.Change(s => s.Setlists.Clear()); viewer.Import([midiPath]);
        Check(!viewer.Room.ReceptionSettings.IsOpen && viewer.PendingChatCount == 0 && !viewer.IsBusy && !player.IsRunning
            && follower.IsPlaying && follower.ControlCalls == 0 && before.SequenceEqual(File.ReadAllBytes(Path.Combine(viewer.DataDirectory, "catalog.json"))),
            "viewer cannot receive requests, schedule playback or replace local data; native follower is untouched");

        f.Port.DurationTicks = 768; f.Remote(RoomAction.Start);
        Until(() => viewer.QueueViewPlayback.Playing && viewer.QueueViewShow!.Entries.Any(e => e.Status == EntryStatus.InProgress));
        Check(StageOperations.Current(viewer.QueueViewShow!)?.Title == "Second", "viewer sees the captain current performance and next pending song");
        f.Remote(RoomAction.Pause); Until(() => viewer.QueueViewPlayback.Paused);
        Check(!viewer.QueueViewPlayback.Playing && follower.IsPlaying && follower.ControlCalls == 0, "shared pause status never pauses viewer's native playback");
        f.Remote(RoomAction.Stop); Until(() => !viewer.QueueViewPlayback.Paused && !viewer.QueueViewPlayback.Playing);

        var invitationFile = Path.Combine(scratch, "viewer-process-invite.txt");
        File.WriteAllText(invitationFile, invitation);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--room-viewer-check"); start.ArgumentList.Add(invitationFile);
            using var child = Process.Start(start)!;
            try { Until(() => child.HasExited); }
            finally { if (!child.HasExited) child.Kill(true); }
            Check(child.ExitCode == 0, "separate viewer process receives program while control commands remain blocked");
        }
        finally { File.Delete(invitationFile); }

        var transports = new[] { f.Captain.Room.TransportCompletion, viewer.Room.TransportCompletion, secondViewer.Room.TransportCompletion }.OfType<Task>().ToArray();
        f.Captain.Room.Leave(); Until(() => !viewer.Room.Connected && !secondViewer.Room.Connected);
        viewer.Room.Tick();
        Check(viewer.StatusMessage == "正在重连队长，列表为上次同步结果" && viewer.StatusIsError,
            "viewer disconnection marks the retained program as stale");
        Check(!viewer.CanEditQueue && viewer.QueueViewShow!.Entries.Count > 0, "disconnected viewer retains last program with no control authority");
        viewer.Room.Leave(); secondViewer.Room.Leave();
        Task.WhenAll(transports).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Check(!viewer.Room.IsRemote && before.SequenceEqual(File.ReadAllBytes(Path.Combine(viewer.DataDirectory, "catalog.json")))
            && follower.ControlCalls == 0 && follower.IsPlaying, "leaving viewing mode restores local catalog without stopping native playback");

        void Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                f.Pump(); viewer.Poll(); viewer.Room!.Tick(); secondViewer.Poll(); secondViewer.Room!.Tick();
                if (DateTime.UtcNow > deadline) throw new TimeoutException(viewer.Room.ConnectionStatus + " / " + f.Player.Status);
                Thread.Sleep(5);
            }
        }
    }

    public static void RunViewerProcess(string invitationFile)
    {
        using var viewer = new RoomClient(RoomInvite.Decode(File.ReadAllText(invitationFile)));
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!viewer.Connected) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); Thread.Sleep(5); }
        if (viewer.Role != RoomRole.Viewer || viewer.Snapshot!.Catalog.Setlists.Count != 1 || viewer.Snapshot.Catalog.Songs.Count > 0
            || viewer.Send(new() { Action = RoomAction.Start })) throw new InvalidOperationException("viewer process permissions failed");
        viewer.Dispose(); viewer.Completion.GetAwaiter().GetResult();
    }

    private sealed class FollowerPort : IStagePlaybackPort
    {
        public bool IsPlaying => true;
        public int ControlCalls;
        public string? BlockReason(QueuePlaybackMode mode) => null;
        public Task LoadAsync(string filePath, QueuePlaybackMode mode, CancellationToken cancellationToken) { ControlCalls++; return Task.CompletedTask; }
        public void Start(QueuePlaybackMode mode) => ControlCalls++;
        public void Pause() => ControlCalls++;
        public void Resume() => ControlCalls++;
        public void Finish(QueuePlaybackMode mode) => ControlCalls++;
        public void Stop(QueuePlaybackMode mode, bool keepInstruments = false) => ControlCalls++;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
