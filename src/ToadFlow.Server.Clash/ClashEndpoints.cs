using Alexpivo394.Revit.Clash.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Uploads;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash;

public sealed record ClashSnapshotView(
    Guid Id,
    DateTimeOffset ExtractedAt,
    int RevitYear,
    string ViewName,
    int Elements,
    long Triangles,
    int Inventory,
    long GeometryBytes,
    long ParametersBytes,
    Guid JobId);

public sealed record ClashModelView(Guid Id, string Name, string ModelPath, ClashSnapshotView? Latest, int SnapshotCount);

/// <summary>Выгрузки моделей для коллизий: модели с последним слепком, история слепков, каталог параметров, настройки хранения.</summary>
public static class ClashEndpoints
{
    public const string ModelsEntity = "clash-models";

    public static void MapClash(this RouteGroupBuilder api)
    {
        var clash = api.MapGroup("/clash");
        clash.MapGet("/models", ModelsAsync);
        clash.MapGet("/models/{id:guid}/snapshots", SnapshotsAsync);
        clash.MapGet("/catalog", CatalogAsync);
        clash.MapGet("/settings", (ClashSettingsStore store, CancellationToken ct) => store.StorageAsync(ct));
        clash.MapPut("/settings", SaveSettingsAsync).RequireAuthorization(Policies.Admin);
    }

    private static async Task<ClashModelView[]> ModelsAsync(ToadFlowDb db, CancellationToken ct)
    {
        var models = await db.Set<ClashModel>().AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);
        var snapshots = await Views(db.Set<ClashSnapshot>().AsNoTracking()).ToListAsync(ct);
        var byModel = snapshots.ToLookup(s => s.ModelId, s => s.View);
        return models.Select(m =>
        {
            var own = byModel[m.Id].OrderByDescending(s => s.ExtractedAt).ToList();
            return new ClashModelView(m.Id, m.Name, m.ModelPath, own.FirstOrDefault(), own.Count);
        }).ToArray();
    }

    private static async Task<IResult> SnapshotsAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        if (!await db.Set<ClashModel>().AnyAsync(m => m.Id == id, ct)) return Results.NotFound();
        var snapshots = await Views(db.Set<ClashSnapshot>().AsNoTracking().Where(s => s.ModelId == id)).ToListAsync(ct);
        return Results.Ok(snapshots.Select(s => s.View).OrderByDescending(s => s.ExtractedAt).ToArray());
    }

    /// <param name="modelId">Модели, чей каталог нужен; пусто — все модели.</param>
    private static async Task<ModelCatalog> CatalogAsync([FromQuery] Guid[]? modelId, ToadFlowDb db, CancellationToken ct)
    {
        var snapshots = db.Set<ClashSnapshot>().AsNoTracking();
        if (modelId is { Length: > 0 }) snapshots = snapshots.Where(s => modelId.Contains(s.ModelId));
        var latest = await snapshots
            .GroupBy(s => s.ModelId)
            .Select(g => g.OrderByDescending(s => s.ExtractedAt).ThenByDescending(s => s.CreatedAt).Select(s => s.Id).First())
            .ToListAsync(ct);
        var catalogs = await db.Set<ClashSnapshot>().AsNoTracking().Where(s => latest.Contains(s.Id)).Select(s => s.Catalog).ToListAsync(ct);
        return ModelCatalog.Merge(catalogs);
    }

    private static async Task<IResult> SaveSettingsAsync(ClashStorageSettings settings, ClashSettingsStore store, ToadFlowDb db, SnapshotPublisher publisher,
        HttpContext http, IDomainEvents events, CancellationToken ct)
    {
        if (settings.Validate() is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors) });

        await store.SaveStorageAsync(settings, ct);
        foreach (var model in await db.Set<ClashModel>().AsNoTracking().Select(m => m.Id).ToListAsync(ct))
            await publisher.PruneAsync(model, settings.KeepSnapshots, ct);
        events.Publish(new SettingsChanged(http.User.Actor(), $"Коллизии: хранить последних выгрузок каждой модели — {settings.KeepSnapshots}"));
        return Results.Ok(settings);
    }

    private static IQueryable<SnapshotRow> Views(IQueryable<ClashSnapshot> snapshots) => snapshots.Select(s => new SnapshotRow(s.ModelId,
        new ClashSnapshotView(s.Id, s.ExtractedAt, s.RevitYear, s.ViewName, s.Elements, s.Triangles, s.Inventory, s.GeometryBytes, s.ParametersBytes, s.JobId)));

    private sealed record SnapshotRow(Guid ModelId, ClashSnapshotView View);
}
