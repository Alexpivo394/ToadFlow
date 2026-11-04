using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>Прогон утверждённой части, который поставил запуск выгрузки для коллизий.</summary>
/// <param name="New">Новых коллизий по итогу сверки; null — прогон ещё не закончен или не удался.</param>
public sealed record TriggeredRunView(Guid RunId, Guid PartId, string PartName, EngineRunState State, int? New, string? Error);

/// <summary>
///     «История»: какие прогоны частей поставил запуск выгрузки (<see cref="EngineRun.TriggerRunId" />). Отдельный адрес модуля, а не
///     поле ответа запуска: ядро сервера о движке коллизий не знает.
/// </summary>
public static class TriggeredRuns
{
    public static void MapClashTriggeredRuns(this RouteGroupBuilder api) =>
        api.MapGet("/clash/extract-runs/{runId:guid}/engine-runs", ListAsync);

    private static async Task<IReadOnlyList<TriggeredRunView>> ListAsync(Guid runId, ToadFlowDb db, CancellationToken ct)
    {
        var runs = await db.Set<EngineRun>().AsNoTracking().Where(r => r.TriggerRunId == runId && r.Kind == EngineRunKind.Approved)
            .Join(db.Set<ClashPart>().AsNoTracking(), r => r.PartId, p => p.Id, (r, p) => new { Run = r, p.Name })
            .ToListAsync(ct);
        return runs.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new TriggeredRunView(r.Run.Id, r.Run.PartId, r.Name, r.Run.State,
                r.Run.State == EngineRunState.Succeeded ? r.Run.Summary?.New : null, r.Run.Error))
            .ToList();
    }
}
