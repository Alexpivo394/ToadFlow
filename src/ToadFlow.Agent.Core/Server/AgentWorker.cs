using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Jobs;
using ToadFlow.Contracts;

namespace ToadFlow.Agent.Server;

/// <summary>
///     Рабочий цикл агента: heartbeat, задания для свободных лимитов, выгрузка в фоне, сдача результата, закрытие
///     простаивающих Revit. Задание берётся до запуска Revit — так лицензия занята, только когда есть что выгружать.
/// </summary>
public sealed class AgentWorker(ServerConnection connection, InstanceManager instances, JobRunner runner, AgentOptions options, TimeProvider time,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleCheckEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<int, int> _pending = new(); // задания, под которые ещё запускается Revit
    private readonly SystemLoad _load = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await connection.StartAsync(ct);

        var lastPoll = DateTimeOffset.MinValue;
        var lastIdleCheck = time.GetUtcNow();
        using var timer = new PeriodicTimer(options.HeartbeatInterval < options.PollInterval ? options.HeartbeatInterval : options.PollInterval, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await connection.EnsureConnectedAsync(ct);
                await connection.HeartbeatAsync(new AgentHeartbeatDto(_load.CpuPercent(), _load.RamUsedMb(), instances.Statuses().ToArray()));

                var now = time.GetUtcNow();
                if (now - lastPoll >= options.PollInterval)
                {
                    lastPoll = now;
                    await TakeJobsAsync(ct);
                }

                if (now - lastIdleCheck >= IdleCheckEvery)
                {
                    lastIdleCheck = now;
                    instances.CloseIdle();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Ошибка в рабочем цикле агента");
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await Task.WhenAll(_running.Values).WaitAsync(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task TakeJobsAsync(CancellationToken ct)
    {
        if (!connection.IsConnected || connection.Settings is not { Draining: false } settings) return;

        instances.MaxSlots = settings.Limits.Sum(l => l.MaxInstances);
        foreach (var limit in settings.Limits.Where(l => l.MaxInstances > 0))
        {
            while (CanTake(limit))
            {
                var assignment = await connection.RequestJobAsync(new JobRequestDto(PredictSlot(limit.Year), limit.Year, HasNwcExporter(limit.Year)));
                if (assignment is null) break;

                _pending.AddOrUpdate(limit.Year, 1, (_, count) => count + 1);
                _running[assignment.JobId] = Task.Run(() => RunJobAsync(assignment, limit.Year, settings, ct), CancellationToken.None);
            }
        }
    }

    private bool CanTake(VersionLimitDto limit)
    {
        var pendingYear = _pending.GetValueOrDefault(limit.Year);
        var all = instances.Instances;
        if (all.Count(i => i.Year == limit.Year && i.State != InstanceState.Idle) + pendingYear >= limit.MaxInstances) return false;

        var idleOfYear = all.Count(i => i.Year == limit.Year && i.State == InstanceState.Idle);
        return idleOfYear > pendingYear || all.Count + _pending.Values.Sum() < instances.MaxSlots;
    }

    private async Task RunJobAsync(JobAssignmentDto assignment, int year, AgentSettingsDto settings, CancellationToken ct)
    {
        try
        {
            JobCompletionDto completion;
            RevitInstance? instance = null;
            try
            {
                instance = await instances.AcquireAsync(year, ct, assignment);
            }
            catch (RevitStartException exception)
            {
                logger.LogWarning("Задание {Job}: {Error}", assignment.JobId, exception.Message);
                completion = new JobCompletionDto(assignment.JobId, JobOutcome.InfrastructureFailure, exception.Message, null);
                await connection.CompleteJobAsync(completion, ct);
                return;
            }
            finally
            {
                _pending.AddOrUpdate(year, 0, (_, count) => Math.Max(0, count - 1));
            }

            var current = connection.Settings ?? settings;
            completion = await runner.RunAsync(instance, assignment, current, connection, ct, options.SilenceTimeoutOverride);
            if (instance.KillReason is null && !instance.Process.HasExited)
            {
                // Аддин сообщил о внутренней ошибке Revit — этот процесс больше не доверяем, повтор пойдёт в новом.
                if (completion.Outcome == JobOutcome.InfrastructureFailure) instances.Kill(instance.Slot, "внутренняя ошибка Revit — перезапуск");
                else instances.Release(instance, current);
            }
            await connection.CompleteJobAsync(completion, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // агент останавливается: сервер вернёт задание в очередь по истечении аренды
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Задание {Job} упало в агенте", assignment.JobId);
        }
        finally
        {
            _running.TryRemove(assignment.JobId, out _);
        }
    }

    private int PredictSlot(int year)
    {
        var all = instances.Instances;
        return all.FirstOrDefault(i => i.Year == year && i.State == InstanceState.Idle)?.Slot
               ?? Enumerable.Range(1, instances.MaxSlots).Except(all.Select(i => i.Slot)).DefaultIfEmpty(1).First();
    }

    private bool HasNwcExporter(int year) =>
        instances.Instances.FirstOrDefault(i => i.Year == year && i.State != InstanceState.Starting)?.HasNwcExporter
        ?? instances.Revits.FirstOrDefault(r => r.Year == year)?.HasNwcExporter ?? false;
}
