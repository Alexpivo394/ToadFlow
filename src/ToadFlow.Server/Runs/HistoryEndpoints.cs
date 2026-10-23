using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Runs;

public sealed record RunCounts(int Succeeded, int Warning, int Failed, int Queued, int Running, int Cancelled, int Total);

public sealed record RunRow(
    Guid Id,
    Guid? ScheduleId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    RunState State,
    string? TriggeredBy,
    RunCounts Counts,
    double? DurationSeconds,
    string? Warning);

public sealed record JobRow(
    Guid Id,
    Guid RunId,
    string RunName,
    string ModuleKind,
    string ModelName,
    string ModelPath,
    JobStatus Status,
    int? RevitYear,
    bool RequiresNwc,
    Guid? AgentId,
    string? AgentName,
    int? Slot,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationSeconds,
    string? Note,
    JsonElement? Result,
    int? WaitingForRevit,
    bool CancelRequested);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

public sealed record RunDetails(RunRow Run, IReadOnlyList<JobRow> Jobs);

public sealed record LogLineView(DateTimeOffset At, string Stage, string Text);

/// <summary>Фильтры истории — общие для запусков и вкладки «Все модели».</summary>
public sealed record HistoryFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? ScheduleId,
    JobStatus? Status,
    Guid? AgentId,
    string? Search,
    int? Page,
    int? PageSize)
{
    public int Skip => (Math.Max(Page ?? 1, 1) - 1) * Take;
    public int Take => Math.Clamp(PageSize ?? 50, 1, 200);
}

public static class HistoryEndpoints
{
    public static void MapHistory(this RouteGroupBuilder api)
    {
        api.MapGet("/runs", async ([AsParameters] HistoryFilter filter, ToadFlowDb db, TimeProvider time) =>
        {
            var query = FilterRuns(db, filter);
            var total = await query.CountAsync();
            var runs = await query.OrderByDescending(r => r.CreatedAt).Skip(filter.Skip).Take(filter.Take).ToListAsync();
            return new Page<RunRow>(await RowsAsync(db, runs, time.GetUtcNow()), total);
        });

        api.MapGet("/runs/{id:guid}", async (Guid id, ToadFlowDb db, AgentRegistry registry, TimeProvider time) =>
        {
            var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();

            var jobs = await db.Jobs.AsNoTracking().Where(j => j.RunId == id).OrderBy(j => j.ModelName).ToListAsync();
            var row = (await RowsAsync(db, [run], time.GetUtcNow())).Single();
            return Results.Ok(new RunDetails(row, await JobRowsAsync(db, registry, jobs, new Dictionary<Guid, string> { [run.Id] = run.Name })));
        });

        api.MapGet("/jobs", async ([AsParameters] HistoryFilter filter, ToadFlowDb db, AgentRegistry registry) =>
        {
            var query = FilterJobs(db, filter);
            var total = await query.CountAsync();
            var jobs = await query.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.ModelName).Skip(filter.Skip).Take(filter.Take).ToListAsync();
            var runIds = jobs.Select(j => j.RunId).Distinct().ToList();
            var names = await db.Runs.Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
            return new Page<JobRow>(await JobRowsAsync(db, registry, jobs, names), total);
        });

        api.MapGet("/jobs/{id:guid}/log", async (Guid id, ToadFlowDb db) =>
            await db.JobLogLines.AsNoTracking().Where(l => l.JobId == id).OrderBy(l => l.Id)
                .Select(l => new LogLineView(l.At, l.Stage, l.Text)).ToListAsync());

