using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Images;

/// <summary>Постановка задания картинок части после сверки утверждённого прогона.</summary>
internal static class ImageJobs
{
    /// <summary>
    ///     Ставит задание на прогон <paramref name="runId" /> или переключает на него прежнее, обнуляя аренду и попытки. Если
    ///     ни одной коллизии прогона картинка не нужна, задания нет. Вызывается в транзакции сверки.
    /// </summary>
    public static Task QueueAsync(ToadFlowDb db, Guid partId, Guid runId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH needed AS (
                SELECT EXISTS (SELECT 1 FROM clash_collisions
                               WHERE part_id = {partId} AND last_found_run_id = {runId}
                                 AND (image_fingerprint_a IS DISTINCT FROM fingerprint_a OR image_fingerprint_b IS DISTINCT FROM fingerprint_b)) AS yes),
            removed AS (DELETE FROM clash_image_jobs WHERE part_id = {partId} AND NOT (SELECT yes FROM needed))
            INSERT INTO clash_image_jobs (part_id, run_id, lease_until, attempt, error)
            SELECT {partId}, {runId}, NULL, 0, NULL WHERE (SELECT yes FROM needed)
            ON CONFLICT (part_id) DO UPDATE SET run_id = EXCLUDED.run_id, lease_until = NULL, attempt = 0, error = NULL
            """, ct);
}

/// <summary>
///     Картинки коллизий: протокол работника (<c>/api/engine/images</c>, ключ работника) и картинка для веба. Задание, которое
///     сверка уже переключила на новый прогон, отвечает 409: картинки по устаревшей геометрии не записываются.
/// </summary>
public static class ImageEndpoints
{
    public const string CacheControl = "private, max-age=31536000, immutable";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void MapClashImages(this IEndpointRouteBuilder app)
    {
        var images = app.MapGroup("/api/engine/images").RequireAuthorization(EngineAuthHandler.Policy);
        images.MapPost("/claim", ClaimAsync);
        images.MapGet("/{partId:guid}/batch", BatchAsync);
        images.MapPost("/{partId:guid}", UploadAsync).WithMetadata(new DisableRequestSizeLimitAttribute());
        images.MapPost("/{partId:guid}/done", DoneAsync);
        images.MapPost("/{partId:guid}/fail", FailAsync);
    }

    /// <summary>
    ///     Следующее задание со свободной или истёкшей арендой, если прогонов движка нет ни в очереди, ни в работе: картинки не
    ///     задерживают поиск. Задание, исчерпавшее попытки, снимается с записью в «События системы».
    /// </summary>
    private static async Task<IResult> ClaimAsync(ToadFlowDb db, TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        while (true)
        {
            var now = time.GetUtcNow();
            var lease = now + ClashImageJob.Lease;
            var queued = nameof(EngineRunState.Queued);
            var running = nameof(EngineRunState.Running);
            var claimed = await db.Set<ClashImageJob>().FromSql($"""
                UPDATE clash_image_jobs j SET attempt = attempt + 1, lease_until = {lease}
                WHERE part_id = (
                    SELECT part_id FROM clash_image_jobs
                    WHERE (lease_until IS NULL OR lease_until < {now})
                      AND NOT EXISTS (SELECT 1 FROM clash_engine_runs WHERE state IN ({queued}, {running}))
                    ORDER BY run_id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """).AsNoTracking().ToListAsync(ct);
            if (claimed.Count == 0) return Results.NoContent();

            var job = claimed[0];
            if (job.Attempt > ClashImageJob.MaxAttempts)
            {
                await db.Set<ClashImageJob>().Where(j => j.PartId == job.PartId && j.RunId == job.RunId).ExecuteDeleteAsync(ct);
                var name = await db.Set<ClashPart>().Where(p => p.Id == job.PartId).Select(p => p.Name).SingleOrDefaultAsync(ct) ?? "";
                events.Publish(new ClashImagesFailed(Actor.System, job.PartId, name, job.Error ?? "работник не сдал пачку за время аренды"));
                continue;
            }

            var models = await db.Set<EngineRun>().AsNoTracking().Where(r => r.Id == job.RunId).Select(r => r.Models).SingleOrDefaultAsync(ct);
            if (models is null)
            {
                await db.Set<ClashImageJob>().Where(j => j.PartId == job.PartId && j.RunId == job.RunId).ExecuteDeleteAsync(ct);
                continue;
            }

            return Results.Ok(new EngineImageAssignment(job.PartId, job.RunId, models));
        }
    }

    /// <summary>
    ///     Пачка коллизий, найденных прогоном задания, без картинки или с картинкой по устаревшим отпечаткам; продлевает аренду.
    ///     Ждёт или идёт прогон движка — пачка пуста с признаком: работник отпускает задание после сданной пачки.
    /// </summary>
    private static async Task<IResult> BatchAsync(Guid partId, Guid runId, ToadFlowDb db, TimeProvider time, CancellationToken ct)
    {
        var lease = time.GetUtcNow() + ClashImageJob.Lease;
        var extended = await db.Set<ClashImageJob>().Where(j => j.PartId == partId && j.RunId == runId)
            .ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, lease), ct);
        if (extended == 0) return Stale();
        if (await db.Set<EngineRun>().AnyAsync(r => r.State == EngineRunState.Queued || r.State == EngineRunState.Running, ct))
            return Results.Ok(new EngineImageBatch(runId, [], EngineRunWaiting: true));

        var items = await Needing(db, partId, runId).OrderBy(c => c.Number).Take(EngineApi.ImageBatchSize)
            .Select(c => new EngineImageRequest(c.Id, c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB, c.X, c.Y, c.Z, c.FingerprintA, c.FingerprintB))
            .ToListAsync(ct);
        return Results.Ok(new EngineImageBatch(runId, items));
    }

    private static IQueryable<ClashCollision> Needing(ToadFlowDb db, Guid partId, Guid runId) =>
        db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId && c.LastFoundRunId == runId
                                                           && (c.ImageFingerprintA != c.FingerprintA || c.ImageFingerprintB != c.FingerprintB));

    /// <summary>
    ///     Сдача пачки: multipart с манифестом и файлами <c>{коллизия:N}.png</c>. Отпечатки берутся из манифеста: коллизия,
    ///     изменившаяся за это время, вернётся в следующую пачку. Не нарисованная — без картинки, до смены отпечатков.
    /// </summary>
    private static async Task<IResult> UploadAsync(Guid partId, Guid runId, HttpRequest request, ToadFlowDb db, ClashStorage storage, TimeProvider time,
        IDomainEvents events, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Ожидается multipart с манифестом и картинками" });
        var form = await request.ReadFormAsync(ct);
        List<EngineImageResult>? manifest;
        try
        {
            manifest = form.TryGetValue(EngineApi.ImageManifestPart, out var json) && json.ToString() is { Length: > 0 } text
                ? JsonSerializer.Deserialize<List<EngineImageResult>>(text, ToadFlowDb.Json)
                : null;
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || manifest.Count > EngineApi.ImageBatchSize)
            return Results.BadRequest(new { error = $"Нужен манифест на 1–{EngineApi.ImageBatchSize} коллизий" });
        var files = form.Files.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Where(i => i.Ok))
        {
            if (!files.TryGetValue($"{item.CollisionId:N}.png", out var file) || !await IsPngAsync(file, ct))
                return Results.BadRequest(new { error = $"Нет картинки PNG коллизии {item.CollisionId}" });
        }

        // Блокировка задания: сверка нового прогона ждёт, пока пачка старого допишется, или пачка видит задание переключённым.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.Set<ClashImageJob>().FromSql($"SELECT * FROM clash_image_jobs WHERE part_id = {partId} AND run_id = {runId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (job is null) return Stale();

        var ids = manifest.Select(i => i.CollisionId).ToList();
        var collisions = await db.Set<ClashCollision>().Where(c => c.PartId == partId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        foreach (var item in manifest)
        {
            if (!collisions.TryGetValue(item.CollisionId, out var collision)) continue;
            if (item.Ok)
            {
                await using var content = files[$"{item.CollisionId:N}.png"].OpenReadStream();
                await storage.WriteImageAsync(partId, collision.Id, content, ct);
            }
            else storage.DeleteImage(partId, collision.Id);

            collision.HasImage = item.Ok;
            collision.ImageFingerprintA = item.FingerprintA;
            collision.ImageFingerprintB = item.FingerprintB;
        }

        job.LeaseUntil = time.GetUtcNow() + ClashImageJob.Lease;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new ClashCollisionsChanged(Actor.System, partId, null));
        return Results.Ok();
    }

    private static async Task<bool> IsPngAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length < PngSignature.Length) return false;
        await using var stream = file.OpenReadStream();
        var head = new byte[PngSignature.Length];
        await stream.ReadExactlyAsync(head, ct);
        return head.AsSpan().SequenceEqual(PngSignature);
    }

    private static async Task<IResult> DoneAsync(Guid partId, Guid runId, ToadFlowDb db, CancellationToken ct)
    {
        var deleted = await db.Set<ClashImageJob>().Where(j => j.PartId == partId && j.RunId == runId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Stale() : Results.Ok();
    }

    /// <summary>Сбой или уступка прогону движка: аренда освобождается, задание возьмут снова. Уступка попытку не тратит.</summary>
    private static async Task<IResult> FailAsync(Guid partId, Guid runId, EngineImageFailure failure, ToadFlowDb db, CancellationToken ct)
    {
        var job = await db.Set<ClashImageJob>().SingleOrDefaultAsync(j => j.PartId == partId && j.RunId == runId, ct);
        if (job is null) return Stale();
        job.LeaseUntil = null;
        if (failure.Yield) job.Attempt = Math.Max(0, job.Attempt - 1);
        else job.Error = failure.Error.Length > 2000 ? failure.Error[..2000] : failure.Error;
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>Картинка коллизии для веба; адрес несёт версию (отпечатки), поэтому ответ кешируется навсегда.</summary>
    internal static async Task<IResult> ImageAsync(Guid id, ToadFlowDb db, ClashStorage storage, HttpContext http, CancellationToken ct)
    {
        var collision = await db.Set<ClashCollision>().AsNoTracking().Where(c => c.Id == id).Select(c => new { c.PartId, c.HasImage })
            .SingleOrDefaultAsync(ct);
        if (collision is not { HasImage: true }) return Results.NotFound();
        var path = storage.ImagePath(collision.PartId, id);
        if (!File.Exists(path)) return Results.NotFound();
        http.Response.Headers.CacheControl = CacheControl;
        return Results.File(path, "image/png");
    }

    private static IResult Stale() => Results.Conflict(new { error = "Задание картинок переключено на другой прогон или снято" });
}
