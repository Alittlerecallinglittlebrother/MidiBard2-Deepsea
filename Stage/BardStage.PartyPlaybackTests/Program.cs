using System.Reflection;
using System.Text;
using BardStage.Core;
using BardStage.Core.Rooms;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Text.ReadOnly;
using MidiBard;
using MidiBard.Managers;
using MidiBard.StageIntegration;
using MidiBard.Util;
using Plugin = MidiBard.MidiBard;

var scratch = Path.Combine(Path.GetTempPath(), "midibard-party-load-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var song = Path.Combine(scratch, "song.mid");
var other = Path.Combine(scratch, "other.mid");
File.WriteAllBytes(song, [1, 2, 3]); File.WriteAllBytes(other, [4, 5, 6]);
api.PartyList.AddRange([new Member(1, "Leader", 1), new Member(2, "Member", 1)]);
PlaylistManager.FilePathList.AddRange([new(other), new(song)]);

var port = new MidiBardQueuePort();
var load = port.LoadAsync(song, QueuePlaybackMode.Ensemble, CancellationToken.None);
Check(PlaylistManager.Loads.SequenceEqual([1]), "leader loads immediately without a chat echo");
Check(!load.IsCompleted, "ensemble waits for the member receipt");
var selection = Chat.Sent.Last(s => s.StartsWith("/p switchto "))[3..];
var id = Guid.ParseExact(selection.Split(' ').Single(s => s.StartsWith("load="))[5..], "N");
Receive("Leader", selection, handled: true);
Check(PlaylistManager.Loads.Count == 1, "local echo, including an already handled message, does not reload");
Receive("Outsider", $"mbloadresult {id:N} ok");
Receive("Member", $"mbloadresult {Guid.NewGuid():N} ok");
Check(!load.IsCompleted, "unknown sender and stale receipt cannot finish loading");
Receive("Member", $"mbloadresult {id:N} ok", handled: true);
await load.WaitAsync(TimeSpan.FromSeconds(3));
Check(port.OwnsPlayback(Plugin.CurrentPlayback!), "the real queue port owns the completed selection");
port.Start(QueuePlaybackMode.Ensemble);
api.PartyList.Leader = 2; port.Tick(); api.PartyList.Leader = 1;
typeof(MidiBardQueuePort).GetField("readyAfter", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(port, DateTimeOffset.MinValue);
port.Tick();
Check(EnsembleManager.ReadyCount == 0, "leader A to B to A cannot restart the old pending ready check");
api.PartyList.Leader = 2;
try { port.Pause(); throw new Exception("Former leader retained playback control"); }
catch (InvalidOperationException) { Check(true, "former leader cannot pause the existing selection"); }
api.PartyList.Leader = 1;
port.Pause();
Check(true, "returning leader can explicitly control the still-loaded selection");

var adoptedPort = new MidiBardQueuePort();
var playingBeforeTransfer = Plugin.CurrentPlayback;
var loadsBeforeTransfer = PlaylistManager.Loads.Count;
api.PartyList.Leader = 2; api.Player.ContentId = 2;
Check(!adoptedPort.AdoptEnsemblePlayback(new string('A', 64)), "handoff rejects a different MIDI without replacing local playback");
Check(adoptedPort.AdoptEnsemblePlayback(PartySongIdentity.Hash(song))
    && ReferenceEquals(playingBeforeTransfer, Plugin.CurrentPlayback) && PlaylistManager.Loads.Count == loadsBeforeTransfer,
    "new party leader adopts the same playback instance without loading or restarting it");
adoptedPort.Pause(); adoptedPort.Resume();
Check(adoptedPort.OwnsPlayback(Plugin.CurrentPlayback!) && EnsembleTransport.Sent.TakeLast(2).SequenceEqual(["pause", "resume"]),
    "new leader can pause and resume the adopted ensemble and suppress native autoplay");
api.PartyList.Leader = 1;
Check(!adoptedPort.AdoptEnsemblePlayback(PartySongIdentity.Hash(song)), "ordinary members cannot adopt playback control");

var proofs = new List<(Guid Room, string Challenge, ulong Sender, long Party)>();
PartyChatCommand.StageProof += (room, challenge, sender, party) => proofs.Add((room, challenge, sender, party));
var proofRoom = Guid.NewGuid(); var proofChallenge = new string('B', 32);
Receive("Member", $"mbstageproof {proofRoom:N} {proofChallenge}", handled: true);
Check(proofs.Count == 1 && proofs[0] == (proofRoom, proofChallenge, 2UL, 1L), "party proof identifies a real member independently of party leadership");
Receive("Outsider", $"mbstageproof {proofRoom:N} {proofChallenge}");
Receive("Member", $"mbstageproof {proofRoom:N} invalid");
Receive("Member", $"mbstageproof {Guid.Empty:N} {proofChallenge}");
Check(proofs.Count == 1, "unresolved senders and malformed room proofs are ignored");
Chat.DeferMessages = true; Chat.Sent.Clear();
PartyChatCommand.SendStageProof(proofRoom, proofChallenge);
api.PartyList.PartyId = 2; Chat.Flush(); api.PartyList.PartyId = 1;
Check(Chat.Sent.Count == 0, "deferred proof cannot leak into a different party");
Chat.DeferMessages = false;

api.Player.ContentId = 2;
PlaylistManager.Loads.Clear(); Chat.Sent.Clear();
var hash = PartySongIdentity.Hash(song);
var remoteId = Guid.NewGuid();
Receive("Leader", $"switchto 1 load={remoteId:N} song={hash} auto=1,2");
Check(PlaylistManager.Loads.SequenceEqual([1]) && Plugin.CurrentPlayback!.FilePath == song,
    "different playlist order resolves by MIDI content instead of the wrong index");
Check(Chat.Sent.Any(s => s == $"/p mbloadresult {remoteId:N} ok"), "member confirms its actual local load");
Receive("Leader", $"switchto 1 load={remoteId:N} song={hash} auto=1,2");
Check(PlaylistManager.Loads.Count == 1, "duplicate remote selection is idempotent");
Receive("Member", $"switchto 1 load={Guid.NewGuid():N} song={hash} auto=1,2");
Check(PlaylistManager.Loads.Count == 1, "a non-leader cannot select an ensemble song");
var missingId = Guid.NewGuid();
Receive("Leader", $"switchto 1 load={missingId:N} song={new string('A', 64)} auto=1,2");
Check(Chat.Sent.Contains($"/p mbloadresult {missingId:N} missing") && PlaylistManager.Loads.Count == 1,
    "missing MIDI reports a failure and does not load a different song");

PlaylistManager.Barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
var followerId = Guid.NewGuid();
Receive("Leader", $"switchto 1 load={followerId:N} song={hash} auto=1,2");
var followerCancellation = (CancellationTokenSource)typeof(PartyChatCommand)
    .GetField("activeLoad", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
adoptedPort.Revoke();
Check(!followerCancellation.IsCancellationRequested,
    "late room lease revocation does not cancel a valid current-leader follower load");
PlaylistManager.Barrier.SetResult();
var followerDeadline = DateTime.UtcNow.AddSeconds(3);
while (typeof(PartyChatCommand).GetField("activeLoad", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) != null
    && DateTime.UtcNow < followerDeadline) await Task.Delay(10);
Check(Chat.Sent.Contains($"/p mbloadresult {followerId:N} ok"),
    "follower confirms loading despite a later room authority notification");
PlaylistManager.Barrier = null;

api.Player.ContentId = 1;
var failure = port.LoadAsync(song, QueuePlaybackMode.Ensemble, CancellationToken.None);
var failureSelection = Chat.Sent.Last(s => s.StartsWith("/p switchto "));
var failureId = Guid.ParseExact(failureSelection.Split(' ').Single(s => s.StartsWith("load="))[5..], "N");
Receive("Member", $"mbloadresult {failureId:N} missing");
try { await failure; throw new Exception("Missing member was accepted"); }
catch (InvalidOperationException ex) { Check(ex.Message.Contains("Member") && ex.Message.Contains("MIDI"), "leader reports the specific member and missing MIDI"); }

PlaylistManager.Barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
var previous = Plugin.CurrentPlayback;
var cancelled = port.LoadAsync(other, QueuePlaybackMode.Ensemble, CancellationToken.None);
api.PartyList.Leader = 2; PartyChatCommand.Tick();
try { await cancelled; throw new Exception("Leader transfer was ignored"); }
catch (OperationCanceledException) { Check(ReferenceEquals(previous, Plugin.CurrentPlayback), "leadership loss cancels in-flight loading before publication"); }
PlaylistManager.Barrier = null;
api.PartyList.Leader = 1;

Chat.Sent.Clear(); Chat.DeferMessages = true;
var unsent = port.LoadAsync(song, QueuePlaybackMode.Ensemble, CancellationToken.None);
api.PartyList.Leader = 2; PartyChatCommand.Tick(); Chat.Flush();
try { await unsent; throw new Exception("Unsent request was not cancelled"); }
catch (OperationCanceledException) { Check(Chat.Sent.Count == 0, "queued chat selection is discarded after leadership loss"); }
api.PartyList.Leader = 1; api.Player.ContentId = 2;
Receive("Leader", $"switchto 1 load={Guid.NewGuid():N} song={hash} auto=1,2");
api.PartyList.Leader = 2;
Chat.Flush();
Check(Chat.Sent.Count == 0, "completed member load cannot send a delayed acknowledgement to a changed party leader");
Chat.DeferMessages = false; api.Player.ContentId = 1; api.PartyList.Leader = 1;

var concurrent = new PartySongLoad(Enumerable.Range(1, 8).Select(i => ((ulong)i, "Player" + i)));
await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() =>
{
    for (var n = 0; n < 100; n++) _ = concurrent.WaitingFor;
    concurrent.Receive(concurrent.Id, (ulong)i, "ok");
})));
Check(await concurrent.Completion && concurrent.WaitingFor.Length == 0, "concurrent receipts and timeout diagnostics safely share member state");

