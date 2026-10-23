using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Events;

/// <summary>Пишет описуемые события во вкладку «События системы».</summary>
internal sealed class SystemEventWriter(ToadFlowDb db) : IDomainEventHandler
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent.Describe() is not { } text) return;

        db.SystemEvents.Add(new SystemEvent
        {
            At = domainEvent.At,
            ActorKind = domainEvent.Actor.Kind,
            ActorName = domainEvent.Actor.Name,
            Kind = domainEvent.GetType().Name,
            Text = text
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Хаб живых обновлений для браузеров: сервер шлёт <c>changed(entity, id)</c>, страница перезапрашивает данные.</summary>
[Authorize(Policies.Viewer)]
public sealed class UiHub : Hub;

internal sealed class UiBroadcaster(IHubContext<UiHub> hub) : IDomainEventHandler
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        await hub.Clients.All.SendAsync("changed", domainEvent.UiEntity, domainEvent.UiId, ct);
        if (domainEvent.Describe() is not null) await hub.Clients.All.SendAsync("changed", "events", "", ct);
    }
}

public static class EventEndpoints
{
    public static void MapEvents(this RouteGroupBuilder api) =>
        api.MapGet("/events", async (ToadFlowDb db, long? before, int? take) =>
        {
            var query = db.SystemEvents.AsNoTracking();
            if (before is { } id) query = query.Where(e => e.Id < id);
            return await query.OrderByDescending(e => e.Id).Take(Math.Clamp(take ?? 50, 1, 200)).ToListAsync();
        });

    public static void AddDomainEvents(this IServiceCollection services)
    {
        services.AddSingleton<DomainEventQueue>();
        services.AddSingleton<IDomainEvents>(provider => provider.GetRequiredService<DomainEventQueue>());
        services.AddHostedService<DomainEventDispatcher>();
        services.AddScoped<IDomainEventHandler, SystemEventWriter>();
        services.AddScoped<IDomainEventHandler, UiBroadcaster>();
        services.AddSignalR().AddJsonProtocol(options =>
            options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    }
}
