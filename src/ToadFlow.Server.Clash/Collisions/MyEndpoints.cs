using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>«Мои коллизии» для веба и плагина: список, счётчики и отметка захода. Открыто любой роли с разделами в профиле.</summary>
public static class MyEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashMy(this RouteGroupBuilder members)
    {
        var my = members.MapGroup("/clash/my");
        my.MapGet("", ListAsync);
        my.MapGet("/summary", SummaryAsync);
        my.MapPost("/visit", VisitAsync);
    }

    /// <param name="sort"><c>number</c> (по умолчанию) — по части и номеру, <c>depth</c> — глубже сначала, <c>new</c> — новые сначала.</param>
    private static async Task<IResult> ListAsync([AsParameters] MyFilter filter, string? sort, int? page, int? pageSize, HttpContext http, ToadFlowDb db,
        TimeProvider time, CancellationToken ct)
    {
        var (disciplines, state) = await UserAsync(db, http.User.UserId(), ct);
        if (disciplines.Length == 0) return Results.Ok(new MyCollisionPage(0, []));
        var parts = await MyCollisions.PartsAsync(db, ct);
        var query = MyCollisions.Query(db, disciplines, parts, filter, MyCollisions.Mark(state, time.GetUtcNow()));

        var total = await query.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? DraftResultEndpoints.DefaultPageSize, 1, DraftResultEndpoints.MaxPageSize);
        var ordered = sort switch
        {
            "depth" => query.OrderByDescending(c => c.DepthMm).ThenBy(c => c.PartId).ThenBy(c => c.Number),
            "new" => query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.PartId).ThenBy(c => c.Number),
            _ => query.OrderBy(c => c.PartId).ThenBy(c => c.Number)
        };
        var rows = await ordered.Skip((Math.Max(1, page ?? 1) - 1) * size).Take(size).ToListAsync(ct);

        var names = await ModelNamesAsync(db, rows, ct);
        var suggested = await CollisionEndpoints.NumbersAsync(db, rows.Select(r => r.SuggestedPredecessorId).OfType<Guid>().ToList(), ct);
        var taken = await TakenAsync(db, rows.Select(r => r.Id).ToList(), ct);
        var byId = parts.ToDictionary(p => p.Id);
        var items = rows.Select(c =>
        {
            var part = byId[c.PartId];
            var who = taken.GetValueOrDefault(c.Id);
            return new MyCollisionRow(CollisionEndpoints.Row(c, names, suggested), part.Id, part.Name, part.Object, part.Part,
                MyCollisions.MineToFix(c, disciplines), who?.Name, who?.At);
        }).ToList();
        return Results.Ok(new MyCollisionPage(total, items));
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, ToadFlowDb db, TimeProvider time, CancellationToken ct)
    {
        var (disciplines, state) = await UserAsync(db, http.User.UserId(), ct);
        if (disciplines.Length == 0) return Results.Ok(new MySummary([], 0, 0, 0, state?.PreviousSeenAt));
        var parts = await MyCollisions.PartsAsync(db, ct);
        var mark = MyCollisions.Mark(state, time.GetUtcNow());

        var toFix = await MyCollisions.Query(db, disciplines, parts, new MyFilter(), mark).CountAsync(ct);
        var takenByOther = await MyCollisions.Query(db, disciplines, parts, new MyFilter(Scope: "taken"), mark).CountAsync(ct);
        var fresh = await MyCollisions.Query(db, disciplines, parts, new MyFilter(Scope: "all", Since: "visit"), mark).CountAsync(ct);
        return Results.Ok(new MySummary(disciplines, toFix, takenByOther, fresh, state?.PreviousSeenAt));
    }

    /// <summary>Отметка захода: сдвигается, если с прошлой прошло больше 30 минут. Одним запросом — два открытых окна не спорят.</summary>
    private static async Task<IResult> VisitAsync(HttpContext http, ToadFlowDb db, TimeProvider time, CancellationToken ct)
    {
        var (user, now) = (http.User.UserId(), time.GetUtcNow());
        var before = now - ClashUserState.VisitStep;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO clash_user_states (user_id, seen_at, previous_seen_at) VALUES ({user}, {now}, NULL)
            ON CONFLICT (user_id) DO UPDATE SET previous_seen_at = clash_user_states.seen_at, seen_at = EXCLUDED.seen_at
            WHERE clash_user_states.seen_at < {before}
            """, ct);
        return Results.NoContent();
    }

    private static async Task<(string[] Disciplines, ClashUserState? State)> UserAsync(ToadFlowDb db, Guid userId, CancellationToken ct)
    {
        var disciplines = await db.Set<User>().AsNoTracking().Where(u => u.Id == userId).Select(u => u.Disciplines).SingleAsync(ct);
        var state = await db.Set<ClashUserState>().AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId, ct);
        return (disciplines, state);
    }

    /// <summary>Имена моделей строк по ключу (Id в форме N).</summary>
    internal static async Task<Dictionary<string, string>> ModelNamesAsync(ToadFlowDb db, IReadOnlyList<ClashCollision> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(r => new[] { r.ModelA, r.ModelB }).Distinct()
            .Select(k => Guid.TryParse(k, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        return (await db.Set<ClashModel>().AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Name }).ToListAsync(ct))
            .ToDictionary(m => m.Id.ToString("N"), m => m.Name);
    }

    internal sealed record Taker(string Name, DateTimeOffset At);

    /// <summary>Кто последним сменил «кто правит» — только для строк страницы.</summary>
    internal static async Task<Dictionary<Guid, Taker>> TakenAsync(ToadFlowDb db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var events = await db.Set<ClashCollisionEvent>().AsNoTracking()
            .Where(e => ids.Contains(e.CollisionId) && e.Kind == CollisionEventKind.Assignee && e.UserId != null)
            .Join(db.Set<User>().AsNoTracking(), e => e.UserId, u => (Guid?)u.Id, (e, u) => new { e.CollisionId, e.At, u.DisplayName })
            .ToListAsync(ct);
        return events.GroupBy(e => e.CollisionId).ToDictionary(g => g.Key, g =>
        {
            var last = g.MaxBy(e => e.At)!;
            return new Taker(last.DisplayName, last.At);
        });
    }
}