var noReceipt = port.LoadAsync(song, QueuePlaybackMode.Ensemble, CancellationToken.None);
try { await noReceipt.WaitAsync(TimeSpan.FromSeconds(35)); throw new Exception("Missing receipt was accepted"); }
catch (InvalidOperationException ex)
{ Check(ex.Message.Contains("Member") && ex.Message.Contains("相同版本"), "real receipt deadline names the missing member and setup"); }

Plugin.config.EnableCrossComputerSongSync = true;
api.Player.ContentId = 2;
var personalLibrary = PlaylistManager.FilePathList.ToArray();
PlaylistManager.FilePathList.Clear();
var cachePath = Path.Combine(scratch, "cached.mid");
File.Copy(song, cachePath);
var downloadGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
PartyChatCommand.SongTransferRequest = (_, _, token) => downloadGate.Task.WaitAsync(token);
PartyChatCommand.ExternalSongLoader = PlaylistManager.LoadExternalPlayback;
var emptyLibraryRequest = Guid.NewGuid();
Receive("Leader", $"switchto 99 load={emptyLibraryRequest:N} song={hash} auto=1,2");
Check(!Chat.Sent.Any(s => s == $"/p mbloadresult {emptyLibraryRequest:N} ok"), "download must finish before a ready acknowledgement");
downloadGate.SetResult(cachePath);
await Until(() => Chat.Sent.Contains($"/p mbloadresult {emptyLibraryRequest:N} ok"));
Check(PlaylistManager.FilePathList.Count == 0 && Plugin.CurrentPlayback!.FilePath == cachePath,
    "empty-library follower loads cache without importing or reordering personal songs");

downloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
var staleDownload = Guid.NewGuid();
var beforeCancelled = Plugin.CurrentPlayback;
Receive("Leader", $"switchto 1 load={staleDownload:N} song={hash} auto=1,2");
Receive("Leader", $"mbloadcancel {staleDownload:N}");
downloadGate.SetResult(cachePath);
await Task.Delay(50);
Check(ReferenceEquals(beforeCancelled, Plugin.CurrentPlayback) && !Chat.Sent.Contains($"/p mbloadresult {staleDownload:N} ok"),
    "leader cancellation prevents a late download from loading or acknowledging");

api.Player.ContentId = 1;
PartyChatCommand.TransferredSongResolver = h => h == hash ? cachePath : null;
var cacheLeader = new MidiBardQueuePort();
var cacheLoad = cacheLeader.LoadAsync(cachePath, QueuePlaybackMode.Ensemble, CancellationToken.None);
Check(!cacheLoad.IsCompleted && PartyChatCommand.EnsembleLoadIssue != null, "cache-only new leader still waits for the whole party");
var cacheId = Guid.ParseExact(Chat.Sent.Last(s => s.StartsWith("/p switchto ")).Split(' ').Single(s => s.StartsWith("load="))[5..], "N");
Receive("Member", $"mbloadresult {cacheId:N} ok");
await cacheLoad;
Check(PartyChatCommand.EnsembleLoadIssue == null && PlaylistManager.FilePathList.Count == 0,
    "cache-only leader becomes ready only after actual member receipt");
