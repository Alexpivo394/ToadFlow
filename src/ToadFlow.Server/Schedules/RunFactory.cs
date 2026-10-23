using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Queue;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Schedules;

/// <summary>
///     Запуск расписания в два шага: сразу создаётся запуск «разворачивается», а список моделей и задания строятся
///     в фоне (<see cref="RunExpander" />) — агент может отвечать до двух минут.
/// </summary>
public sealed class RunFactory(ToadFlowDb db, RunExpansionQueue queue, IDomainEvents events, TimeProvider time)
{
    public async Task<Run> StartAsync(Guid scheduleId, Guid? userId, Actor actor)
    {
        var schedule = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == scheduleId);
        var run = new Run
        {
            Id = Guid.CreateVersion7(),
            ScheduleId = scheduleId,
            Name = schedule.Name,
            TriggeredByUserId = userId,
            State = RunState.Expanding,
            CreatedAt = time.GetUtcNow()
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        events.Publish(new RunChanged(actor, run.Id));
        queue.Enqueue(run.Id);
        return run;
    }
}

public sealed class RunExpansionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid runId) => _channel.Writer.TryWrite(runId);
}

/// <summary>Строит задания запусков в состоянии «разворачивается». После перезапуска сервера подхватывает недостроенные.</summary>
public sealed class RunExpander(RunExpansionQueue queue, IServiceScopeFactory scopes, ILogger<RunExpander> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
            foreach (var id in await db.Runs.Where(r => r.State == RunState.Expanding).Select(r => r.Id).ToListAsync(ct)) queue.Enqueue(id);
        }

        await foreach (var runId in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ExpandAsync(scope.ServiceProvider, runId, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось построить задания запуска {Run}", runId);
                await FailAsync(runId, exception.Message, ct);
            }
        }
    }

    /// <summary>Непредвиденная ошибка не должна оставить запуск навсегда «разворачивается»: он завершается с объяснением.</summary>
    private async Task FailAsync(Guid runId, string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
            var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId && r.State == RunState.Expanding, ct);
            if (run is null) return;

            await CompleteEmptyAsync(db, scope.ServiceProvider.GetRequiredService<IDomainEvents>(), scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                run, $"Не удалось построить задания: {reason}", ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось завершить запуск {Run} после ошибки", runId);
        }
    }

    private static async Task ExpandAsync(IServiceProvider services, Guid runId, CancellationToken ct)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var time = services.GetRequiredService<TimeProvider>();
        var events = services.GetRequiredService<IDomainEvents>();
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId && r.State == RunState.Expanding, ct);
        if (run is null) return;

        var schedule = run.ScheduleId is { } scheduleId ? await db.Schedules.SingleOrDefaultAsync(s => s.Id == scheduleId, ct) : null;
        var source = schedule is null ? null : await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == schedule.SourceId, ct);
        if (schedule is null || source is null)
        {
            await CompleteEmptyAsync(db, events, time, run, "Расписание или источник удалены", ct);
            return;
        }

        IReadOnlyList<ExpandedModelDto> models;
        try
        {
            (models, run.Warning) = await services.GetRequiredService<SelectionExpander>().ExpandAsync(schedule, source, ct);
        }
        catch (ExpansionFailedException failure)
        {
            await CompleteEmptyAsync(db, events, time, run, failure.Message, ct);
            return;
        }

        models = models.Where(m => m.Rvt || m.Nwc).ToList();
        if (models.Count == 0)
        {
            await CompleteEmptyAsync(db, events, time, run, AppendWarning(run.Warning, "нет моделей для выгрузки"), ct);
            return;
        }

        var duplicates = models.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            run.Warning = AppendWarning(run.Warning, $"модели с одинаковыми именами перезапишут друг друга: {string.Join(", ", duplicates)}");

        var settings = await services.GetRequiredService<ISettingsStore>().GetAsync(ct);
        var module = services.GetRequiredService<ServerModuleRegistry>().Get(schedule.ModuleKind);
        var outputFolder = OutputFolderTemplate.Render(schedule.OutputFolderTemplate, run.CreatedAt, settings.TimeZone, schedule.Name);
        using var moduleSettings = JsonDocument.Parse(schedule.ModuleSettingsJson);
        var now = time.GetUtcNow();

        foreach (var model in models)
        {
            var year = model.RevitYear ?? source.RevitYear;
            var job = new Job
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                ModuleKind = schedule.ModuleKind,
                ModelName = model.Name,
                ModelPath = model.ModelPath,
                RevitYear = year,
                RequiresNwc = model.Nwc,
                Status = JobStatus.Queued,
                MaxAttempts = 1 + schedule.MaxRetries,
                Priority = schedule.Priority,
                TimeoutMinutes = schedule.ModelTimeoutMinutes,
                CreatedAt = now
            };

            if (year is { } knownYear)
            {
                job.PayloadJson = await module.BuildPayloadAsync(
                    new JobBuildContext(schedule.Id, moduleSettings.RootElement, model, knownYear, outputFolder), services, ct);
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.Note = "Не удалось определить версию Revit, в которой сохранена модель";
                job.FinishedAt = now;
            }

            db.Jobs.Add(job);
        }

        run.State = RunState.Queued;
        await db.SaveChangesAsync(ct);
        events.Publish(new RunChanged(Actor.System, run.Id));
        await services.GetRequiredService<RunProgress>().UpdateAsync(run.Id);
    }

    private static async Task CompleteEmptyAsync(ToadFlowDb db, IDomainEvents events, TimeProvider time, Run run, string warning, CancellationToken ct)
    {
        run.State = RunState.Completed;
        run.Warning = AppendWarning(run.Warning, warning);
        run.StartedAt ??= time.GetUtcNow();
        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        events.Publish(new RunFinished(Actor.System, run.Id, run.Name, 0, 0, 0, 0));
    }

    private static string AppendWarning(string? existing, string warning) =>
        string.IsNullOrEmpty(existing) ? Capitalize(warning) : $"{existing}; {warning}";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
