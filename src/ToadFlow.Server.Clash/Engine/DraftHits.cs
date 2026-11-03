using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>
///     Коллизии прогона в базе. Части результата пишутся COPY — сотни тысяч строк за секунды; повтор той же части после
///     обрыва заменяет её. Видимы строки только последнего удачного прогона части: прогон, который упал или ещё идёт,
///     прошлый результат не трогает.
/// </summary>
public sealed class DraftHits(ToadFlowDb db, TimeProvider time, ILogger<DraftHits> logger)
{
    private const string Columns =
        "run_id, number, part_id, discipline_pair, model_a, unique_id_a, element_id_a, discipline_a, sets_a, category_a, family_a, type_a, level_a, " +
        "model_b, unique_id_b, element_id_b, discipline_b, sets_b, category_b, family_b, type_b, level_b, x, y, z, depth_mm, checks";

    /// <summary>
    ///     Пишет часть результата: номера коллизий — <c>part * ResultChunkHits + i + 1</c>. Утверждённый прогон пишет в
    ///     <c>clash_approved_hits</c> с отпечатками — оттуда коллизии сверяются с базой после последней части.
    ///     Попытка и состояние проверяются под блокировкой строки прогона: выдача новой попытки, отмена и уборщик берут ту же.
    ///     Заодно под блокировкой продлевается аренда.
    /// </summary>
    /// <returns>false — прогон уже не в работе у этой попытки, ничего не записано.</returns>
    public async Task<bool> WriteChunkAsync(EngineRun run, int part, IReadOnlyList<EngineHit> hits, CancellationToken ct)
    {
        var first = part * EngineApi.ResultChunkHits + 1;
        var last = first + EngineApi.ResultChunkHits - 1;
        var approved = run.Kind == EngineRunKind.Approved;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var running = nameof(EngineRunState.Running);
            var current = await db.Database.SqlQuery<string>(
                $"SELECT state AS \"Value\" FROM clash_engine_runs WHERE id = {run.Id} AND attempt = {run.Attempt} AND state = {running} FOR UPDATE").ToListAsync(ct);
            if (current.Count == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            var lease = time.GetUtcNow() + EngineRun.Lease;
            await db.Database.ExecuteSqlAsync($"UPDATE clash_engine_runs SET lease_until = {lease} WHERE id = {run.Id}", ct);
            if (approved)
                await db.Set<ClashApprovedHit>().Where(h => h.RunId == run.Id && h.Number >= first && h.Number <= last).ExecuteDeleteAsync(ct);
            else
                await db.Set<ClashDraftHit>().Where(h => h.RunId == run.Id && h.Number >= first && h.Number <= last).ExecuteDeleteAsync(ct);

            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var copy = approved
                ? $"COPY clash_approved_hits ({Columns}, fingerprint_a, fingerprint_b) FROM STDIN (FORMAT BINARY)"
                : $"COPY clash_draft_hits ({Columns}) FROM STDIN (FORMAT BINARY)";
            await using (var importer = await connection.BeginBinaryImportAsync(copy, ct))
            {
                for (var i = 0; i < hits.Count; i++)
                {
                    var hit = hits[i];
                    await importer.StartRowAsync(ct);
                    await importer.WriteAsync(run.Id, NpgsqlDbType.Uuid, ct);
                    await importer.WriteAsync(first + i, NpgsqlDbType.Integer, ct);
                    await importer.WriteAsync(run.PartId, NpgsqlDbType.Uuid, ct);
                    await importer.WriteAsync(ClashDraftHit.PairOf(hit.DisciplineA, hit.DisciplineB), NpgsqlDbType.Text, ct);
                    await WriteSideAsync(importer, hit.ModelA, hit.UniqueIdA, hit.ElementIdA, hit.DisciplineA, hit.SetsA, hit.CategoryA, hit.FamilyA, hit.TypeA,
                        hit.LevelA, ct);
                    await WriteSideAsync(importer, hit.ModelB, hit.UniqueIdB, hit.ElementIdB, hit.DisciplineB, hit.SetsB, hit.CategoryB, hit.FamilyB, hit.TypeB,
                        hit.LevelB, ct);
                    await importer.WriteAsync(hit.X, NpgsqlDbType.Double, ct);
                    await importer.WriteAsync(hit.Y, NpgsqlDbType.Double, ct);
                    await importer.WriteAsync(hit.Z, NpgsqlDbType.Double, ct);
                    await importer.WriteAsync(hit.DepthMm, NpgsqlDbType.Real, ct);
                    await importer.WriteAsync((hit.Checks ?? []).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                    if (!approved) continue;
                    await importer.WriteAsync(hit.FingerprintA, NpgsqlDbType.Bigint, ct);
                    await importer.WriteAsync(hit.FingerprintB, NpgsqlDbType.Bigint, ct);
                }

                await importer.CompleteAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return true;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Последняя часть пришла: прогон удачен, его строки становятся результатом части, строки прошлых прогонов удаляются.</summary>
    /// <remarks>
    ///     Состояние прогона проверяется под блокировкой строки: отмена и уборщик аренды берут ту же, поэтому отменённый или
    ///     возвращённый в очередь прогон результатом части не становится.
    /// </remarks>
    /// <returns>false — прогон уже не в работе (отменён, сдан, возвращён в очередь), ничего не записано.</returns>
    public async Task<bool> CompleteAsync(EngineRun run, EngineRunStats? stats, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await db.Database.SqlQuery<string>($"SELECT state AS \"Value\" FROM clash_engine_runs WHERE id = {run.Id} AND attempt = {run.Attempt} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (state != nameof(EngineRunState.Running))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var count = await db.Set<ClashDraftHit>().CountAsync(h => h.RunId == run.Id, ct);
        await db.Set<ClashPart>().Where(p => p.Id == run.PartId).ExecuteUpdateAsync(p => p.SetProperty(x => x.ResultRunId, run.Id), ct);
        await db.Set<ClashDraftHit>().Where(h => h.PartId == run.PartId && h.RunId != run.Id).ExecuteDeleteAsync(ct);

        run.State = EngineRunState.Succeeded;
        run.HitCount = count;
        run.Stats = stats;
        run.Stage = null;
        run.LeaseUntil = null;
        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Строки прогона пришли COPY, а прошлые удалены: без свежей статистики планировщик оценивает новый run_id в единицы строк и
        // сравнивает находки с коллизиями (предпросмотр утверждения) вложенными циклами — 24 с вместо долей секунды на части DON.
        // Прогон уже сохранён удачным: обрыв запроса или сбой ANALYZE не должны превращать его в ошибку и терять событие для веба.
        try
        {
            await db.Database.ExecuteSqlRawAsync("ANALYZE clash_draft_hits", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Прогон движка {Run}: статистика находок не обновлена (ANALYZE clash_draft_hits)", run.Id);
        }

        return true;
    }

    /// <summary>Строки прогона, который не дошёл до конца.</summary>
    public async Task DiscardAsync(Guid runId, CancellationToken ct)
    {
        await db.Set<ClashDraftHit>().Where(h => h.RunId == runId).ExecuteDeleteAsync(ct);
        await db.Set<ClashApprovedHit>().Where(h => h.RunId == runId).ExecuteDeleteAsync(ct);
    }

    private static async Task WriteSideAsync(NpgsqlBinaryImporter importer, string model, string uniqueId, long elementId, string discipline,
        IReadOnlyList<Guid> sets, string category, string? family, string? type, string? level, CancellationToken ct)
    {
        await importer.WriteAsync(model, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(uniqueId, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(elementId, NpgsqlDbType.Bigint, ct);
        await importer.WriteAsync(discipline, NpgsqlDbType.Text, ct);
        await importer.WriteAsync(sets.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ct);
        await importer.WriteAsync(category, NpgsqlDbType.Text, ct);
        await WriteNullableAsync(importer, family, ct);
        await WriteNullableAsync(importer, type, ct);
        await WriteNullableAsync(importer, level, ct);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter importer, string? value, CancellationToken ct)
    {
        if (value is null) await importer.WriteNullAsync(ct);
        else await importer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }
}
