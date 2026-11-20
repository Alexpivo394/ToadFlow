namespace ToadFlow.Server.Modules;

public static class ModuleExtensions
{
    /// <summary>
    ///     Подключает модуль: его таблицы (через <see cref="IServerModule.ConfigureModel" />) — всегда; службы — только если модуль
    ///     включён настройкой <c>Modules</c>. Маршруты включённых модулей подключает <see cref="ServerSetup.UseToadFlowServer" />.
    /// </summary>
    public static WebApplicationBuilder AddServerModule<TModule>(this WebApplicationBuilder builder) where TModule : class, IServerModule, new()
    {
        var module = new TModule();
        builder.Services.AddSingleton<IServerModule>(module);
        var selected = ModuleSet.ParseSetting(builder.Configuration[ModuleSet.Setting]);
        if (selected is null || selected.Contains(module.Name)) module.AddServices(builder.Services);
        return builder;
    }

    internal static void MapModules(this RouteGroupBuilder api, IServiceProvider services)
    {
        foreach (var module in services.GetRequiredService<ModuleSet>().Enabled) module.MapEndpoints(api);
    }

    internal static void MapModuleMemberEndpoints(this RouteGroupBuilder members, IServiceProvider services)
    {
        foreach (var module in services.GetRequiredService<ModuleSet>().Enabled) module.MapMemberEndpoints(members);
    }

    internal static void MapModuleAgentEndpoints(this RouteGroupBuilder agentApi, IServiceProvider services)
    {
        foreach (var module in services.GetRequiredService<ModuleSet>().Enabled) module.MapAgentEndpoints(agentApi);
    }

    internal static void MapModuleExternalEndpoints(this IEndpointRouteBuilder app, IServiceProvider services)
    {
        foreach (var module in services.GetRequiredService<ModuleSet>().Enabled) module.MapExternalEndpoints(app);
    }
}

/// <summary>
///     Включённые модули по основному виду задания (<see cref="IServerModule.Kind" />) — тому, что выбирают расписания. Виды
///     выключенных модулей и служебные виды (например, <c>loi-catalog</c>) здесь не находятся: расписание с таким видом не
///     создать. Модуль по любому виду задания — <see cref="ModuleSet.NameOfKind" />.
/// </summary>
public sealed class ServerModuleRegistry(ModuleSet modules)
{
    private readonly Dictionary<string, IServerModule> _modules = modules.Enabled.ToDictionary(m => m.Kind);

    public IServerModule? Find(string kind) => _modules.GetValueOrDefault(kind);

    public IServerModule Get(string kind) => Find(kind) ?? throw new InvalidOperationException($"Модуль «{kind}» не подключён");

    public IReadOnlyCollection<string> Kinds => _modules.Keys;
}
