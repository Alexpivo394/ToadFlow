using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Viewer;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Последний утверждённый прогон части: состояние, версия и итог сверки.</summary>
public sealed record ApprovedRunView(DraftRunSummary Run, int? Version, ApprovedRunSummary? Summary);

public sealed record StatusCount(CollisionStatus Status, int Count);

/// <param name="Total">Открытые коллизии: новые, активные, вернувшиеся, отмеченные исправленными.</param>
/// <param name="Statuses">Число коллизий в каждом статусе.</param>
/// <param name="Checks">Включённые проверки утверждённой версии в порядке матрицы, с числом открытых коллизий.</param>
public sealed record CollisionSummary(int Total, IReadOnlyList<StatusCount> Statuses, IReadOnlyList<CheckCount> Checks);

/// <param name="SuggestedPredecessorNumber">Номер коллизии, с которой стоит объединить эту; null — предложения нет.</param>
public sealed record CollisionRow(
    Guid Id,
    int Number,
    CollisionStatus Status,
    CollisionAssignee Assignee,
    bool FoundNow,
    float DepthMm,
    double X,
    double Y,
    double Z,
    string ModelA,
    string ModelNameA,
    string UniqueIdA,
    long ElementIdA,
    string DisciplineA,
    string CategoryA,
    string? FamilyA,
    string? TypeA,
    string? LevelA,
    string ModelB,
    string ModelNameB,
    string UniqueIdB,
    long ElementIdB,
    string DisciplineB,
    string CategoryB,
    string? FamilyB,
    string? TypeB,
    string? LevelB,
    IReadOnlyList<string> Checks,
    int FirstVersion,
    int LastFoundVersion,
    Guid? SuggestedPredecessorId,
    int? SuggestedPredecessorNumber);

public sealed record CollisionPage(int Total, IReadOnlyList<CollisionRow> Items);

/// <summary>Запись ленты для показа: событие коллизии или образец глубины.</summary>
/// <param name="Kind">Имя <see cref="CollisionEventKind" /> или <see cref="CollisionFeed.DepthKind" />.</param>
/// <param name="UserId">Автор записи — для карточки коллеги (<c>/api/users/{id}/card</c>); null — система.</param>
public sealed record FeedEntry(
    DateTimeOffset At,
    string Kind,
    string? UserName,
    string Text,
    CollisionStatus? FromStatus,
    CollisionStatus? ToStatus,
    int? Version,
    float? DepthMm,
    Guid? UserId = null);

public sealed record CollisionCard(CollisionRow Collision, IReadOnlyList<FeedEntry> Feed);

public sealed record CollisionStatusRequest(CollisionStatus Status, string? Note);

/// <param name="Note">«Передать» с примечанием: оно идёт в ленту отдельной записью.</param>
public sealed record CollisionAssigneeRequest(CollisionAssignee Assignee, string? Note = null);

public sealed record CollisionNoteRequest(string? Text);

public sealed record CollisionMergeRequest(Guid PredecessorId);

public static class CollisionFeed
{
    /// <summary>Образец глубины прогона в ленте.</summary>
    public const string DepthKind = "Depth";

    public const int MaxNoteLength = 4000;

    public static string AssigneeText(CollisionAssignee assignee) => assignee switch
    {
        CollisionAssignee.SideA => "сторона A",
        CollisionAssignee.SideB => "сторона B",
        _ => "обе стороны"
    };
}

/// <summary>
///     Коллизии утверждённой проверки: прогон, сводка, список, карточка с лентой, статусы, «кто правит», примечания,
///     объединение с прежней коллизией и 3D. Читает любая роль; статусы, «кто правит» и примечания меняет проектировщик
///     и старше, объединение и прогоны — координатор.
/// </summary>
public static class CollisionEndpoints
{
    internal static readonly CollisionStatus[] Open = [CollisionStatus.New, CollisionStatus.Active, CollisionStatus.Returned, CollisionStatus.FixedPending];

    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashCollisions(this RouteGroupBuilder members)
    {
        var approved = members.MapGroup("/clash/parts/{id:guid}/approved");
        approved.MapGet("/run", RunAsync);
        approved.MapGet("/summary", SummaryAsync);
        approved.MapGet("/collisions", ListAsync);
        var approvedWrite = approved.MapGroup("").RequireAuthorization(Policies.Operator);
        approvedWrite.MapPost("/run", StartAsync);
        approvedWrite.MapPost("/run/cancel", CancelAsync);

        var collision = members.MapGroup("/clash/collisions/{id:guid}");
        collision.MapGet("", CardAsync);
        collision.MapGet("/scene", ViewerEndpoints.CollisionSceneAsync);
        collision.MapGet("/meshes", ViewerEndpoints.CollisionMeshesAsync);
        var write = collision.MapGroup("").RequireAuthorization(Policies.Designer);
        write.MapPost("/status", StatusAsync);
        write.MapPost("/assignee", AssigneeAsync);
        write.MapPost("/notes", NoteAsync);
        write.MapPost("/take", TakeAsync);
        collision.MapPost("/merge", MergeAsync).RequireAuthorization(Policies.Operator);
    }