api.PartyList.Add(new Member(3, "NewMember", 1));
try { cacheLeader.Start(QueuePlaybackMode.Ensemble); throw new Exception("unloaded new member accepted"); }
catch (InvalidOperationException) { Check(true, "joining member invalidates readiness before starting ensemble"); }
api.PartyList.RemoveAt(api.PartyList.Count - 1);
var cacheFail = cacheLeader.LoadAsync(cachePath, QueuePlaybackMode.Ensemble, CancellationToken.None);
cacheId = Guid.ParseExact(Chat.Sent.Last(s => s.StartsWith("/p switchto ")).Split(' ').Single(s => s.StartsWith("load="))[5..], "N");
Receive("Member", $"mbloadresult {cacheId:N} failed");
try { await cacheFail; throw new Exception("failed load accepted"); }
catch (InvalidOperationException) { Check(PartyChatCommand.EnsembleLoadIssue != null, "failed member blocks manual ensemble start too"); }
PlaylistManager.FilePathList.AddRange(personalLibrary);
Plugin.config.EnableCrossComputerSongSync = false;
PartyChatCommand.SongTransferRequest = null; PartyChatCommand.ExternalSongLoader = null; PartyChatCommand.TransferredSongResolver = null;

await port.LoadAsync(other, QueuePlaybackMode.Solo, CancellationToken.None);
port.Start(QueuePlaybackMode.Solo);
Check(Plugin.IsPlaying, "solo playback still loads and starts without party receipts");

Plugin.IsPlaying = false;
Plugin.config.EnableCrossComputerSongSync = true;
Plugin.config.AutoAssignEnsembleTracks = false;
Chat.Sent.Clear();
await PartyChatCommand.PrepareManualSongAsync(1, CancellationToken.None);
Check(Chat.Sent.Count == 0 && PartyChatCommand.EnsembleLoadIssue != null
    && Plugin.CurrentPlayback!.MidiFileConfig.Tracks.Count == 2, "manual selection opens a local editable draft without distributing or becoming ready");
var draft = Plugin.CurrentPlayback!.MidiFileConfig;
draft.Tracks[0].AssignedCids = [2]; draft.Tracks[0].Instrument = 20; draft.Tracks[0].Transpose = 12;
draft.Tracks[1].AssignedCids = [1]; draft.Tracks[1].Instrument = 2; draft.Tracks[1].Transpose = -12;
RoomSongPlan? publishedPlan = null;
var publishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
PartyChatCommand.ManualPlanPublisher = (plan, token) => { publishedPlan = plan; return publishGate.Task.WaitAsync(token); };
var manualJob = PartyChatCommand.DistributeCurrentManualAsync(CancellationToken.None);
Check(Chat.Sent.Count == 0 && !manualJob.IsCompleted, "manual selection is announced only after the room accepts the assignment snapshot");
publishGate.SetResult();
await Until(() => Chat.Sent.Any(s => s.StartsWith("/p mbmanual ")));
var manualSelection = Chat.Sent.Last(s => s.StartsWith("/p mbmanual "))[3..];
var manualId = Guid.ParseExact(manualSelection.Split(' ').Single(s => s.StartsWith("load="))[5..], "N");
Check(!manualSelection.Contains("auto=") && manualId == publishedPlan!.Id,
    "manual distribution uses a distinct selection command and does not require leader UI order");
await Until(() => Plugin.CurrentPlayback!.MidiFileConfig.LeaderDistributed);
Check(Plugin.CurrentPlayback!.MidiFileConfig.Tracks[0].AssignedCids.SequenceEqual([2UL])
    && Plugin.CurrentPlayback.MidiFileConfig.Tracks[0].Instrument == 20 && Plugin.CurrentPlayback.MidiFileConfig.Tracks[1].Transpose == -12,
    "leader uses the same published performer, instrument and transpose snapshot");
Receive("Member", $"mbloadresult {manualId:N} ok");
await manualJob;
Check(PartyChatCommand.EnsembleLoadIssue == null, "manual distribution becomes ready after real member receipt");
Plugin.CurrentPlayback!.MidiFileConfig.Tracks[0].Transpose = 24;
Check(PartyChatCommand.EnsembleLoadIssue != null, "configuration mutation invalidates the ready snapshot even outside the editor");
Plugin.CurrentPlayback.MidiFileConfig.Tracks[0].Transpose = 12;
PartyChatCommand.InvalidateAssignment();
Check(PartyChatCommand.EnsembleLoadIssue != null, "editing a ready manual assignment requires another distribution");

