using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Overview;

public sealed record FarmKpi(int Queued, int Running);

public sealed record DailyPoint(DateOnly Date, int Succeeded, int Failed);

public sealed record UpcomingRun(Guid ScheduleId, string Name, DateTimeOffset At, int ModelCount);

public sealed record RecentError(DateTimeOffset At, string ModelName, string? Note, string? AgentName, int? RevitYear, Guid JobId, Guid RunId);

public sealed record FarmSummaryView(FarmKpi Kpi, IReadOnlyList<DailyPoint> Daily, IReadOnlyList<UpcomingRun> Upcoming, IReadOnlyList<RecentError> RecentErrors);

/// <summary>
///     <c>GET /api/farm/summary</c> — сводка «Фермы → Очередь и журнал» по всем включённым модулям: очередь, модели по дням,
///     ближайшие запуски, последние ошибки. Раньше это была главная (<c>/api/dashboard</c>).
/// </summary>
public static class FarmSummaryEndpoints
{
    private const int Days = 14;

    public static void MapFarmSummary(this RouteGroupBuilder api) => api.MapGet("/farm/summary", async (ToadFlowDb db, ISettingsStore settings,
        ModuleSet modules, TimeProvider time) =>
    {
        var now = time.GetUtcNow();
        var timeZone = (await settings.GetAsync()).TimeZone;
        var kinds = modules.EnabledKinds;
        var jobs = db.Jobs.AsNoTracking().Where(j => kinds.Contains(j.ModuleKind));

        var kpi = new FarmKpi(await jobs.CountAsync(j => j.Status == JobStatus.Queued), await jobs.CountAsync(j => j.Status == JobStatus.Running));

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var firstDay = today.AddDays(-(Days - 1));
        var since = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), timeZone.GetUtcOffset(firstDay.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var finished = await jobs.Where(j => j.FinishedAt >= since && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning || j.Status == JobStatus.Failed))
            .Select(j => new { j.FinishedAt, j.Status }).ToListAsync();
        var byDay = finished.GroupBy(j => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(j.FinishedAt!.Value, timeZone).DateTime)).ToDictionary(g => g.Key);
        var daily = Enumerable.Range(0, Days).Select(i => firstDay.AddDays(i)).Select(day => byDay.TryGetValue(day, out var group)
            ? new DailyPoint(day, group.Count(j => j.Status != JobStatus.Failed), group.Count(j => j.Status == JobStatus.Failed))
            : new DailyPoint(day, 0, 0)).ToList();

        var upcoming = (await db.Schedules.AsNoTracking().Where(s => s.IsEnabled && s.NextRunAt != null && kinds.Contains(s.ModuleKind))
                .OrderBy(s => s.NextRunAt).Take(5).ToListAsync())
            .Select(s => new UpcomingRun(s.Id, s.Name, s.NextRunAt!.Value, s.LastKnownModels?.Count ?? s.Selection.Models.Length)).ToList();

        var names = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.MachineName);
        var errors = (await jobs.Where(j => j.Status == JobStatus.Failed).OrderByDescending(j => j.FinishedAt).Take(10).ToListAsync())
            .Select(j => new RecentError(j.FinishedAt ?? j.CreatedAt, j.ModelName, j.Note, j.AgentId is { } id ? names.GetValueOrDefault(id) : null,
                j.RevitYear, j.Id, j.RunId)).ToList();

        return new FarmSummaryView(kpi, daily, upcoming, errors);
    });
}
