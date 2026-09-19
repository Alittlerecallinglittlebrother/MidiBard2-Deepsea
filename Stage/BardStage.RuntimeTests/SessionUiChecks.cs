using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;

internal static unsafe partial class RuntimeUi
{
    private static void RenderSessions(StageController controller, MainWindow window, string output)
    {
        // Close the template modal left open by the compact-window regression.
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 423, 505);
        var showId = Get<Guid?>(window, "stageShowId")!.Value;
        controller.SyncAvailable = true;
        Set(window, "selectStageTab", true); Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 630, 545);
        StageCheck(controller.SyncEnabled && controller.SyncShowId == showId, "native sync checkbox binds the displayed show");
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "stage-sync.png"));
        Click(window, 207, 74); Frame(window, 1100, 740);
        Set(window, "availablePerformers", 6);
        Click(window, 424, 101);
        if (!SpinWait.SpinUntil(() => { controller.Poll(); return !controller.IsChecking && controller.Preflight != null; }, 5000))
            throw new InvalidOperationException("native preflight did not complete");
        StageCheck(controller.Preflight!.ShowId == showId && controller.Preflight.Issues.Count > 0, "native preflight checks files and performer limits asynchronously");
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "preflight.png"));
        Frame(window, 760, 540); Frame(window, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "preflight-small.png"));
        Set(window, "availablePerformers", 7);
        StageCheck(controller.IsPreflightStale(showId, 7), "changing attendance invalidates the preflight report");
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "preflight-stale.png"));
        Click(window, 20, 177); Frame(window, 1100, 740); Frame(window, 1100, 740);
        StageCheck(Get<Guid?>(window, "selectedEntryId") == controller.Preflight.Issues.First().EntryId,
            "native preflight issue navigates to the affected program");
        Click(window, 274, 74); Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 140, 123); Frame(window, 1100, 740); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "archive-confirm.png"));
        Click(window, 383, 394); Frame(window, 1100, 740); Frame(window, 1100, 740);
        var session = controller.State.Sessions.Single(s => s.SetlistId == showId);
        StageCheck(session.EndedAtUtc.HasValue && session.Attempts.Count >= 3, "native archive preserves prior attempts after explicit confirmation");
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "sessions.png"));
        Frame(window, 760, 540); Frame(window, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "sessions-small.png"));
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        var count = controller.State.Setlists.Count;
        Click(window, 60, 206); Frame(window, 1100, 740); Frame(window, 1100, 740);
        StageCheck(controller.State.Setlists.Count == count + 1 && controller.CurrentSetlist!.Entries.All(e => e.Status == EntryStatus.Queued)
            && controller.State.Sessions.Single(s => s.Id == session.Id).Attempts.Count == session.Attempts.Count,
            "native copy creates a clean next show while preserving the archive");
    }
}
