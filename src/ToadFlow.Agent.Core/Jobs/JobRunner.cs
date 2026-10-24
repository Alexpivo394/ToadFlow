using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Instances;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Jobs;

/// <summary>Куда исполнитель сообщает о ходе задания и об автоматически убитых экземплярах (на сервер).</summary>
public interface IJobReporter
{
    Task ProgressAsync(JobProgressDto progress);

    Task InstanceKilledAsync(int slot, string reason);
}

/// <summary>
///     Выгрузка одной модели в экземпляре Revit: отправить задание аддину, пересылать шаги, следить за молчанием Revit,
///     общим таймаутом и падением процесса. Зависший или упавший Revit — сбой инфраструктуры: задание повторится.
/// </summary>
public sealed class JobRunner(InstanceManager instances, TimeProvider time, ILogger<JobRunner> logger)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnresponsiveAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, bool> _cancelled = new();

    /// <summary>Попросить бросить задание: аддин получит отмену и ответит «отменено».</summary>
    public void Cancel(Guid jobId) => _cancelled[jobId] = true;

    /// <param name="silenceOverride">Только для тестов и диагностики: молчание вместо минут из настроек.</param>
    public async Task<JobCompletionDto> RunAsync(RevitInstance instance, JobAssignmentDto assignment, AgentSettingsDto settings, IJobReporter reporter,
        CancellationToken ct, TimeSpan? silenceOverride = null)
    {
        var jobId = assignment.JobId;
        var silenceLimit = silenceOverride ?? TimeSpan.FromMinutes(settings.SilenceTimeoutMinutes);
        var silenceText = silenceLimit >= TimeSpan.FromMinutes(1) ? $"{silenceLimit.TotalMinutes:0} мин" : $"{silenceLimit.TotalSeconds:0} с";
        var started = time.GetUtcNow();
        var lastFlush = started;
        var buffer = new List<JobLogDto>();
        DateTimeOffset? cancelSentAt = null;
        Task<PipeMessage>? pending = null;

        instance.BeginJob(assignment);
        try
        {
            await instance.Pipe.SendAsync(new JobMessage
            {
                JobId = jobId, ModuleKind = assignment.ModuleKind, PayloadJson = assignment.PayloadJson, TimeoutMinutes = assignment.TimeoutMinutes
            });

            while (true)
            {
                // агент останавливается: Revit убьют при остановке, сервер вернёт задание в очередь по истечении аренды
                ct.ThrowIfCancellationRequested();
                pending ??= instance.JobEvents.ReadAsync(ct).AsTask();
                var delay = Task.Delay(Tick, time, ct);
                await Task.WhenAny(pending, instance.Closed, delay);
                var now = time.GetUtcNow();

                if (pending.IsCompletedSuccessfully)
                {
                    var message = pending.Result;
                    pending = null;
                    switch (message)
                    {
                        case ProgressMessage progress when progress.JobId == jobId:
                            buffer.Add(new JobLogDto(now, progress.Stage, progress.Text));
                            instance.SetStage(progress.Stage);
                            instance.Touch();
                            instance.MarkResponsive();
                            break;
                        case ResultMessage result when result.JobId == jobId:
                            await FlushAsync();
                            return new JobCompletionDto(jobId, result.Outcome, result.Note, result.ResultJson);
                    }
                }

                if (instance.Closed.IsCompleted)
                {
                    await FlushAsync();
                    if (cancelSentAt is not null) return new JobCompletionDto(jobId, JobOutcome.Cancelled, "отменено пользователем", null);
                    var reason = instance.KillReason is { } killed ? $"Revit {killed}" : "Revit завершился во время задания";
                    return Failure(reason);
                }

                var silence = now - instance.LastActivityAt;
                if (silence >= (UnresponsiveAfter < silenceLimit ? UnresponsiveAfter : silenceLimit / 2)) instance.MarkUnresponsive();

                if (silence > silenceLimit)
                    return await KillAsync($"не отвечал {silenceText}");

                if (now - started > TimeSpan.FromMinutes(assignment.TimeoutMinutes))
                    return await KillAsync($"превышен таймаут {assignment.TimeoutMinutes} мин на модель");

                if (_cancelled.ContainsKey(jobId))
                {
                    if (cancelSentAt is null)
                    {
                        cancelSentAt = now;
                        await instance.Pipe.SendAsync(new CancelMessage { JobId = jobId });
                    }
                    else if (now - cancelSentAt > CancelGrace)
                    {
                        instances.Kill(instance.Slot, "не прервал выгрузку после отмены");
                        await FlushAsync();
                        return new JobCompletionDto(jobId, JobOutcome.Cancelled, "отменено пользователем; Revit не остановился и был убит", null);
                    }
                }

                if (buffer.Count > 0 && now - lastFlush >= FlushEvery) await FlushAsync();
            }
        }
        catch (IOException exception)
        {
            await FlushAsync();
            return Failure($"связь с Revit потеряна: {exception.Message}");
        }
        finally
        {
            _cancelled.TryRemove(jobId, out _);
        }

        async Task FlushAsync()
        {
            lastFlush = time.GetUtcNow();
            if (buffer.Count == 0) return;
            var lines = buffer.ToArray();
            buffer.Clear();
            try
            {
                await reporter.ProgressAsync(new JobProgressDto(jobId, instance.Slot, lines));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Не удалось отправить ход выгрузки на сервер");
            }
        }

        async Task<JobCompletionDto> KillAsync(string reason)
        {
            logger.LogWarning("Revit в слоте {Slot}: {Reason} — убиваю", instance.Slot, reason);
            instances.Kill(instance.Slot, reason);
            buffer.Add(new JobLogDto(time.GetUtcNow(), "Сбой", $"Revit {reason} — процесс {instance.Process.Pid} убит"));
            await FlushAsync();
            try
            {
                await reporter.InstanceKilledAsync(instance.Slot, reason);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Не удалось сообщить серверу об убитом Revit");
            }

            return Failure($"Revit {reason} — убит");
        }

        JobCompletionDto Failure(string note) => new(jobId, JobOutcome.InfrastructureFailure, note, null);
    }
}
