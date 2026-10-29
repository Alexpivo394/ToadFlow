using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Проверка — включённая клетка матрицы прогона и сколько в ней коллизий (может быть 0).</summary>
/// <param name="Key">Ключ проверки: <c>{группа:N}|{группа:N}</c>, меньший Id первым — как в <see cref="ClashDraftHit.Checks" />.</param>
/// <param name="GroupA">Группа строки матрицы: из двух групп та, что раньше в конфигурации.</param>
public sealed record CheckCount(string Key, Guid GroupA, string GroupAName, Guid GroupB, string GroupBName, int Count, double ToleranceMm);

/// <summary>Пара наборов внутри проверки: набор группы строки и набор группы столбца.</summary>
public sealed record SetPairCount(Guid SetA, string SetAName, Guid SetB, string SetBName, int Count);

/// <param name="Checks">Проверки в порядке матрицы: по группе строки, внутри — по группе столбца.</param>
/// <param name="Legacy">В результате есть строки прогона до групп — без проверок; такой результат нужно прогнать заново.</param>
public sealed record DraftSummary(int Total, IReadOnlyList<CheckCount> Checks, bool Legacy);

/// <summary>Значения для выпадающих фильтров списка — только те, что есть у коллизий проверки.</summary>
public sealed record DraftFacets(IReadOnlyList<ValueCount> Levels, IReadOnlyList<ValueCount> Models, IReadOnlyList<ValueCount> Categories);

/// <param name="Checks">Ключи проверок коллизии.</param>
/// <param name="SetPairKey">Пара наборов в проверке из запроса (<c>{набор:N}|{набор:N}</c>); пусто, если проверка не задана.</param>
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
    string? LevelB,
    IReadOnlyList<string> Checks,
    string SetPairKey);

public sealed record HitPage(int Total, IReadOnlyList<DraftHitView> Items);

