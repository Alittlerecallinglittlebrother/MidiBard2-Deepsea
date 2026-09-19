#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BardStage;
using MidiBard.IPC;
using MidiBard.Managers.Ipc;

namespace MidiBard;

static partial class PlaylistManager
{
    internal static Func<IReadOnlyList<string>, string?>? StageRemovalIssue { get; set; }

    private static bool AllowStageRemoval(IReadOnlyList<string> paths)
    {
        if (StageRemovalIssue?.Invoke(paths) is not { } issue) return true;
        api.PluginLog.Warning(issue);
        return false;
    }

    internal static ILibraryEdit BeginStageLibraryEdit(IReadOnlyList<BardStage.Core.SongEntry> before, IReadOnlyList<BardStage.Core.SongEntry> after) =>
        new StageLibraryEdit(before, after);

    private sealed class StageLibraryEdit : ILibraryEdit
    {
        private readonly PlaylistContainer container = CurrentContainer;
        private readonly List<SongEntry> original;
        private readonly int originalIndex;
        private bool committed;

        public StageLibraryEdit(IReadOnlyList<BardStage.Core.SongEntry> before, IReadOnlyList<BardStage.Core.SongEntry> after)
        {
            original = container.SongPaths.ToList();
            originalIndex = container.CurrentSongIndex;
            var removed = before.Select(s => s.FilePath).Except(after.Select(s => s.FilePath), StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (StageRemovalIssue?.Invoke(removed.ToArray()) is { } issue) throw new InvalidOperationException(issue);
            var songs = original.Where(s => !removed.Contains(s.FilePath)).ToList();
            var present = songs.Select(s => s.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var song in after)
                if (present.Add(song.FilePath)) songs.Add(new SongEntry { FilePath = song.FilePath, SongLength = TimeSpan.FromSeconds(song.DurationSeconds) });
            var current = original.ElementAtOrDefault(originalIndex);
            container.SongPaths = songs;
            container.CurrentSongIndex = current == null ? -1 : songs.IndexOf(current);
            try { container.SaveStrict(); }
            catch
            {
                container.SongPaths = original;
                container.CurrentSongIndex = originalIndex;
                throw;
            }
        }

        public void Commit()
        {
            committed = true;
            // Local persistence already succeeded; a notification failure must not roll back committed catalog data.
            try { IPCHandles.SyncPlaylist(); }
            catch (Exception ex) { api.PluginLog.Warning(ex, "Playlist saved; local client notification failed."); }
        }

        public void Dispose()
        {
            if (committed) return;
            container.SongPaths = original;
            container.CurrentSongIndex = originalIndex;
            container.SaveStrict();
        }
    }
}
