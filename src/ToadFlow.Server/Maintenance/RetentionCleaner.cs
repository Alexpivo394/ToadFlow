using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Maintenance;

/// <summary>
///     Раз в сутки (и при старте) удаляет законченные запуски старше срока хранения — вместе с заданиями и логами —
///     и старые «События системы». Незаконченные запуски не трогает.
/// </summary>
public sealed class RetentionCleaner(IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionCleaner> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        do
        {
            try
            {
                await CleanAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось удалить старые логи");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var days = (await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(ct)).LogRetentionDays;
        var cutoff = time.GetUtcNow().AddDays(-days);

        var runs = await db.Runs.Where(r => (r.State == RunState.Completed || r.State == RunState.Cancelled) && r.FinishedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        var events = await db.SystemEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync(ct);
        if (runs + events > 0) logger.LogInformation("Удалено старых запусков: {Runs}, событий: {Events}", runs, events);
    }
}
