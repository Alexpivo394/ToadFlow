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
    /// <summary>Имя модуля в настройке <c>Modules</c>: <c>export</c>, <c>loi</c>, <c>clash</c>.</summary>
    string Name { get; }

    /// <summary>Название для людей: «Выгрузка», «Проверка LOI», «Коллизии».</summary>
    string Title { get; }

    /// <summary>Основной вид задания модуля: его выбирают расписания.</summary>
    string Kind { get; }

    /// <summary>Все виды заданий модуля, включая <see cref="Kind" />; соответствие «модуль → виды» задаётся только здесь.</summary>
    IReadOnlyCollection<string> Kinds => [Kind];

    /// <summary>Адрес пространства модуля в вебе — туда ведёт плитка «Обзора».</summary>
    string WebPath => $"/{Name}";

    /// <summary>Адрес истории модуля в вебе — туда ведёт строка «модели с ошибкой за сутки».</summary>
    string HistoryPath => $"/{Name}/history";

    /// <summary>С какого числа моделей с ошибкой за сутки строка попадает в «Требует внимания»; меньше — норма модуля.</summary>
    int FailedJobsAlarm => 1;

    /// <summary>
    ///     У модуля есть задания фермы. false — модуль только хранит и показывает данные: его нет среди видов расписаний, в
    ///     очереди и в строке «модели с ошибкой за сутки», а <see cref="Kind" /> и <see cref="Kinds" /> пусты.
    /// </summary>
    bool HasJobs => true;

    /// <summary>Задания пишут результат в папку результатов расписания; false — папка не нужна, её можно не указывать.</summary>
    bool UsesOutputFolder => true;

    void AddServices(IServiceCollection services);

    void ConfigureModel(ModelBuilder model);

    /// <param name="api">Группа <c>/api</c>; по умолчанию требует роль Наблюдатель.</param>
    void MapEndpoints(RouteGroupBuilder api);

    /// <param name="members">
    ///     Группа <c>/api</c> для любой роли, включая проектировщика (политика <c>Member</c>). Сюда — только то, что
    ///     проектировщику положено видеть; правку закрывать своей политикой.
    /// </param>
    void MapMemberEndpoints(RouteGroupBuilder members)
    {
    }

    /// <param name="agentApi">Группа <c>/api/agent</c>: вызовы агентов по их ключу (политика <c>Agent</c>), без пользовательских ролей.</param>
    void MapAgentEndpoints(RouteGroupBuilder agentApi)
    {
    }

    /// <summary>Маршруты со своей схемой входа вне групп <c>/api</c> и <c>/api/agent</c> — например, для своего рабочего процесса модуля.</summary>
    void MapExternalEndpoints(IEndpointRouteBuilder app)
    {
    }

    /// <summary>Ошибки настроек расписания для показа пользователю; пусто — всё в порядке.</summary>
    Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct);

    /// <summary>Payload задания для агента. Строится один раз при создании задания.</summary>
    /// <exception cref="ModelSkippedException">Модель этой задаче не подлежит — задание не создаётся, причина попадает в предупреждение запуска.</exception>
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

/// <summary>Модель не для этой задачи (например, проверке LOI не нашлось профиля): задание не создаётся.</summary>
/// <param name="reason">Почему — со строчной буквы, попадает в предупреждение запуска после имени модели.</param>
public sealed class ModelSkippedException(string reason) : Exception(reason);
