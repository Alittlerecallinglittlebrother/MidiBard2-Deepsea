using System.Reflection;
using System.Text;
using BardStage.Core;
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
{ Check(ex.Message.Contains("Member") && ex.Message.Contains("3.2.5.14"), "real receipt deadline names the missing member and required version"); }

await port.LoadAsync(other, QueuePlaybackMode.Solo, CancellationToken.None);
port.Start(QueuePlaybackMode.Solo);
Check(Plugin.IsPlaying, "solo playback still loads and starts without party receipts");

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