        api.MapGet("/jobs/{id:guid}/log.txt", async (Guid id, ToadFlowDb db, ISettingsStore settings) =>
        {
            var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id);
            if (job is null) return Results.NotFound();

            var timeZone = (await settings.GetAsync()).TimeZone;
            var lines = await db.JobLogLines.AsNoTracking().Where(l => l.JobId == id).OrderBy(l => l.Id).ToListAsync();
            var text = new StringBuilder();
            foreach (var line in lines)
                text.Append($"[{TimeZoneInfo.ConvertTime(line.At, timeZone):HH:mm:ss}] {line.Stage}: {line.Text}").Append("\r\n");
            if (job.Note is not null) text.Append($"Итог: {job.Status}, {job.Note}\r\n");

            var fileName = $"{Path.GetFileNameWithoutExtension(job.ModelName)}_{job.Id.ToString()[..8]}.log";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray(), "text/plain; charset=utf-8", fileName);
        });
    }

    private static IQueryable<Run> FilterRuns(ToadFlowDb db, HistoryFilter filter)
    {
        var query = db.Runs.AsNoTracking();
        if (filter.From is { } from) query = query.Where(r => r.CreatedAt >= from.ToUniversalTime());
        if (filter.To is { } to) query = query.Where(r => r.CreatedAt <= to.ToUniversalTime());
        if (filter.ScheduleId is { } scheduleId) query = query.Where(r => r.ScheduleId == scheduleId);
        if (filter.Status is { } status) query = query.Where(r => db.Jobs.Any(j => j.RunId == r.Id && j.Status == status));
        if (filter.AgentId is { } agentId) query = query.Where(r => db.Jobs.Any(j => j.RunId == r.Id && j.AgentId == agentId));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = $"%{filter.Search.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.Name, pattern) || db.Jobs.Any(j => j.RunId == r.Id && EF.Functions.ILike(j.ModelName, pattern)));
        }

        return query;
    }

    private static IQueryable<Job> FilterJobs(ToadFlowDb db, HistoryFilter filter)
    {
        var query = db.Jobs.AsNoTracking();
        if (filter.From is { } from) query = query.Where(j => j.CreatedAt >= from.ToUniversalTime());
        if (filter.To is { } to) query = query.Where(j => j.CreatedAt <= to.ToUniversalTime());
        if (filter.ScheduleId is { } scheduleId) query = query.Where(j => db.Runs.Any(r => r.Id == j.RunId && r.ScheduleId == scheduleId));
        if (filter.Status is { } status) query = query.Where(j => j.Status == status);
        if (filter.AgentId is { } agentId) query = query.Where(j => j.AgentId == agentId);
        if (!string.IsNullOrWhiteSpace(filter.Search)) query = query.Where(j => EF.Functions.ILike(j.ModelName, $"%{filter.Search.Trim()}%"));
        return query;
    }

    private static async Task<List<RunRow>> RowsAsync(ToadFlowDb db, IReadOnlyList<Run> runs, DateTimeOffset now)
    {
        var ids = runs.Select(r => r.Id).ToList();
        var statuses = await db.Jobs.AsNoTracking().Where(j => ids.Contains(j.RunId)).Select(j => new { j.RunId, j.Status }).ToListAsync();
        var userIds = runs.Where(r => r.TriggeredByUserId != null).Select(r => r.TriggeredByUserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return runs.Select(run =>
        {
            var mine = statuses.Where(s => s.RunId == run.Id).Select(s => s.Status).ToList();
            var counts = new RunCounts(Count(mine, JobStatus.Succeeded), Count(mine, JobStatus.Warning), Count(mine, JobStatus.Failed),
                Count(mine, JobStatus.Queued), Count(mine, JobStatus.Running), Count(mine, JobStatus.Cancelled), mine.Count);
            var duration = run.StartedAt is { } started ? ((run.FinishedAt ?? now) - started).TotalSeconds : (double?)null;
            var triggeredBy = run.TriggeredByUserId is { } userId ? users.GetValueOrDefault(userId, "удалённый пользователь") : null;
            return new RunRow(run.Id, run.ScheduleId, run.Name, run.CreatedAt, run.StartedAt, run.FinishedAt, run.State, triggeredBy, counts,
                duration, run.Warning);
        }).ToList();
    }

    private static async Task<List<JobRow>> JobRowsAsync(ToadFlowDb db, AgentRegistry registry, IReadOnlyList<Job> jobs, IReadOnlyDictionary<Guid, string> runNames)
    {
        var agents = await db.Agents.AsNoTracking().ToListAsync();
        var names = agents.ToDictionary(a => a.Id, a => a.MachineName);
        var serving = agents.Where(a => a.State == AgentState.Online && registry.IsConnected(a.Id)).ToList();

        return jobs.Select(job =>
        {
            int? waiting = null;
            if (job is { Status: JobStatus.Queued, RevitYear: { } year } && !serving.Any(a => CanServe(a, year, job.RequiresNwc))) waiting = year;

            JsonElement? result = null;
            if (job.ResultJson is { } json)
            {
                using var document = JsonDocument.Parse(json);
                result = document.RootElement.Clone();
            }

            var duration = job is { StartedAt: { } started, FinishedAt: { } finished } ? (finished - started).TotalSeconds : (double?)null;
            return new JobRow(job.Id, job.RunId, runNames.GetValueOrDefault(job.RunId, ""), job.ModuleKind, job.ModelName, job.ModelPath, job.Status,
                job.RevitYear, job.RequiresNwc, job.AgentId, job.AgentId is { } agentId ? names.GetValueOrDefault(agentId) : null, job.InstanceSlot,
                job.Attempt, job.MaxAttempts, job.CreatedAt, job.StartedAt, job.FinishedAt, duration, job.Note, result, waiting, job.CancelRequested);
        }).ToList();
    }

    private static bool CanServe(Agent agent, int year, bool requiresNwc) =>
        agent.Limits.Any(l => l.Year == year && l.MaxInstances > 0) && (!requiresNwc || agent.Revits.Any(r => r.Year == year && r.HasNwcExporter));

    private static int Count(List<JobStatus> statuses, JobStatus status) => statuses.Count(s => s == status);
}
