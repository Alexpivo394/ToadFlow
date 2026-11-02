namespace ToadFlow.Server.Data;

/// <summary>
///     Заполнение при обновлении: <c>opened_at</c> — из <c>created_at</c> и последней записи ленты о возврате; все открытые коллизии
///     считаются просмотренными каждым пользователем с разделами этих коллизий — иначе у каждого сразу появились бы тысячи «новых».
///     Вызывается миграцией <c>ClashCollisionSeen</c>; тест проверяет тот же текст.
/// </summary>
public static class ClashSeenBackfill
{
    public const string Sql = """
        UPDATE clash_collisions c SET opened_at = GREATEST(c.created_at, COALESCE(
            (SELECT max(e.at) FROM clash_collision_events e WHERE e.collision_id = c.id AND e.kind = 'Status' AND e.to_status = 'Returned'),
            c.created_at));

        INSERT INTO clash_collision_seen (user_id, collision_id, seen_at)
        SELECT u.id, c.id, GREATEST(now(), c.opened_at)
        FROM users u
        JOIN clash_collisions c ON c.discipline_a = ANY(u.disciplines) OR c.discipline_b = ANY(u.disciplines)
        WHERE c.status IN ('New', 'Active', 'Returned', 'FixedPending', 'Unchecked')
        ON CONFLICT (user_id, collision_id) DO NOTHING;
        """;
}
