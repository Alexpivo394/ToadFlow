using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Queue;

/// <summary>
///     Очередь заданий в Postgres. Задание выдаётся одним атомарным запросом (<c>FOR UPDATE SKIP LOCKED</c>), поэтому
///     два агента никогда не получат одно и то же. Агент держит аренду, продлевая её heartbeat'ом.
/// </summary>
public sealed class JobQueue(ToadFlowDb db, RunProgress runs, IDomainEvents events, TimeProvider time) : IAgentJobs
{
    public async Task<JobAssignmentDto?> ClaimAsync(Guid agentId, JobRequestDto request)
    {
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId);
        if (agent.IsDraining || agent.State is AgentState.Disabled or AgentState.Draining) return null;

        var limit = agent.Limits.FirstOrDefault(l => l.Year == request.Year)?.MaxInstances ?? 0;
        var busy = await db.Jobs.CountAsync(j => j.AgentId == agentId && j.Status == JobStatus.Running && j.RevitYear == request.Year);
        if (busy >= limit) return null;

        var now = time.GetUtcNow();
        var lease = now + AgentHub.Lease;
        var running = nameof(JobStatus.Running);
        var queued = nameof(JobStatus.Queued);
        var claimed = await db.Jobs.FromSql($"""
            UPDATE jobs SET status = {running}, agent_id = {agentId}, instance_slot = {request.Slot}, attempt = attempt + 1,
                            started_at = {now}, lease_until = {lease}
            WHERE id = (
                SELECT id FROM jobs
                WHERE status = {queued} AND revit_year = {request.Year} AND (NOT requires_nwc OR {request.HasNwcExporter})
                      AND NOT cancel_requested
                ORDER BY priority DESC, created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync();
        if (claimed.Count == 0) return null;

        var job = claimed[0];
        await runs.UpdateAsync(job.RunId);
        events.Publish(new JobChanged(AgentActor(agent), job.Id, job.RunId));
        return new JobAssignmentDto(job.Id, job.ModuleKind, job.ModelName, job.PayloadJson, job.TimeoutMinutes, job.Attempt);
    }

    public async Task AppendLogAsync(Guid agentId, JobProgressDto progress)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == progress.JobId && j.AgentId == agentId && j.Status == JobStatus.Running);
        if (job is null || progress.Lines.Length == 0) return;

        db.JobLogLines.AddRange(progress.Lines.Select(line => new JobLogLine { JobId = job.Id, At = line.At, Stage = line.Stage, Text = line.Text }));
        await db.SaveChangesAsync();
        events.Publish(new JobChanged(Actor.System, job.Id, job.RunId));
    }

    public async Task CompleteAsync(Guid agentId, JobCompletionDto completion)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == completion.JobId && j.AgentId == agentId && j.Status == JobStatus.Running);
        if (job is null) return;

        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId);
        if (completion.Outcome == JobOutcome.InfrastructureFailure && !job.CancelRequested)
        {
            await FailInfrastructureAsync(job, completion.Note ?? "сбой Revit", AgentActor(agent));
            return;
        }

        job.Status = completion.Outcome switch
        {
            JobOutcome.Succeeded => JobStatus.Succeeded,
            JobOutcome.Warning => JobStatus.Warning,
            JobOutcome.Failed => JobStatus.Failed,
            _ => JobStatus.Cancelled
        };
        job.Note = completion.Note;
        job.ResultJson = completion.ResultJson;
        Finish(job);
        await db.SaveChangesAsync();
        await runs.UpdateAsync(job.RunId);
        events.Publish(new JobChanged(AgentActor(agent), job.Id, job.RunId));
    }

    /// <summary>
    ///     Revit завис, упал, убит или агент пропал: если попытки остались — задание снова в очередь, иначе — ошибка.
    ///     Ошибки самой модели сюда не попадают — они не повторяются.
    /// </summary>
    public async Task FailInfrastructureAsync(Job job, string reason, Actor actor)
    {
        db.JobLogLines.Add(new JobLogLine
        {
            JobId = job.Id,
            At = time.GetUtcNow(),
            Stage = "Сбой",
            Text = $"попытка {job.Attempt} из {job.MaxAttempts} не удалась: {reason}"
        });

        if (job.Attempt < job.MaxAttempts)
        {
            job.Status = JobStatus.Queued;
            job.AgentId = null;
            job.InstanceSlot = null;
            job.LeaseUntil = null;
            job.StartedAt = null;
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.Note = reason;
            Finish(job);
        }

        await db.SaveChangesAsync();
        await runs.UpdateAsync(job.RunId);
        events.Publish(new JobChanged(actor, job.Id, job.RunId));
    }

    private void Finish(Job job)
    {
        job.FinishedAt = time.GetUtcNow();
        job.LeaseUntil = null;
    }

    private static Actor AgentActor(Agent agent) => new(ActorKind.Agent, agent.MachineName);
}
