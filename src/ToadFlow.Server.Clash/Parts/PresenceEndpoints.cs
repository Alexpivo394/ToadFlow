using System.Collections.Concurrent;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Кто открыл часть: вкладка и, если открыт, набор.</summary>
public sealed record PresenceView(Guid UserId, string DisplayName, string Tab, Guid? SetId);

public sealed record PresenceRequest(string Tab, Guid? SetId);

/// <summary>Кто-то открыл часть, ушёл из неё или сменил вкладку — значки присутствия пора обновить. В журнал не пишется.</summary>
public sealed record ClashPresenceChanged(Actor Actor, Guid PartId) : DomainEvent(Actor)
{
    public override string UiEntity => "clash-presence";
    public override string UiId => PartId.ToString();
}

/// <summary>
///     Присутствие в частях — в памяти сервера: страница отмечается раз в 15 с, отметка живёт 40 с. Ушедшие пропадают при
///     следующей отметке любого пользователя или явным выходом со страницы.
/// </summary>
public sealed class PartPresence(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(40);

    private readonly ConcurrentDictionary<(Guid Part, Guid User), (PresenceView View, DateTimeOffset Seen)> _present = new();

    /// <returns>Изменился ли список присутствующих в части: новый человек, другая вкладка или набор, кто-то ушёл.</returns>
    public bool Touch(Guid partId, PresenceView view)
    {
        var changed = Expire();
        var key = (partId, view.UserId);
        changed |= !_present.TryGetValue(key, out var old) || old.View != view;
        _present[key] = (view, time.GetUtcNow());
        return changed;
    }

    public bool Leave(Guid partId, Guid userId) => _present.TryRemove((partId, userId), out _) | Expire();

    public IReadOnlyList<PresenceView> Of(Guid partId)
    {
        Expire();
        return _present.Where(p => p.Key.Part == partId).Select(p => p.Value.View).OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private bool Expire()
    {
        var now = time.GetUtcNow();
        var removed = false;
        foreach (var (key, value) in _present)
            if (now - value.Seen > Lifetime) removed |= _present.TryRemove(key, out _);
        return removed;
    }
}

/// <summary>Присутствие в части: видно и наблюдателю, отметка ничего не меняет в данных.</summary>
public static class PresenceEndpoints
{
    public static void MapClashPresence(this RouteGroupBuilder api)
    {
        var presence = api.MapGroup("/clash/parts/{id:guid}/presence");
        presence.MapGet("", (Guid id, PartPresence present) => present.Of(id));
        presence.MapPost("", (Guid id, PresenceRequest request, HttpContext http, PartPresence present, IDomainEvents events) =>
        {
            var view = new PresenceView(http.User.UserId(), http.User.Actor().Name, string.IsNullOrWhiteSpace(request.Tab) ? "?" : request.Tab.Trim(), request.SetId);
            if (present.Touch(id, view)) events.Publish(new ClashPresenceChanged(http.User.Actor(), id));
            return Results.NoContent();
        });
        presence.MapDelete("", (Guid id, HttpContext http, PartPresence present, IDomainEvents events) =>
        {
            if (present.Leave(id, http.User.UserId())) events.Publish(new ClashPresenceChanged(http.User.Actor(), id));
            return Results.NoContent();
        });
    }
}
