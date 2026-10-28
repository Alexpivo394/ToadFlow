using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>
///     Протокол работника движка (<c>/api/engine</c>, ключ работника): взять прогон, скачать слепки, сообщить ход, сдать
///     результат частями или сбой. Прогон, который больше не идёт (отменён, сдан, отдан другой попытке), отвечает 409.
/// </summary>
public static class EngineEndpoints
{
    public static void MapClashEngine(this IEndpointRouteBuilder app)
    {
        var engine = app.MapGroup("/api/engine").RequireAuthorization(EngineAuthHandler.Policy);
        engine.MapPost("/runs/claim", ClaimAsync);
        engine.MapGet("/snapshots/{id:guid}/{file}", DownloadAsync);
        engine.MapPost("/runs/{id:guid}/progress", ProgressAsync);
        engine.MapPost("/runs/{id:guid}/result", ResultAsync).WithMetadata(new DisableRequestSizeLimitAttribute());
        engine.MapPost("/runs/{id:guid}/fail", FailAsync);
    }

    /// <summary>Следующий прогон очереди, если сейчас ни один не идёт: движок считает по одному прогону.</summary>
    private static async Task<IResult> ClaimAsync(ToadFlowDb db, TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lease = now + EngineRun.Lease;
        var queued = nameof(EngineRunState.Queued);
        var running = nameof(EngineRunState.Running);
        var claimed = await db.Set<EngineRun>().FromSql($"""
            UPDATE clash_engine_runs SET state = {running}, attempt = attempt + 1, lease_until = {lease}, started_at = {now}, stage = NULL
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
        events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
        return Results.Ok(new EngineRunAssignment(run.Id, run.ConfigurationJson, run.Models, run.Attempt));
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
        var updated = await db.Set<EngineRun>().Where(r => r.Id == id && r.State == EngineRunState.Running)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.LeaseUntil, lease).SetProperty(x => x.Stage, stage), ct);
        if (updated == 0) return NotRunning();
        events.Publish(new ClashDraftChanged(Actor.System, await db.Set<EngineRun>().Where(r => r.Id == id).Select(r => r.PartId).SingleAsync(ct)));
        return Results.Ok();
    }

    private static async Task<IResult> ResultAsync(Guid id, int part, bool last, HttpRequest request, ToadFlowDb db, DraftHits hits, TimeProvider time,
        IDomainEvents events,
        CancellationToken ct)
    {
        var run = await db.Set<EngineRun>().SingleOrDefaultAsync(r => r.Id == id && r.State == EngineRunState.Running, ct);
        if (run is null) return NotRunning();
        if (part < 0) return Results.BadRequest(new { error = "Номер части — от 0" });

        var result = await JsonSerializer.DeserializeAsync<EngineRunResult>(request.Body, ToadFlowDb.Json, ct);
        if (result is null || result.Hits.Count > EngineApi.ResultChunkHits)
            return Results.BadRequest(new { error = $"Часть результата — до {EngineApi.ResultChunkHits} коллизий" });

        await hits.WriteChunkAsync(run, part, result.Hits, ct);
        run.LeaseUntil = time.GetUtcNow() + EngineRun.Lease;
        if (last)
        {
            await hits.CompleteAsync(run, result.Stats, ct);
            events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
        }
        else await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static async Task<IResult> FailAsync(Guid id, EngineFailure failure, ToadFlowDb db, DraftHits hits, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var run = await db.Set<EngineRun>().SingleOrDefaultAsync(r => r.Id == id && r.State == EngineRunState.Running, ct);
        if (run is null) return NotRunning();

        await hits.DiscardAsync(run.Id, ct);
        Release(run, failure.Retry, failure.Error, time.GetUtcNow());
        events.Publish(new ClashDraftChanged(Actor.System, run.PartId));
        await db.SaveChangesAsync(ct);
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
