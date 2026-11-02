using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Analytics;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>
///     Сверка утверждённого прогона с базой коллизий части: найденные пары получают номера и статусы, ненайденные открытые —
///     статус по фактам работника, лента и образцы глубины пополняются. Всё в одной транзакции; массовая запись — COPY.
/// </summary>
public sealed class CollisionReconciler(ToadFlowDb db, TimeProvider time)
{
    private const string CollisionColumns =
        "id, part_id, number, status, assignee, found_now, model_a, unique_id_a, element_id_a, discipline_a, sets_a, category_a, family_a, type_a, level_a, " +
        "model_b, unique_id_b, element_id_b, discipline_b, sets_b, category_b, family_b, type_b, level_b, discipline_pair, checks, x, y, z, depth_mm, " +
        "fingerprint_a, fingerprint_b, first_version, last_found_version, last_found_run_id, suggested_predecessor_id, created_at, updated_at, " +
        "first_run_id, opened_at";

    /// <summary>
    ///     Последняя часть утверждённого прогона пришла: сверяет коллизии, закрывает прогон, делает его основой следующего.
    ///     Прогон, который успели отменить или вернуть в очередь, не сверяется.
    /// </summary>
    /// <returns>Итог сверки; null — прогон уже не в работе.</returns>
    public async Task<ApprovedRunSummary?> ApplyAsync(EngineRun run, IReadOnlyList<EngineGonePair> gone, IReadOnlyList<EngineIdentityMatch> matches,
        EngineRunStats? stats, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var version = run.Version ?? 0;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var part = await db.Set<ClashPart>().FromSqlInterpolated($"SELECT * FROM clash_parts WHERE id = {run.PartId} FOR UPDATE").SingleAsync(ct);
            // Отмена и возврат в очередь могли пройти, пока писалась последняя часть: найденные пары уже удалены.
            var state = await db.Database.SqlQuery<string>($"SELECT state AS \"Value\" FROM clash_engine_runs WHERE id = {run.Id} FOR UPDATE").SingleAsync(ct);
            if (state != nameof(EngineRunState.Running)) return null;
            var hits = await db.Set<ClashApprovedHit>().AsNoTracking().Where(h => h.RunId == run.Id).OrderBy(h => h.Number).ToListAsync(ct);
            var existing = (await db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == run.PartId).ToListAsync(ct))
                .ToDictionary(c => (c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB));

            var inserted = new List<ClashCollision>();
            var updated = new List<ClashCollision>();
            var feed = new List<ClashCollisionEvent>();
            var samples = new List<ClashCollisionSample>();
            int created = 0, returned = 0, fixedNow = 0, goneByRules = 0, uncheckedNow = 0;
            var moves = new List<(ClashCollision Collision, CollisionStatus To)>(); // переходы прогона — для снимка сводки по разделам
            var seen = new HashSet<Guid>();

            foreach (var hit in hits)
            {
                var key = (hit.ModelA, hit.UniqueIdA, hit.ModelB, hit.UniqueIdB);
                if (existing.TryGetValue(key, out var collision))
                {
                    if (!seen.Add(collision.Id)) continue;
                    var was = collision.Status;
                    var (next, note) = CollisionTransitions.Found(was);
                    var sample = collision.FingerprintA != hit.FingerprintA || collision.FingerprintB != hit.FingerprintB
                                 || Math.Abs(collision.DepthMm - hit.DepthMm) > ClashCollisionSample.DepthStepMm;
                    var newVersion = collision.LastFoundVersion != version;
                    Fill(collision, hit, run, version, now);
                    collision.Status = next;
                    if (Reopened(was, next)) collision.OpenedAt = now;
                    if (next != was)
                    {
                        feed.Add(StatusEvent(collision.Id, was, next, note, version, run.Id, now));
                        if (next == CollisionStatus.Returned) returned++;
                        moves.Add((collision, next));
                    }

                    if (newVersion) feed.Add(FoundEvent(collision.Id, version, hit.DepthMm, run.Id, now));
                    if (sample) samples.Add(SampleOf(collision, run, version, now));
                    updated.Add(collision);
                    continue;
                }

                collision = new ClashCollision
                {
                    Id = Guid.CreateVersion7(),
                    PartId = run.PartId,
                    Number = part.NextCollisionNumber++,
                    Status = CollisionStatus.New,
                    Assignee = CollisionAssignee.Both,
                    FirstVersion = version,
                    FirstRunId = run.Id,
                    CreatedAt = now,
                    OpenedAt = now
                };
                Fill(collision, hit, run, version, now);
                existing[key] = collision;
                seen.Add(collision.Id);
                inserted.Add(collision);
                feed.Add(FoundEvent(collision.Id, version, hit.DepthMm, run.Id, now));
                samples.Add(SampleOf(collision, run, version, now));
                created++;
                moves.Add((collision, CollisionStatus.New));
            }

