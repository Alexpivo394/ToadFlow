using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Queue;

/// <summary>Задания, чья аренда истекла (агент пропал посреди выгрузки), считаются сбоем инфраструктуры.</summary>
public sealed class LeaseSweeper(IServiceScopeFactory scopes, TimeProvider time, ILogger<LeaseSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Сервер простоял, пока агенты доделывали задания: до их переподключения аренды не считаются истёкшими.
            await ExtendLeasesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Не удалось продлить аренды заданий после запуска");
        }

        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось проверить аренды заданий");
            }
        }
    }

    public async Task ExtendLeasesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobQueue>().ExtendLeasesAfterStartAsync(ct);
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var now = time.GetUtcNow();

        var expired = await db.Jobs.Where(j => j.Status == JobStatus.Running && j.LeaseUntil < now).ToListAsync(ct);
        foreach (var job in expired) await queue.FailInfrastructureAsync(job, "агент потерял связь во время задания", Actor.System, now);
    }
}

public static class QueueSetup
{
    public static void AddQueue(this IServiceCollection services)
    {
        services.AddScoped<RunProgress>();
        services.AddScoped<JobQueue>();
        services.AddScoped<Agents.IAgentJobs>(provider => provider.GetRequiredService<JobQueue>());
        services.AddHostedService<LeaseSweeper>();
    }
}

/// <summary>Сроки аренды заданий.</summary>
public static class LeaseOptions
{
    /// <summary>На сколько продлевается аренда каждым heartbeat.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Аренда выполняющихся заданий после запуска сервера: срок аренды плюс три минуты на переподключение агентов
    ///     (они повторяют подключение раз в 30 с).
    /// </summary>
    public static readonly TimeSpan StartupGrace = Lease + TimeSpan.FromMinutes(3);
}
