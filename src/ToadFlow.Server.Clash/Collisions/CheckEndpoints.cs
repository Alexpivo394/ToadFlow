using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Collisions;

/// <param name="Discipline">Раздел модели: заданный вручную в части или из разбора имени.</param>
public sealed record CheckModelView(Guid ModelId, string Name, string? Discipline);

/// <summary>Утверждённая проверка (часть с утверждённой версией) для раздела «Коллизии → Проверки».</summary>
/// <param name="LastRunAt">Когда закончился последний удачный утверждённый прогон; null — ещё не было.</param>
/// <param name="Open">Открытые коллизии: новые, активные, вернувшиеся, отмеченные исправленными.</param>
public sealed record CheckView(
    Guid PartId,
    string Name,
    string? Object,
    string? Part,
    int Version,
    DateTimeOffset? LastRunAt,
    ApprovedRunSummary? LastRun,
    int Open,
    IReadOnlyList<CheckModelView> Models);

/// <summary>«Проверки»: утверждённые части по проектам. Открыто любой роли — настройка частей остаётся координатору.</summary>
public static class CheckEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashChecks(this RouteGroupBuilder members)
    {
        members.MapGet("/clash/checks", async (ToadFlowDb db, PartModels models, CancellationToken ct) => await ChecksAsync(db, models, null, ct));
        members.MapGet("/clash/checks/{id:guid}", async (Guid id, ToadFlowDb db, PartModels models, CancellationToken ct) =>
            (await ChecksAsync(db, models, id, ct)).SingleOrDefault() is { } check ? Results.Ok(check) : Results.NotFound());
    }

    /// <param name="partId">Одна часть; null — все утверждённые.</param>
    public static async Task<IReadOnlyList<CheckView>> ChecksAsync(ToadFlowDb db, PartModels models, Guid? partId, CancellationToken ct)
    {
        var parts = await db.Set<ClashPart>().AsNoTracking().Where(p => p.ApprovedVersion != null && (partId == null || p.Id == partId))
            .Select(p => new { p.Id, p.Name, p.Object, p.Part, Version = p.ApprovedVersion!.Value, p.ApprovedRunId }).ToListAsync(ct);
        if (parts.Count == 0) return [];
        var ids = parts.Select(p => p.Id).ToList();

        var runIds = parts.Select(p => p.ApprovedRunId).OfType<Guid>().ToList();
        var runs = await db.Set<EngineRun>().AsNoTracking().Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.FinishedAt, r.Summary }).ToDictionaryAsync(r => r.Id, ct);
        var open = await db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.PartId) && CollisionEndpoints.Open.Contains(c.Status))
            .GroupBy(c => c.PartId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var partModels = (await db.Set<ClashPartModel>().AsNoTracking().Where(m => ids.Contains(m.PartId)).ToListAsync(ct)).ToLookup(m => m.PartId);
        var classified = (await models.AllAsync(ct)).ToDictionary(m => m.Model.Id);

        return parts
            .OrderBy(p => p.Object ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p =>
            {
                var run = p.ApprovedRunId is { } r ? runs.GetValueOrDefault(r) : null;
                var own = partModels[p.Id]
                    .Where(m => classified.ContainsKey(m.ModelId))
                    .Select(m => new CheckModelView(m.ModelId, classified[m.ModelId].Model.Name, m.DisciplineOverride ?? classified[m.ModelId].Classification.Discipline))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return new CheckView(p.Id, p.Name, p.Object, p.Part, p.Version, run?.FinishedAt, run?.Summary, open.GetValueOrDefault(p.Id), own);
            })
            .ToList();
    }
}