            var facts = gone.ToDictionary(g => (g.ModelA, g.UniqueIdA, g.ModelB, g.UniqueIdB));
            var unchecked_ = run.UncheckedModels.Select(m => m.ToString("N")).ToHashSet(StringComparer.Ordinal);
            foreach (var collision in existing.Values)
            {
                if (seen.Contains(collision.Id)) continue;
                var was = collision.Status;
                var next = was;
                if (was is not (CollisionStatus.Fake or CollisionStatus.Fixed))
                {
                    var fact = facts.GetValueOrDefault((collision.ModelA, collision.UniqueIdA, collision.ModelB, collision.UniqueIdB));
                    next = CollisionTransitions.NotFound(was, new GoneFacts(
                        Presence(fact?.PresenceA), Presence(fact?.PresenceB),
                        unchecked_.Contains(collision.ModelA) || unchecked_.Contains(collision.ModelB),
                        fact?.PreviousConfigWouldHit));
                }

                if (next == was && !collision.FoundNow) continue;
                collision.FoundNow = false;
                if (next != was)
                {
                    collision.Status = next;
                    collision.UpdatedAt = now;
                    feed.Add(StatusEvent(collision.Id, was, next, null, version, run.Id, now));
                    moves.Add((collision, next));
                    switch (next)
                    {
                        case CollisionStatus.Fixed: fixedNow++; break;
                        case CollisionStatus.GoneByRules: goneByRules++; break;
                        case CollisionStatus.Unchecked: uncheckedNow++; break;
                    }
                }

                updated.Add(collision);
            }

