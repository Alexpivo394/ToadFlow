using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Server;
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
///     Выгрузка одной модели в экземпляре Revit: отправить задание аддину, пересылать шаги, следить за зависанием
///     (<see cref="HangWatch" />), общим таймаутом и падением процесса. Зависший или упавший Revit — сбой инфраструктуры:
///     задание повторится.
/// </summary>
public sealed class JobRunner(InstanceManager instances, TimeProvider time, ILogger<JobRunner> logger, ISystemMemory? memory = null)
{
    /// <summary>Этап диагностических строк в журнале задания.</summary>
    public const string DiagnosticsStage = "Диагностика";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnresponsiveAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PipeDrain = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<Guid, bool> _cancelled = new();

    /// <summary>Попросить бросить задание: аддин получит отмену и ответит «отменено».</summary>
    public void Cancel(Guid jobId) => _cancelled[jobId] = true;

    /// <summary>Задание отменили — в том числе пока под него запускался Revit.</summary>
    public bool IsCancelled(Guid jobId) => _cancelled.ContainsKey(jobId);

    /// <summary>Задание закончилось, не дойдя до <see cref="RunAsync" />: отмена ему больше не нужна.</summary>
    public void Forget(Guid jobId) => _cancelled.TryRemove(jobId, out _);

    /// <param name="silenceOverride">Только для тестов и диагностики: молчание вместо минут из настроек.</param>
    public async Task<JobCompletionDto> RunAsync(RevitInstance instance, JobAssignmentDto assignment, AgentSettingsDto settings, IJobReporter reporter,
        CancellationToken ct, TimeSpan? silenceOverride = null)
    {
        var jobId = assignment.JobId;
        var watch = new HangWatch(silenceOverride ?? TimeSpan.FromMinutes(settings.SilenceTimeoutMinutes));
        var quietAfter = UnresponsiveAfter < watch.SilenceLimit ? UnresponsiveAfter : watch.SilenceLimit / 2;
        var started = time.GetUtcNow();
        var lastFlush = started;
        var buffer = new List<JobLogDto>();
        var stages = new StageTimings(started);
        var verdict = HangVerdict.Active;
        var dialogs = new List<string>();
        DateTimeOffset? cancelSentAt = null;
        Task<PipeMessage>? pending = null;

        instance.BeginJob(assignment);
        Sample(started);
        // До отправки задания: если Revit упадёт, в журнале останется, с чем он начинал.
        buffer.Add(new JobLogDto(started, DiagnosticsStage, DescribeStart()));
        await FlushAsync();
        logger.LogInformation("Задание {Job} ({Model}, попытка {Attempt}): Revit {Year}, слот {Slot}, PID {Pid}", jobId, assignment.ModelName,
            assignment.Attempt, instance.Year, instance.Slot, instance.Process.Pid);
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
                    if (Accept(message, now) is { } result) return await CompleteAsync(result, now);
                }

                if (instance.Closed.IsCompleted)
                {
                    // Строки, которые Revit успел записать в пайп перед смертью: последний известный этап, а то и итог.
                    // Пайп дочитывается в настоящем времени — подменные часы тестов его не ускорят.
                    await Task.WhenAny(instance.Pipe.Completion, Task.Delay(PipeDrain, CancellationToken.None));
                    if (pending is { IsCompletedSuccessfully: true } && Accept(pending.Result, now) is { } last) return await CompleteAsync(last, now);
                    while (instance.JobEvents.TryRead(out var late))
                        if (Accept(late, now) is { } result)
                            return await CompleteAsync(result, now);

                    if (cancelSentAt is not null)
                    {
                        Finish(now, "Cancelled", null);
                        await FlushAsync();
                        return new JobCompletionDto(jobId, JobOutcome.Cancelled, "отменено пользователем", null);
                    }

                    var reason = instance.KillReason is { } killed ? $"Revit {killed}" : $"Revit завершился во время задания на этапе «{stages.Current}»";
                    Finish(now, "InfrastructureFailure", reason);
                    await FlushAsync();
                    return Failure(reason);
                }

                var sampled = watch.SampleDue(now);
                if (sampled) Sample(now);
                var next = watch.Judge(now, instance.LastActivityAt, quietAfter);
                if (next != verdict) OnVerdictChanged(next, now);
                verdict = next;

