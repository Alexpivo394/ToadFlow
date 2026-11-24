using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Usage.Data;
using ToadFlow.Server.Usage.Ingest;
using ToadFlow.Server.Usage.Query;
using ToadFlow.Server.Usage.Service;

namespace ToadFlow.Server.Usage;

/// <summary>
///     Модуль «Аналитика»: как проектировщики работают в Revit (сессии, ожидания, команды, машины — присылает ToadTools) и в
///     сервисе (посещения веба, вызовы плагинов). Заданий и расписаний у модуля нет.
/// </summary>
public sealed class UsageServerModule : IServerModule
{
    public string Name => "usage";

    public string Title => "Аналитика";

    public string Kind => "";

    public IReadOnlyCollection<string> Kinds => [];

    public bool HasJobs => false;

    public string HistoryPath => "/usage";

    public bool UsesOutputFolder => false;

    public void AddServices(IServiceCollection services)
    {
        services.AddScoped<UsageIngest>();
        services.AddScoped<UsageQuery>();
        services.AddSingleton<PluginCallCounter>();
        services.AddHostedService<PluginCallFlusher>();
        // Учёт вызовов плагинов — в начале конвейера сервера; у выключенного модуля фильтра нет вовсе.
        services.AddTransient<IStartupFilter, PluginCallStartupFilter>();
    }

    public void ConfigureModel(ModelBuilder model) => UsageModel.Configure(model);

    /// <summary>Чтение — от Наблюдателя: проектировщик данные отправляет, но не видит.</summary>
    public void MapEndpoints(RouteGroupBuilder api)
    {
        var usage = api.MapGroup("/usage").WithBadRequests();
        usage.MapUsageFilters();
        usage.MapUsageWaits();
        usage.MapUsageMachines();
        usage.MapUsageCoverage();
        usage.MapUsageCommands();
        usage.MapUsageService();
    }

    /// <summary>Приём пачек от ToadTools и отметки переходов веба — любой роли: проектировщик данные отправляет, но не читает.</summary>
    public void MapMemberEndpoints(RouteGroupBuilder members)
    {
        var usage = members.MapGroup("/usage");
        usage.MapUsageIngest();
        usage.MapUsagePageViews();
    }

    public Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["У модуля «Аналитика» нет расписаний"]);

    public Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct) =>
        throw new NotSupportedException("У модуля «Аналитика» нет расписаний и заданий");
}
