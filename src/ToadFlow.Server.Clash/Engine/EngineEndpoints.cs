using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>
///     Протокол работника движка (<c>/api/engine</c>, ключ работника): взять прогон, скачать слепки, сообщить ход, сдать
///     результат частями или сбой. Прогон, который больше не идёт (отменён, сдан, отдан другой попытке), отвечает 409.
/// </summary>
public static class EngineEndpoints
{
    /// <summary>Ключ advisory-блокировки выдачи прогонов.</summary>
    internal const long ClaimLockKey = 0x544F4144_434C4153;

    public static void MapClashEngine(this IEndpointRouteBuilder app)
    {
        var engine = app.MapGroup("/api/engine").RequireAuthorization(EngineAuthHandler.Policy);
        engine.MapPost("/runs/claim", ClaimAsync);
        engine.MapGet("/snapshots/{id:guid}/{file}", DownloadAsync);
        engine.MapPost("/runs/{id:guid}/progress", ProgressAsync);
        engine.MapPost("/runs/{id:guid}/result", ResultAsync).WithMetadata(new DisableRequestSizeLimitAttribute());
        engine.MapPost("/runs/{id:guid}/fail", FailAsync);
        engine.MapGet("/runs/{id:guid}/basis", BasisAsync);
    }

    /// <summary>
    ///     Следующий прогон очереди, если сейчас ни один не идёт: движок считает по одному прогону. Основа утверждённого
    ///     прогона — утверждённый прогон части на момент выдачи: пока прогон ждал, мог закончиться другой. Выдачи идут по очереди
    ///     (advisory-блокировка): иначе два работника под READ COMMITTED оба увидели бы «ничего не идёт». Строки прежней попытки
    ///     стираются тут же: новая попытка пишет все части заново, а лишние части старой попали бы в сверку.
    /// </summary>
    private static async Task<IResult> ClaimAsync(ToadFlowDb db, TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lease = now + EngineRun.Lease;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClaimLockKey})", ct);
        var queued = nameof(EngineRunState.Queued);
        var running = nameof(EngineRunState.Running);
        var approved = nameof(EngineRunKind.Approved);
        var claimed = await db.Set<EngineRun>().FromSql($"""
            UPDATE clash_engine_runs r SET state = {running}, attempt = attempt + 1, lease_until = {lease}, started_at = {now}, stage = NULL,
                previous_run_id = CASE WHEN r.kind = {approved} THEN (SELECT p.approved_run_id FROM clash_parts p WHERE p.id = r.part_id) ELSE r.previous_run_id END
            WHERE id = (
                SELECT id FROM clash_engine_runs
                WHERE state = {queued} AND NOT EXISTS (SELECT 1 FROM clash_engine_runs WHERE state = {running})
                ORDER BY created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
        if (claimed.Count == 0) return Results.NoContent();

        var run = claimed[0];
        await db.Set<ClashDraftHit>().Where(h => h.RunId == run.Id).ExecuteDeleteAsync(ct);
        await db.Set<ClashApprovedHit>().Where(h => h.RunId == run.Id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
        return Results.Ok(new EngineRunAssignment(run.Id, run.ConfigurationJson, run.Models, run.Attempt,
            run.Kind == EngineRunKind.Approved ? EngineApi.Approved : EngineApi.Draft));
    }

    /// <summary>
    ///     Основа утверждённого прогона: конфигурация и слепки прошлого удачного прогона и все коллизии части, кроме исправленных.
    ///     Прошлого прогона нет — пустая основа.
    /// </summary>
    private static async Task<IResult> BasisAsync(Guid id, [FromQuery(Name = EngineApi.AttemptQuery)] int attempt, ToadFlowDb db, CancellationToken ct)
    {
        var run = await db.Set<EngineRun>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == id && r.Attempt == attempt && r.State == EngineRunState.Running, ct);
        if (run is null) return NotRunning();
        var previous = run.PreviousRunId is { } previousId
            ? await db.Set<EngineRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == previousId && r.State == EngineRunState.Succeeded, ct)
            : null;
        if (previous is null) return Results.Ok(new EngineBasis(null, [], []));

        var open = await db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == run.PartId && c.Status != CollisionStatus.Fixed)
            .Select(c => new EngineOpenPair(c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB, c.FoundNow && c.LastFoundRunId == previous.Id, c.X, c.Y, c.Z,
                c.DepthMm, c.FingerprintA, c.FingerprintB))
            .ToListAsync(ct);
        return Results.Ok(new EngineBasis(previous.ConfigurationJson, previous.Models, open, previous.Stats?.EngineVersion ?? 0));
    }

    private static async Task<IResult> DownloadAsync(Guid id, string file, ToadFlowDb db, ClashStorage storage, CancellationToken ct)
    {
        if (ClashUploads.FileName(file) is not { } name) return Results.NotFound();
        var snapshot = await db.Set<ClashSnapshot>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot is null) return Results.NotFound();

        var path = Path.Combine(storage.SnapshotFolder(snapshot.ModelId, snapshot.Id), name);
        return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: true) : Results.NotFound();
    }

    private static async Task<IResult> ProgressAsync(Guid id, EngineProgress progress, ToadFlowDb db, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var lease = time.GetUtcNow() + EngineRun.Lease;
        var stage = string.IsNullOrWhiteSpace(progress.Text) ? progress.Stage : $"{progress.Stage}: {progress.Text}";
        var updated = await db.Set<EngineRun>().Where(r => r.Id == id && r.Attempt == progress.Attempt && r.State == EngineRunState.Running)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.LeaseUntil, lease).SetProperty(x => x.Stage, stage), ct);
        if (updated == 0) return NotRunning();
        events.Publish(new ClashDraftChanged(Actor.System, await db.Set<EngineRun>().Where(r => r.Id == id).Select(r => r.PartId).SingleAsync(ct)));
        return Results.Ok();
    }

    /// <summary>
    ///     Часть результата. Неожиданный сбой (сверка, COPY, база) уходит работнику ответом 500 с текстом ошибки: без него в прогоне
    ///     осталось бы пустое «Internal Server Error».
    /// </summary>
    private static async Task<IResult> ResultAsync(Guid id, int part, bool last, HttpRequest request, ToadFlowDb db, DraftHits hits,
        CollisionReconciler reconciler, IDomainEvents events, ILogger<DraftHits> logger, CancellationToken ct)
    {
        try
        {
            return await ResultCoreAsync(id, part, last, request, db, hits, reconciler, events, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Прогон движка {Run}: часть результата {Part} не принята", id, part);
            return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ResultCoreAsync(Guid id, int part, bool last, HttpRequest request, ToadFlowDb db, DraftHits hits,
        CollisionReconciler reconciler, IDomainEvents events, CancellationToken ct)
    {
        if (part < 0) return Results.BadRequest(new { error = "Номер части — от 0" });

        var result = await JsonSerializer.DeserializeAsync<EngineRunResult>(request.Body, ToadFlowDb.Json, ct);
        if (result is null || result.Hits.Count > EngineApi.ResultChunkHits)
            return Results.BadRequest(new { error = $"Часть результата — до {EngineApi.ResultChunkHits} коллизий" });

        var run = await db.Set<EngineRun>().SingleOrDefaultAsync(r => r.Id == id && r.Attempt == result.Attempt && r.State == EngineRunState.Running, ct);
        if (run is null) return NotRunning();

        // Запись, сверка и финиш проверяют попытку и состояние под блокировкой строки прогона. Если проверка не прошла, прогон
        // отменили или отдали другой попытке: строки стёрла отмена или выдача, а чужую попытку трогать нельзя.
        if (!await hits.WriteChunkAsync(run, part, result.Hits, ct)) return NotRunning();
        if (last && run.Kind == EngineRunKind.Approved)
        {
            if (await reconciler.ApplyAsync(run, result.Gone ?? [], result.Matches ?? [], result.Stats, ct) is null) return NotRunning();

            events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
            events.Publish(new ClashCollisionsChanged(Actor.System, run.PartId, null));
        }
        else if (last)
        {
            if (!await hits.CompleteAsync(run, result.Stats, ct)) return Results.Conflict(new { error = "Прогон уже отменён или завершён" });

            events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
        }

        return Results.Ok();
    }

    private static async Task<IResult> FailAsync(Guid id, EngineFailure failure, ToadFlowDb db, DraftHits hits, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var running = nameof(EngineRunState.Running);
        var locked = await db.Set<EngineRun>().FromSql($"SELECT * FROM clash_engine_runs WHERE id = {id} AND attempt = {failure.Attempt} AND state = {running} FOR UPDATE")
            .ToListAsync(ct);
        if (locked.Count == 0) return NotRunning();

        var run = locked[0];
        await hits.DiscardAsync(run.Id, ct);
        Release(run, failure.Retry, failure.Error, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
        return Results.Ok();
    }

    /// <summary>Прогон не дошёл до конца: снова в очередь, если сбой может не повториться и попытки остались; иначе — ошибка.</summary>
    internal static void Release(EngineRun run, bool retry, string error, DateTimeOffset now)
    {
        run.LeaseUntil = null;
        run.Stage = null;
        if (retry && run.Attempt < run.MaxAttempts)
        {
            run.State = EngineRunState.Queued;
            run.Error = error;
            return;
        }

        run.State = EngineRunState.Failed;
        run.Error = error;
        run.FinishedAt = now;
    }

    private static IResult NotRunning() => Results.Conflict(new { error = "Прогон больше не идёт" });
}
