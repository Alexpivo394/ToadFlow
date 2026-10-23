using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Agents;

/// <summary>Агент без heartbeat дольше <see cref="Silence" /> считается потерявшим связь.</summary>
public sealed class AgentPresenceMonitor(IServiceScopeFactory scopes, AgentRegistry registry, TimeProvider time, ILogger<AgentPresenceMonitor> logger)
    : BackgroundService
{
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await CheckAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось проверить, на связи ли агенты");
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var events = scope.ServiceProvider.GetRequiredService<IDomainEvents>();
        var limit = time.GetUtcNow() - Silence;

        var silent = await db.Agents
            .Where(a => (a.State == AgentState.Online || a.State == AgentState.Draining) && (a.LastSeenAt == null || a.LastSeenAt < limit))
            .ToListAsync(ct);
        foreach (var agent in silent)
        {
            agent.State = AgentState.Offline;
            registry.Abort(agent.Id);
            events.Publish(new AgentWentOffline(AgentHub.AgentActor(agent), agent.Id, agent.MachineName));
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Новые общие настройки сразу уходят всем агентам на связи.</summary>
internal sealed class AgentSettingsBroadcaster(ToadFlowDb db, AgentRegistry registry, ISettingsStore settings) : IDomainEventHandler
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent is not SettingsChanged) return;

        var online = registry.Online();
        foreach (var agent in await db.Agents.Where(a => online.Contains(a.Id)).ToListAsync(ct))
            await AgentEndpoints.PushSettingsAsync(agent, registry, settings);
    }
}
