using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Plugin;

/// <param name="Status">Как у «Моих»: <c>open</c> (по умолчанию), <c>fake</c>, <c>fixed</c>, <c>gone</c>, <c>all</c> или имена статусов.</param>
/// <param name="Group"><c>mine</c> — правит сторона модели или обе, <c>other</c> — взяла другая сторона, <c>all</c> (по умолчанию).</param>
/// <param name="Pair">Пара разделов, как <see cref="ClashCollision.DisciplinePair" />.</param>
/// <param name="Level">Ключ уровня (<see cref="CollisionLevel.Key" />); <c>-</c> — без уровня.</param>
/// <param name="Q">Номер коллизии, ElementId или UniqueId элемента.</param>
public sealed record ModelCollisionQuery(string? Status = null, string? Group = null, string? Pair = null, string? Level = null, string? Q = null);

/// <summary>
///     Коллизии открытой в Revit модели по всем утверждённым частям. Своя сторона — та, чей элемент из этой модели, поэтому
///     список не зависит от разделов пользователя. Порядок: «мне править», пара по справочнику, уровень снизу вверх, №.
/// </summary>
public sealed class ModelCollisions(ToadFlowDb db, DisciplineOrder order, TimeProvider time)
{
    public const int DefaultPageSize = 100;

    /// <returns>null — модели с таким ключом нет.</returns>
    public async Task<ModelCollisionPage?> PageAsync(string key, ModelCollisionQuery query, int page, int size, CancellationToken ct)
    {
        if (!Guid.TryParse(key, out var id) || !await db.Set<ClashModel>().AnyAsync(m => m.Id == id, ct)) return null;
        var model = id.ToString("N");
        var parts = await MyCollisions.PartsAsync(db, ct);
        var partIds = parts.Select(p => p.Id).ToArray();

        var source = db.Set<ClashCollision>().AsNoTracking().Where(c => partIds.Contains(c.PartId) && (c.ModelA == model || c.ModelB == model));
        if (!string.IsNullOrWhiteSpace(query.Q))
            source = new CollisionFilter("all", null, null, null, null, null, null, query.Q, null).Apply(source, db, new Dictionary<string, string>(), time.GetUtcNow());

        var statuses = MyCollisions.Statuses(query.Status);
        var group = query.Group?.Trim().ToLowerInvariant();
        var pair = string.IsNullOrWhiteSpace(query.Pair) ? null : query.Pair.Trim();
        var level = string.IsNullOrWhiteSpace(query.Level) ? null : query.Level.Trim() == CollisionLevel.None ? "" : query.Level.Trim();

        IQueryable<ClashCollision> ByStatus(IQueryable<ClashCollision> q) => statuses is null ? q : q.Where(c => statuses.Contains(c.Status));
        IQueryable<ClashCollision> ByPair(IQueryable<ClashCollision> q) => pair is null ? q : q.Where(c => c.DisciplinePair == pair);
        IQueryable<ClashCollision> ByLevel(IQueryable<ClashCollision> q) => level is null
            ? q
            : q.Where(c => (c.LevelA == c.LevelB || c.LevelB == null ? c.LevelA ?? "" : c.LevelA == null ? c.LevelB : c.LevelA + " / " + c.LevelB) == level);
        IQueryable<ClashCollision> ByGroup(IQueryable<ClashCollision> q) => group switch
        {
            "mine" => q.Where(c => c.Assignee == CollisionAssignee.Both || (c.Assignee == CollisionAssignee.SideA && c.ModelA == model)
                                                                        || (c.Assignee == CollisionAssignee.SideB && c.ModelB == model)),
            "other" => q.Where(c => !(c.Assignee == CollisionAssignee.Both || (c.Assignee == CollisionAssignee.SideA && c.ModelA == model)
                                                                           || (c.Assignee == CollisionAssignee.SideB && c.ModelB == model))),
            _ => q
        };

        // Пары и уровни выборки без фильтров пары и уровня: из них и порядок строк, и оба фасета.
        var groups = await ByGroup(ByStatus(source))
            .Select(c => new
            {
                c.DisciplinePair,
                c.Z,
                Level = c.LevelA == c.LevelB || c.LevelB == null ? c.LevelA ?? "" : c.LevelA == null ? c.LevelB : c.LevelA + " / " + c.LevelB
            })
            .GroupBy(x => new { x.DisciplinePair, x.Level })
            .Select(g => new { g.Key.DisciplinePair, g.Key.Level, MinZ = g.Min(x => x.Z), Count = g.Count() })
            .ToListAsync(ct);

        var ranks = await order.LoadAsync(groups.SelectMany(g =>
        {
            var (a, b) = DisciplineRanks.Split(g.DisciplinePair);
            return new[] { a, b };
        }), ct);
        var pairs = groups.Select(g => g.DisciplinePair).Distinct()
            .Select(p =>
            {
                var (a, b) = DisciplineRanks.Split(p);
                return (Pair: p, Rank: ranks.PairRank(a, b), Label: ranks.Label(a, b));
            })
            .OrderBy(p => p.Rank.First).ThenBy(p => p.Rank.Second).ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var pairKeys = pairs.Select(p => p.Pair).ToArray();
        var pairIndex = pairKeys.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var levelKeys = groups
            .OrderBy(g => pairIndex[g.DisciplinePair]).ThenBy(g => g.Level.Length == 0).ThenBy(g => g.MinZ)
            .ThenBy(g => g.Level, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => CollisionLevel.Composite(g.DisciplinePair, g.Level)).ToArray();

        var pairFacet = pairs
            .Select(p => new FacetView(p.Pair, p.Label, groups.Where(g => g.DisciplinePair == p.Pair && (level is null || g.Level == level)).Sum(g => g.Count)))
            .Where(f => f.Count > 0).ToList();
        var levelFacet = groups.Where(g => pair is null || g.DisciplinePair == pair)
            .GroupBy(g => g.Level)
            .Select(g => (Level: g.Key, MinZ: g.Min(x => x.MinZ), Count: g.Sum(x => x.Count)))
            .OrderBy(l => l.Level.Length == 0).ThenBy(l => l.MinZ).ThenBy(l => l.Level, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => l.Level.Length == 0 ? new FacetView(CollisionLevel.None, "Без уровня", l.Count) : new FacetView(l.Level, l.Level, l.Count))
            .ToList();

        var byStatus = await ByGroup(ByPair(ByLevel(source))).GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var open = CollisionEndpoints.Open;
        var statusFacet = new List<FacetView>
        {
            new("open", "Открытые", byStatus.Where(s => open.Contains(s.Key)).Sum(s => s.Count)),
            new("fake", "Фиктивные", byStatus.Where(s => s.Key == CollisionStatus.Fake).Sum(s => s.Count)),
            new("fixed", "Исправленные", byStatus.Where(s => s.Key == CollisionStatus.Fixed).Sum(s => s.Count)),
            new("all", "Все", byStatus.Sum(s => s.Count))
        };

        var ungrouped = ByStatus(ByPair(ByLevel(source)));
        var all = await ungrouped.CountAsync(ct);
        var mine = await ungrouped.CountAsync(c => c.Assignee == CollisionAssignee.Both || (c.Assignee == CollisionAssignee.SideA && c.ModelA == model)
                                                   || (c.Assignee == CollisionAssignee.SideB && c.ModelB == model), ct);

        var filtered = ByGroup(ungrouped);
        var total = group is "mine" ? mine : group is "other" ? all - mine : all;
        var ids = await filtered
            .OrderByDescending(c => c.Assignee == CollisionAssignee.Both || (c.Assignee == CollisionAssignee.SideA && c.ModelA == model)
                                    || (c.Assignee == CollisionAssignee.SideB && c.ModelB == model))
            .ThenBy(CollisionLevel.PairPosition(pairKeys))
            .ThenBy(CollisionLevel.Position(levelKeys))
            .ThenBy(c => c.Number).ThenBy(c => c.PartId)
            .Skip((Math.Max(1, page) - 1) * size).Take(size).Select(c => c.Id).ToListAsync(ct);
        var rows = await db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var index = ids.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i);
        rows = rows.OrderBy(r => index[r.Id]).ToList();

