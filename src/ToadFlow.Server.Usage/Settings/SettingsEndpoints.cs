using ToadFlow.Server.Auth;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Usage.Settings;

public static class SettingsEndpoints
{
    public const string Entity = "usage-settings";

    /// <summary><c>GET /api/usage/settings</c> — от Наблюдателя; <c>PUT</c> — только Администратор, изменение — в журнал действий.</summary>
    public static void MapUsageSettings(this RouteGroupBuilder usage)
    {
        usage.MapGet("/settings", (UsageSettingsStore store, CancellationToken ct) => store.GetAsync(ct));
        usage.MapPut("/settings", async (UsageSettings settings, UsageSettingsStore store, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (settings.Validate() is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors) });
            await store.SaveAsync(settings, ct);
            events.Publish(new CatalogChanged(http.User.Actor(), Entity, Guid.Empty, $"Аналитика: хранить данные {settings.RetentionMonths} мес."));
            return Results.Ok(settings);
        }).RequireAuthorization(Policies.Admin);
    }
}
