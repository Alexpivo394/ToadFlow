namespace ToadFlow.Server.Clash.Plugin;

/// <summary>Маршруты плагина «Коллизии» в Revit. Открыты любой роли — и личным токеном.</summary>
public static class PluginEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashPlugin(this RouteGroupBuilder members)
    {
        var models = members.MapGroup("/clash/models");
        models.MapGet("/resolve", (string? path, string? name, ModelResolver resolver, CancellationToken ct) => resolver.FindAsync(path, name, ct));
        models.MapGet("/{key}/collisions", async (string key, string? status, string? group, string? pair, string? level, string? q, int? page, int? pageSize,
            ModelCollisions collisions, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? ModelCollisions.DefaultPageSize, 1, Parts.DraftResultEndpoints.MaxPageSize);
            var result = await collisions.PageAsync(key, new ModelCollisionQuery(status, group, pair, level, q), page ?? 1, size, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
