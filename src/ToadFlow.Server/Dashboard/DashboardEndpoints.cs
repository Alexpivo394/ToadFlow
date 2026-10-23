using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Dashboard;

public sealed record Kpi(
    int Exported24h,
    int Failed24h,
    double FailedPercent,
    int Queued,
    int Running,
    double? AvgDurationSeconds24h,
    int AgentsOnline,
    int AgentsTotal);

public sealed record DailyPoint(DateOnly Date, int Succeeded, int Failed);

public sealed record UpcomingRun(Guid ScheduleId, string Name, DateTimeOffset At, int ModelCount);

public sealed record RecentError(DateTimeOffset At, string ModelName, string? Note, string? AgentName, int? RevitYear, Guid JobId, Guid RunId);

public sealed record DashboardView(Kpi Kpi, IReadOnlyList<DailyPoint> Daily, IReadOnlyList<UpcomingRun> Upcoming, IReadOnlyList<RecentError> RecentErrors);

public static class DashboardEndpoints
{
    private const int Days = 14;

    public static void MapDashboard(this RouteGroupBuilder api) => api.MapGet("/dashboard", async (ToadFlowDb db, ISettingsStore settings, TimeProvider time) =>
    {
        var now = time.GetUtcNow();
        var dayAgo = now.AddDays(-1);
        var timeZone = (await settings.GetAsync()).TimeZone;
        var jobs = db.Jobs.AsNoTracking();

        var exported = await jobs.Where(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning) && j.FinishedAt >= dayAgo)
            .Select(j => new { j.StartedAt, j.FinishedAt }).ToListAsync();
        var failed = await jobs.CountAsync(j => j.Status == JobStatus.Failed && j.FinishedAt >= dayAgo);
        var durations = exported.Where(j => j.StartedAt != null).Select(j => (j.FinishedAt!.Value - j.StartedAt!.Value).TotalSeconds).ToList();
        var agents = await db.Agents.AsNoTracking().Select(a => a.State).ToListAsync();
        var kpi = new Kpi(
            exported.Count,
            failed,
            exported.Count + failed == 0 ? 0 : Math.Round(100.0 * failed / (exported.Count + failed), 1),
            await jobs.CountAsync(j => j.Status == JobStatus.Queued),
            await jobs.CountAsync(j => j.Status == JobStatus.Running),
            durations.Count == 0 ? null : durations.Average(),
            agents.Count(s => s is AgentState.Online or AgentState.Draining),
            agents.Count(s => s != AgentState.Disabled));

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var firstDay = today.AddDays(-(Days - 1));
        var since = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), timeZone.GetUtcOffset(firstDay.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var finished = await jobs.Where(j => j.FinishedAt >= since && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning || j.Status == JobStatus.Failed))
            .Select(j => new { j.FinishedAt, j.Status }).ToListAsync();
        var byDay = finished.GroupBy(j => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(j.FinishedAt!.Value, timeZone).DateTime)).ToDictionary(g => g.Key);
        var daily = Enumerable.Range(0, Days).Select(i => firstDay.AddDays(i)).Select(day => byDay.TryGetValue(day, out var group)
            ? new DailyPoint(day, group.Count(j => j.Status != JobStatus.Failed), group.Count(j => j.Status == JobStatus.Failed))
            : new DailyPoint(day, 0, 0)).ToList();

        var upcoming = (await db.Schedules.AsNoTracking().Where(s => s.IsEnabled && s.NextRunAt != null).OrderBy(s => s.NextRunAt).Take(5).ToListAsync())
            .Select(s => new UpcomingRun(s.Id, s.Name, s.NextRunAt!.Value, s.LastKnownModels?.Count ?? s.Selection.Models.Length)).ToList();

        var names = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.MachineName);
        var errors = (await jobs.Where(j => j.Status == JobStatus.Failed).OrderByDescending(j => j.FinishedAt).Take(10).ToListAsync())
            .Select(j => new RecentError(j.FinishedAt ?? j.CreatedAt, j.ModelName, j.Note, j.AgentId is { } id ? names.GetValueOrDefault(id) : null,
                j.RevitYear, j.Id, j.RunId)).ToList();

        return new DashboardView(kpi, daily, upcoming, errors);
    });
}
