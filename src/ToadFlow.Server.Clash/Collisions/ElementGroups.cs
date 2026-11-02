using Microsoft.EntityFrameworkCore;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Группа коллизий одного элемента своей стороны.</summary>
/// <param name="Model">Ключ модели элемента (Id в форме N).</param>
/// <param name="Count">Коллизии элемента под фильтром.</param>
/// <param name="MaxDepthMm">Глубина самой глубокой из них.</param>
public sealed record ElementGroup(
    string Model,
    string ModelName,
    string UniqueId,
    long ElementId,
    string Discipline,
    string Category,
    string? Family,
    string? Type,
    string? Level,
    int Count,
    float MaxDepthMm);

/// <param name="Groups">Групп под фильтром.</param>
/// <param name="Collisions">Коллизий в группах без повторов: коллизия двух своих элементов входит в две группы, но считается один раз.</param>
public sealed record ElementGroupPage(int Groups, int Collisions, IReadOnlyList<ElementGroup> Items);

/// <summary>
///     Группировка коллизий по элементу своей стороны: сторона своя, если её раздел в наборе разделов. Коллизия, у которой свои обе
///     стороны (ВК × ВК), попадает в группу каждого из двух элементов.
/// </summary>
public static class ElementGroups
{
    public const string Key = "element";

    /// <summary>Параметр <c>group</c> просит группы по элементу.</summary>
    public static bool Requested(string? group) => string.Equals(group?.Trim(), Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Группа в запросе: инициализатор свойств, а не конструктор — по нему SQL сортирует и считает.</summary>
    public sealed class Row
    {
        public string Model { get; init; } = "";
        public string UniqueId { get; init; } = "";
        public long ElementId { get; init; }
        public string Discipline { get; init; } = "";
        public string Category { get; init; } = "";
        public string? Family { get; init; }
        public string? Type { get; init; }
        public string? Level { get; init; }
        public int Count { get; init; }
        public float MaxDepthMm { get; init; }
    }

    /// <summary>Группы элементов сторон из <paramref name="disciplines" />, без порядка.</summary>
    public static IQueryable<Row> Of(IQueryable<ClashCollision> query, string[] disciplines)
    {
        var sides = query.Where(c => disciplines.Contains(c.DisciplineA))
            .Select(c => new
            {
                Model = c.ModelA, UniqueId = c.UniqueIdA, ElementId = c.ElementIdA, Discipline = c.DisciplineA, Category = c.CategoryA,
                Family = c.FamilyA, Type = c.TypeA, Level = c.LevelA, c.DepthMm
            })
            .Concat(query.Where(c => disciplines.Contains(c.DisciplineB))
                .Select(c => new
                {
                    Model = c.ModelB, UniqueId = c.UniqueIdB, ElementId = c.ElementIdB, Discipline = c.DisciplineB, Category = c.CategoryB,
                    Family = c.FamilyB, Type = c.TypeB, Level = c.LevelB, c.DepthMm
                }));
        return sides.GroupBy(s => new { s.Model, s.UniqueId })
            .Select(g => new Row
            {
                Model = g.Key.Model, UniqueId = g.Key.UniqueId, ElementId = g.Max(s => s.ElementId), Discipline = g.Max(s => s.Discipline)!,
                Category = g.Max(s => s.Category)!, Family = g.Max(s => s.Family), Type = g.Max(s => s.Type), Level = g.Max(s => s.Level),
                Count = g.Count(), MaxDepthMm = g.Max(s => s.DepthMm)
            });
    }

    /// <summary>
    ///     Страница групп: крупные первыми, затем по глубине, модели и UniqueId — группы из одной коллизии поэтому идут после
    ///     остальных.
    /// </summary>
    /// <param name="modelNames">Имена моделей по ключам страницы.</param>
    public static async Task<ElementGroupPage> PageAsync(IQueryable<ClashCollision> query, string[] disciplines, int page, int size,
        Func<IReadOnlyList<string>, Task<Dictionary<string, string>>> modelNames, CancellationToken ct)
    {
        var groups = Of(query, disciplines);
        var total = await groups.CountAsync(ct);
        var collisions = await query.CountAsync(c => disciplines.Contains(c.DisciplineA) || disciplines.Contains(c.DisciplineB), ct);
        var items = await groups.OrderByDescending(g => g.Count).ThenByDescending(g => g.MaxDepthMm).ThenBy(g => g.Model).ThenBy(g => g.UniqueId)
            .Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
        var names = await modelNames(items.Select(g => g.Model).Distinct().ToList());
        return new ElementGroupPage(total, collisions, items.Select(g => new ElementGroup(g.Model, names.GetValueOrDefault(g.Model, g.Model), g.UniqueId,
            g.ElementId, g.Discipline, g.Category, g.Family, g.Type, g.Level, g.Count, g.MaxDepthMm)).ToList());
    }
}
