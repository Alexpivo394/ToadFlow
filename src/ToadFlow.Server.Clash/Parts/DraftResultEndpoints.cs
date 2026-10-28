using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Parts;

/// <param name="Enabled">Клетка матрицы прогона для пары включена; false — клетки нет или выключена (так быть не должно).</param>
public sealed record PairCount(string DisciplinePair, int Count, bool Enabled, double ToleranceMm);

/// <summary>Пара наборов: первый набор каждой стороны коллизии.</summary>
public sealed record SetPairCount(Guid SetA, string SetAName, Guid SetB, string SetBName, int Count);

public sealed record DraftSummary(int Total, IReadOnlyList<PairCount> ByDisciplines, IReadOnlyList<SetPairCount> BySets);

public sealed record DraftHitView(
    Guid RunId,
    int Number,
    float DepthMm,
    double X,
    double Y,
    double Z,
    string ModelA,
    string ModelNameA,
    string UniqueIdA,
    long ElementIdA,
    string DisciplineA,
    IReadOnlyList<string> SetNamesA,
    string CategoryA,
    string? FamilyA,
    string? TypeA,
    string? LevelA,
    string ModelB,
    string ModelNameB,
    string UniqueIdB,
    long ElementIdB,
    string DisciplineB,
    IReadOnlyList<string> SetNamesB,
    string CategoryB,
    string? FamilyB,
    string? TypeB,
    string? LevelB);

public sealed record HitPage(int Total, IReadOnlyList<DraftHitView> Items);

/// <summary>Результат черновика — коллизии последнего удачного прогона части: сводка в базе и список страницами.</summary>
public static class DraftResultEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static void MapClashDraftResults(this RouteGroupBuilder api)
    {
        var draft = api.MapGroup("/clash/parts/{id:guid}/draft");
        draft.MapGet("/summary", SummaryAsync);
        draft.MapGet("/hits", HitsAsync);
    }

    private static async Task<IResult> SummaryAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        if (await ResultRunAsync(id, db, ct) is not { } run) return Results.Ok(new DraftSummary(0, [], []));

        var hits = db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id);
        var config = ConfigurationJson.Deserialize(run.ConfigurationJson);
        var names = config.Sets.ToDictionary(s => s.Id, s => s.Name);
        var byPair = await hits.GroupBy(h => h.DisciplinePair).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var bySets = await hits.GroupBy(h => new { A = h.SetsA.FirstOrDefault(), B = h.SetsB.FirstOrDefault() })
            .Select(g => new { g.Key.A, g.Key.B, Count = g.Count() }).ToListAsync(ct);

        var pairs = byPair.Select(p =>
        {
            var disciplines = p.Key.Split(" × ");
            var cell = disciplines.Length == 2 ? config.CellFor(disciplines[0], disciplines[1]) : null;
            return new PairCount(p.Key, p.Count, cell?.Enabled ?? false, cell?.ToleranceMm ?? 0);
        }).OrderByDescending(p => p.Count).ToList();
        var sets = bySets.Select(s => new SetPairCount(s.A, names.GetValueOrDefault(s.A, "—"), s.B, names.GetValueOrDefault(s.B, "—"), s.Count))
            .OrderByDescending(s => s.Count).ToList();
        return Results.Ok(new DraftSummary(byPair.Sum(p => p.Count), pairs, sets));
    }

    /// <param name="disciplines">Пара разделов, как в сводке: «АР × ОВ».</param>
    /// <param name="setA">С <paramref name="setB" /> — пара наборов в любом порядке; один — набор любой из сторон.</param>
    /// <param name="model">Id модели в форме <c>N</c> — коллизии, где участвует её элемент.</param>
    /// <param name="sort"><c>depth</c> — глубже сначала; иначе по номеру.</param>
    private static async Task<IResult> HitsAsync(Guid id, string? disciplines, Guid? setA, Guid? setB, string? model, string? level, string? category,
        float? minDepth, string? sort, int? page, int? pageSize, ToadFlowDb db, CancellationToken ct)
    {
        if (await ResultRunAsync(id, db, ct) is not { } run) return Results.Ok(new HitPage(0, []));

        var query = db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id);
        if (!string.IsNullOrWhiteSpace(disciplines)) query = query.Where(h => h.DisciplinePair == disciplines);
        if (setA is { } a && setB is { } b)
            query = query.Where(h => (h.SetsA.Contains(a) && h.SetsB.Contains(b)) || (h.SetsA.Contains(b) && h.SetsB.Contains(a)));
        else if ((setA ?? setB) is { } single) query = query.Where(h => h.SetsA.Contains(single) || h.SetsB.Contains(single));
        if (!string.IsNullOrWhiteSpace(model)) query = query.Where(h => h.ModelA == model || h.ModelB == model);
        if (!string.IsNullOrWhiteSpace(level))
        {
            var wanted = level.Trim().ToLower();
            query = query.Where(h => h.LevelA!.ToLower() == wanted || h.LevelB!.ToLower() == wanted);
        }

        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(h => h.CategoryA == category || h.CategoryB == category);
        if (minDepth is { } depth) query = query.Where(h => h.DepthMm >= depth);

        var total = await query.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(1, page ?? 1);
        var ordered = sort == "depth" ? query.OrderByDescending(h => h.DepthMm).ThenBy(h => h.Number) : query.OrderBy(h => h.Number);
        var rows = await ordered.Skip((number - 1) * size).Take(size).ToListAsync(ct);

        var sets = ConfigurationJson.Deserialize(run.ConfigurationJson).Sets.ToDictionary(s => s.Id, s => s.Name);
        var models = run.Models.ToDictionary(m => m.ModelId.ToString("N"), m => m.ModelName);
        return Results.Ok(new HitPage(total, rows.Select(h => View(h, models, sets)).ToList()));
    }

    private static Task<EngineRun?> ResultRunAsync(Guid partId, ToadFlowDb db, CancellationToken ct) =>
        db.Set<ClashPart>().AsNoTracking().Where(p => p.Id == partId && p.ResultRunId != null)
            .Join(db.Set<EngineRun>().AsNoTracking(), p => p.ResultRunId, r => r.Id, (_, r) => r)
            .SingleOrDefaultAsync(ct);

    private static DraftHitView View(ClashDraftHit h, IReadOnlyDictionary<string, string> models, IReadOnlyDictionary<Guid, string> sets) => new(
        h.RunId,
        h.Number, h.DepthMm, h.X, h.Y, h.Z,
        h.ModelA, models.GetValueOrDefault(h.ModelA, h.ModelA), h.UniqueIdA, h.ElementIdA, h.DisciplineA, Names(h.SetsA, sets), h.CategoryA, h.FamilyA, h.TypeA,
        h.LevelA,
        h.ModelB, models.GetValueOrDefault(h.ModelB, h.ModelB), h.UniqueIdB, h.ElementIdB, h.DisciplineB, Names(h.SetsB, sets), h.CategoryB, h.FamilyB, h.TypeB,
        h.LevelB);

    private static IReadOnlyList<string> Names(Guid[] ids, IReadOnlyDictionary<Guid, string> sets) => ids.Select(id => sets.GetValueOrDefault(id, "—")).ToList();
}