        var names = await MyEndpoints.ModelNamesAsync(db, rows, ct);
        var suggested = await CollisionEndpoints.NumbersAsync(db, rows.Select(r => r.SuggestedPredecessorId).OfType<Guid>().ToList(), ct);
        var taken = await MyEndpoints.TakenAsync(db, ids, ct);
        var partNames = parts.ToDictionary(p => p.Id, p => p.Name);
        var items = rows.Select(c =>
        {
            var who = taken.GetValueOrDefault(c.Id);
            return new ModelCollisionRow(CollisionEndpoints.Row(c, names, suggested), c.PartId, partNames[c.PartId], SideOf(c, model),
                MineToFix(c, model), who?.Name, who?.At);
        }).ToList();

        return new ModelCollisionPage(total, mine, all - mine, items, new ModelFacets(pairFacet, levelFacet, statusFacet));
    }

    private static string SideOf(ClashCollision c, string model) => (c.ModelA == model, c.ModelB == model) switch
    {
        (true, true) => "AB",
        (true, false) => "A",
        _ => "B"
    };

    /// <summary>Правит сторона этой модели или обе. Обе стороны из модели — правит она при любом «кто правит».</summary>
    private static bool MineToFix(ClashCollision c, string model) =>
        c.Assignee == CollisionAssignee.Both
        || (c.Assignee == CollisionAssignee.SideA && c.ModelA == model)
        || (c.Assignee == CollisionAssignee.SideB && c.ModelB == model);
}
