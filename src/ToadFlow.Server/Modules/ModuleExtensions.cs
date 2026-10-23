namespace ToadFlow.Server.Modules;

public static class ModuleExtensions
{
    /// <summary>Подключает модуль: его службы, таблицы (через <see cref="IServerModule.ConfigureModel" />) и API.</summary>
    public static WebApplicationBuilder AddServerModule<TModule>(this WebApplicationBuilder builder) where TModule : class, IServerModule, new()
    {
        var module = new TModule();
        builder.Services.AddSingleton<IServerModule>(module);
        module.AddServices(builder.Services);
        return builder;
    }

    internal static void MapModules(this RouteGroupBuilder api, IServiceProvider services)
    {
        foreach (var module in services.GetServices<IServerModule>()) module.MapEndpoints(api);
    }
}

/// <summary>Модули по виду задания.</summary>
public sealed class ServerModuleRegistry(IEnumerable<IServerModule> modules)
{
    private readonly Dictionary<string, IServerModule> _modules = modules.ToDictionary(m => m.Kind);

    public IServerModule? Find(string kind) => _modules.GetValueOrDefault(kind);

    public IServerModule Get(string kind) => Find(kind) ?? throw new InvalidOperationException($"Модуль «{kind}» не подключён");

    public IReadOnlyCollection<string> Kinds => _modules.Keys;
}
