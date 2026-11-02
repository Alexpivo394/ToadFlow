namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Правку запрещают права пользователя (поштучно — 403); текст — для пользователя.</summary>
public sealed class CollisionForbiddenException(string message) : Exception(message);

/// <summary>Кто меняет коллизию: права и подписи ленты.</summary>
/// <param name="Disciplines">Разделы профиля — проектировщик меняет статус и «кто правит» только в коллизиях своих разделов.</param>
/// <param name="IsStaff">Координатор или администратор — меняет любые.</param>
public sealed record CollisionActor(Guid UserId, string DisplayName, string[] Disciplines, bool IsStaff);

/// <summary>
///     Ручные правки коллизии — одна логика для поштучных маршрутов и массового действия. Метод меняет коллизию и возвращает записи
///     ленты; недопустимый переход — <see cref="CollisionTransitionException" />, нет прав — <see cref="CollisionForbiddenException" />.
/// </summary>
public static class CollisionChanges
{
    public const string NotMine = "Менять статус и «кто правит» можно только в коллизиях своего раздела: ни один её элемент не из ваших разделов";

    /// <param name="fakeSetBy">Кто поставил «фиктивную» (последняя запись ленты); null — система.</param>
    public static IReadOnlyList<ClashCollisionEvent> Status(ClashCollision collision, CollisionStatus target, string? note, CollisionActor actor,
        Guid? fakeSetBy, DateTimeOffset now)
    {
        RequireMine(collision, actor);
        if (collision.Status == CollisionStatus.Fake && target != CollisionStatus.Fake && !actor.IsStaff && fakeSetBy != actor.UserId)
            throw new CollisionForbiddenException("Снять фиктивную, поставленную другим, может координатор");
        var next = CollisionTransitions.Manual(collision.Status, target, collision.FoundNow, note);
        var entry = Event(collision.Id, now, actor.UserId, CollisionEventKind.Status, note?.Trim() ?? "");
        entry.FromStatus = collision.Status;
        entry.ToStatus = next;
        // Снятая вручную «фиктивная» снова открыта — для всех снова «новая», как вернувшаяся после прогона.
        if (CollisionReconciler.Reopened(collision.Status, next)) collision.OpenedAt = now;
        collision.Status = next;
        return [entry];
    }

    /// <param name="note">«Передать» с примечанием: оно идёт в ленту отдельной записью сразу после смены.</param>
    public static IReadOnlyList<ClashCollisionEvent> Assignee(ClashCollision collision, CollisionAssignee assignee, string? note, CollisionActor actor,
        DateTimeOffset now)
    {
        RequireMine(collision, actor);
        if (collision.Assignee == assignee) throw new CollisionUnchangedException("«Кто правит» уже такой");
        var text = note?.Trim() ?? "";
        if (text.Length > CollisionFeed.MaxNoteLength) throw new CollisionTransitionException($"Примечание — до {CollisionFeed.MaxNoteLength} символов");
        var entry = Event(collision.Id, now, actor.UserId, CollisionEventKind.Assignee,
            $"Кто правит: {CollisionFeed.AssigneeText(collision, collision.Assignee)} → {CollisionFeed.AssigneeText(collision, assignee)}");
        collision.Assignee = assignee;
        // На микросекунду позже — лента по времени показывает примечание после смены (точность времени в базе — микросекунды).
        return text.Length > 0 ? [entry, Event(collision.Id, now.AddMicroseconds(1), actor.UserId, CollisionEventKind.Note, text)] : [entry];
    }

    /// <summary>«Беру»: «кто правит» — моя сторона, а если оба элемента из моих разделов — обе стороны.</summary>
    public static IReadOnlyList<ClashCollisionEvent> Take(ClashCollision collision, CollisionActor actor, DateTimeOffset now)
    {
        var side = MyCollisions.Side(collision, actor.Disciplines)
                   ?? throw new CollisionTransitionException("Взять можно только коллизию своего раздела: ни один её элемент не из ваших разделов");
        var text = collision.Assignee == side
            ? $"взял(а) {actor.DisplayName}: {CollisionFeed.AssigneeText(collision, side)}"
            : $"взял(а) {actor.DisplayName}: {CollisionFeed.AssigneeText(collision, collision.Assignee)} → {CollisionFeed.AssigneeText(collision, side)}";
        collision.Assignee = side;
        return [Event(collision.Id, now, actor.UserId, CollisionEventKind.Assignee, text)];
    }

    public static IReadOnlyList<ClashCollisionEvent> Note(ClashCollision collision, string? text, CollisionActor actor, DateTimeOffset now)
    {
        var note = text?.Trim() ?? "";
        if (note.Length is 0 or > CollisionFeed.MaxNoteLength)
            throw new CollisionTransitionException($"Примечание — от 1 до {CollisionFeed.MaxNoteLength} символов");
        return [Event(collision.Id, now, actor.UserId, CollisionEventKind.Note, note)];
    }

    /// <summary>Статус и «кто правит» проектировщик меняет только в коллизиях своего раздела, как в «Моих»; координатор — в любых.</summary>
    private static void RequireMine(ClashCollision collision, CollisionActor actor)
    {
        if (!actor.IsStaff && MyCollisions.Side(collision, actor.Disciplines) is null) throw new CollisionForbiddenException(NotMine);
    }

    internal static ClashCollisionEvent Event(Guid collisionId, DateTimeOffset at, Guid? user, CollisionEventKind kind, string text) =>
        new() { Id = Guid.CreateVersion7(), CollisionId = collisionId, At = at, UserId = user, Kind = kind, Text = text };
}
