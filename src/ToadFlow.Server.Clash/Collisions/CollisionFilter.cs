using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>
///     Варианты фильтров списка с числами коллизий. Каждый фасет считается по текущему фильтру без собственного поля. Уровни и
///     категории — по обеим сторонам, коллизия в значении считается один раз; модели — по имени. Уровни — снизу вверх по отметке, остальное — по числу.
/// </summary>
public sealed record CollisionFacets(
    IReadOnlyList<ValueCount> Pairs,
    IReadOnlyList<ValueCount> Levels,
    IReadOnlyList<ValueCount> Models,
    IReadOnlyList<ValueCount> Categories);

/// <summary>
///     Фильтр коллизий из строки адреса — один для списка проверки, отчёта прогона и выгрузок.
/// </summary>
/// <param name="Status">Статусы через запятую; <c>all</c> — все; не задано — открытые.</param>
/// <param name="Check">Ключ проверки (клетки матрицы).</param>
/// <param name="Discipline">Раздел любой из сторон.</param>
/// <param name="Pair">Пара разделов, как в <see cref="ClashCollision.DisciplinePair" />.</param>
/// <param name="Level">Уровень любой из сторон или ключ группы «A / B», без учёта регистра; <c>-</c> — без уровня.</param>
/// <param name="Model">Модель любой из сторон: имя или ключ (Id в форме N).</param>
/// <param name="Q">Номер коллизии, UniqueId или ElementId элемента.</param>
/// <param name="StuckDays">Только висящие: открытые, без записей в ленте столько дней (из обзора координатора).</param>
/// <param name="Element">Элемент любой из сторон: <c>{модель:N}:{UniqueId}</c>.</param>
/// <param name="Category">Категория элемента любой из сторон.</param>
public sealed record CollisionFilter(
    string? Status,
    string? Check,
    string? Discipline,
    string? Pair,
    CollisionAssignee? Assignee,
    string? Level,
    string? Model,
    string? Q,
    int? StuckDays,
    string? Element = null,
    string? Category = null)
{
    public static CollisionFilter From(HttpRequest request)
    {
        var query = request.Query;
        string? Text(string key) => query.TryGetValue(key, out var value) && value.ToString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

        return new CollisionFilter(
            Text("status"),
            Text("check"),
            Text("discipline")?.Trim(),
            Text("pair")?.Trim(),
            Enum.TryParse<CollisionAssignee>(Text("assignee"), ignoreCase: true, out var assignee) ? assignee : null,
            Text("level"),
            Text("model"),
            Text("q"),
            int.TryParse(Text("stuckDays"), out var days) ? days : null,
            Text("element")?.Trim(),
            Text("category")?.Trim());
    }

    /// <param name="modelNames">Имена моделей части по ключу — фильтр модели принимает и имя.</param>
    public IQueryable<ClashCollision> Apply(IQueryable<ClashCollision> query, ToadFlowDb db, IReadOnlyDictionary<string, string> modelNames,
        DateTimeOffset now)
    {
        if (StuckDays is { } days and > 0) query = Overview.OverviewEndpoints.Stuck(db, query, now - TimeSpan.FromDays(days));
        if (string.IsNullOrWhiteSpace(Status)) query = query.Where(c => CollisionEndpoints.Open.Contains(c.Status));
        else if (!Status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var wanted = Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<CollisionStatus>(s, true, out var parsed) ? parsed : (CollisionStatus?)null).OfType<CollisionStatus>().ToArray();
            query = query.Where(c => wanted.Contains(c.Status));
        }

        if (!string.IsNullOrWhiteSpace(Check)) query = query.Where(c => c.Checks.Contains(Check));
        if (Discipline is { } discipline) query = query.Where(c => c.DisciplineA == discipline || c.DisciplineB == discipline);
        if (Pair is { } pair) query = query.Where(c => c.DisciplinePair == pair);
        if (Assignee is { } who) query = query.Where(c => c.Assignee == who);
        query = ByLevel(query, Level);

        if (!string.IsNullOrWhiteSpace(Model))
        {
            // Фасет сводит одноимённые модели части в одну строку — имя выбирает их все.
            var keys = modelNames.Where(m => m.Value == Model).Select(m => m.Key).ToArray();
            query = ByModels(query, keys.Length > 0 ? keys : [Model]);
        }

        query = ByElement(query, Element);
        if (Category is { } category) query = query.Where(c => c.CategoryA == category || c.CategoryB == category);
        return ByText(query, Q);
    }

    /// <summary>Модель любой из сторон — одна из <paramref name="keys" /> (Id в форме N).</summary>
    internal static IQueryable<ClashCollision> ByModels(IQueryable<ClashCollision> query, IReadOnlyCollection<string> keys) =>
        query.Where(c => keys.Contains(c.ModelA) || keys.Contains(c.ModelB));

    /// <summary>Поиск: номер коллизии, ElementId или часть UniqueId элемента любой из сторон; пусто — без фильтра.</summary>
    internal static IQueryable<ClashCollision> ByText(IQueryable<ClashCollision> query, string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return query;
        var text = q.Trim();
        var number = int.TryParse(text, out var n) ? n : -1;
        var elementId = long.TryParse(text, out var e) ? e : -1;
        return query.Where(c => c.Number == number || c.ElementIdA == elementId || c.ElementIdB == elementId
                                || c.UniqueIdA.Contains(text) || c.UniqueIdB.Contains(text));
    }

    /// <summary>Уровень любой из сторон или ключ группы отчёта «A / B» (<see cref="CollisionLevel.Key" />); <c>-</c> — без уровня.</summary>
    internal static IQueryable<ClashCollision> ByLevel(IQueryable<ClashCollision> query, string? level)
    {
        if (level?.Trim() == CollisionLevel.None)
            return query.Where(c => (c.LevelA == null || c.LevelA == "") && (c.LevelB == null || c.LevelB == ""));
        if (string.IsNullOrWhiteSpace(level)) return query;
        var wanted = level.Trim().ToLower();
        return query.Where(c => c.LevelA!.ToLower() == wanted || c.LevelB!.ToLower() == wanted || (c.LevelA + " / " + c.LevelB).ToLower() == wanted);
    }

    /// <summary>Элемент любой из сторон по <c>{модель:N}:{UniqueId}</c>; без двоеточия — без фильтра.</summary>
    internal static IQueryable<ClashCollision> ByElement(IQueryable<ClashCollision> query, string? element)
    {
        var colon = element?.IndexOf(':') ?? -1;
        if (colon <= 0) return query;
        var (model, uniqueId) = (element![..colon].Trim(), element[(colon + 1)..].Trim());
        if (Guid.TryParse(model, out var guid)) model = guid.ToString("N");
        return query.Where(c => (c.ModelA == model && c.UniqueIdA == uniqueId) || (c.ModelB == model && c.UniqueIdB == uniqueId));
    }

    /// <summary>Сортировка: <c>depth</c> — глубже сначала; <c>level</c> — по уровню снизу вверх (<see cref="LevelOrderAsync" />), затем по номеру; иначе по номеру.</summary>
    public static async Task<IOrderedQueryable<ClashCollision>> SortAsync(IQueryable<ClashCollision> query, string? sort, CancellationToken ct) => sort switch
    {
        "depth" => query.OrderByDescending(c => c.DepthMm).ThenBy(c => c.Number),
        "level" => query.OrderBy(CollisionLevel.LevelPosition(await LevelOrderAsync(query, ct))).ThenBy(c => c.Number),
        _ => query.OrderBy(c => c.Number)
    };

    /// <summary>
    ///     Уровни выборки (<see cref="CollisionLevel.Key" />) снизу вверх, как в отчёте: по наименьшей отметке Z их коллизий, при равной —
    ///     по имени; без уровня — в конце.
    /// </summary>
    public static async Task<string[]> LevelOrderAsync(IQueryable<ClashCollision> query, CancellationToken ct)
    {
        var levels = await query.GroupBy(CollisionLevel.Key).Select(g => new { g.Key, MinZ = g.Min(c => c.Z) }).ToListAsync(ct);
        return levels.OrderBy(l => l.Key.Length == 0).ThenBy(l => l.MinZ).ThenBy(l => l.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => l.Key).ToArray();
    }

    /// <summary>Фасеты <see cref="CollisionFacets" />: каждая выборка отфильтрована всеми полями, кроме своего.</summary>
    /// <param name="pairs">Выборка без фильтра пары.</param>
    /// <param name="levels">Без фильтра уровня.</param>
    /// <param name="models">Без фильтра модели.</param>
    /// <param name="categories">Без фильтра категории.</param>
    /// <param name="modelNames">Имена моделей по ключам; модель без имени отдаётся ключом.</param>
    public static async Task<CollisionFacets> FacetsAsync(IQueryable<ClashCollision> pairs, IQueryable<ClashCollision> levels,
        IQueryable<ClashCollision> models, IQueryable<ClashCollision> categories,
        Func<IReadOnlyList<string>, Task<Dictionary<string, string>>> modelNames, CancellationToken ct)
    {
        var pairCounts = await pairs.GroupBy(c => c.DisciplinePair).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        var levelSides = levels.Where(c => c.LevelA != null && c.LevelA != "").Select(c => new { c.Id, Value = c.LevelA!, c.Z })
            .Union(levels.Where(c => c.LevelB != null && c.LevelB != "").Select(c => new { c.Id, Value = c.LevelB!, c.Z }))
            .Union(levels.Where(c => (c.LevelA == null || c.LevelA == "") && (c.LevelB == null || c.LevelB == ""))
                .Select(c => new { c.Id, Value = CollisionLevel.None, c.Z }));
        var levelCounts = await levelSides.GroupBy(s => s.Value).Select(g => new { g.Key, Count = g.Count(), MinZ = g.Min(s => s.Z) }).ToListAsync(ct);

        var modelSides = models.Select(c => new { c.Id, Value = c.ModelA }).Union(models.Select(c => new { c.Id, Value = c.ModelB }));
        var modelCounts = await modelSides.GroupBy(s => s.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var names = await modelNames(modelCounts.Select(m => m.Key).ToList());

        var categorySides = categories.Select(c => new { c.Id, Value = c.CategoryA }).Union(categories.Select(c => new { c.Id, Value = c.CategoryB }));
        var categoryCounts = await categorySides.GroupBy(s => s.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        return new CollisionFacets(
            Ordered(pairCounts.Select(p => (p.Key, p.Count))),
            // Уровни — снизу вверх, как в отчёте и сортировке по уровню; без уровня — в конце.
            levelCounts.OrderBy(l => l.Key == CollisionLevel.None).ThenBy(l => l.MinZ).ThenBy(l => l.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(l => new ValueCount(l.Key, l.Count)).ToList(),
            Ordered(modelCounts.Select(m => (names.GetValueOrDefault(m.Key, m.Key), m.Count))),
            Ordered(categoryCounts.Select(c => (c.Key, c.Count))));
    }

    private static IReadOnlyList<ValueCount> Ordered(IEnumerable<(string Value, int Count)> values) =>
        values.GroupBy(v => v.Value).Select(g => new ValueCount(g.Key, g.Sum(v => v.Count)))
            .OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
}
