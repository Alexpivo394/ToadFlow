using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Modules;

/// <summary>
///     Модуль — тип задания и всё, что ему нужно на сервере: службы, таблицы, API, проверка настроек расписания
///     и сборка задания для агента. Ядро про конкретные модули ничего не знает.
/// </summary>
public interface IServerModule
{
    string Kind { get; }

    void AddServices(IServiceCollection services);

    void ConfigureModel(ModelBuilder model);

    /// <param name="api">Группа <c>/api</c>; по умолчанию требует роль Наблюдатель.</param>
    void MapEndpoints(RouteGroupBuilder api);

    /// <summary>Ошибки настроек расписания для показа пользователю; пусто — всё в порядке.</summary>
    Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct);

    /// <summary>Payload задания для агента. Строится один раз при создании задания.</summary>
    Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct);

    /// <summary>
    ///     Агент сдал задание (любого вида, не только <see cref="Kind" />) — например, чтобы модуль сохранил его результат у себя.
    ///     Вызывается после сохранения итога; ошибка модуля итог не отменяет.
    /// </summary>
    Task OnJobCompletedAsync(Job job, IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}

/// <param name="RunStartedAt">Начало запуска по часовому поясу из «Общих» — общее для всех заданий запуска.</param>
public sealed record JobBuildContext(Guid ScheduleId, JsonElement ModuleSettings, ExpandedModelDto Model, int RevitYear, string OutputFolder,
    DateTimeOffset RunStartedAt);
