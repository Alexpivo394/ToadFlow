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
    DateTimeOffset? TakenAt,
    bool Unseen = false);

public sealed record MyCollisionPage(int Total, IReadOnlyList<MyCollisionRow> Items);

/// <param name="Disciplines">Разделы профиля; пусто — «Моих» нет, веб просит обратиться к координатору.</param>
/// <param name="ToFix">Открытые, которые правлю я: моя сторона или обе.</param>
/// <param name="TakenByOther">Открытые, которые взяла другая сторона.</param>
/// <param name="NewSinceVisit">Открытые, найденные впервые или вернувшиеся после прошлого захода.</param>
/// <param name="PreviousSeenAt">Прошлый заход; null — не было, новые — за последние 7 дней.</param>
/// <param name="NewForMe">Открытые, которые пользователь не открывал с тех пор, как они появились или вернулись.</param>
public sealed record MySummary(IReadOnlyList<string> Disciplines, int ToFix, int TakenByOther, int NewSinceVisit, DateTimeOffset? PreviousSeenAt,
    int NewForMe = 0);

/// <param name="Scope"><c>tofix</c> (по умолчанию), <c>taken</c>, <c>all</c>.</param>
/// <param name="Status"><c>open</c> (по умолчанию), <c>fake</c>, <c>fixed</c>, <c>gone</c>, <c>all</c> или имена статусов через запятую.</param>
/// <param name="Pair">Пара разделов, как <see cref="ClashCollision.DisciplinePair" />.</param>
/// <param name="Model">Ключ модели (<c>ClashModel.SnapshotKey</c>): плагин спрашивает коллизии открытой модели.</param>
/// <param name="Since"><c>visit</c> — только новые с прошлого захода (плагин до 3.4.0); <c>unseen</c> — новые для меня: не открывал с тех пор, как появилась или вернулась.</param>
/// <param name="Element">Элемент любой из сторон: <c>{модель:N}:{UniqueId}</c>.</param>
/// <param name="Category">Категория элемента любой из сторон.</param>
/// <param name="Q">Номер коллизии, ElementId или часть UniqueId — как в списке проверки (<see cref="CollisionFilter.Q" />).</param>
public sealed record MyFilter(
    string? Scope = null,
    string? Status = null,
    string? Object = null,
    Guid? PartId = null,
    string? Pair = null,
    string? Level = null,
    string? Model = null,
    string? Since = null,
    string? Element = null,
    string? Category = null,
    string? Q = null);

/// <summary>
///     «Мои коллизии» — одна выборка для веба и плагина. Коллизия моя, если хотя бы один её элемент из модели моего раздела;
///     учитываются только утверждённые проверки.
/// </summary>
public static class MyCollisions
{
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
    /// <param name="userId">Чьи отметки просмотра считать для <c>since=unseen</c>.</param>
    /// <param name="modelKeys">
    ///     Ключи моделей фильтра <see cref="MyFilter.Model" />, если имя уже разобрано (<see cref="MyEndpoints.ModelKeysAsync" />); null —
    ///     ключ из фильтра как есть.
    /// </param>
    public static IQueryable<ClashCollision> Query(ToadFlowDb db, string[] disciplines, IEnumerable<ApprovedPart> parts, MyFilter filter,
        DateTimeOffset mark, Guid userId = default, IReadOnlyCollection<string>? modelKeys = null)
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
        query = CollisionFilter.ByLevel(query, filter.Level);

        if (modelKeys is not null) query = CollisionFilter.ByModels(query, modelKeys);
        else if (!string.IsNullOrWhiteSpace(filter.Model))
        {
            var key = Guid.TryParse(filter.Model, out var guid) ? guid.ToString("N") : filter.Model.Trim();
            query = query.Where(c => c.ModelA == key || c.ModelB == key);
        }

        query = CollisionFilter.ByElement(query, filter.Element);
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim();
            query = query.Where(c => c.CategoryA == category || c.CategoryB == category);
        }

        query = CollisionFilter.ByText(query, filter.Q);

        if (string.Equals(filter.Since, "visit", StringComparison.OrdinalIgnoreCase)) query = NewSince(db, query, mark);
        else if (string.Equals(filter.Since, "unseen", StringComparison.OrdinalIgnoreCase)) query = Unseen(db, query, userId);
        return query;
    }

    /// <summary>Новые для пользователя: открытые, и он не открывал их карточку после того, как они появились или вернулись.</summary>
    public static IQueryable<ClashCollision> Unseen(ToadFlowDb db, IQueryable<ClashCollision> query, Guid userId) =>
        query.Where(c => CollisionEndpoints.Open.Contains(c.Status)
                         && !db.Set<ClashCollisionSeen>().Any(s => s.UserId == userId && s.CollisionId == c.Id && s.SeenAt >= c.OpenedAt));

    /// <summary>Id непросмотренных пользователем среди <paramref name="ids" />.</summary>
    public static async Task<HashSet<Guid>> UnseenAsync(ToadFlowDb db, IReadOnlyCollection<Guid> ids, Guid userId, CancellationToken ct) =>
        ids.Count == 0
            ? []
            : (await Unseen(db, db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.Id)), userId).Select(c => c.Id).ToListAsync(ct)).ToHashSet();

    /// <summary>Отметка просмотра: вставка или сдвиг <c>seen_at</c> одним запросом — два окна не спорят.</summary>
    public static Task MarkSeenAsync(ToadFlowDb db, Guid userId, IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO clash_collision_seen (user_id, collision_id, seen_at) SELECT {userId}, id, {now} FROM unnest({ids.ToArray()}) AS id
            ON CONFLICT (user_id, collision_id) DO UPDATE SET seen_at = EXCLUDED.seen_at
            """, ct);

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
    internal static CollisionStatus[]? Statuses(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "open" => CollisionEndpoints.Open,
        "all" => null,
        "fake" => [CollisionStatus.Fake],
        "fixed" => [CollisionStatus.Fixed],
        "gone" => [CollisionStatus.GoneByRules],
        _ => status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<CollisionStatus>(s, true, out var parsed) ? parsed : (CollisionStatus?)null).OfType<CollisionStatus>().ToArray()
    };
}