api.Player.ContentId = 2;
Plugin.config.AutoAssignEnsembleTracks = true;
PartyChatCommand.ManualPlanReceiver = (_, _, _) => Task.FromResult(publishedPlan!);
var newPlan = publishedPlan! with { Id = Guid.NewGuid() };
PartyChatCommand.ManualPlanReceiver = (_, _, _) => Task.FromResult(newPlan);
PartyChatCommand.SongTransferRequest = (_, _, _) => Task.FromResult<string?>(cachePath);
PartyChatCommand.ExternalSongLoader = PlaylistManager.LoadExternalPlayback;
PlaylistManager.FilePathList.Clear();
Receive("Leader", $"mbmanual 90 load={newPlan.Id:N} song={hash}");
await Until(() => Chat.Sent.Contains($"/p mbloadresult {newPlan.Id:N} ok"));
Check(Plugin.config.AutoAssignEnsembleTracks && Plugin.CurrentPlayback!.MidiFileConfig.LeaderDistributed
    && Plugin.CurrentPlayback.MidiFileConfig.Tracks[0].Instrument == 20 && PlaylistManager.FilePathList.Count == 0,
    "empty-library follower uses leader manual plan even with local automatic assignment enabled");
var accepted = Plugin.CurrentPlayback;
Receive("Outsider", $"mbmanual 90 load={Guid.NewGuid():N} song={hash}");
Check(ReferenceEquals(accepted, Plugin.CurrentPlayback), "outsider cannot request a manual selection");

var wrongPlanId = Guid.NewGuid();
Receive("Leader", $"mbmanual 1 load={wrongPlanId:N} song={hash}");
await Until(() => Chat.Sent.Contains($"/p mbloadresult {wrongPlanId:N} failed"));
Check(ReferenceEquals(accepted, Plugin.CurrentPlayback), "mismatched assignment revision is rejected without replacing playback");

var cancelledPlanId = Guid.NewGuid();
var planGate = new TaskCompletionSource<RoomSongPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
PartyChatCommand.ManualPlanReceiver = (_, _, token) => planGate.Task.WaitAsync(token);
Receive("Leader", $"mbmanual 1 load={cancelledPlanId:N} song={hash}");
Receive("Leader", $"mbloadcancel {cancelledPlanId:N}");
planGate.SetResult(newPlan with { Id = cancelledPlanId });
await Until(() => !PartyChatCommand.IsLoading);
Check(ReferenceEquals(accepted, Plugin.CurrentPlayback) && !Chat.Sent.Contains($"/p mbloadresult {cancelledPlanId:N} ok"),
    "cancelled manual plan cannot load after a late reply");

var staleRoster = newPlan with { Id = Guid.NewGuid(), Members = [1, 3] };
PartyChatCommand.ManualPlanReceiver = (_, _, _) => Task.FromResult(staleRoster);
Receive("Leader", $"mbmanual 1 load={staleRoster.Id:N} song={hash}");
await Until(() => Chat.Sent.Contains($"/p mbloadresult {staleRoster.Id:N} failed"));
Check(ReferenceEquals(accepted, Plugin.CurrentPlayback), "manual plan for an old roster is rejected");

api.Player.ContentId = 1;
Plugin.config.AutoAssignEnsembleTracks = false;
PartyChatCommand.ManualPlanPublisher = (plan, _) => { publishedPlan = plan; return Task.CompletedTask; };
PartyChatCommand.TransferredSongResolver = h => h == hash ? cachePath : null;
var repeatManual = new MidiBardQueuePort().LoadAsync(cachePath, QueuePlaybackMode.Ensemble, CancellationToken.None);
await Until(() => publishedPlan!.Id != manualId);
Receive("Member", $"mbloadresult {publishedPlan!.Id:N} ok");
await repeatManual;
Check(PartyChatCommand.EnsembleLoadIssue == null && PlaylistManager.FilePathList.Count == 0,
    "manual queue selection can redistribute an existing cache-only configuration");

static void Receive(string sender, string text, bool handled = false)
{
    var message = DispatchProxy.Create<IHandleableChatMessage, MessageProxy>();
    var values = ((MessageProxy)(object)message).Values;
    values["LogKind"] = XivChatType.Party;
    values["IsHandled"] = handled;
    values["OriginalSender"] = new ReadOnlySeString(Encoding.UTF8.GetBytes(sender));
    values["OriginalMessage"] = new ReadOnlySeString(Encoding.UTF8.GetBytes(text));
    values["Message"] = new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(text));
    PartyChatCommand.OnChatMessage(message);
}

static void Check(bool valid, string message)
{
    if (!valid) throw new InvalidOperationException(message);
    Console.WriteLine("PASS: " + message);
}

static async Task Until(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(4);
    while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("party load check"); await Task.Delay(10); }
}
