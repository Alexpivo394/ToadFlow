using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Queue;

/// <summary>
///     Очередь заданий в Postgres. Задание выдаётся одним атомарным запросом (<c>FOR UPDATE SKIP LOCKED</c>), поэтому
///     два агента никогда не получат одно и то же. Агент держит аренду, продлевая её heartbeat'ом.
/// </summary>
public sealed class JobQueue(ToadFlowDb db, RunProgress runs, IDomainEvents events, TimeProvider time, IServiceProvider services,
    ILogger<JobQueue> logger) : IAgentJobs
{
    public async Task<JobAssignmentDto?> ClaimAsync(Guid agentId, JobRequestDto request)
    {
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId);
        if (agent.IsDraining || agent.State is AgentState.Disabled or AgentState.Draining) return null;

        var limit = agent.Limits.FirstOrDefault(l => l.Year == request.Year)?.MaxInstances ?? 0;
        var busy = await db.Jobs.CountAsync(j => j.AgentId == agentId && j.Status == JobStatus.Running && j.RevitYear == request.Year);
        if (busy >= limit) return null;

        // Лимит экземпляров проверяется внутри самой выдачи, а выдачи одного агента идут по очереди: иначе два параллельных
        // запроса оба увидят «занято меньше лимита» и выдадут два задания.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var lockKey = agentId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))");

        var now = time.GetUtcNow();
        var lease = now + LeaseOptions.Lease;
        var running = nameof(JobStatus.Running);
        var queued = nameof(JobStatus.Queued);
        var export = ModuleKinds.Export;
        var anyModule = AgentCapabilities.KnowsModuleKind(agent.AgentVersion);
        var clash = ModuleKinds.ClashExtract;
        var clashCapable = AgentCapabilities.CanRun(agent.AgentVersion, ModuleKinds.ClashExtract);
        var claimed = await db.Jobs.FromSql($"""
            UPDATE jobs SET status = {running}, agent_id = {agentId}, instance_slot = {request.Slot}, attempt = attempt + 1,
                            started_at = {now}, lease_until = {lease}
            WHERE id = (
                SELECT id FROM jobs
                WHERE status = {queued} AND revit_year = {request.Year} AND (NOT requires_nwc OR {request.HasNwcExporter})
                      AND (SELECT count(*) FROM jobs r WHERE r.agent_id = {agentId} AND r.status = {running} AND r.revit_year = {request.Year}) < {limit}
                      AND (module_kind = {export} OR {anyModule}) AND (module_kind <> {clash} OR {clashCapable}) AND NOT cancel_requested
                ORDER BY priority DESC, created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED) AND status = {queued}
            RETURNING *
            """).AsNoTracking().ToListAsync();
        await transaction.CommitAsync();
        if (claimed.Count == 0) return null;

        var job = claimed[0];
        await runs.UpdateAsync(job.RunId);
        events.Publish(new JobChanged(AgentActor(agent), job.Id, job.RunId));
        return new JobAssignmentDto(job.Id, job.ModuleKind, job.ModelName, job.PayloadJson, job.TimeoutMinutes, job.Attempt, job.MaxAttempts);
    }

    public async Task AppendLogAsync(Guid agentId, JobProgressDto progress)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == progress.JobId && j.AgentId == agentId && j.Status == JobStatus.Running
            && (progress.Attempt == null || j.Attempt == progress.Attempt));
        if (job is null || progress.Lines.Length == 0) return;

        db.JobLogLines.AddRange(progress.Lines.Select(line => new JobLogLine { JobId = job.Id, At = line.At, Stage = line.Stage, Text = line.Text }));
        await db.SaveChangesAsync();
        events.Publish(new JobChanged(Actor.System, job.Id, job.RunId));
    }

    public async Task CompleteAsync(Guid agentId, JobCompletionDto completion)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == completion.JobId && j.AgentId == agentId && j.Status == JobStatus.Running
                                                                           && (completion.Attempt == null || j.Attempt == completion.Attempt));
        if (job is null) return;

        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId);
        if (completion.Outcome == JobOutcome.InfrastructureFailure && !job.CancelRequested)
        {
            await FailInfrastructureAsync(job, completion.Note ?? "сбой Revit", AgentActor(agent));
            return;
        }

        var status = completion.Outcome switch
        {
            JobOutcome.Succeeded => JobStatus.Succeeded,
            JobOutcome.Warning => JobStatus.Warning,
            JobOutcome.Failed => JobStatus.Failed,
            _ => JobStatus.Cancelled
        };
        var now = time.GetUtcNow();
        var attempt = job.Attempt;
        // Условный переход: пока итог читался, sweeper мог вернуть задание в очередь, а другая попытка — начаться.
        var rows = await db.Jobs.Where(j => j.Id == job.Id && j.AgentId == agentId && j.Status == JobStatus.Running && j.Attempt == attempt)
            .ExecuteUpdateAsync(j => j
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.Note, completion.Note)
                .SetProperty(x => x.ResultJson, completion.ResultJson)
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null));
        if (rows == 0) return;

        job = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        await runs.UpdateAsync(job.RunId);
        events.Publish(new JobChanged(AgentActor(agent), job.Id, job.RunId));
        await NotifyModulesAsync(job);
    }

    /// <summary>Задания агента, которые сервер считает выполняющимися: по ним агент после переподключения решает, что доделывать.</summary>
    public async Task<ActiveJobDto[]> ActiveAsync(Guid agentId) =>
        await db.Jobs.AsNoTracking().Where(j => j.AgentId == agentId && j.Status == JobStatus.Running)
            .Select(j => new ActiveJobDto(j.Id, j.Attempt)).ToArrayAsync();

    /// <summary>После перезапуска сервера аренды всех выполняющихся заданий продлеваются: агентам нужно время переподключиться.</summary>
    public async Task ExtendLeasesAfterStartAsync(CancellationToken ct)
    {
        var until = time.GetUtcNow() + LeaseOptions.StartupGrace;
        await db.Jobs.Where(j => j.Status == JobStatus.Running && (j.LeaseUntil == null || j.LeaseUntil < until)).ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, until), ct);
    }

    private async Task NotifyModulesAsync(Job job)
    {
        foreach (var module in services.GetServices<IServerModule>())
        {
            try
            {
                await module.OnJobCompletedAsync(job, services, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Модуль {Module} не обработал итог задания {Job}", module.Kind, job.Id);
            }
        }
    }

    /// <summary>
    ///     Revit завис, упал, убит или агент пропал: если попытки остались — задание снова в очередь, иначе — ошибка.
    ///     Ошибки самой модели сюда не попадают — они не повторяются. Переход условный: если задание уже сдано, отменено или
    ///     перешло на другую попытку, ничего не меняется и возвращается <c>false</c>.
    /// </summary>
    public async Task<bool> FailInfrastructureAsync(Job job, string reason, Actor actor, DateTimeOffset? expiredBefore = null)
    {
        var now = time.GetUtcNow();
        var current = job;
        for (var tries = 0; tries < 3; tries++)
        {
            var attempt = current.Attempt;
            var cancel = current.CancelRequested;
            var scope = db.Jobs.Where(j => j.Id == current.Id && j.Status == JobStatus.Running && j.Attempt == attempt && j.CancelRequested == cancel
                                                 && (expiredBefore == null || j.LeaseUntil < expiredBefore));
            int rows;
            if (cancel)
            {
                // Задание отменили, а Revit или агент пропали раньше, чем аддин ответил: в очередь его не вернуть — оттуда
                // отменённые не выдаются, и запуск висел бы «выполняется» вечно.
                rows = await scope.ExecuteUpdateAsync(j => j
                    .SetProperty(x => x.Status, JobStatus.Cancelled)
                    .SetProperty(x => x.Note, "отменено пользователем")
                    .SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null));
            }
            else if (attempt < current.MaxAttempts)
            {
                rows = await scope.ExecuteUpdateAsync(j => j
                    .SetProperty(x => x.Status, JobStatus.Queued)
                    .SetProperty(x => x.AgentId, (Guid?)null)
                    .SetProperty(x => x.InstanceSlot, (int?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.StartedAt, (DateTimeOffset?)null));
            }
            else
            {
                rows = await scope.ExecuteUpdateAsync(j => j
                    .SetProperty(x => x.Status, JobStatus.Failed)
                    .SetProperty(x => x.Note, reason)
                    .SetProperty(x => x.FinishedAt, now)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null));
            }

            if (rows > 0)
            {
                db.JobLogLines.Add(new JobLogLine
                {
                    JobId = current.Id,
                    At = now,
                    Stage = "Сбой",
                    Text = $"попытка {attempt} из {current.MaxAttempts} не удалась: {reason}"
                });
                await db.SaveChangesAsync();
                await runs.UpdateAsync(current.RunId);
                events.Publish(new JobChanged(actor, current.Id, current.RunId));
                return true;
            }

            // Не сошлись данные: возможно, пока считали, пришла отмена. Перечитываем; если задание всё ещё той же попытки — повтор.
            var fresh = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == current.Id);
            if (fresh is not { Status: JobStatus.Running } || fresh.Attempt != attempt) return false;
            if (expiredBefore is not null && !(fresh.LeaseUntil < expiredBefore)) return false;
            current = fresh;
        }

        return false;
    }

    private static Actor AgentActor(Agent agent) => new(ActorKind.Agent, agent.MachineName);
}
