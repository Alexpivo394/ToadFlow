using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Queue;

/// <summary>Состояние запуска следует за его заданиями: первое взятое — «выполняется», все конечные — «завершён».</summary>
public sealed class RunProgress(ToadFlowDb db, IDomainEvents events, TimeProvider time)
{
    public async Task UpdateAsync(Guid runId)
    {
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId);
        if (run is null || run.State is RunState.Expanding or RunState.Completed or RunState.Cancelled) return;

        var statuses = await db.Jobs.Where(j => j.RunId == runId).Select(j => j.Status).ToListAsync();
        var now = time.GetUtcNow();
        var before = run.State;

        if (statuses.Count > 0 && statuses.All(Job.IsFinal))
        {
            run.State = run.CancelRequested || statuses.All(s => s == JobStatus.Cancelled) ? RunState.Cancelled : RunState.Completed;
            run.StartedAt ??= now;
            run.FinishedAt = now;
        }
        else if (run.State == RunState.Queued && statuses.Any(s => s != JobStatus.Queued))
        {
            run.State = RunState.Running;
            run.StartedAt = now;
        }

        if (run.State == before) return;

        await db.SaveChangesAsync();
        events.Publish(run.State is RunState.Completed or RunState.Cancelled
            ? new RunFinished(Actor.System, run.Id, run.Name,
                statuses.Count(s => s == JobStatus.Succeeded), statuses.Count(s => s == JobStatus.Warning),
                statuses.Count(s => s == JobStatus.Failed), statuses.Count(s => s == JobStatus.Cancelled))
            : new RunChanged(Actor.System, run.Id));
    }
}
