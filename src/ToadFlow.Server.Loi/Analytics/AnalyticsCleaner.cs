using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Loi.Analytics;

/// <summary>Раз в сутки (и при старте) удаляет проверки старше срока хранения аналитики; срок 0 — хранить всегда.</summary>
public sealed class AnalyticsCleaner(IServiceScopeFactory scopes, TimeProvider time, ILogger<AnalyticsCleaner> logger) : BackgroundService
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
                logger.LogError(exception, "Не удалось удалить старые проверки LOI");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var years = (await scope.ServiceProvider.GetRequiredService<LoiSettingsStore>().AnalyticsAsync(ct)).RetentionYears;
        if (years <= 0) return;

        var cutoff = time.GetUtcNow().AddYears(-years);
        var deleted = await scope.ServiceProvider.GetRequiredService<ToadFlowDb>().Set<LoiCheck>().Where(c => c.CheckedAt < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) logger.LogInformation("Удалено старых проверок LOI: {Count}", deleted);
    }
}
