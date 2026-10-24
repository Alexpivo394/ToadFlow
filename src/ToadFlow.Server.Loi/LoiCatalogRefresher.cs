using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Loi;

/// <summary>Не из чего снять каталог: нет агента с Revit нужной версии.</summary>
public sealed class CatalogUnavailableException(string message) : Exception(message);

/// <summary>
///     Обновление каталога — обычный запуск с одним заданием: его видно в истории с логом, его можно отменить и повторить,
///     а выполняет его свободный Revit, как любое задание. Итог сохраняет <see cref="LoiServerModule.OnJobCompletedAsync" />.
/// </summary>
public sealed class LoiCatalogRefresher(ToadFlowDb db, IDomainEvents events, TimeProvider time)
{
    private const int TimeoutMinutes = 15;

    /// <param name="year">Версия Revit; null — новейшая из тех, что есть у агентов.</param>
    /// <exception cref="CatalogUnavailableException">Нет агента с Revit этой версии.</exception>
    public async Task<Run> StartAsync(int? year, Guid userId, Actor actor)
    {
        var revits = await YearsAsync(db);
        var target = year ?? (revits.Count > 0 ? revits.Max() : null as int?);
        if (target is not { } revitYear) throw new CatalogUnavailableException("Нет агентов 1.1 или новее с Revit — каталог снимать негде");
        if (!revits.Contains(revitYear)) throw new CatalogUnavailableException($"Нет агента 1.1 или новее с Revit {revitYear}");

        var now = time.GetUtcNow();
        var run = new Run
        {
            Id = Guid.CreateVersion7(),
            Name = $"Каталог категорий и параметров Revit {revitYear}",
            TriggeredByUserId = userId,
            State = RunState.Queued,
            CreatedAt = now
        };
        db.Runs.Add(run);
        db.Jobs.Add(new Job
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            ModuleKind = ModuleKinds.LoiCatalog,
            ModelName = $"Каталог Revit {revitYear}",
            RevitYear = revitYear,
            PayloadJson = JsonSerializer.Serialize(new LoiCatalogPayload(revitYear), ToadFlowDb.Json),
            Status = JobStatus.Queued,
            MaxAttempts = 2,
            Priority = 1, // человек ждёт каталог в редакторе
            TimeoutMinutes = TimeoutMinutes,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        events.Publish(new RunChanged(actor, run.Id));
        return run;
    }

    /// <summary>Версии Revit, в которых есть кому снять каталог: агенты, умеющие задания LOI, с лимитом экземпляров больше нуля.</summary>
    public static async Task<List<int>> YearsAsync(ToadFlowDb db) =>
        (await db.Agents.AsNoTracking().Where(a => a.State != AgentState.Disabled).ToListAsync())
        .Where(a => AgentCapabilities.KnowsModuleKind(a.AgentVersion))
        .SelectMany(a => a.Limits.Where(l => l.MaxInstances > 0).Select(l => l.Year))
        .Distinct()
        .OrderByDescending(y => y)
        .ToList();
}
