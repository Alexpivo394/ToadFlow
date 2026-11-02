using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Массовое действие над коллизиями — то же, что поштучное.</summary>
public enum BulkAction
{
    /// <summary>«Беру»: «кто правит» — моя сторона.</summary>
    Take,

    /// <summary>«Кто правит» — сторона из запроса.</summary>
    Assignee,

    /// <summary>Статус «фиктивная»; примечание обязательно — одно на все.</summary>
    Fake,

    /// <summary>Статус «исправлена (ждёт прогона)».</summary>
    FixedPending,

    /// <summary>Одно примечание в ленту каждой.</summary>
    Note,

    /// <summary>Отметить просмотренными — без записи в ленту.</summary>
    Seen
}

/// <summary>Фильтр «все по фильтру»: поля <see cref="CollisionFilter" /> или, с <see cref="My" />, — <see cref="MyFilter" /> в пределах части.</summary>
/// <param name="My">Выбор сделан в «Моих»: фильтр «Моих» пользователя, ограниченный частью запроса.</param>
/// <param name="Scope">Только для «Моих»: <c>tofix</c>, <c>taken</c>, <c>all</c>.</param>
/// <param name="Object">Только для «Моих»: проект.</param>
/// <param name="Since">Только для «Моих»: новые.</param>
public sealed record BulkFilter(
    string? Status = null,
    string? Check = null,
    string? Discipline = null,
    string? Pair = null,
    CollisionAssignee? Assignee = null,
    string? Level = null,
    string? Model = null,
    string? Q = null,
    int? StuckDays = null,
    string? Element = null,
    string? Category = null,
    bool My = false,
    string? Scope = null,
    string? Object = null,
    string? Since = null);

/// <summary>Массовое действие: ровно одно из <see cref="Ids" /> и <see cref="Filter" />.</summary>
/// <param name="ExpectedCount">С <see cref="Filter" /> обязательно: сколько коллизий человек видел под фильтром; не совпало — 409, ничего не меняется.</param>
public sealed record BulkRequest(
    Guid PartId,
    BulkAction Action,
    string? Note = null,
    CollisionAssignee? Assignee = null,
    IReadOnlyList<Guid>? Ids = null,
    BulkFilter? Filter = null,
    int? ExpectedCount = null);

/// <summary>Коллизия, которую действие не изменило, и почему — тот же текст, что у поштучного отказа.</summary>
public sealed record BulkRefusal(int Number, string Reason);

/// <param name="Unchanged">Коллизии, которые уже были такими, как просили (например, «кто правит» уже тот же): не изменены, но и не отказ.</param>
public sealed record BulkResult(int Done, IReadOnlyList<BulkRefusal> Refused, int Unchanged = 0);

/// <summary>
///     Массовые действия: одна транзакция под блокировкой части, права и переходы — по каждой коллизии той же логикой, что поштучно
///     (<see cref="CollisionChanges" />). Отказы по коллизии не мешают остальным.
/// </summary>
public static class BulkEndpoints
{
    public const int MaxCount = 5000;
    public const string TooMany = "За раз — не больше 5 000 коллизий";

    public static async Task<IResult> BulkAsync(BulkRequest request, ToadFlowDb db, HttpContext http, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        if ((request.Ids is null) == (request.Filter is null)) return Bad("Укажите коллизии: список или фильтр");
        if (request.Ids is { } list && list.Distinct().Count() > MaxCount) return Bad(TooMany);
        if (Invalid(request) is { } error) return Bad(error);
        if (request.Filter is not null && request.ExpectedCount is null) return Bad("Укажите, сколько коллизий выбрано");

        var actor = await CollisionEndpoints.ActorAsync(db, http.User, ct);
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Set<ClashPart>().AnyAsync(p => p.Id == request.PartId, ct)) return Results.NotFound();
        await CollisionEndpoints.LockPartAsync(db, request.PartId, ct);

        var selected = request.Filter is { } filter
            ? await FilteredAsync(db, request.PartId, filter, actor, now, ct)
            : db.Set<ClashCollision>().Where(c => c.PartId == request.PartId && request.Ids!.Contains(c.Id));
        var count = await selected.CountAsync(ct);
        if (request.Filter is not null && count != request.ExpectedCount)
            return Results.Json(new { error = $"Коллизий по фильтру теперь {count} — проверьте и повторите", count }, statusCode: StatusCodes.Status409Conflict);
        if (count > MaxCount) return Bad(TooMany);

        var collisions = await selected.OrderBy(c => c.Number).ToListAsync(ct);
        if (request.Action == BulkAction.Seen)
        {
            // Отметка просмотра — личная, без записи в ленту и без события для других.
            await MyCollisions.MarkSeenAsync(db, actor.UserId, collisions.Select(c => c.Id).ToList(), now, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new BulkResult(collisions.Count, []));
        }

