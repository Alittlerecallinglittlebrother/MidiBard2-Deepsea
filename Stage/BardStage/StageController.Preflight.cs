using BardStage.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BardStage;

public sealed partial class StageController
{
    private Task<PreflightReport>? preflightTask;
    private CancellationTokenSource? preflightCancellation;
    public PreflightReport? Preflight { get; private set; }
    public bool IsChecking => preflightTask != null;
    public string PreflightError { get; private set; } = "";
    public string SessionExportDirectory => Path.Combine(DataDirectory, "Exports");
    public string LastSessionExportPath { get; private set; } = "";

    public void CheckPreflight(Guid showId, int performers)
    {
        if (disposed || IsChecking) return;
        var snapshot = Clone(State);
        preflightCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = preflightCancellation.Token;
        PreflightError = "";
        preflightTask = Task.Run(() => PreflightOperations.Check(snapshot, showId, performers, token), token);
    }

    public void CancelPreflight() => preflightCancellation?.Cancel();
    public bool IsPreflightStale(Guid showId, int performers) => Preflight == null || Preflight.ShowId != showId
        || !State.Setlists.Any(s => s.Id == showId) || Preflight.Fingerprint != PreflightOperations.Fingerprint(State, showId, performers);

    private void PollPreflight()
    {
        if (preflightTask is not { IsCompleted: true }) return;
        var task = preflightTask; preflightTask = null;
        try { Preflight = task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { PreflightError = "检查已取消"; }
        catch (Exception ex) { PreflightError = ex.Message; }
        finally { preflightCancellation?.Dispose(); preflightCancellation = null; }
    }

    public void ExportSessionToFolder(Guid sessionId, bool csv)
    {
        try
        {
            Directory.CreateDirectory(SessionExportDirectory);
            var filename = $"session-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}." + (csv ? "csv" : "json");
            ExportSession(sessionId, Path.Combine(SessionExportDirectory, filename), csv);
        }
        catch (Exception ex) { SetStatus("导出失败：" + ex.Message, true); }
    }

    public void ExportSession(Guid sessionId, string destination, bool csv)
    {
        try
        {
            var session = Clone(State.Sessions.Single(s => s.Id == sessionId));
            var exportedAt = DateTimeOffset.UtcNow;
            var path = Path.GetFullPath(destination);
            var protectedNames = new[] { "catalog.json", "catalog.json.bak", "catalog.v1.bak", "catalog.v2.bak", "catalog.writer.lock", "announcements.json", "announcements.json.bak" };
            if (protectedNames.Any(name => path.Equals(Path.GetFullPath(Path.Combine(DataDirectory, name)), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("不能覆盖曲库、模板或备份文件。");
            var snapshot = JsonSerializer.SerializeToNode(session, JsonOptions)!.AsObject();
            snapshot["exportedAtUtc"] = JsonValue.Create(exportedAt);
            snapshot["isLiveSnapshot"] = !session.EndedAtUtc.HasValue;
            var text = csv ? SessionCsv(session, exportedAt) : snapshot.ToJsonString(JsonOptions);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, text, new UTF8Encoding(csv)); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            LastSessionExportPath = path;
            SetStatus("演出记录已导出：" + path);
        }
        catch (Exception ex) { SetStatus("导出失败：" + ex.Message, true); }
    }

    private static string SessionCsv(ShowSession session, DateTimeOffset exportedAt)
    {
        static string Cell(string value)
        {
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "演出,曲目,结果,来源,开始UTC,结束UTC,实际秒数,计划秒数,点歌观众" };
        foreach (var attempt in session.Attempts)
            lines.Add(string.Join(",", new[] { session.ShowName, attempt.Program.Title, attempt.Outcome.ToString(), attempt.Source.ToString(),
                attempt.StartedAtUtc?.ToString("O") ?? "", attempt.EndedAtUtc?.ToString("O") ?? "",
                attempt.Source == RecordSource.Legacy && attempt.Outcome != AttemptOutcome.InProgress && (!attempt.StartedAtUtc.HasValue || !attempt.EndedAtUtc.HasValue) ? "" : SessionOperations.ActualSeconds(attempt, session.EndedAtUtc ?? exportedAt).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                attempt.Program.PlannedSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), string.Join("、", attempt.Program.Requesters) }.Select(Cell)));
        return string.Join("\r\n", lines) + "\r\n";
    }
}
