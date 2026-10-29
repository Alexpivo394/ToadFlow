using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>«Прогнать» черновик части: прогон в очередь движка со снимком конфигурации и последних слепков моделей.</summary>
public static class DraftRunEndpoints
{
    public static void MapClashDraftRuns(this RouteGroupBuilder api)
    {
        var run = api.MapGroup("/clash/parts/{id:guid}/draft/run");
        run.MapGet("", LastAsync);

        var write = run.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("", StartAsync);
        write.MapPost("/cancel", CancelAsync);
    }

    private static async Task<IResult> LastAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var run = await db.Set<EngineRun>().AsNoTracking().Where(r => r.PartId == id && r.Kind == EngineRunKind.Draft)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        return run is null ? Results.NoContent() : Results.Ok(DraftRunSummary.Of(run));
    }

    private static async Task<IResult> StartAsync(Guid id, ToadFlowDb db, PartModels models, HttpContext http, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();

        var errors = await DraftRunChecks.ProblemsAsync(id, ConfigurationJson.Deserialize(part.DraftJson), models, ct);
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("; ", errors), errors });
        var partModels = await models.OfPartAsync(id, ct);

        // Ещё не начатый прогон этой части уже не нужен: прогон по нынешнему черновику его заменяет.
        await db.Set<EngineRun>().Where(r => r.PartId == id && r.Kind == EngineRunKind.Draft && r.State == EngineRunState.Queued)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.State, EngineRunState.Cancelled).SetProperty(x => x.FinishedAt, time.GetUtcNow()), ct);

        var run = new EngineRun
        {
            Id = Guid.CreateVersion7(),
            PartId = id,
            State = EngineRunState.Queued,
            ConfigurationJson = part.DraftJson,
            Models = partModels.Select(m => new EngineRunModel(m.ModelId, m.Name, m.Discipline!, m.Latest!.Id, m.Latest.GeometryBytes, m.Latest.ParametersBytes))
                .ToList(),
            CreatedByUserId = http.User.UserId(),
            CreatedAt = time.GetUtcNow()
        };
        db.Set<EngineRun>().Add(run);
        await db.SaveChangesAsync(ct);
        events.Publish(new ClashDraftChanged(http.User.Actor(), id));
        return Results.Ok(DraftRunSummary.Of(run));
    }

    private static async Task<IResult> CancelAsync(Guid id, ToadFlowDb db, DraftHits hits, TimeProvider time, HttpContext http, IDomainEvents events,
        CancellationToken ct)
    {
        var active = await db.Set<EngineRun>()
            .Where(r => r.PartId == id && r.Kind == EngineRunKind.Draft && (r.State == EngineRunState.Queued || r.State == EngineRunState.Running))
            .ToListAsync(ct);
        foreach (var run in active)
        {
            await hits.DiscardAsync(run.Id, ct);
            run.State = EngineRunState.Cancelled;
            run.LeaseUntil = null;
            run.FinishedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        if (active.Count > 0) events.Publish(new ClashDraftChanged(http.User.Actor(), id));
        return active.Count == 0 ? Results.NotFound(new { error = "Прогона нет" }) : Results.Ok(DraftRunSummary.Of(active[^1]));
    }
}