                // Аддин молчит — возможно, поток Revit API ждёт ответа в окне-диалоге: на известное окно агент отвечает сам,
                // окно, ответ на которое за человеком, прерывает задание с его текстом, а не держит Revit до таймаута.
                if (sampled && verdict != HangVerdict.Active)
                {
                    foreach (var dialog in await Task.Run(instance.Dialogs.Check, CancellationToken.None))
                    {
                        switch (dialog.Outcome)
                        {
                            case DialogOutcome.Closed:
                                dialogs.Add(dialog.Window);
                                buffer.Add(new JobLogDto(now, DiagnosticsStage, $"Revit ждал ответа в окне {dialog.Window} на этапе «{stages.Current}» — агент закрыл окно"));
                                // Отсчёт молчания — заново: Revit продолжает работу после окна.
                                instance.Touch();
                                verdict = HangVerdict.Active;
                                instance.MarkResponsive();
                                break;
                            case DialogOutcome.NotClosed:
                                buffer.Add(new JobLogDto(now, DiagnosticsStage, $"агент ответил на окно Revit {dialog.Window}, но окно не закрылось"));
                                break;
                            case DialogOutcome.NeedsPerson:
                                buffer.Add(new JobLogDto(now, DiagnosticsStage,
                                    $"Revit ждёт ответа в окне {dialog.Window} на этапе «{stages.Current}» — {DialogRules.Describe(dialog.Kind)}; " +
                                    $"задание прервётся через {Format(DialogWatcher.AnswerGrace)}"));
                                break;
                            case DialogOutcome.Fatal:
                                return await KillAsync($"показал окно {dialog.Window} на этапе «{stages.Current}» — {DialogRules.Describe(dialog.Kind)}");
                        }
                    }

                    if (instance.Dialogs.Blocking is { } blocked && now - instance.LastActivityAt >= DialogWatcher.AnswerGrace)
                        return await KillAsync($"ждал ответа в окне {blocked.Describe()} на этапе «{stages.Current}» — " +
                                               DialogRules.Describe(DialogRules.Classify(blocked)));
                }

                if (verdict == HangVerdict.Hung)
                    return await KillAsync($"не отвечал {Format(watch.SilenceLimit)} на этапе «{stages.Current}» ({watch.DescribeActivity()})");