/// <summary>Результат черновика — коллизии последнего удачного прогона части по проверкам: сводка, пары наборов, фильтры и список страницами.</summary>
public static class DraftResultEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static void MapClashDraftResults(this RouteGroupBuilder api)
    {
        var draft = api.MapGroup("/clash/parts/{id:guid}/draft");
        draft.MapGet("/summary", SummaryAsync);
        draft.MapGet("/checks/{check}/setpairs", SetPairsAsync);
        draft.MapGet("/facets", FacetsAsync);
        draft.MapGet("/hits", HitsAsync);
    }

    /// <summary>Ключ проверки пары групп — как у движка: Id в форме N, меньший первым.</summary>
    public static string CheckKey(Guid a, Guid b)
    {
        var (x, y) = (a.ToString("N"), b.ToString("N"));
        return string.CompareOrdinal(x, y) <= 0 ? $"{x}|{y}" : $"{y}|{x}";
    }

    private static async Task<IResult> SummaryAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        if (await ResultRunAsync(id, db, ct) is not { } run) return Results.Ok(new DraftSummary(0, [], false));

        var hits = db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id);
        var total = await hits.CountAsync(ct);
        var counts = await hits.SelectMany(h => h.Checks).GroupBy(c => c).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var legacy = await hits.AnyAsync(h => h.Checks.Length == 0, ct);

        var config = ConfigurationJson.Deserialize(run.ConfigurationJson);
        var checks = new List<CheckCount>();
        for (var i = 0; i < config.Groups.Count; i++)
        for (var j = i; j < config.Groups.Count; j++)
        {
            var (a, b) = (config.Groups[i], config.Groups[j]);
            if (config.CellFor(a.Id, b.Id) is not { Enabled: true } cell) continue;
            var key = CheckKey(a.Id, b.Id);
            checks.Add(new CheckCount(key, a.Id, a.Name, b.Id, b.Name, counts.GetValueOrDefault(key), cell.ToleranceMm));
        }

        return Results.Ok(new DraftSummary(total, checks, legacy));
    }

    private static async Task<IResult> SetPairsAsync(Guid id, string check, ToadFlowDb db, CancellationToken ct)
    {
        if (await ResultRunAsync(id, db, ct) is not { } run) return Results.Ok(Array.Empty<SetPairCount>());
        var config = ConfigurationJson.Deserialize(run.ConfigurationJson);
        if (Groups(config, check) is not { } groups) return Results.NotFound(new { error = "Такой проверки нет в прогоне" });

        var rows = await db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id && h.Checks.Contains(check))
            .Select(h => new { h.SetsA, h.SetsB }).ToListAsync(ct);
        var names = config.Sets.ToDictionary(s => s.Id, s => s.Name);
        var setGroups = config.Sets.ToDictionary(s => s.Id, s => s.GroupId);
        var pairs = rows.GroupBy(r => SetPair(r.SetsA, r.SetsB, groups, setGroups))
            .Select(g => new SetPairCount(g.Key.A, names.GetValueOrDefault(g.Key.A, "—"), g.Key.B, names.GetValueOrDefault(g.Key.B, "—"), g.Count()))
            .OrderByDescending(p => p.Count).ToList();
        return Results.Ok(pairs);
    }

    private static async Task<IResult> FacetsAsync(Guid id, string? check, ToadFlowDb db, CancellationToken ct)
    {
        if (await ResultRunAsync(id, db, ct) is not { } run) return Results.Ok(new DraftFacets([], [], []));

        var hits = db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id);
        if (!string.IsNullOrWhiteSpace(check)) hits = hits.Where(h => h.Checks.Contains(check));
        var levels = await hits.GroupBy(h => h.LevelA ?? h.LevelB).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var models = await hits.Select(h => new { h.ModelA, h.ModelB }).GroupBy(h => h).Select(g => new { g.Key.ModelA, g.Key.ModelB, Count = g.Count() })
            .ToListAsync(ct);
        var categories = await hits.Select(h => new { h.CategoryA, h.CategoryB }).GroupBy(h => h)
            .Select(g => new { g.Key.CategoryA, g.Key.CategoryB, Count = g.Count() }).ToListAsync(ct);

        var modelNames = run.Models.ToDictionary(m => m.ModelId.ToString("N"), m => m.ModelName);
        return Results.Ok(new DraftFacets(
            Ordered(levels.Where(l => l.Key is not null).Select(l => (l.Key!, l.Count))),
            Ordered(models.SelectMany(m => m.ModelA == m.ModelB ? [(m.ModelA, m.Count)] : new[] { (m.ModelA, m.Count), (m.ModelB, m.Count) })
                .Select(m => (modelNames.GetValueOrDefault(m.Item1, m.Item1), m.Count))),
            Ordered(categories.SelectMany(c => c.CategoryA == c.CategoryB ? [(c.CategoryA, c.Count)] : new[] { (c.CategoryA, c.Count), (c.CategoryB, c.Count) }))));
    }

    private static IReadOnlyList<ValueCount> Ordered(IEnumerable<(string Value, int Count)> values) =>
        values.GroupBy(v => v.Value).Select(g => new ValueCount(g.Key, g.Sum(v => v.Count)))
            .OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <param name="disciplines">Пара разделов: «АР × ОВ».</param>
    /// <param name="check">Ключ проверки — коллизии, попавшие в неё.</param>
    /// <param name="setA">С <paramref name="setB" /> — пара наборов в любом порядке; один — набор любой из сторон.</param>
    /// <param name="model">Id модели в форме <c>N</c> или её имя — коллизии, где участвует её элемент.</param>
    /// <param name="sort"><c>depth</c> — глубже сначала; <c>setpair</c> — по паре наборов проверки, внутри глубже сначала; иначе по номеру.</param>
    private static async Task<IResult> HitsAsync(Guid id, string? disciplines, string? check, Guid? setA, Guid? setB, string? model, string? level,
        string? category, float? minDepth, string? sort, int? page, int? pageSize, ToadFlowDb db, CancellationToken ct)
    {
        if (await ResultRunAsync(id, db, ct) is not { } run) return Results.Ok(new HitPage(0, []));
        var config = ConfigurationJson.Deserialize(run.ConfigurationJson);
        var models = run.Models.ToDictionary(m => m.ModelId.ToString("N"), m => m.ModelName);

        var query = db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id);
        if (!string.IsNullOrWhiteSpace(disciplines)) query = query.Where(h => h.DisciplinePair == disciplines);
        if (!string.IsNullOrWhiteSpace(check)) query = query.Where(h => h.Checks.Contains(check));
        if (setA is { } a && setB is { } b)
            query = query.Where(h => (h.SetsA.Contains(a) && h.SetsB.Contains(b)) || (h.SetsA.Contains(b) && h.SetsB.Contains(a)));
        else if ((setA ?? setB) is { } single) query = query.Where(h => h.SetsA.Contains(single) || h.SetsB.Contains(single));
        if (!string.IsNullOrWhiteSpace(model))
        {
            var key = models.FirstOrDefault(m => m.Value == model).Key ?? model;
            query = query.Where(h => h.ModelA == key || h.ModelB == key);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            var wanted = level.Trim().ToLower();
            query = query.Where(h => h.LevelA!.ToLower() == wanted || h.LevelB!.ToLower() == wanted);
        }

        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(h => h.CategoryA == category || h.CategoryB == category);
        if (minDepth is { } depth) query = query.Where(h => h.DepthMm >= depth);

        var total = await query.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var skip = (Math.Max(1, page ?? 1) - 1) * size;
        var groups = check is null ? null : Groups(config, check);
        var setGroups = config.Sets.ToDictionary(s => s.Id, s => s.GroupId);
        var names = config.Sets.ToDictionary(s => s.Id, s => s.Name);

        List<ClashDraftHit> rows;
        if (sort == "setpair" && groups is { } pair)
        {
            // Пара наборов проверки считается по группам, а не одним SQL: элемент может входить в наборы нескольких групп.
            var keys = await query.Select(h => new { h.Number, h.SetsA, h.SetsB, h.DepthMm }).ToListAsync(ct);
            var numbers = keys
                .Select(k => (k.Number, k.DepthMm, Pair: SetPair(k.SetsA, k.SetsB, pair, setGroups)))
                .OrderBy(k => names.GetValueOrDefault(k.Pair.A, ""), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(k => names.GetValueOrDefault(k.Pair.B, ""), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(k => k.Pair.A).ThenBy(k => k.Pair.B)
                .ThenByDescending(k => k.DepthMm).ThenBy(k => k.Number)
                .Skip(skip).Take(size).Select(k => k.Number).ToList();
            var found = await db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id && numbers.Contains(h.Number)).ToDictionaryAsync(h => h.Number, ct);
            rows = numbers.Select(n => found[n]).ToList();
        }
        else
        {
            var ordered = sort == "depth" ? query.OrderByDescending(h => h.DepthMm).ThenBy(h => h.Number) : query.OrderBy(h => h.Number);
            rows = await ordered.Skip(skip).Take(size).ToListAsync(ct);
        }

        return Results.Ok(new HitPage(total, rows.Select(h => View(h, models, names, groups is { } g ? SetPairKey(SetPair(h.SetsA, h.SetsB, g, setGroups)) : "")).ToList()));
    }

    /// <summary>Группы проверки по ключу в порядке матрицы; null — такой включённой клетки в прогоне нет.</summary>
    private static (Guid A, Guid B)? Groups(ClashConfiguration config, string check)
    {
        for (var i = 0; i < config.Groups.Count; i++)
        for (var j = i; j < config.Groups.Count; j++)
            if (CheckKey(config.Groups[i].Id, config.Groups[j].Id) == check)
                return (config.Groups[i].Id, config.Groups[j].Id);
        return null;
    }

    /// <summary>
    ///     Пара наборов коллизии в проверке: первый набор стороны из группы строки и первый набор другой стороны из группы
    ///     столбца. Стороны A и B коллизии упорядочены по элементам, а не по группам, поэтому пробуются оба соответствия.
    /// </summary>
    private static (Guid A, Guid B) SetPair(Guid[] setsA, Guid[] setsB, (Guid A, Guid B) groups, IReadOnlyDictionary<Guid, Guid> setGroups)
    {
        Guid First(Guid[] sets, Guid group) => sets.FirstOrDefault(s => setGroups.TryGetValue(s, out var g) && g == group);
        var (x, y) = (First(setsA, groups.A), First(setsB, groups.B));
        if (x != Guid.Empty && y != Guid.Empty) return (x, y);
        var (u, v) = (First(setsB, groups.A), First(setsA, groups.B));
        return u != Guid.Empty && v != Guid.Empty ? (u, v) : (setsA.FirstOrDefault(), setsB.FirstOrDefault());
    }

    private static string SetPairKey((Guid A, Guid B) pair) => $"{pair.A:N}|{pair.B:N}";

    private static Task<EngineRun?> ResultRunAsync(Guid partId, ToadFlowDb db, CancellationToken ct) =>
        db.Set<ClashPart>().AsNoTracking().Where(p => p.Id == partId && p.ResultRunId != null)
            .Join(db.Set<EngineRun>().AsNoTracking(), p => p.ResultRunId, r => r.Id, (_, r) => r)
            .SingleOrDefaultAsync(ct);

    private static DraftHitView View(ClashDraftHit h, IReadOnlyDictionary<string, string> models, IReadOnlyDictionary<Guid, string> sets, string setPairKey) => new(
        h.RunId,
        h.Number, h.DepthMm, h.X, h.Y, h.Z,
        h.ModelA, models.GetValueOrDefault(h.ModelA, h.ModelA), h.UniqueIdA, h.ElementIdA, h.DisciplineA, Names(h.SetsA, sets), h.CategoryA, h.FamilyA, h.TypeA,
        h.LevelA,
        h.ModelB, models.GetValueOrDefault(h.ModelB, h.ModelB), h.UniqueIdB, h.ElementIdB, h.DisciplineB, Names(h.SetsB, sets), h.CategoryB, h.FamilyB, h.TypeB,
        h.LevelB,
        h.Checks,
        setPairKey);

    private static IReadOnlyList<string> Names(Guid[] ids, IReadOnlyDictionary<Guid, string> sets) => ids.Select(id => sets.GetValueOrDefault(id, "—")).ToList();
}
