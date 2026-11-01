using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Collisions;

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
public sealed record CollisionFilter(
    string? Status,
    string? Check,
    string? Discipline,
    string? Pair,
    CollisionAssignee? Assignee,
    string? Level,
    string? Model,
    string? Q,
    int? StuckDays)
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
            int.TryParse(Text("stuckDays"), out var days) ? days : null);
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
        if (Level?.Trim() == CollisionLevel.None)
            query = query.Where(c => (c.LevelA == null || c.LevelA == "") && (c.LevelB == null || c.LevelB == ""));
        else if (!string.IsNullOrWhiteSpace(Level))
        {
            // Уровень любой из сторон или ключ группы отчёта «A / B» (CollisionLevel.Key).
            var wanted = Level.Trim().ToLower();
            query = query.Where(c => c.LevelA!.ToLower() == wanted || c.LevelB!.ToLower() == wanted || (c.LevelA + " / " + c.LevelB).ToLower() == wanted);
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            var key = modelNames.FirstOrDefault(m => m.Value == Model).Key ?? Model;
            query = query.Where(c => c.ModelA == key || c.ModelB == key);
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var text = Q.Trim();
            var number = int.TryParse(text, out var n) ? n : -1;
            var elementId = long.TryParse(text, out var e) ? e : -1;
            query = query.Where(c => c.Number == number || c.ElementIdA == elementId || c.ElementIdB == elementId
                                     || c.UniqueIdA.Contains(text) || c.UniqueIdB.Contains(text));
        }

        return query;
    }

    /// <summary>Сортировка: <c>depth</c> — глубже сначала; иначе по номеру.</summary>
    public static IOrderedQueryable<ClashCollision> Sort(IQueryable<ClashCollision> query, string? sort) =>
        sort == "depth" ? query.OrderByDescending(c => c.DepthMm).ThenBy(c => c.Number) : query.OrderBy(c => c.Number);
}
