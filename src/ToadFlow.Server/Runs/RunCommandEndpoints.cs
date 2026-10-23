using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Queue;

namespace ToadFlow.Server.Runs;

/// <summary>Отмена и повтор запусков и отдельных моделей (оператор и выше).</summary>
public static class RunCommandEndpoints
{
    public static void MapRunCommands(this RouteGroupBuilder api)
    {
        var write = api.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("/runs/{id:guid}/cancel", CancelRunAsync);
        write.MapPost("/jobs/{id:guid}/cancel", CancelJobAsync);
        write.MapPost("/runs/{id:guid}/retry-failed", RetryFailedAsync);
        write.MapPost("/jobs/{id:guid}/retry", RetryJobAsync);
    }

    private static async Task<IResult> CancelRunAsync(Guid id, ToadFlowDb db, AgentRegistry registry, RunProgress progress, TimeProvider time,
        HttpContext http, IDomainEvents events)
    {
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == id);
        if (run is null) return Results.NotFound();
        if (run.State is RunState.Completed or RunState.Cancelled) return Results.Conflict(new { error = "Запуск уже закончился" });

        run.CancelRequested = true;
        if (run.State == RunState.Expanding)
        {
            run.State = RunState.Cancelled;
            run.FinishedAt = time.GetUtcNow();
        }

        var jobs = await db.Jobs.Where(j => j.RunId == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)).ToListAsync();
        foreach (var job in jobs) Cancel(job, time);
        await db.SaveChangesAsync();
        await SignalAgentsAsync(jobs, registry);
        await progress.UpdateAsync(id);
        events.Publish(new RunChanged(http.User.Actor(), id));
        return Results.NoContent();
    }

    private static async Task<IResult> CancelJobAsync(Guid id, ToadFlowDb db, AgentRegistry registry, RunProgress progress, TimeProvider time,
        HttpContext http, IDomainEvents events)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id);
        if (job is null) return Results.NotFound();
        if (Job.IsFinal(job.Status)) return Results.Conflict(new { error = "Модель уже обработана" });

        Cancel(job, time);
        await db.SaveChangesAsync();
        await SignalAgentsAsync([job], registry);
        await progress.UpdateAsync(job.RunId);
        events.Publish(new JobChanged(http.User.Actor(), job.Id, job.RunId));
        return Results.NoContent();
    }

    private static async Task<IResult> RetryFailedAsync(Guid id, ToadFlowDb db, TimeProvider time, HttpContext http, IDomainEvents events)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
        if (run is null) return Results.NotFound();

        var failed = await db.Jobs.AsNoTracking().Where(j => j.RunId == id && j.Status == JobStatus.Failed && j.RevitYear != null).ToListAsync();
        if (failed.Count == 0) return Results.Conflict(new { error = "В запуске нет упавших моделей, которые можно повторить" });

        var retry = await CreateRetryRunAsync(db, time, http, run, $"{run.Name} — повтор упавших", failed);
        events.Publish(new RunChanged(http.User.Actor(), retry.Id));
        return Results.Ok(new { runId = retry.Id });
    }

    private static async Task<IResult> RetryJobAsync(Guid id, ToadFlowDb db, TimeProvider time, HttpContext http, IDomainEvents events)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id);
        if (job is null) return Results.NotFound();
        if (!Job.IsFinal(job.Status)) return Results.Conflict(new { error = "Модель ещё в работе" });
        if (job.RevitYear is null) return Results.Conflict(new { error = "Версия Revit модели неизвестна — повторять нечего" });

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == job.RunId);
        var retry = await CreateRetryRunAsync(db, time, http, run, $"{run.Name} — повтор {job.ModelName}", [job]);
        events.Publish(new RunChanged(http.User.Actor(), retry.Id));
        return Results.Ok(new { runId = retry.Id });
    }

    private static async Task<Run> CreateRetryRunAsync(ToadFlowDb db, TimeProvider time, HttpContext http, Run source, string name, IReadOnlyList<Job> jobs)
    {
        var now = time.GetUtcNow();
        var run = new Run
        {
            Id = Guid.CreateVersion7(),
            ScheduleId = source.ScheduleId,
            Name = name,
            TriggeredByUserId = http.User.UserId(),
            State = RunState.Queued,
            CreatedAt = now
        };
        db.Runs.Add(run);
        db.Jobs.AddRange(jobs.Select(job => new Job
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            ModuleKind = job.ModuleKind,
            ModelName = job.ModelName,
            ModelPath = job.ModelPath,
            RevitYear = job.RevitYear,
            RequiresNwc = job.RequiresNwc,
            PayloadJson = job.PayloadJson,
            Status = JobStatus.Queued,
            MaxAttempts = job.MaxAttempts,
            Priority = job.Priority,
            TimeoutMinutes = job.TimeoutMinutes,
            CreatedAt = now
        }));
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Задание в очереди отменяется сразу; выполняющееся — просьбой агенту, итог пришлёт он.</summary>
    private static void Cancel(Job job, TimeProvider time)
    {
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = time.GetUtcNow();
            job.Note = "отменено пользователем";
        }
        else
        {
            job.CancelRequested = true;
        }
    }

    private static async Task SignalAgentsAsync(IEnumerable<Job> jobs, AgentRegistry registry)
    {
        foreach (var job in jobs.Where(j => j is { Status: JobStatus.Running, AgentId: not null }))
            if (registry.Client(job.AgentId!.Value) is { } client)
                await client.CancelJob(job.Id);
    }
}
