using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Usage.Data;
using ToadFlow.Server.Usage.Ingest;

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
    }

    public void ConfigureModel(ModelBuilder model) => UsageModel.Configure(model);

    public void MapEndpoints(RouteGroupBuilder api)
    {
    }

    /// <summary>Приём пачек от ToadTools — любой роли: проектировщик данные отправляет, но не читает.</summary>
    public void MapMemberEndpoints(RouteGroupBuilder members) => members.MapGroup("/usage").MapUsageIngest();

    public Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["У модуля «Аналитика» нет расписаний"]);

    public Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct) =>
        throw new NotSupportedException("У модуля «Аналитика» нет расписаний и заданий");
}
