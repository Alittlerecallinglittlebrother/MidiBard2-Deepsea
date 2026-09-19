using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BardStage.Core;

namespace BardStage;

public sealed partial class StageController : IDisposable
{
    public const string AuthorWebsite = "https://shenhai.meoo.zone/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private readonly CatalogStore store;
    private readonly AnnouncementStore announcementStore;
    private readonly FileStream? writerLock;
    private readonly CancellationTokenSource lifetime = new();
    private Task<(CatalogState State, ImportReport Report)>? importTask;
    private readonly ConcurrentQueue<IncomingChatRequest> pendingChat = new();
    private RequestSettings receptionSettings = new();
    private bool chatWriteBlocked;
    private bool disposed;

    public StageController(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        store = new CatalogStore(dataDirectory);
        announcementStore = new AnnouncementStore(dataDirectory);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            try { writerLock = new FileStream(Path.Combine(dataDirectory, "catalog.writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                IsReadOnly = true;
                SetStatus("另一个客户端正在管理曲库，本窗口只读。", true);
            }
            State = store.Load();
            if (State.Setlists.Count == 0 && !IsReadOnly)
            {
                var first = new ShowSetlist { Name = "新演出" };
                State.Setlists.Add(first);
                State.SelectedSetlistId = first.Id;
                store.Save(State);
            }
            var interrupted = State.Setlists.SelectMany(s => s.Entries).Count(e => e.Status == EntryStatus.InProgress);
            if (interrupted > 0)
                SetStatus($"已恢复 {interrupted} 个未结束的节目，请核对手动记录。", true);
        }
        catch (Exception ex)
        {
            State = new CatalogState();
            IsReadOnly = true;
            SetStatus($"数据未载入，已停止写入：{ex.Message}", true);
        }
        receptionSettings = Clone(State.RequestSettings);
        if (IsReadOnly) receptionSettings.IsOpen = false;
        try { Announcements = announcementStore.Load(); }
        catch (Exception ex) { AnnouncementError = ex.Message; Announcements = AnnouncementTemplates.Defaults(); }
    }

    public CatalogState State { get; private set; }
    public long QueueRevision { get; private set; }
    internal bool LastChangeStorageError { get; private set; }
    public AnnouncementSettings Announcements { get; private set; }
    public string AnnouncementError { get; private set; } = "";
    public bool IsReadOnly { get; }
    public bool IsBusy => importTask != null;
    public string StatusMessage { get; private set; } = "";
    public bool StatusIsError { get; private set; }
    public string DataDirectory { get; }
    public string ObservedMidiBardTitle { get; private set; } = "";
    public int PendingChatCount => pendingChat.Count;
    public int DroppedChatCount { get; private set; }
    public bool ChatWriteBlocked => chatWriteBlocked;
    public RequestSettings ReceptionSettings => Volatile.Read(ref receptionSettings);
    public ShowSetlist? CurrentSetlist => State.Setlists.FirstOrDefault(s => s.Id == State.SelectedSetlistId);

    public void ObserveMidiBardTitle(string title) => ObservedMidiBardTitle = title;

    public bool SaveAnnouncements(AnnouncementSettings settings)
    {
        if (disposed || IsReadOnly || AnnouncementError.Length > 0) return false;
        try
        {
            var snapshot = Clone(settings);
            announcementStore.Save(snapshot);
            Announcements = snapshot;
            SetStatus("报幕模板已保存");
            return true;
        }
        catch (Exception ex) { SetStatus($"模板未保存：{ex.Message}", true); return false; }
    }

    public bool Change(Action<CatalogState> action, string successMessage = "", RecordSource source = RecordSource.Manual, DateTimeOffset? atUtc = null)
    {
        if (disposed || IsReadOnly || IsBusy || Room?.IsRemote == true) return false;
        LastChangeStorageError = false;
        var previous = Clone(State);
        try
        {
            action(State);
            RequestOperations.Reconcile(State);
            AutoQueueOperations.ArrangePending(State);
            SessionOperations.Capture(previous, State, atUtc ?? DateTimeOffset.UtcNow, source);
            if (State.Setlists.SelectMany(s => s.Entries).Count(e => e.Status == EntryStatus.InProgress) > 1)
                throw new InvalidOperationException("请先结束或中断正在进行的节目，再开始另一场演出。");
            SaveWithPlayerLibrary(State, previous);
            QueueRevision++;
            DetachManualPlayback(previous, source);
            Volatile.Write(ref receptionSettings, Clone(State.RequestSettings));
            SetStatus(string.IsNullOrWhiteSpace(successMessage) ? "已保存" : successMessage);
            return true;
        }
        catch (Exception ex)
        {
            State = previous;
            LastChangeStorageError = ex is IOException or UnauthorizedAccessException;
            SetStatus(ex.Message, true);
            return false;
        }
    }

    public void Import(IEnumerable<string> paths)
    {
        if (disposed || IsReadOnly || IsBusy || Room?.IsRemote == true) return;
        var snapshot = Clone(State);
        var inputs = paths.ToArray();
        importingPlayerLibrary = false;
        importTask = Task.Run(() =>
        {
            var report = new MidiLibraryImporter().Import(inputs, snapshot, lifetime.Token);
            return (snapshot, report);
        }, lifetime.Token);
        SetStatus("正在读取 MIDI 曲库...");
    }

    public void Poll()
    {
        if (disposed) return;
        if (importTask is { IsCompleted: true }) CompleteImport();
        PollPreflight();
        DrainPlayback();
        DrainChatRequests();
    }

    private void CompleteImport()
    {
        var completed = importTask;
        importTask = null;
        try
        {
            var (state, report) = completed!.GetAwaiter().GetResult();
            RequestOperations.Reconcile(state);
            AutoQueueOperations.ArrangePending(state);
            SaveWithPlayerLibrary(state, State, importingPlayerLibrary);
            State = state;
            if (importingPlayerLibrary) CompletePlayerLibrarySynchronization(state);
            QueueRevision++;
            Volatile.Write(ref receptionSettings, Clone(State.RequestSettings));
            var summary = $"导入 {report.Added} 首，跳过重复 {report.Duplicates} 首，修复路径 {report.Relocated} 首";
            if (report.Errors.Count > 0)
            {
                summary += $"；{report.Errors.Count} 个问题";
                if (importingPlayerLibrary) summary += "（已跳过，修复文件后自动重试，或点击刷新共享曲库）";
                summary += "\n" + string.Join("\n", report.Errors.Take(5));
                if (report.Errors.Count > 5) summary += "\n其余问题已写入 import-errors.txt";
                try { File.WriteAllLines(Path.Combine(DataDirectory, "import-errors.txt"), report.Errors); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { summary += "\n问题记录未能写入文件"; }
            }
            SetStatus(summary, report.Errors.Count > 0);
        }
        catch (OperationCanceledException) { SetStatus("已取消导入"); }
        catch (Exception ex)
        {
            var retry = importingPlayerLibrary ? "（自动重试已暂停，请解决问题后点击刷新共享曲库）" : "";
            SetStatus($"导入未保存{retry}：{ex.Message}", true);
        }
        finally { importingPlayerLibrary = false; }
    }

    public void ReceiveChat(IncomingChatRequest request)
    {
        if (disposed || IsReadOnly) return;
        if (pendingChat.Count >= 256)
        {
            DroppedChatCount++;
            SetStatus("点歌接收缓冲已满，部分新请求未接收。", true);
            return;
        }
        pendingChat.Enqueue(request);
    }

    public void RetryPendingChat() => chatWriteBlocked = false;

    private void DrainChatRequests()
    {
        if (IsReadOnly || IsBusy || chatWriteBlocked || pendingChat.Count == 0) return;
        var batch = pendingChat.Take(32).ToArray();
        var added = 0;
        var rejected = 0;
        var lastError = "";
        var proposed = Clone(State);
        foreach (var request in batch)
        {
            try
            {
                RequestOperations.Submit(proposed, request.SetlistId, request.Name, request.World,
                    request.Query, request.Channel, request.ReceivedAtUtc);
                added++;
            }
            catch (InvalidOperationException ex) { rejected++; lastError = ex.Message; }
        }
        if (added > 0 && !Change(state => state.Requests = proposed.Requests))
        {
            chatWriteBlocked = true;
            SetStatus($"点歌尚未保存，已保留 {pendingChat.Count} 条待重试：{StatusMessage}", true);
            return;
        }
        foreach (var _ in batch) pendingChat.TryDequeue(out var ignored);
        SetStatus(rejected == 0 ? $"已接收 {added} 条点歌" : $"已接收 {added} 条；未接收 {rejected} 条：{lastError}", rejected > 0);
    }

    public void SetStatus(string text, bool error = false)
    {
        StatusMessage = text;
        StatusIsError = error;
    }

    public void ExportCurrentSetlist(string filePath)
    {
        if (CurrentSetlist is not { } current || IsBusy) return;
        try
        {
            var ids = current.Entries.Where(e => e.SongId.HasValue).Select(e => e.SongId!.Value).ToHashSet();
            var export = new CatalogState
            {
                SelectedSetlistId = current.Id,
                Setlists = [current],
                Songs = State.Songs.Where(s => ids.Contains(s.Id)).ToList(),
            };
            var text = JsonSerializer.Serialize(export, JsonOptions);
            var path = Path.GetFullPath(filePath);
            if (path.Equals(announcementStore.FilePath, StringComparison.OrdinalIgnoreCase) || path.Equals(announcementStore.FilePath + ".bak", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择独立的节目单导出文件，不能覆盖报幕模板。");
            if (path.Equals(store.FilePath, StringComparison.OrdinalIgnoreCase) || path.Equals(store.FilePath + ".bak", StringComparison.OrdinalIgnoreCase) || path.Equals(Path.Combine(DataDirectory, "catalog.v1.bak"), StringComparison.OrdinalIgnoreCase) || path.Equals(Path.Combine(DataDirectory, "catalog.v2.bak"), StringComparison.OrdinalIgnoreCase) || path.Equals(Path.Combine(DataDirectory, "catalog.writer.lock"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择独立的节目单导出文件。");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            SetStatus("节目单已导出");
        }
        catch (Exception ex) { SetStatus($"导出失败：{ex.Message}", true); }
    }

    public void ImportSetlist(string filePath)
    {
        if (IsBusy || IsReadOnly) return;
        try
        {
            var input = new FileInfo(filePath);
            if (input.Length > 16 * 1024 * 1024) throw new InvalidOperationException("节目单文件超过 16 MB。");
            var incoming = CatalogStore.Deserialize(File.ReadAllText(filePath));
            if (incoming.Setlists.Count == 0) throw new InvalidOperationException("文件中没有节目单。");
            Change(state =>
            {
                var map = new Dictionary<Guid, Guid>();
                foreach (var source in incoming.Songs)
                {
                    var existing = state.Songs.FirstOrDefault(s => s.Sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { map[source.Id] = existing.Id; continue; }
                    var originalId = source.Id;
                    source.Id = Guid.NewGuid();
                    map[originalId] = source.Id;
                    state.Songs.Add(source);
                }
                foreach (var show in incoming.Setlists)
                {
                    show.Id = Guid.NewGuid();
                    show.Name += "（导入）";
                    show.LockedNextEntryId = null;
                    show.TargetEndUtc = null;
                    foreach (var entry in show.Entries)
                    {
                        entry.Id = Guid.NewGuid();
                        if (entry.SongId is { } songId) entry.SongId = map[songId];
                        entry.Status = EntryStatus.Queued;
                        entry.StartedAtUtc = null;
                        entry.EndedAtUtc = null;
                        entry.PausedAtUtc = null;
                        entry.PausedSeconds = 0;
                    }
                    state.Setlists.Add(show);
                    state.SelectedSetlistId = show.Id;
                }
            }, "节目单已导入，演出状态已重置");
        }
        catch (Exception ex) { SetStatus($"导入失败：{ex.Message}", true); }
    }

    public void ContactAuthor()
    {
        try { Process.Start(new ProcessStartInfo(AuthorWebsite) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"无法打开作者网站：{ex.Message}", true); }
    }

    public void OpenPath(string path, bool selectFile = false)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("路径不存在。", path);
            if (selectFile && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = true });
                return;
            }
            if (!Directory.Exists(path)) throw new InvalidOperationException("只允许打开文件所在目录。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    public void Dispose()
    {
        Room?.Dispose();
        disposed = true;
        pendingChat.Clear();
        playbackSignals.Clear();
        preflightCancellation?.Cancel();
        lifetime.Cancel();
        writerLock?.Dispose();
        if (importTask != null)
            _ = importTask.ContinueWith(t => { _ = t.Exception; lifetime.Dispose(); }, TaskScheduler.Default);
        else lifetime.Dispose();
    }
}
