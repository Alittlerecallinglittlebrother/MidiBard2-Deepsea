using BardStage.Core;

namespace BardStage;

public sealed partial class StageController
{
    public AutoQueuePlayer? QueuePlayer { get; set; }
    public Action? ImportPlayerLibrary { get; set; }
    public ShowSetlist? QueueShow => State.Setlists.FirstOrDefault(s => s.Id == State.RequestSettings.TargetSetlistId);

    public bool EnsureAutomaticQueue()
    {
        if (QueueShow != null && State.RequestSettings.AutoArrange) return true;
        return Change(s =>
        {
            if (!s.Setlists.Any(x => x.Id == s.RequestSettings.TargetSetlistId))
            {
                var show = s.Setlists.FirstOrDefault(x => x.Id == s.SelectedSetlistId);
                if (show == null) { show = new ShowSetlist { Name = "点歌队列" }; s.Setlists.Add(show); }
                s.RequestSettings.TargetSetlistId = show.Id;
            }
            s.RequestSettings.AutoArrange = true;
        }, "点歌队列已就绪");
    }

    public void SetReception(bool open)
    {
        if (!EnsureAutomaticQueue()) return;
        Change(s => s.RequestSettings.IsOpen = open, open ? "正在接收观众点歌" : "已关闭点歌接收");
    }

    public void AddToQueue(Guid songId)
    {
        if (!EnsureAutomaticQueue()) return;
        Change(s => SetlistOperations.AddSong(s.Setlists.Single(x => x.Id == s.RequestSettings.TargetSetlistId), s.Songs.Single(x => x.Id == songId)), "已加入队列");
    }

    public void Requeue(Guid entryId)
    {
        if (QueuePlayer?.ActiveEntryId == entryId || QueuePlayer?.IsEnginePlaying == true) { SetStatus("请先停止当前演奏", true); return; }
        var show = State.Setlists.FirstOrDefault(s => s.Entries.Any(e => e.Id == entryId));
        if (show == null) return;
        if (show.Entries.Single(e => e.Id == entryId).Status == EntryStatus.InProgress)
            if (!Change(s => SetlistOperations.Skip(s.Setlists.Single(x => x.Id == show.Id), entryId, DateTimeOffset.UtcNow))) return;
        Change(s =>
        {
            var target = s.Setlists.Single(x => x.Id == show.Id);
            SetlistOperations.ResetEntry(target, entryId);
            SetlistOperations.Move(target, entryId, target.Entries.Count - 1);
        }, "已重新排到队尾，原演奏记录已保留");
    }
}