                if (now - started > TimeSpan.FromMinutes(assignment.TimeoutMinutes))
                    return await KillAsync($"превышен таймаут {assignment.TimeoutMinutes} мин на модель (этап «{stages.Current}», {watch.DescribeActivity()})");

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
                        Finish(now, "Cancelled", "Revit не прервал выгрузку после отмены и был убит");
                        await FlushAsync();
                        return new JobCompletionDto(jobId, JobOutcome.Cancelled, "отменено пользователем; Revit не остановился и был убит", null);
                    }
                }

                if (buffer.Count > 0 && now - lastFlush >= FlushEvery) await FlushAsync();
            }
        }
        catch (IOException exception)
        {
            var reason = $"связь с Revit потеряна на этапе «{stages.Current}»: {exception.Message}";
            Finish(time.GetUtcNow(), "InfrastructureFailure", reason);
            await FlushAsync();
            return Failure(reason);
        }
        finally
        {
            _cancelled.TryRemove(jobId, out _);
        }

        // Шаг — в журнал и в этап; итог — вернуть вызывающему.
        ResultMessage? Accept(PipeMessage message, DateTimeOffset now)
        {
            switch (message)
            {
                case ProgressMessage progress when progress.JobId == jobId:
                    buffer.Add(new JobLogDto(now, progress.Stage, progress.Text));
                    instance.SetStage(progress.Stage);
                    stages.Enter(progress.Stage, now);
                    instance.Touch();
                    instance.MarkResponsive();
                    return null;
                case ResultMessage result when result.JobId == jobId:
                    return result;
                default:
                    return null;
            }
        }

        async Task<JobCompletionDto> CompleteAsync(ResultMessage result, DateTimeOffset now)
        {
            if (result.RecycleReason is { } recycle) instance.RecycleReason = recycle;
            Finish(now, result.Outcome.ToString(), result.RecycleReason is { } why ? $"Revit будет перезапущен: {why}" : null);
            await FlushAsync();
            var note = dialogs.Count == 0 ? result.Note
                : string.Join("; ", new[] { result.Note, $"агент закрыл окно Revit: {string.Join("; ", dialogs)}" }.Where(part => !string.IsNullOrEmpty(part)));
            return new JobCompletionDto(jobId, result.Outcome, note, result.ResultJson);
        }

        void Sample(DateTimeOffset now)
        {
            watch.Observe(now, instance.Process.Sample());
            instance.PeakWorkingSetMb = watch.PeakWorkingSetMb;
        }

        void OnVerdictChanged(HangVerdict next, DateTimeOffset now)
        {
            switch (next)
            {
                case HangVerdict.WorkingSilently:
                    // Revit занят долгим вызовом API и не может сообщать о ходе — это не зависание, пока процесс работает.
                    instance.MarkResponsive();
                    buffer.Add(new JobLogDto(now, DiagnosticsStage,
                        $"Revit молчит {Format(now - instance.LastActivityAt)} на этапе «{stages.Current}», но процесс работает ({watch.DescribeActivity()}) — ждём"));
                    break;
                case HangVerdict.Quiet:
                    instance.MarkUnresponsive();
                    buffer.Add(new JobLogDto(now, DiagnosticsStage,
                        $"Revit молчит {Format(now - instance.LastActivityAt)} на этапе «{stages.Current}» и не работает ({watch.DescribeActivity()}); " +
                        $"будет убит через {Format(watch.SilenceLimit)} простоя"));
                    break;
                case HangVerdict.Active:
                    instance.MarkResponsive();
                    break;
            }
        }

        string DescribeStart()
        {
            var parts = new List<string>
            {
                $"агент {Environment.MachineName}", $"Revit {instance.Year}", $"слот {instance.Slot}", $"PID {instance.Process.Pid}",
                $"попытка {assignment.Attempt}", $"моделей в этом Revit до задания: {instance.ModelsDone}"
            };
            if (instance.Process.Sample() is { } sample) parts.Add($"память Revit {sample.WorkingSetMb} МБ (private {sample.PrivateMb} МБ)");
            if (memory?.Snapshot() is { } ram) parts.Add($"свободно RAM {ram.AvailableMb} из {ram.TotalMb} МБ");
            if (StartDiagnostics.Session() is { } session) parts.Add(session);
            if (instance.JournalPath is { } journal) parts.Add($"журнал Revit: {journal}");
            return string.Join(", ", parts);
        }

        void Finish(DateTimeOffset now, string outcome, string? reason)
        {
            stages.Close(now);
            var text = $"итог {outcome} за {Format(now - started)}; этапы: {stages.Describe()}; пик памяти Revit {watch.PeakWorkingSetMb} МБ " +
                       $"(private {watch.PeakPrivateMb} МБ)" + (reason is null ? "" : $"; {reason}");
            buffer.Add(new JobLogDto(now, DiagnosticsStage, text));
            logger.LogInformation("Задание {Job} в слоте {Slot} (PID {Pid}): {Summary}", jobId, instance.Slot, instance.Process.Pid, text);
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
            logger.LogWarning("Revit в слоте {Slot} (PID {Pid}), задание {Job}: {Reason} — убиваю", instance.Slot, instance.Process.Pid, jobId, reason);
            instances.Kill(instance.Slot, reason);
            var now = time.GetUtcNow();
            buffer.Add(new JobLogDto(now, "Сбой", $"Revit {reason} — процесс {instance.Process.Pid} убит"));
            Finish(now, "InfrastructureFailure", $"причина: {reason}");
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

    internal static string Format(TimeSpan span) =>
        span >= TimeSpan.FromHours(1) ? $"{(int)span.TotalHours} ч {span.Minutes} мин"
        : span >= TimeSpan.FromMinutes(1) ? span.Seconds == 0 ? $"{(int)span.TotalMinutes} мин" : $"{(int)span.TotalMinutes} мин {span.Seconds} с"
        : $"{span.TotalSeconds:0} с";

    /// <summary>
    ///     Сколько длился каждый этап задания по шагам аддина: открытие, экспорт NWC, сохранение, закрытие, публикация.
    ///     Подписи Revit после двоеточия («Открытие модели: Загрузка элементов») сводятся к своему этапу.
    /// </summary>
    internal sealed class StageTimings(DateTimeOffset started)
    {
        private readonly List<(string Stage, TimeSpan Duration)> _done = [];
        private DateTimeOffset _since = started;

        public string Current { get; private set; } = "Ожидание потока Revit";

        public void Enter(string stage, DateTimeOffset now)
        {
            var name = Normalize(stage);
            if (name == Current) return;
            Close(now);
            Current = name;
        }

        public void Close(DateTimeOffset now)
        {
            if (now > _since) _done.Add((Current, now - _since));
            _since = now;
        }

        public string Describe() =>
            _done.Count == 0 ? "—" : string.Join("; ", _done.GroupBy(s => s.Stage).Select(g => $"{g.Key} {Format(TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)))}"));

        private static string Normalize(string stage)
        {
            var colon = stage.IndexOf(':');
            return (colon > 0 ? stage[..colon] : stage).Trim();
        }
    }
}
