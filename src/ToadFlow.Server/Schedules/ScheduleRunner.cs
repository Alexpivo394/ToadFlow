using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Schedules;

/// <summary>
///     Раз в полминуты запускает расписания, чьё время пришло. Если сервер лежал и пропустил несколько запусков,
///     догоняет одним: следующий запуск считается от текущего момента.
/// </summary>
public sealed class ScheduleRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<ScheduleRunner> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await RunDueAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось запустить расписания");
            }
        }
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var runs = scope.ServiceProvider.GetRequiredService<RunFactory>();
        var timeZone = (await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        var now = time.GetUtcNow();

        var due = await db.Schedules.Where(s => s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now).ToListAsync(ct);
        foreach (var schedule in due)
        {
            // Сначала сдвигаем время следующего запуска: если создание запуска упадёт, расписание не будет дёргаться каждые 30 секунд.
            if (schedule.RecurrenceKind == RecurrenceKind.Once)
            {
                schedule.IsEnabled = false;
                schedule.NextRunAt = null;
            }
            else
            {
                schedule.NextRunAt = Recurrence.Next(RecurrenceRule.Of(schedule), now, timeZone);
            }

            await db.SaveChangesAsync(ct);
            await runs.StartAsync(schedule.Id, null, Actor.System);
        }
    }
}

public static class ScheduleSetup
{
    public static void AddSchedules(this IServiceCollection services)
    {
        services.AddSingleton<RunExpansionQueue>();
        services.AddScoped<RunFactory>();
        services.AddScoped<SelectionExpander>();
        services.AddHostedService<RunExpander>();
        services.AddHostedService<ScheduleRunner>();
    }
}