        var fakeIds = collisions.Where(c => c.Status == CollisionStatus.Fake).Select(c => c.Id).ToList();
        var fakeSetBy = fakeIds.Count == 0
            ? []
            : (await db.Set<ClashCollisionEvent>().AsNoTracking()
                .Where(e => fakeIds.Contains(e.CollisionId) && e.Kind == CollisionEventKind.Status && e.ToStatus == CollisionStatus.Fake)
                .Select(e => new { e.CollisionId, e.At, e.UserId }).ToListAsync(ct))
            .GroupBy(e => e.CollisionId).ToDictionary(g => g.Key, g => g.MaxBy(e => e.At)!.UserId);

        var bulkId = Guid.CreateVersion7();
        var (done, unchanged, refused) = (0, 0, new List<BulkRefusal>());
        foreach (var collision in collisions)
        {
            try
            {
                var entries = request.Action switch
                {
                    BulkAction.Take => CollisionChanges.Take(collision, actor, now),
                    BulkAction.Assignee => CollisionChanges.Assignee(collision, request.Assignee!.Value, null, actor, now),
                    BulkAction.Fake => CollisionChanges.Status(collision, CollisionStatus.Fake, request.Note, actor, fakeSetBy.GetValueOrDefault(collision.Id), now),
                    BulkAction.FixedPending => CollisionChanges.Status(collision, CollisionStatus.FixedPending, request.Note, actor,
                        fakeSetBy.GetValueOrDefault(collision.Id), now),
                    BulkAction.Note => CollisionChanges.Note(collision, request.Note, actor, now),
                    _ => throw new CollisionTransitionException("Такого действия нет")
                };
                foreach (var entry in entries) entry.BulkId = bulkId;
                db.Set<ClashCollisionEvent>().AddRange(entries);
                collision.UpdatedAt = now;
                done++;
            }
            catch (CollisionUnchangedException)
            {
                unchanged++;
            }
            catch (Exception exception) when (exception is CollisionTransitionException or CollisionForbiddenException)
            {
                refused.Add(new BulkRefusal(collision.Number, exception.Message));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (done > 0) events.Publish(new ClashCollisionsChanged(http.User.Actor(), request.PartId, null));
        return Results.Ok(new BulkResult(done, refused, unchanged));
    }

    /// <returns>Почему запрос нельзя выполнить ни для одной коллизии; null — можно.</returns>
    private static string? Invalid(BulkRequest request) => request.Action switch
    {
        BulkAction.Fake when string.IsNullOrWhiteSpace(request.Note) => "Для фиктивной коллизии нужно примечание: почему она фиктивная",
        BulkAction.Fake or BulkAction.FixedPending when request.Note?.Trim().Length > CollisionFeed.MaxNoteLength =>
            $"Примечание — до {CollisionFeed.MaxNoteLength} символов",
        BulkAction.Note when request.Note?.Trim().Length is null or 0 or > CollisionFeed.MaxNoteLength =>
            $"Примечание — от 1 до {CollisionFeed.MaxNoteLength} символов",
        BulkAction.Assignee when request.Assignee is null => "Выберите, кто правит",
        _ => null
    };

    /// <summary>Коллизии части под фильтром — проверки или, с <see cref="BulkFilter.My" />, «Моих» пользователя.</summary>
    private static async Task<IQueryable<ClashCollision>> FilteredAsync(ToadFlowDb db, Guid partId, BulkFilter filter, CollisionActor actor,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!filter.My)
        {
            var names = await CollisionEndpoints.ModelNamesAsync(db, partId, ct);
            return new CollisionFilter(filter.Status, filter.Check, filter.Discipline, filter.Pair, filter.Assignee, filter.Level, filter.Model, filter.Q,
                    filter.StuckDays, filter.Element, filter.Category)
                .Apply(db.Set<ClashCollision>().Where(c => c.PartId == partId), db, names, now);
        }

        var (disciplines, state) = await MyEndpoints.UserAsync(db, actor.UserId, ct);
        var parts = (await MyCollisions.PartsAsync(db, ct)).Where(p => p.Id == partId).ToList();
        var my = await MyEndpoints.ModelKeyAsync(db, new MyFilter(filter.Scope, filter.Status, filter.Object, partId, filter.Pair, filter.Level,
            filter.Model, filter.Since, filter.Element, filter.Category), ct);
        // «Мои» читают без отслеживания; массовое действие меняет коллизии — те же строки по Id, с отслеживанием.
        var ids = MyCollisions.Query(db, disciplines, parts, my, MyCollisions.Mark(state, now), actor.UserId).Select(c => c.Id);
        return db.Set<ClashCollision>().Where(c => ids.Contains(c.Id));
    }

    private static IResult Bad(string error) => Results.BadRequest(new { error });
}