    private static async Task<IResult> RunAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var run = await db.Set<EngineRun>().AsNoTracking().Where(r => r.PartId == id && r.Kind == EngineRunKind.Approved)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        return run is null ? Results.NoContent() : Results.Ok(new ApprovedRunView(DraftRunSummary.Of(run), run.Version, run.Summary));
    }

    private static async Task<IResult> StartAsync(Guid id, ApprovedRuns runs, HttpContext http, CancellationToken ct)
    {
        var run = await runs.EnqueueAsync(id, null, [], http.User.Actor(), ct);
        return run is null
            ? Results.BadRequest(new { error = "Прогнать нельзя: часть не утверждена или утверждённую конфигурацию нельзя прогнать (см. «События системы»)" })
            : Results.Ok(new ApprovedRunView(DraftRunSummary.Of(run), run.Version, run.Summary));
    }

    private static async Task<IResult> CancelAsync(Guid id, ToadFlowDb db, DraftHits hits, TimeProvider time, HttpContext http, IDomainEvents events,
        CancellationToken ct)
    {
        // Сверка идёт под той же блокировкой: отмена либо успевает до неё, либо видит прогон законченным.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockPartAsync(db, id, ct);
        var active = await db.Set<EngineRun>()
            .Where(r => r.PartId == id && r.Kind == EngineRunKind.Approved && (r.State == EngineRunState.Queued || r.State == EngineRunState.Running))
            .ToListAsync(ct);
        foreach (var run in active)
        {
            await hits.DiscardAsync(run.Id, ct);
            run.State = EngineRunState.Cancelled;
            run.LeaseUntil = null;
            run.FinishedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (active.Count > 0) events.Publish(new ClashDraftChanged(http.User.Actor(), id));
        return active.Count == 0 ? Results.NotFound(new { error = "Прогона нет" }) : Results.Ok();
    }

    private static async Task<IResult> SummaryAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();
        var collisions = db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == id);
        var statuses = await collisions.GroupBy(c => c.Status).Select(g => new StatusCount(g.Key, g.Count())).ToListAsync(ct);
        var open = collisions.Where(c => Open.Contains(c.Status));
        var counts = await open.SelectMany(c => c.Checks).GroupBy(c => c).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var checks = new List<CheckCount>();
        if (part.ApprovedVersion is { } version)
        {
            var json = await db.Set<ClashPartVersion>().Where(v => v.PartId == id && v.Version == version).Select(v => v.ConfigurationJson).SingleAsync(ct);
            var config = ConfigurationJson.Deserialize(json);
            for (var i = 0; i < config.Groups.Count; i++)
            for (var j = i; j < config.Groups.Count; j++)
            {
                var (a, b) = (config.Groups[i], config.Groups[j]);
                if (config.CellFor(a.Id, b.Id) is not { Enabled: true } cell) continue;
                var key = DraftResultEndpoints.CheckKey(a.Id, b.Id);
                checks.Add(new CheckCount(key, a.Id, a.Name, b.Id, b.Name, counts.GetValueOrDefault(key), cell.ToleranceMm));
            }
        }

        return Results.Ok(new CollisionSummary(statuses.Where(s => Open.Contains(s.Status)).Sum(s => s.Count), statuses, checks));
    }

    /// <param name="status">Статусы через запятую; <c>all</c> — все; не задано — открытые.</param>
    /// <param name="q">Номер коллизии, UniqueId или ElementId элемента.</param>
    /// <param name="sort"><c>depth</c> — глубже сначала; иначе по номеру.</param>
    /// <param name="stuckDays">Только висящие: открытые, без записей в ленте столько дней (из обзора координатора).</param>
    private static async Task<IResult> ListAsync(Guid id, string? check, string? status, CollisionAssignee? assignee, string? level, string? model, string? q,
        string? sort, int? page, int? pageSize, int? stuckDays, ToadFlowDb db, TimeProvider time, CancellationToken ct)
    {
        var query = db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == id);
        if (stuckDays is { } days and > 0) query = Overview.OverviewEndpoints.Stuck(db, query, time.GetUtcNow() - TimeSpan.FromDays(days));
        if (string.IsNullOrWhiteSpace(status)) query = query.Where(c => Open.Contains(c.Status));
        else if (!status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<CollisionStatus>(s, true, out var parsed) ? parsed : (CollisionStatus?)null).OfType<CollisionStatus>().ToArray();
            query = query.Where(c => wanted.Contains(c.Status));
        }

        if (!string.IsNullOrWhiteSpace(check)) query = query.Where(c => c.Checks.Contains(check));
        if (assignee is { } who) query = query.Where(c => c.Assignee == who);
        if (!string.IsNullOrWhiteSpace(level))
        {
            var wanted = level.Trim().ToLower();
            query = query.Where(c => c.LevelA!.ToLower() == wanted || c.LevelB!.ToLower() == wanted);
        }

        var names = await ModelNamesAsync(db, id, ct);
        if (!string.IsNullOrWhiteSpace(model))
        {
            var key = names.FirstOrDefault(m => m.Value == model).Key ?? model;
            query = query.Where(c => c.ModelA == key || c.ModelB == key);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim();
            var number = int.TryParse(text, out var n) ? n : -1;
            var elementId = long.TryParse(text, out var e) ? e : -1;
            query = query.Where(c => c.Number == number || c.ElementIdA == elementId || c.ElementIdB == elementId
                                     || c.UniqueIdA.Contains(text) || c.UniqueIdB.Contains(text));
        }

        var total = await query.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? DraftResultEndpoints.DefaultPageSize, 1, DraftResultEndpoints.MaxPageSize);
        var ordered = sort == "depth" ? query.OrderByDescending(c => c.DepthMm).ThenBy(c => c.Number) : query.OrderBy(c => c.Number);
        var rows = await ordered.Skip((Math.Max(1, page ?? 1) - 1) * size).Take(size).ToListAsync(ct);
        var suggested = await NumbersAsync(db, rows.Select(r => r.SuggestedPredecessorId).OfType<Guid>().ToList(), ct);
        return Results.Ok(new CollisionPage(total, rows.Select(r => Row(r, names, suggested)).ToList()));
    }

    private static async Task<IResult> CardAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var collision = await db.Set<ClashCollision>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (collision is null) return Results.NotFound();
        var names = await ModelNamesAsync(db, collision.PartId, ct);
        var suggested = await NumbersAsync(db, collision.SuggestedPredecessorId is { } s ? [s] : [], ct);

        var events = await db.Set<ClashCollisionEvent>().AsNoTracking().Where(e => e.CollisionId == id).ToListAsync(ct);
        var userIds = events.Select(e => e.UserId).OfType<Guid>().Distinct().ToList();
        var users = await db.Set<User>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var samples = await db.Set<ClashCollisionSample>().AsNoTracking().Where(s => s.CollisionId == id).ToListAsync(ct);
        var feed = events
            .Select(e => new FeedEntry(e.At, e.Kind.ToString(), e.UserId is { } u ? users.GetValueOrDefault(u, "") : null, e.Text, e.FromStatus, e.ToStatus,
                e.Version, null, e.UserId))
            .Concat(samples.Select(s => new FeedEntry(s.At, CollisionFeed.DepthKind, null, $"Глубина {s.DepthMm:0} мм", null, null, s.Version, s.DepthMm)))
            .OrderByDescending(e => e.At).ThenBy(e => e.Kind == CollisionFeed.DepthKind ? 1 : 0)
            .ToList();
        return Results.Ok(new CollisionCard(Row(collision, names, suggested), feed));
    }

    private static Task<IResult> StatusAsync(Guid id, CollisionStatusRequest request, ToadFlowDb db, HttpContext http, TimeProvider time,
        IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, http, time, events, ct, async collision =>
            collision.Status == CollisionStatus.Fake && request.Status != CollisionStatus.Fake && !http.User.IsStaff()
            && await FakeSetByAsync(db, collision.Id, ct) != http.User.UserId()
                ? "Снять фиктивную, поставленную другим, может координатор"
                : null,
            (collision, now, user) =>
        {
            var next = CollisionTransitions.Manual(collision.Status, request.Status, collision.FoundNow, request.Note);
            var entry = Event(collision.Id, now, user, CollisionEventKind.Status, request.Note?.Trim() ?? "");
            entry.FromStatus = collision.Status;
            entry.ToStatus = next;
            collision.Status = next;
            return entry;
        });

    private static Task<IResult> AssigneeAsync(Guid id, CollisionAssigneeRequest request, ToadFlowDb db, HttpContext http, TimeProvider time,
        IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, http, time, events, ct, (collision, now, user) =>
        {
            if (collision.Assignee == request.Assignee) throw new CollisionTransitionException("«Кто правит» уже такой");
            var note = request.Note?.Trim() ?? "";
            if (note.Length > CollisionFeed.MaxNoteLength) throw new CollisionTransitionException($"Примечание — до {CollisionFeed.MaxNoteLength} символов");
            var entry = Event(collision.Id, now, user, CollisionEventKind.Assignee,
                $"Кто правит: {CollisionFeed.AssigneeText(collision.Assignee)} → {CollisionFeed.AssigneeText(request.Assignee)}");
            collision.Assignee = request.Assignee;
            // На микросекунду позже — лента по времени показывает примечание после смены (точность времени в базе — микросекунды).
            if (note.Length > 0) db.Set<ClashCollisionEvent>().Add(Event(collision.Id, now.AddMicroseconds(1), user, CollisionEventKind.Note, note));
            return entry;
        });

    /// <summary>«Беру»: «кто правит» — моя сторона, а если оба элемента из моих разделов — обе стороны.</summary>
    private static async Task<IResult> TakeAsync(Guid id, ToadFlowDb db, HttpContext http, TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        var me = await db.Set<User>().AsNoTracking().Where(u => u.Id == http.User.UserId()).Select(u => new { u.DisplayName, u.Disciplines }).SingleAsync(ct);
        return await ChangeAsync(id, db, http, time, events, ct, (collision, now, user) =>
        {
            var side = MyCollisions.Side(collision, me.Disciplines)
                       ?? throw new CollisionTransitionException("Взять можно только коллизию своего раздела: ни один её элемент не из ваших разделов");
            var text = collision.Assignee == side
                ? $"взял(а) {me.DisplayName}: {CollisionFeed.AssigneeText(side)}"
                : $"взял(а) {me.DisplayName}: {CollisionFeed.AssigneeText(collision.Assignee)} → {CollisionFeed.AssigneeText(side)}";
            collision.Assignee = side;
            return Event(collision.Id, now, user, CollisionEventKind.Assignee, text);
        });
    }

    private static Task<IResult> NoteAsync(Guid id, CollisionNoteRequest request, ToadFlowDb db, HttpContext http, TimeProvider time, IDomainEvents events,
        CancellationToken ct) =>
        ChangeAsync(id, db, http, time, events, ct, (collision, now, user) =>
        {
            var text = request.Text?.Trim() ?? "";
            if (text.Length is 0 or > CollisionFeed.MaxNoteLength)
                throw new CollisionTransitionException($"Примечание — от 1 до {CollisionFeed.MaxNoteLength} символов");
            return Event(collision.Id, now, user, CollisionEventKind.Note, text);
        });

    /// <summary>
    ///     Объединение с прежней коллизией: у прежней — пара этой (новые UniqueId, точка, глубина), её номер, статус и лента
    ///     остаются; эта удаляется. Прежняя, если эта найдена, проходит переход «найдена». Прежняя, которую последний прогон
    ///     нашёл, не объединяется: её пара ещё есть в моделях и следующим прогоном вернулась бы новой коллизией.
    /// </summary>
    private static async Task<IResult> MergeAsync(Guid id, CollisionMergeRequest request, ToadFlowDb db, HttpContext http, TimeProvider time,
        IDomainEvents events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await PartOfAsync(db, id, ct) is not { } partId) return Results.NotFound();
        await LockPartAsync(db, partId, ct);
        var recreated = await db.Set<ClashCollision>().SingleOrDefaultAsync(c => c.Id == id, ct);
        var predecessor = await db.Set<ClashCollision>().SingleOrDefaultAsync(c => c.Id == request.PredecessorId, ct);
        if (recreated is null || predecessor is null) return Results.NotFound();
        if (predecessor.Id == recreated.Id || predecessor.PartId != recreated.PartId)
            return Results.BadRequest(new { error = "Объединять можно только две разные коллизии одной части" });
        if (predecessor.FoundNow)
            return Results.BadRequest(new { error = $"Коллизия №{predecessor.Number} найдена последним прогоном: её пара ещё есть в моделях" });

        var now = time.GetUtcNow();
        var changes = new List<string>();
        if (predecessor.ModelA != recreated.ModelA || predecessor.UniqueIdA != recreated.UniqueIdA)
            changes.Add($"элемент A: {predecessor.UniqueIdA} → {recreated.UniqueIdA}");
        if (predecessor.ModelB != recreated.ModelB || predecessor.UniqueIdB != recreated.UniqueIdB)
            changes.Add($"элемент B: {predecessor.UniqueIdB} → {recreated.UniqueIdB}");

        db.Set<ClashCollision>().Remove(recreated);
        await db.SaveChangesAsync(ct);

        var was = predecessor.Status;
        (predecessor.ModelA, predecessor.UniqueIdA, predecessor.ElementIdA, predecessor.DisciplineA, predecessor.SetsA, predecessor.CategoryA) =
            (recreated.ModelA, recreated.UniqueIdA, recreated.ElementIdA, recreated.DisciplineA, recreated.SetsA, recreated.CategoryA);
        (predecessor.FamilyA, predecessor.TypeA, predecessor.LevelA) = (recreated.FamilyA, recreated.TypeA, recreated.LevelA);
        (predecessor.ModelB, predecessor.UniqueIdB, predecessor.ElementIdB, predecessor.DisciplineB, predecessor.SetsB, predecessor.CategoryB) =
            (recreated.ModelB, recreated.UniqueIdB, recreated.ElementIdB, recreated.DisciplineB, recreated.SetsB, recreated.CategoryB);
        (predecessor.FamilyB, predecessor.TypeB, predecessor.LevelB) = (recreated.FamilyB, recreated.TypeB, recreated.LevelB);
        (predecessor.DisciplinePair, predecessor.Checks, predecessor.X, predecessor.Y, predecessor.Z, predecessor.DepthMm) =
            (recreated.DisciplinePair, recreated.Checks, recreated.X, recreated.Y, recreated.Z, recreated.DepthMm);
        (predecessor.FingerprintA, predecessor.FingerprintB, predecessor.FoundNow) = (recreated.FingerprintA, recreated.FingerprintB, recreated.FoundNow);
        (predecessor.LastFoundVersion, predecessor.LastFoundRunId, predecessor.UpdatedAt) = (recreated.LastFoundVersion, recreated.LastFoundRunId, now);

        var user = http.User.UserId();
        db.Set<ClashCollisionEvent>().Add(Event(predecessor.Id, now, user, CollisionEventKind.Merged,
            $"Объединена с №{recreated.Number}: {string.Join("; ", changes)}"));
        if (recreated.FoundNow)
        {
            var (next, note) = CollisionTransitions.Found(was);
            if (next != was)
            {
                var entry = Event(predecessor.Id, now, null, CollisionEventKind.Status, note ?? "");
                (entry.FromStatus, entry.ToStatus) = (was, next);
                db.Set<ClashCollisionEvent>().Add(entry);
                predecessor.Status = next;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new ClashCollisionsChanged(http.User.Actor(), predecessor.PartId, predecessor.Id));
        return Results.Ok();
    }

    /// <summary>Кто поставил «фиктивную» — по последней записи ленты; null — система.</summary>
    private static Task<Guid?> FakeSetByAsync(ToadFlowDb db, Guid collisionId, CancellationToken ct) =>
        db.Set<ClashCollisionEvent>().AsNoTracking()
            .Where(e => e.CollisionId == collisionId && e.Kind == CollisionEventKind.Status && e.ToStatus == CollisionStatus.Fake)
            .OrderByDescending(e => e.At).Select(e => e.UserId).FirstOrDefaultAsync(ct);

    private static Task<IResult> ChangeAsync(Guid id, ToadFlowDb db, HttpContext http, TimeProvider time, IDomainEvents events, CancellationToken ct,
        Func<ClashCollision, DateTimeOffset, Guid, ClashCollisionEvent> change) =>
        ChangeAsync(id, db, http, time, events, ct, _ => Task.FromResult<string?>(null), change);

    /// <param name="forbidden">Почему этому пользователю правка запрещена (403); null — можно.</param>
    private static async Task<IResult> ChangeAsync(Guid id, ToadFlowDb db, HttpContext http, TimeProvider time, IDomainEvents events, CancellationToken ct,
        Func<ClashCollision, Task<string?>> forbidden, Func<ClashCollision, DateTimeOffset, Guid, ClashCollisionEvent> change)
    {
        // Под блокировкой части, как сверка: правка видит статус после сверки и не затирается ею.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await PartOfAsync(db, id, ct) is not { } partId) return Results.NotFound();
        await LockPartAsync(db, partId, ct);
        var collision = await db.Set<ClashCollision>().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (collision is null) return Results.NotFound();
        if (await forbidden(collision) is { } reason) return Results.Json(new { error = reason }, statusCode: StatusCodes.Status403Forbidden);
        var now = time.GetUtcNow();
        try
        {
            db.Set<ClashCollisionEvent>().Add(change(collision, now, http.User.UserId()));
        }
        catch (CollisionTransitionException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        collision.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new ClashCollisionsChanged(http.User.Actor(), collision.PartId, collision.Id));
        return Results.Ok();
    }

    private static async Task<Guid?> PartOfAsync(ToadFlowDb db, Guid collisionId, CancellationToken ct) =>
        await db.Set<ClashCollision>().AsNoTracking().Where(c => c.Id == collisionId).Select(c => (Guid?)c.PartId).SingleOrDefaultAsync(ct);

    /// <summary>Блокировка строки части до конца транзакции — та же, что держит сверка утверждённого прогона.</summary>
    private static Task LockPartAsync(ToadFlowDb db, Guid partId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM clash_parts WHERE id = {partId} FOR UPDATE", ct);

    private static ClashCollisionEvent Event(Guid collisionId, DateTimeOffset at, Guid? user, CollisionEventKind kind, string text) =>
        new() { Id = Guid.CreateVersion7(), CollisionId = collisionId, At = at, UserId = user, Kind = kind, Text = text };

    /// <summary>Имена моделей части по ключу (Id в форме N).</summary>
    private static async Task<Dictionary<string, string>> ModelNamesAsync(ToadFlowDb db, Guid partId, CancellationToken ct) =>
        (await db.Set<ClashPartModel>().AsNoTracking().Where(m => m.PartId == partId)
            .Join(db.Set<ClashModel>().AsNoTracking(), pm => pm.ModelId, m => m.Id, (_, m) => new { m.Id, m.Name }).ToListAsync(ct))
        .ToDictionary(m => m.Id.ToString("N"), m => m.Name);

    internal static async Task<Dictionary<Guid, int>> NumbersAsync(ToadFlowDb db, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? []
            : await db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);

    internal static CollisionRow Row(ClashCollision c, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<Guid, int> suggested) => new(
        c.Id, c.Number, c.Status, c.Assignee, c.FoundNow, c.DepthMm, c.X, c.Y, c.Z,
        c.ModelA, names.GetValueOrDefault(c.ModelA, c.ModelA), c.UniqueIdA, c.ElementIdA, c.DisciplineA, c.CategoryA, c.FamilyA, c.TypeA, c.LevelA,
        c.ModelB, names.GetValueOrDefault(c.ModelB, c.ModelB), c.UniqueIdB, c.ElementIdB, c.DisciplineB, c.CategoryB, c.FamilyB, c.TypeB, c.LevelB,
        c.Checks, c.FirstVersion, c.LastFoundVersion,
        c.SuggestedPredecessorId is { } p && suggested.ContainsKey(p) ? p : null,
        c.SuggestedPredecessorId is { } q && suggested.TryGetValue(q, out var number) ? number : null);
}
