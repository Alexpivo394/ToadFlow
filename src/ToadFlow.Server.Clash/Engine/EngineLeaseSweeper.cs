using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>Каждые 30 с: прогон, работник которого замолчал дольше аренды, возвращается в очередь или завершается ошибкой.</summary>
public sealed class EngineLeaseSweeper(IServiceScopeFactory scopes, TimeProvider time, ILogger<EngineLeaseSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось проверить аренду прогонов движка");
            }
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var hits = scope.ServiceProvider.GetRequiredService<DraftHits>();
        var now = time.GetUtcNow();
        var running = nameof(EngineRunState.Running);
        // Сверка последней части держит прогон под блокировкой и аренду не продлевает: такой прогон пропускаем — иначе после её
        // коммита уборщик вернул бы в очередь уже закрытый прогон, и его сверили бы второй раз.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var expired = await db.Set<EngineRun>()
            .FromSqlInterpolated($"SELECT * FROM clash_engine_runs WHERE state = {running} AND lease_until < {now} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
        foreach (var run in expired)
        {
            await hits.DiscardAsync(run.Id, ct);
            EngineEndpoints.Release(run, retry: true, "движок перестал отвечать во время прогона", now);
            scope.ServiceProvider.GetRequiredService<IDomainEvents>().Publish(new ClashDraftChanged(Actor.System, run.PartId));
            logger.LogWarning("Прогон движка {Run}: аренда истекла, попытка {Attempt} из {Max}", run.Id, run.Attempt, run.MaxAttempts);
        }

        if (expired.Count > 0) await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
