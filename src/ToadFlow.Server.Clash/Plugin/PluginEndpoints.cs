namespace ToadFlow.Server.Clash.Plugin;

/// <summary>Маршруты плагина «Коллизии» в Revit. Открыты любой роли — и личным токеном.</summary>
public static class PluginEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashPlugin(this RouteGroupBuilder members)
    {
        var models = members.MapGroup("/clash/models");
        models.MapGet("/resolve", (string? path, string? name, ModelResolver resolver, CancellationToken ct) => resolver.FindAsync(path, name, ct));
    }
}
