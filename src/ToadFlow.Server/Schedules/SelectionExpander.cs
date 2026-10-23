using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Schedules;

/// <summary>Не удалось получить список моделей: агентов нет в сети и прошлого списка тоже нет.</summary>
public sealed class ExpansionFailedException(string message) : Exception(message);

/// <summary>
///     Превращает отметки расписания в список моделей с версиями. Сервер на Linux сетевые папки Windows не видит,
///     поэтому список строит агент; если ни один не ответил — берётся последний известный список.
/// </summary>
public sealed class SelectionExpander(ToadFlowDb db, AgentRegistry registry, TimeProvider time, ILogger<SelectionExpander> logger)
{
    public static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(2);

    public async Task<(IReadOnlyList<ExpandedModelDto> Models, string? Warning)> ExpandAsync(Schedule schedule, Source source, CancellationToken ct)
    {
        var online = registry.Online();
        var candidates = await db.Agents.AsNoTracking().Where(a => online.Contains(a.Id) && a.State != AgentState.Disabled)
            .Select(a => new { a.Id, a.MachineName }).ToListAsync(ct);

        foreach (var agent in candidates)
        {
            if (registry.Client(agent.Id) is not { } client) continue;
            try
            {
                var models = await client.ExpandSelection(source.ToDto(), schedule.Selection).WaitAsync(AgentTimeout, ct);
                schedule.LastKnownModels = models.ToList();
                schedule.LastKnownModelsAt = time.GetUtcNow();
                return (models, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Агент {Agent} не смог построить список моделей «{Schedule}»", agent.MachineName, schedule.Name);
            }
        }

        if (schedule.LastKnownModels is { } cached)
            return (cached, $"Агентов с доступом к источнику нет в сети — список моделей от {schedule.LastKnownModelsAt:dd.MM.yyyy HH:mm} UTC");

        throw new ExpansionFailedException("Не удалось получить список моделей: агентов с доступом к источнику нет в сети");
    }
}
