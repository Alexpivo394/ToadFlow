using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Projects;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Collisions;

/// <param name="MineToFix">«Кто правит» — моя сторона или обе; false — взяла другая сторона.</param>
/// <param name="TakenBy">Кто последним сменил «кто правит» (запись ленты с пользователем); null — никто.</param>
public sealed record MyCollisionRow(
    CollisionRow Collision,
    Guid PartId,
    string PartName,
    string? Object,
    string? Part,
    bool MineToFix,
    string? TakenBy,
    DateTimeOffset? TakenAt);

public sealed record MyCollisionPage(int Total, IReadOnlyList<MyCollisionRow> Items);

/// <param name="Disciplines">Разделы профиля; пусто — «Моих» нет, веб просит обратиться к координатору.</param>
/// <param name="ToFix">Открытые, которые правлю я: моя сторона или обе.</param>
/// <param name="TakenByOther">Открытые, которые взяла другая сторона.</param>
/// <param name="NewSinceVisit">Открытые, найденные впервые или вернувшиеся после прошлого захода.</param>
/// <param name="PreviousSeenAt">Прошлый заход; null — не было, новые — за последние 7 дней.</param>
public sealed record MySummary(IReadOnlyList<string> Disciplines, int ToFix, int TakenByOther, int NewSinceVisit, DateTimeOffset? PreviousSeenAt);

/// <param name="Scope"><c>tofix</c> (по умолчанию), <c>taken</c>, <c>all</c>.</param>
/// <param name="Status"><c>open</c> (по умолчанию), <c>fake</c>, <c>fixed</c>, <c>gone</c>, <c>all</c> или имена статусов через запятую.</param>
/// <param name="Pair">Пара разделов, как <see cref="ClashCollision.DisciplinePair" />.</param>
/// <param name="Model">Ключ модели (<c>ClashModel.SnapshotKey</c>): плагин спрашивает коллизии открытой модели.</param>
/// <param name="Since"><c>visit</c> — только новые с прошлого захода.</param>
public sealed record MyFilter(
    string? Scope = null,
    string? Status = null,
    string? Object = null,
    Guid? PartId = null,
    string? Pair = null,
    string? Level = null,
    string? Model = null,
    string? Since = null);

/// <summary>
///     «Мои коллизии» — одна выборка для веба и плагина. Коллизия моя, если хотя бы один её элемент из модели моего раздела;
///     учитываются только утверждённые проверки.
/// </summary>
public static class MyCollisions
{
    /// <summary>Открытые для «Моих»: к открытым проверки добавляется «не проверена» — её тоже надо разобрать.</summary>
    public static readonly CollisionStatus[] Open =
        [CollisionStatus.New, CollisionStatus.Active, CollisionStatus.Returned, CollisionStatus.FixedPending, CollisionStatus.Unchecked];

    public sealed record ApprovedPart(Guid Id, string Name, string? Object, string? Part);

    public static Task<List<ApprovedPart>> PartsAsync(ToadFlowDb db, CancellationToken ct) =>
        db.Set<ClashPart>().AsNoTracking().Where(p => p.ApprovedVersion != null)
            .Select(p => new ApprovedPart(p.Id, p.Name, p.Object, p.Part)).ToListAsync(ct);

    /// <summary>Проект части, как в списках: без объекта — <see cref="ProjectEndpoints.Unparsed" />.</summary>
    private static string ProjectOf(string? @object) => string.IsNullOrWhiteSpace(@object) ? ProjectEndpoints.Unparsed : @object.Trim();

    /// <summary>От какой отметки считать новые.</summary>
    public static DateTimeOffset Mark(ClashUserState? state, DateTimeOffset now) =>
        state?.PreviousSeenAt ?? (state?.SeenAt ?? now) - ClashUserState.FirstVisitWindow;

