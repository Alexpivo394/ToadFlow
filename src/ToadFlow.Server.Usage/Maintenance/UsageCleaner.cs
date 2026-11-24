using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Usage.Data;
using ToadFlow.Server.Usage.Settings;

namespace ToadFlow.Server.Usage.Maintenance;

/// <summary>
///     Раз в сутки и при старте удаляет данные аналитики старше срока хранения: случаи и счётчики — по своему времени, машину — если
///     она не выходила на связь дольше срока.
/// </summary>
public sealed class UsageCleaner(IServiceScopeFactory scopes, TimeProvider time, ILogger<UsageCleaner> logger) : BackgroundService
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
                logger.LogError(exception, "Аналитика: не удалось удалить старые данные");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var months = (await scope.ServiceProvider.GetRequiredService<UsageSettingsStore>().GetAsync(ct)).RetentionMonths;
        var cutoff = time.GetUtcNow().AddMonths(-Math.Max(UsageSettings.MinMonths, months));
        var day = DateOnly.FromDateTime(cutoff.UtcDateTime);
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();

        var deleted = await db.Set<UsageOperation>().Where(o => o.StartedAt < cutoff).ExecuteDeleteAsync(ct)
                      + await db.Set<UsageSession>().Where(s => s.SeenAt < cutoff).ExecuteDeleteAsync(ct)
                      + await db.Set<UsageSessionDay>().Where(d => d.Day < day).ExecuteDeleteAsync(ct)
                      + await db.Set<UsageCommandDay>().Where(c => c.Day < day).ExecuteDeleteAsync(ct)
                      + await db.Set<UsageWebDay>().Where(w => w.Day < day).ExecuteDeleteAsync(ct)
                      + await db.Set<UsagePluginDay>().Where(p => p.Day < day).ExecuteDeleteAsync(ct)
                      + await db.Set<UsageMachine>().Where(m => m.LastSeenAt < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) logger.LogInformation("Аналитика: удалено старых записей {Count}", deleted);
    }
}