            Suggest(inserted, existing, matches, feed, now);

            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await CopyCollisionsAsync(connection, "clash_collisions", inserted, ct);
            if (updated.Count > 0)
            {
                await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE tmp_clash_collisions (LIKE clash_collisions) ON COMMIT DROP", ct);
                await CopyCollisionsAsync(connection, "tmp_clash_collisions", updated, ct);
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE clash_collisions c SET
                        status = t.status, found_now = t.found_now, element_id_a = t.element_id_a, discipline_a = t.discipline_a, sets_a = t.sets_a,
                        category_a = t.category_a, family_a = t.family_a, type_a = t.type_a, level_a = t.level_a, element_id_b = t.element_id_b,
                        discipline_b = t.discipline_b, sets_b = t.sets_b, category_b = t.category_b, family_b = t.family_b, type_b = t.type_b,
                        level_b = t.level_b, discipline_pair = t.discipline_pair, checks = t.checks, x = t.x, y = t.y, z = t.z, depth_mm = t.depth_mm,
                        fingerprint_a = t.fingerprint_a, fingerprint_b = t.fingerprint_b, last_found_version = t.last_found_version,
                        last_found_run_id = t.last_found_run_id, updated_at = t.updated_at, opened_at = t.opened_at
                    FROM tmp_clash_collisions t WHERE c.id = t.id
                    """, ct);
            }

            await CopyEventsAsync(connection, feed, ct);
            await CopySamplesAsync(connection, samples, ct);

            var counts = await db.Set<ClashCollision>().Where(c => c.PartId == run.PartId).GroupBy(c => c.Status)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
            var summary = new ApprovedRunSummary(hits.Count, created, counts.GetValueOrDefault(CollisionStatus.Active), returned, fixedNow, goneByRules,
                uncheckedNow, counts.GetValueOrDefault(CollisionStatus.Fake), stats?.ReusedPairs ?? 0);
            await AddStatsAsync(run, now, moves, ct);
            AddPairStats(run, hits, moves);

            await db.Set<ClashApprovedHit>().Where(h => h.RunId == run.Id).ExecuteDeleteAsync(ct);
            part.ApprovedRunId = run.Id;
            run.State = EngineRunState.Succeeded;
            run.HitCount = hits.Count;
            run.Stats = stats;
            run.Summary = summary;
            run.Stage = null;
            run.LeaseUntil = null;
            run.FinishedAt = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return summary;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    ///     Счётчики прогона по парам разделов для сравнения прогонов: находки — по паре находки, переходы — по паре коллизии. Строка
    ///     <see cref="ClashRunPairStat.Marker" /> — итог по всем парам; она пишется всегда и отмечает, что счётчики у прогона есть.
    /// </summary>
    private void AddPairStats(EngineRun run, IReadOnlyList<ClashApprovedHit> hits, IReadOnlyList<(ClashCollision Collision, CollisionStatus To)> moves)
    {
        var stats = new Dictionary<string, ClashRunPairStat>(StringComparer.Ordinal);
        IEnumerable<ClashRunPairStat> Of(string pair) => new[] { pair, ClashRunPairStat.Marker }.Distinct().Select(p =>
            stats.TryGetValue(p, out var stat) ? stat : stats[p] = new ClashRunPairStat { RunId = run.Id, PartId = run.PartId, DisciplinePair = p });

        _ = Of(ClashRunPairStat.Marker).ToList();
        foreach (var hit in hits)
        foreach (var stat in Of(hit.DisciplinePair)) stat.Found++;
        foreach (var (collision, to) in moves)
        foreach (var stat in Of(collision.DisciplinePair))
        {
            switch (to)
            {
                case CollisionStatus.New: stat.New++; break;
                case CollisionStatus.Returned: stat.Returned++; break;
                case CollisionStatus.Fixed: stat.Fixed++; break;
                case CollisionStatus.GoneByRules: stat.GoneByRules++; break;
            }
        }

        db.Set<ClashRunPairStat>().AddRange(stats.Values);
    }

    /// <summary>Закрытая коллизия снова открыта (вернулась): для всех она снова «новая» — <see cref="ClashCollision.OpenedAt" />.</summary>
    internal static bool Reopened(CollisionStatus was, CollisionStatus next) =>
        !CollisionEndpoints.Open.Contains(was) && CollisionEndpoints.Open.Contains(next);

    private sealed record StatRow(string Discipline, int Open, int Fake);

    /// <summary>
    ///     Снимок сводки по разделам после сверки: открытые и фиктивные — запросом по коллизиям части (UNION сторон A и B убирает
    ///     повтор коллизии внутри одного раздела, строка <see cref="ClashCollisionStat.Total" /> — итог), переходы — из сверки.
    /// </summary>
    private async Task AddStatsAsync(EngineRun run, DateTimeOffset now, IReadOnlyList<(ClashCollision Collision, CollisionStatus To)> moves,
        CancellationToken ct)
    {
        var open = CollisionEndpoints.Open.Select(s => s.ToString()).ToArray();
        var fake = nameof(CollisionStatus.Fake);
        var rows = await db.Database.SqlQuery<StatRow>($"""
            SELECT d.discipline AS "Discipline",
                   count(*) FILTER (WHERE d.status = ANY({open}))::int AS "Open",
                   count(*) FILTER (WHERE d.status = {fake})::int AS "Fake"
            FROM (SELECT id, status, discipline_a AS discipline FROM clash_collisions WHERE part_id = {run.PartId}
                  UNION SELECT id, status, discipline_b FROM clash_collisions WHERE part_id = {run.PartId}
                  UNION SELECT id, status, '' FROM clash_collisions WHERE part_id = {run.PartId}) d
            GROUP BY d.discipline
            """).ToListAsync(ct);

        var stats = rows.ToDictionary(r => r.Discipline, r => new ClashCollisionStat
        {
            RunId = run.Id, PartId = run.PartId, At = now, Discipline = r.Discipline, Open = r.Open, Fake = r.Fake
        });
        foreach (var (collision, to) in moves)
        foreach (var discipline in new[] { ClashCollisionStat.Total, collision.DisciplineA, collision.DisciplineB }.Distinct())
        {
            if (!stats.TryGetValue(discipline, out var stat)) continue;
            switch (to)
            {
                case CollisionStatus.New: stat.New++; break;
                case CollisionStatus.Fixed: stat.Fixed++; break;
                case CollisionStatus.Returned: stat.Returned++; break;
                case CollisionStatus.GoneByRules: stat.GoneByRules++; break;
            }
        }

        db.Set<ClashCollisionStat>().AddRange(stats.Values);
    }

    private static ElementPresence Presence(string? value) =>
        Enum.TryParse<ElementPresence>(value, ignoreCase: true, out var presence) ? presence : ElementPresence.InGeometry;

    private static void Fill(ClashCollision c, ClashApprovedHit hit, EngineRun run, int version, DateTimeOffset now)
    {
        (c.ModelA, c.UniqueIdA, c.ElementIdA, c.DisciplineA, c.SetsA, c.CategoryA, c.FamilyA, c.TypeA, c.LevelA) =
            (hit.ModelA, hit.UniqueIdA, hit.ElementIdA, hit.DisciplineA, hit.SetsA, hit.CategoryA, hit.FamilyA, hit.TypeA, hit.LevelA);
        (c.ModelB, c.UniqueIdB, c.ElementIdB, c.DisciplineB, c.SetsB, c.CategoryB, c.FamilyB, c.TypeB, c.LevelB) =
            (hit.ModelB, hit.UniqueIdB, hit.ElementIdB, hit.DisciplineB, hit.SetsB, hit.CategoryB, hit.FamilyB, hit.TypeB, hit.LevelB);
        (c.DisciplinePair, c.Checks, c.X, c.Y, c.Z, c.DepthMm, c.FingerprintA, c.FingerprintB) =
            (hit.DisciplinePair, hit.Checks, hit.X, hit.Y, hit.Z, hit.DepthMm, hit.FingerprintA, hit.FingerprintB);
        c.FoundNow = true;
        c.LastFoundVersion = version;
        c.LastFoundRunId = run.Id;
        c.UpdatedAt = now;
    }

    // Новая коллизия, у которой элемент — вероятно, пересозданный элемент другой коллизии с тем же вторым элементом.
    private static void Suggest(List<ClashCollision> inserted, Dictionary<(string, string, string, string), ClashCollision> existing,
        IReadOnlyList<EngineIdentityMatch> matches, List<ClashCollisionEvent> feed, DateTimeOffset now)
    {
        if (inserted.Count == 0 || matches.Count == 0) return;
        var oldOf = new Dictionary<(string Model, string UniqueId), string>();
        foreach (var match in matches) oldOf.TryAdd((match.Model, match.NewUniqueId), match.OldUniqueId);

        foreach (var collision in inserted)
        {
            var predecessor =
                Find(existing, oldOf, (collision.ModelA, collision.UniqueIdA), (collision.ModelB, collision.UniqueIdB)) ??
                Find(existing, oldOf, (collision.ModelB, collision.UniqueIdB), (collision.ModelA, collision.UniqueIdA));
            if (predecessor is null || predecessor.Id == collision.Id) continue;
            collision.SuggestedPredecessorId = predecessor.Id;
            feed.Add(new ClashCollisionEvent
            {
                Id = Guid.CreateVersion7(), CollisionId = collision.Id, At = now, Kind = CollisionEventKind.Suggestion,
                Text = $"Возможно, это коллизия №{predecessor.Number}: элемент пересоздан с новым UniqueId"
            });
        }
    }

    private static ClashCollision? Find(Dictionary<(string, string, string, string), ClashCollision> existing, Dictionary<(string, string), string> oldOf,
        (string Model, string UniqueId) recreated, (string Model, string UniqueId) other)
    {
        if (!oldOf.TryGetValue(recreated, out var oldId)) return null;
        var old = (recreated.Model, oldId);
        var (a, b) = string.CompareOrdinal(old.Model, other.Model) switch
        {
            < 0 => (old, other),
            > 0 => (other, old),
            _ => string.CompareOrdinal(old.oldId, other.UniqueId) <= 0 ? (old, other) : (other, old)
        };
        return existing.GetValueOrDefault((a.Item1, a.Item2, b.Item1, b.Item2));
    }

    private static ClashCollisionEvent StatusEvent(Guid id, CollisionStatus from, CollisionStatus to, string? note, int version, Guid runId,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), CollisionId = id, At = now, Kind = CollisionEventKind.Status, FromStatus = from, ToStatus = to, Version = version, RunId = runId,
        Text = note ?? ""
    };

    private static ClashCollisionEvent FoundEvent(Guid id, int version, float depth, Guid runId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(), CollisionId = id, At = now, Kind = CollisionEventKind.Found, Version = version, RunId = runId,
        Text = $"Найдена версией {version}, глубина {depth:0} мм"
    };

    private static ClashCollisionSample SampleOf(ClashCollision c, EngineRun run, int version, DateTimeOffset now) => new()
    {
        CollisionId = c.Id, RunId = run.Id, Version = version, At = now, DepthMm = c.DepthMm, X = c.X, Y = c.Y, Z = c.Z, FingerprintA = c.FingerprintA,
        FingerprintB = c.FingerprintB
    };

    private static async Task CopyCollisionsAsync(NpgsqlConnection connection, string table, List<ClashCollision> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var importer = await connection.BeginBinaryImportAsync($"COPY {table} ({CollisionColumns}) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var c in rows)
        {
            await importer.StartRowAsync(ct);
            await importer.WriteAsync(c.Id, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(c.PartId, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(c.Number, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(c.Status.ToString(), NpgsqlDbType.Varchar, ct);
            await importer.WriteAsync(c.Assignee.ToString(), NpgsqlDbType.Varchar, ct);
            await importer.WriteAsync(c.FoundNow, NpgsqlDbType.Boolean, ct);
            await WriteSideAsync(importer, c.ModelA, c.UniqueIdA, c.ElementIdA, c.DisciplineA, c.SetsA, c.CategoryA, c.FamilyA, c.TypeA, c.LevelA, ct);
            await WriteSideAsync(importer, c.ModelB, c.UniqueIdB, c.ElementIdB, c.DisciplineB, c.SetsB, c.CategoryB, c.FamilyB, c.TypeB, c.LevelB, ct);
            await importer.WriteAsync(c.DisciplinePair, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.Checks, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.X, NpgsqlDbType.Double, ct);
            await importer.WriteAsync(c.Y, NpgsqlDbType.Double, ct);
            await importer.WriteAsync(c.Z, NpgsqlDbType.Double, ct);
            await importer.WriteAsync(c.DepthMm, NpgsqlDbType.Real, ct);
            await importer.WriteAsync(c.FingerprintA, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(c.FingerprintB, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(c.FirstVersion, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(c.LastFoundVersion, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(c.LastFoundRunId, NpgsqlDbType.Uuid, ct);
            if (c.SuggestedPredecessorId is { } suggested) await importer.WriteAsync(suggested, NpgsqlDbType.Uuid, ct);
            else await importer.WriteNullAsync(ct);
            await importer.WriteAsync(c.CreatedAt, NpgsqlDbType.TimestampTz, ct);
            await importer.WriteAsync(c.UpdatedAt, NpgsqlDbType.TimestampTz, ct);
            if (c.FirstRunId is { } firstRun) await importer.WriteAsync(firstRun, NpgsqlDbType.Uuid, ct);
            else await importer.WriteNullAsync(ct);
            await importer.WriteAsync(c.OpenedAt, NpgsqlDbType.TimestampTz, ct);
        }

        await importer.CompleteAsync(ct);
    }

    private static async Task WriteSideAsync(NpgsqlBinaryImporter importer, string model, string uniqueId, long elementId, string discipline, Guid[] sets,
        string category, string? family, string? type, string? level, CancellationToken ct)
    {
        await importer.WriteAsync(model, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(uniqueId, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(elementId, NpgsqlDbType.Bigint, ct);
        await importer.WriteAsync(discipline, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(sets, NpgsqlDbType.Array | NpgsqlDbType.Uuid, ct);
        await importer.WriteAsync(category, NpgsqlDbType.Text, ct);
        await WriteNullableAsync(importer, family, ct);
        await WriteNullableAsync(importer, type, ct);
        await WriteNullableAsync(importer, level, ct);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter importer, long? value, CancellationToken ct)
    {
        if (value is { } number) await importer.WriteAsync(number, NpgsqlDbType.Bigint, ct);
        else await importer.WriteNullAsync(ct);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter importer, string? value, CancellationToken ct)
    {
        if (value is null) await importer.WriteNullAsync(ct);
        else await importer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    private static async Task CopyEventsAsync(NpgsqlConnection connection, List<ClashCollisionEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY clash_collision_events (id, collision_id, at, user_id, kind, text, from_status, to_status, version, run_id) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var e in rows)
        {
            await importer.StartRowAsync(ct);
            await importer.WriteAsync(e.Id, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(e.CollisionId, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(e.At, NpgsqlDbType.TimestampTz, ct);
            if (e.UserId is { } user) await importer.WriteAsync(user, NpgsqlDbType.Uuid, ct);
            else await importer.WriteNullAsync(ct);
            await importer.WriteAsync(e.Kind.ToString(), NpgsqlDbType.Varchar, ct);
            await importer.WriteAsync(e.Text, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(importer, e.FromStatus?.ToString(), ct);
            await WriteNullableAsync(importer, e.ToStatus?.ToString(), ct);
            if (e.Version is { } version) await importer.WriteAsync(version, NpgsqlDbType.Integer, ct);
            else await importer.WriteNullAsync(ct);
            if (e.RunId is { } eventRun) await importer.WriteAsync(eventRun, NpgsqlDbType.Uuid, ct);
            else await importer.WriteNullAsync(ct);
        }

        await importer.CompleteAsync(ct);
    }

    private static async Task CopySamplesAsync(NpgsqlConnection connection, List<ClashCollisionSample> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY clash_collision_samples (collision_id, run_id, version, at, depth_mm, x, y, z, fingerprint_a, fingerprint_b) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var s in rows)
        {
            await importer.StartRowAsync(ct);
            await importer.WriteAsync(s.CollisionId, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(s.RunId, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(s.Version, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(s.At, NpgsqlDbType.TimestampTz, ct);
            await importer.WriteAsync(s.DepthMm, NpgsqlDbType.Real, ct);
            await importer.WriteAsync(s.X, NpgsqlDbType.Double, ct);
            await importer.WriteAsync(s.Y, NpgsqlDbType.Double, ct);
            await importer.WriteAsync(s.Z, NpgsqlDbType.Double, ct);
            await importer.WriteAsync(s.FingerprintA, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(s.FingerprintB, NpgsqlDbType.Bigint, ct);
        }

        await importer.CompleteAsync(ct);
    }
}