    /// <summary>Мои коллизии утверждённых частей по фильтру, без сортировки.</summary>
    public static IQueryable<ClashCollision> Query(ToadFlowDb db, string[] disciplines, IEnumerable<ApprovedPart> parts, MyFilter filter,
        DateTimeOffset mark)
    {
        var ids = parts.Where(p => (filter.PartId is not { } partId || p.Id == partId)
                                   && (string.IsNullOrWhiteSpace(filter.Object) || ProjectOf(p.Object) == filter.Object.Trim()))
            .Select(p => p.Id).ToArray();
        var query = db.Set<ClashCollision>().AsNoTracking()
            .Where(c => ids.Contains(c.PartId) && (disciplines.Contains(c.DisciplineA) || disciplines.Contains(c.DisciplineB)));

        var scope = filter.Scope?.Trim().ToLowerInvariant();
        if (scope != "all")
        {
            var toFix = scope != "taken";
            query = query.Where(c => (c.Assignee == CollisionAssignee.Both
                                      || (c.Assignee == CollisionAssignee.SideA && disciplines.Contains(c.DisciplineA))
                                      || (c.Assignee == CollisionAssignee.SideB && disciplines.Contains(c.DisciplineB))) == toFix);
        }

        if (Statuses(filter.Status) is { } statuses) query = query.Where(c => statuses.Contains(c.Status));
        if (!string.IsNullOrWhiteSpace(filter.Pair)) query = query.Where(c => c.DisciplinePair == filter.Pair);
        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            var level = filter.Level.Trim().ToLower();
            query = query.Where(c => c.LevelA!.ToLower() == level || c.LevelB!.ToLower() == level);
        }

        if (!string.IsNullOrWhiteSpace(filter.Model))
        {
            var key = Guid.TryParse(filter.Model, out var guid) ? guid.ToString("N") : filter.Model.Trim();
            query = query.Where(c => c.ModelA == key || c.ModelB == key);
        }

        if (string.Equals(filter.Since, "visit", StringComparison.OrdinalIgnoreCase)) query = NewSince(db, query, mark);
        return query;
    }

    /// <summary>Найдены впервые или вернулись после отметки.</summary>
    public static IQueryable<ClashCollision> NewSince(ToadFlowDb db, IQueryable<ClashCollision> query, DateTimeOffset mark) =>
        query.Where(c => c.CreatedAt > mark || db.Set<ClashCollisionEvent>().Any(e =>
            e.CollisionId == c.Id && e.Kind == CollisionEventKind.Status && e.ToStatus == CollisionStatus.Returned && e.At > mark));

    /// <summary>Сторона, которую «Беру» ставит пользователю с этими разделами; null — ни один элемент не его.</summary>
    public static CollisionAssignee? Side(ClashCollision collision, string[] disciplines) =>
        (disciplines.Contains(collision.DisciplineA, StringComparer.Ordinal), disciplines.Contains(collision.DisciplineB, StringComparer.Ordinal)) switch
        {
            (true, true) => CollisionAssignee.Both,
            (true, false) => CollisionAssignee.SideA,
            (false, true) => CollisionAssignee.SideB,
            _ => null
        };

    public static bool MineToFix(ClashCollision c, string[] disciplines) =>
        c.Assignee == CollisionAssignee.Both
        || (c.Assignee == CollisionAssignee.SideA && disciplines.Contains(c.DisciplineA, StringComparer.Ordinal))
        || (c.Assignee == CollisionAssignee.SideB && disciplines.Contains(c.DisciplineB, StringComparer.Ordinal));

    /// <returns>null — без фильтра по статусу.</returns>
    private static CollisionStatus[]? Statuses(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "open" => Open,
        "all" => null,
        "fake" => [CollisionStatus.Fake],
        "fixed" => [CollisionStatus.Fixed],
        "gone" => [CollisionStatus.GoneByRules],
        _ => status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<CollisionStatus>(s, true, out var parsed) ? parsed : (CollisionStatus?)null).OfType<CollisionStatus>().ToArray()
    };
}
