using BardStage.Core;

namespace BardStage;

public interface IStagePlaybackPort
{
    bool IsPlaying { get; }
    string? BlockReason(QueuePlaybackMode mode);
    Task LoadAsync(string filePath, QueuePlaybackMode mode, CancellationToken cancellationToken);
    void Start(QueuePlaybackMode mode);
    void Pause();
    void Resume();
    void Finish(QueuePlaybackMode mode);
    void Stop(QueuePlaybackMode mode, bool keepInstruments = false);
}

public sealed class AutoQueuePlayer(StageController controller, IStagePlaybackPort port, Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private Task? loadTask;
    private (Guid Show, Guid Entry, bool CancelReady, Task Task)? pendingSkip;
    private CancellationTokenSource? cancellation;
    private Guid? showId, entryId;
    private QueuePlaybackMode mode;
    private DateTimeOffset deadline, notBefore;
    private bool waitingForStart, sawStart, disposed, running, controlRevoked, handoffSuspended, handoffCancelledLoad;
    public bool Enabled { get; private set; } = true;
    public bool IsRunning => running;
    public bool IsLoading => loadTask != null;
    public bool IsEnginePlaying => port.IsPlaying;
    public bool IsPaused => Entry?.PausedAtUtc.HasValue == true;
    public Guid? ActiveEntryId => entryId;
    public string Status { get; private set; } = "自动演奏未开始";
    public bool StatusIsError { get; private set; }
    private SetlistEntry? Entry => controller.State.Setlists.FirstOrDefault(s => s.Id == showId)?.Entries.FirstOrDefault(e => e.Id == entryId);

    public void Start()
    {
        if (disposed || controller.IsBusy || controller.IsReadOnly || loadTask != null || controller.Room?.IsRemote == true) return;
        if (!CheckControl()) return;
        controlRevoked = false;
        StatusIsError = false;
        controller.Poll();
        if (IsPaused)
        {
            try { port.Resume(); running = true; Status = "正在继续演奏"; }
            catch (Exception ex) { Fail(ex.Message); }
            return;
        }
        if (Entry?.Status == EntryStatus.InProgress && (mode == QueuePlaybackMode.Ensemble || port.IsPlaying))
        { running = true; Status = "正在演奏"; return; }
        if (!controller.EnsureAutomaticQueue()) return;
        var show = controller.QueueShow!;
        if (controller.State.Setlists.Any(s => StageOperations.Current(s) != null) || port.IsPlaying)
        { Status = "有未结束的演奏，请先停止或重新排队"; return; }
        showId = show.Id; mode = controller.State.RequestSettings.PlaybackMode;
        controller.ConfigureSync(show.Id, true);
        running = true; entryId = null; sawStart = waitingForStart = false; notBefore = now(); Status = "等待点歌";
    }

    public void SetContinuous(bool enabled)
    {
        if (disposed || !CheckControl()) return;
        Enabled = enabled;
        StatusIsError = false;
        if (!enabled && entryId == null && loadTask == null) running = false;
        Status = enabled ? "自动连播已开启" : "自动连播已关闭";
    }

    public void Pause()
    {
        if (disposed || !CheckControl()) return;
        StatusIsError = false;
        if (waitingForStart || loadTask != null) { Stop(); return; }
        try { if (Entry?.Status == EntryStatus.InProgress) port.Pause(); }
        catch (Exception ex) { Fail(ex.Message); return; }
        Status = "演奏已暂停";
    }

    public void Stop()
    {
        if (disposed || !CheckControl()) return;
        StatusIsError = false;
        running = false; cancellation?.Cancel();
        try { if (entryId.HasValue) port.Stop(mode, true); }
        catch (Exception ex) { Fail(ex.Message); return; }
        waitingForStart = false; Status = "演奏已停止";
    }

    public void Skip()
    {
        if (disposed || controller.IsBusy || controller.SyncBlocked || pendingSkip != null || !CheckControl()) return;
        StatusIsError = false;
        var show = controller.State.Setlists.FirstOrDefault(s => s.Id == showId) ?? controller.QueueShow;
        var entry = Entry ?? (show == null ? null : StageOperations.Next(show));
        if (show == null || entry == null) return;
        cancellation?.Cancel();
        var cancelReady = waitingForStart && mode == QueuePlaybackMode.Ensemble;
        try { if (entryId.HasValue) port.Stop(mode, !cancelReady); }
        catch (Exception ex) { Fail(ex.Message); return; }
        if (entryId.HasValue && port is RoomPlaybackCoordinator { LastTransportTask: { } task })
        {
            pendingSkip = (show.Id, entry.Id, cancelReady, task);
            Status = "等待队长端确认跳过";
            return;
        }
        CompleteSkip(show.Id, entry.Id, cancelReady);
    }

    private void CompleteSkip(Guid targetShow, Guid targetEntry, bool cancelReady)
    {
        var show = controller.State.Setlists.FirstOrDefault(s => s.Id == targetShow);
        var entry = show?.Entries.FirstOrDefault(e => e.Id == targetEntry);
        if (show == null || entry == null) { Fail("待跳过曲目已改变"); return; }
        if (entry.Status is EntryStatus.Queued or EntryStatus.InProgress
            && !controller.Change(s => SetlistOperations.Skip(s.Setlists.Single(x => x.Id == show.Id), entry.Id, now()), "已跳过")) return;
        if (cancelReady || !Enabled) running = false;
        entryId = null; waitingForStart = false; sawStart = false; notBefore = now().AddSeconds(show.GapSeconds);
        Status = cancelReady ? "合奏准备已取消，等待播放" : running ? "已跳过，等待下一首" : "已跳过";
    }

    public void Tick()
    {
        if (disposed) return;
        if (controller.Room?.Coordinator?.IsCoordinated == true && controller.Room.Coordinator.ExecutionIssue is { } handoffReason)
        { SuspendForHandoff(handoffReason); return; }
        if (handoffSuspended) { handoffSuspended = false; StatusIsError = false; }
        CheckControl();
        if (controller.SyncBlocked || controller.ChatWriteBlocked) { Status = "记录保存失败，等待重试"; return; }
        if (pendingSkip is { } skip)
        {
            if (!skip.Task.IsCompleted) return;
            pendingSkip = null;
            try { skip.Task.GetAwaiter().GetResult(); }
            catch (Exception ex) { Fail("跳过未完成：" + ex.Message); return; }
            CompleteSkip(skip.Show, skip.Entry, skip.CancelReady);
        }
        if (loadTask != null)
        {
            if (!loadTask.IsCompleted) return;
            var completed = loadTask; loadTask = null;
            var cancelled = cancellation?.IsCancellationRequested == true;
            cancellation?.Dispose(); cancellation = null;
            try { completed.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { if (!controlRevoked && !handoffCancelledLoad) StopLoaded(); handoffCancelledLoad = false; return; }
            catch (Exception ex) { if (!controlRevoked && !handoffCancelledLoad) { Fail("载入失败：" + ex.Message); entryId = null; } handoffCancelledLoad = false; return; }
            if (controlRevoked) return;
            if (handoffCancelledLoad) { handoffCancelledLoad = false; return; }
            if (cancelled || !running) { StopLoaded(); return; }
            var loadedShow = controller.QueueShow;
            if (Entry?.Status != EntryStatus.Queued || loadedShow?.Id != showId || StageOperations.Next(loadedShow!)?.Id != entryId)
            { StopLoaded(); Status = "队列已改变，重新选择下一首"; return; }
            try
            {
                if (port.BlockReason(mode) is { } reason) { Fail(reason); entryId = null; return; }
                waitingForStart = true; deadline = now().AddSeconds(mode == QueuePlaybackMode.Ensemble ? 90 : 15);
                port.Start(mode); Status = mode == QueuePlaybackMode.Ensemble ? "等待合奏准备与倒计时" : "正在开始演奏";
            }
            catch (Exception ex) { Fail(ex.Message); }
            return;
        }
        if (controlRevoked) return;
        if (entryId.HasValue)
        {
            var entry = Entry;
            if (entry?.Status == EntryStatus.InProgress)
            {
                sawStart = true; waitingForStart = false;
                Status = entry.PausedAtUtc.HasValue ? "演奏已暂停" : "正在演奏：" + entry.Title;
                return;
            }
            if (entry?.Status is EntryStatus.Completed or EntryStatus.Skipped)
            {
                if (entry.Status == EntryStatus.Completed)
                    try { port.Finish(mode); } catch (Exception ex) { Fail(ex.Message); entryId = null; return; }
                if (entry.Status == EntryStatus.Skipped) { running = false; Status = "演奏已停止，等待播放"; }
                if (!Enabled) running = false;
                notBefore = now().AddSeconds(controller.QueueShow?.GapSeconds ?? 3);
                entryId = null; sawStart = waitingForStart = false;
            }
            else if (waitingForStart)
            {
                if (now() > deadline) { Stop(); Fail("未收到演奏开始，请检查乐器或合奏准备后重试"); entryId = null; }
                return;
            }
            else if (sawStart) { Fail("当前曲目被手动修改，连播已暂停"); entryId = null; return; }
            else entryId = null;
        }
        if (!running || controller.IsBusy) return;
        if (!controller.SyncEnabled || controller.SyncShowId != showId) { Fail("状态同步已关闭，请重新开始连播"); return; }
        var show = controller.State.Setlists.FirstOrDefault(s => s.Id == showId);
        if (show == null || controller.QueueShow?.Id != showId) { Fail("点歌队列已切换，请重新开始连播"); return; }
        if (now() < notBefore) { Status = $"下一首倒计时 {Math.Ceiling((notBefore - now()).TotalSeconds)} 秒"; return; }
        if (controller.State.Setlists.Any(s => StageOperations.Current(s) != null) || port.IsPlaying)
        { Status = "等待当前演奏结束"; return; }
        var next = StageOperations.Next(show);
        if (next == null) { Status = "等待观众点歌"; return; }
        if (next.Kind != EntryKind.Song)
        {
            if (!controller.Change(s => SetlistOperations.Skip(s.Setlists.Single(x => x.Id == show.Id), next.Id, now()), "自动队列已略过非歌曲环节")) Fail(controller.StatusMessage);
            return;
        }
        if (port.BlockReason(mode) is { } blocked) { Status = blocked; return; }
        var song = controller.State.Songs.FirstOrDefault(s => s.Id == next.SongId);
        if (song == null || !File.Exists(song.FilePath)) { Fail("MIDI 文件不存在：" + next.Title); return; }
        entryId = next.Id; cancellation = new CancellationTokenSource();
        try { loadTask = port.LoadAsync(song.FilePath, mode, cancellation.Token); Status = "正在载入：" + next.Title; }
        catch (Exception ex) { Fail(ex.Message); entryId = null; cancellation.Dispose(); cancellation = null; }
    }

    public void SuspendForHandoff(string reason)
    {
        if (disposed) return;
        if (!handoffSuspended)
        {
            handoffSuspended = true;
            if (loadTask != null) { cancellation?.Cancel(); handoffCancelledLoad = true; }
            pendingSkip = null;
            waitingForStart = false;
            if (Entry?.Status != EntryStatus.InProgress) entryId = null;
        }
        Status = reason;
    }

    public void TransportFault(string reason) => Fail(reason);

    public bool RevokeControl(string reason)
    {
        if (disposed) return false;
        var scheduled = running || waitingForStart || entryId.HasValue || loadTask != null;
        if (!controlRevoked)
        {
            running = waitingForStart = sawStart = false;
            cancellation?.Cancel();
            if (Entry?.Status != EntryStatus.InProgress) entryId = showId = null;
            controlRevoked = true;
        }
        // Losing party authority cancels our scheduler without stopping the new leader's playback.
        StatusIsError = true; Status = reason;
        return scheduled;
    }

    private bool CheckControl()
    {
        if (controller.Room?.Coordinator?.IsCoordinated == true)
        {
            if (controller.Room.Coordinator.ExecutionIssue is { } issue) { SuspendForHandoff(issue); return false; }
            return true;
        }
        if (controller.LocalQueueControlIssue is { } reason)
        {
            RevokeControl(reason);
            return false;
        }
        if (controlRevoked && StatusIsError)
        {
            StatusIsError = false;
            Status = "主控权限已恢复，等待播放";
        }
        if (controlRevoked && loadTask == null)
        {
            if (Entry?.Status != EntryStatus.InProgress) entryId = showId = null;
            controlRevoked = false;
        }
        return true;
    }

    private void Fail(string message) { running = false; waitingForStart = false; StatusIsError = true; Status = message; }
    private void StopLoaded()
    {
        try { port.Stop(mode, Enabled); } catch (Exception ex) { Fail(ex.Message); }
        entryId = null; waitingForStart = sawStart = false;
    }
    public void Dispose()
    {
        disposed = true; Enabled = false; cancellation?.Cancel();
        if (loadTask != null) _ = loadTask.ContinueWith(t => { _ = t.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
        else cancellation?.Dispose();
    }
}
