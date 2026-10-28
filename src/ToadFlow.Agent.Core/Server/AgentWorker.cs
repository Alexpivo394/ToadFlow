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
///     Сверх лимитов экземпляров задание берётся, только если на него хватает физической памяти (<see cref="MemoryGate" />).
/// </summary>
public sealed class AgentWorker(ServerConnection connection, InstanceManager instances, JobRunner runner, AgentOptions options, TimeProvider time,
    ISystemMemory memory, ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleCheckEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<int, int> _pending = new(); // задания, под которые ещё запускается Revit
    private readonly SystemLoad _load = new();
    private string? _memoryBlocked;

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
                    FreeMemoryIfCritical();
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
            while (CanTake(limit) && HasMemoryFor(limit.Year, settings))
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
        if (idleOfYear > pendingYear) return true;

        // Новый Revit этого года сейчас не запустится — взятое задание сразу упало бы и потеряло попытку.
        return !instances.StartDeferred(limit.Year) && all.Count + _pending.Values.Sum() < instances.MaxSlots;
    }

    /// <summary>Хватит ли памяти на ещё одно задание этого года. Решение пишется в журнал агента, только когда меняется.</summary>
    private bool HasMemoryFor(int year, AgentSettingsDto settings)
    {
        if (memory.Snapshot() is not { } ram) return true;

        var all = instances.Instances;
        // Задания, под которые Revit ещё не попал в список экземпляров, растут с нуля.
        var unstarted = Math.Max(0, _pending.Values.Sum() - all.Count(i => i.State == InstanceState.Starting));
        var busy = all.Where(i => i.State != InstanceState.Idle)
            .Select(i => (CurrentMb: i.Process?.WorkingSetMb ?? 0, PeakMb: i.PeakWorkingSetMb))
            .Concat(Enumerable.Repeat((CurrentMb: 0L, PeakMb: 0L), unstarted))
            .ToList();
        var idle = all.FirstOrDefault(i => i.Year == year && i.State == InstanceState.Idle);
        var decision = MemoryGate.Check(ram, busy, idle?.Process?.WorkingSetMb, instances.ExpectedJobMb(settings.RamPerJobMb), settings.RamReserveMb);

        var blocked = decision.Allowed ? null : decision.Reason;
        if (blocked is not null && _memoryBlocked is null) logger.LogWarning("Revit {Year}: новое задание не беру — {Reason}", year, decision.Reason);
        if (blocked is null && _memoryBlocked is not null) logger.LogInformation("Памяти снова хватает: {Reason}", decision.Reason);
        _memoryBlocked = blocked;
        return decision.Allowed;
    }

    /// <summary>Свободной памяти меньше резерва — закрыть простаивающие Revit. Работающие задания не трогаем.</summary>
    private void FreeMemoryIfCritical()
    {
        if (connection.Settings is not { } settings || memory.Snapshot() is not { } ram || ram.AvailableMb >= settings.RamReserveMb) return;

        var closed = instances.CloseIdleForMemory();
        if (closed > 0)
            logger.LogWarning("Свободно {Available} из {Total} МБ — меньше резерва {Reserve} МБ: закрыто простаивающих Revit: {Closed}", ram.AvailableMb,
                ram.TotalMb, settings.RamReserveMb, closed);
    }

    private async Task RunJobAsync(JobAssignmentDto assignment, int year, AgentSettingsDto settings, CancellationToken ct)
    {
        try
        {
            JobCompletionDto completion;
            RevitInstance? instance = null;
            try
            {
                instance = await instances.AcquireAsync(year, ct, assignment, () => runner.IsCancelled(assignment.JobId));
            }
            catch (RevitStartException exception)
            {
                logger.LogWarning("Задание {Job}: {Error}", assignment.JobId, exception.Message);
                completion = runner.IsCancelled(assignment.JobId)
                    ? new JobCompletionDto(assignment.JobId, JobOutcome.Cancelled, "отменено пользователем, пока запускался Revit", null)
                    : new JobCompletionDto(assignment.JobId, JobOutcome.InfrastructureFailure, exception.Message, null);
                await connection.CompleteJobAsync(completion, ct);
                return;
            }
            finally
            {
                _pending.AddOrUpdate(year, 0, (_, count) => Math.Max(0, count - 1));
            }

            var current = connection.Settings ?? settings;
            try
            {
                completion = await runner.RunAsync(instance, assignment, current, connection, ct, options.SilenceTimeoutOverride);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Иначе задание осталось бы «выполняется»: heartbeat продлевал бы его аренду, пока жив этот Revit.
                logger.LogError(exception, "Задание {Job} упало в агенте — Revit в слоте {Slot} будет перезапущен", assignment.JobId, instance.Slot);
                instances.Kill(instance.Slot, "сбой агента во время задания");
                completion = new JobCompletionDto(assignment.JobId, JobOutcome.InfrastructureFailure, $"сбой агента: {exception.Message}", null);
            }

            if (instance.KillReason is null && !instance.Process.HasExited)
            {
                // Аддин сообщил о внутренней ошибке Revit — этот процесс больше не доверяем, повтор пойдёт в новом.
                // Освобождение само перезапускает Revit, если аддин усомнился в его состоянии (RecycleReason).
                if (completion.Outcome == JobOutcome.InfrastructureFailure)
                {
                    instances.RecordPeak(instance.PeakWorkingSetMb);
                    instances.Kill(instance.Slot, "внутренняя ошибка Revit — перезапуск");
                }
                else instances.Release(instance, current);
            }
            else instances.RecordPeak(instance.PeakWorkingSetMb);

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
            runner.Forget(assignment.JobId);
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
