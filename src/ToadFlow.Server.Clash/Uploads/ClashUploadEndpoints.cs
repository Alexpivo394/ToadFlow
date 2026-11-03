using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Clash.Uploads;

/// <summary>
///     Приём слепка от агента частями: начать загрузку задания, дописывать части с заданного места, узнать, сколько принято,
///     завершить. Загрузку принимают, пока задание выполняется у этого агента: слепок брошенной попытки не публикуется.
/// </summary>
public static class ClashUploadEndpoints
{
    /// <summary>Больше этого слепок не бывает: защита от испорченного запроса, а не ограничение моделей.</summary>
    private const long MaxFileBytes = 64L * 1024 * 1024 * 1024;

    public static void MapClashUploads(this RouteGroupBuilder agentApi)
    {
        var uploads = agentApi.MapGroup("/clash/uploads");
        uploads.MapPost("", StartAsync);
        uploads.MapGet("/{id:guid}", GetAsync);
        uploads.MapPut("/{id:guid}/{file}", AppendAsync);
        uploads.MapPost("/{id:guid}/complete", CompleteAsync);
    }

    private static async Task<IResult> StartAsync(ClashUploadRequest request, HttpContext http, ToadFlowDb db, ClashStorage storage, TimeProvider time)
    {
        if (request.GeometryBytes is <= 0 or > MaxFileBytes || request.ParametersBytes is <= 0 or > MaxFileBytes)
            return Results.BadRequest(new { error = "Недопустимый размер файлов слепка" });
        if (!IsSha256(request.GeometrySha256) || !IsSha256(request.ParametersSha256))
            return Results.BadRequest(new { error = "Контрольная сумма — SHA-256 в шестнадцатеричном виде" });

        var agentId = http.User.AgentId();
        var job = await RunningJobAsync(db, request.JobId, agentId, time, http.RequestAborted, request.Attempt);
        if (job is null) return NotRunning();
        if (ModelIdOf(job) is not { } modelId) return Results.UnprocessableEntity(new { error = "В задании нет ключа модели" });

        var existing = await db.Set<ClashUpload>().AsNoTracking().FirstOrDefaultAsync(u => u.JobId == job.Id && u.AgentId == agentId
            && u.GeometryBytes == request.GeometryBytes && u.GeometrySha256 == request.GeometrySha256
            && u.ParametersBytes == request.ParametersBytes && u.ParametersSha256 == request.ParametersSha256, http.RequestAborted);
        if (existing is not null) return Results.Ok(StateOf(storage, existing));

        var upload = new ClashUpload
        {
            Id = Guid.CreateVersion7(),
            JobId = job.Id,
            AgentId = agentId,
            ModelId = modelId,
            GeometryBytes = request.GeometryBytes,
            GeometrySha256 = request.GeometrySha256.ToLowerInvariant(),
            ParametersBytes = request.ParametersBytes,
            ParametersSha256 = request.ParametersSha256.ToLowerInvariant(),
            CreatedAt = time.GetUtcNow()
        };
        Directory.CreateDirectory(storage.UploadFolder(upload.Id));
        db.Set<ClashUpload>().Add(upload);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/agent/clash/uploads/{upload.Id}", StateOf(storage, upload));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, ToadFlowDb db, ClashStorage storage)
    {
        var upload = await UploadAsync(db, id, http);
        return upload is null ? Results.NotFound() : Results.Ok(StateOf(storage, upload));
    }

    private static async Task<IResult> AppendAsync(Guid id, string file, long offset, HttpContext http, ToadFlowDb db, ClashStorage storage, TimeProvider time)
    {
        if (ClashUploads.FileName(file) is not { } fileName) return Results.NotFound();
        var upload = await UploadAsync(db, id, http);
        if (upload is null) return Results.NotFound();
        if (upload.SnapshotId is not null) return Results.Conflict(StateOf(storage, upload));
        if (await RunningJobAsync(db, upload.JobId, upload.AgentId, time, http.RequestAborted, startedBy: upload.CreatedAt) is null) return NotRunning();

        var declared = file == ClashUploads.Geometry ? upload.GeometryBytes : upload.ParametersBytes;
        var path = Path.Combine(storage.UploadFolder(upload.Id), fileName);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var start = stream.Length;
        if (offset != start) return Results.Conflict(StateOf(storage, upload));

        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        long written = 0;
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            written += read;
            if (written > ClashUploads.MaxChunkBytes || start + written > declared)
            {
                stream.SetLength(start);
                return written > ClashUploads.MaxChunkBytes
                    ? Results.Json(new { error = $"Часть больше {ClashUploads.MaxChunkBytes} байт" }, statusCode: StatusCodes.Status413PayloadTooLarge)
                    : Results.UnprocessableEntity(new { error = "Файл длиннее объявленного размера" });
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
        }

        await stream.FlushAsync(http.RequestAborted);
        await stream.DisposeAsync();
        return Results.Ok(StateOf(storage, upload));
    }

    private static async Task<IResult> CompleteAsync(Guid id, HttpContext http, ToadFlowDb db, ClashStorage storage, SnapshotPublisher publisher, TimeProvider time)
    {
        var upload = await db.Set<ClashUpload>().SingleOrDefaultAsync(u => u.Id == id && u.AgentId == http.User.AgentId(), http.RequestAborted);
        if (upload is null) return Results.NotFound();
        if (upload.SnapshotId is { } published)
        {
            var snapshot = await db.Set<ClashSnapshot>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == published, http.RequestAborted);
            return snapshot is null ? Results.Conflict(new { error = "Слепок этой загрузки уже удалён" }) : Results.Ok(SnapshotPublisher.ResultOf(snapshot));
        }

        if (await RunningJobAsync(db, upload.JobId, upload.AgentId, time, http.RequestAborted, startedBy: upload.CreatedAt) is null) return NotRunning();

        try
        {
            return Results.Ok(await publisher.PublishAsync(upload, http.RequestAborted));
        }
        catch (SnapshotRejectedException rejected)
        {
            return Results.UnprocessableEntity(new { error = rejected.Message });
        }
    }

    /// <summary>
    ///     Задание, если оно выполняется у этого агента, — с продлённой арендой: агент загружает слепок, уже отпустив Revit,
    ///     и heartbeat экземпляра аренду больше не продлевает. Загрузка идёт — значит, агент жив.
    /// </summary>
    private static async Task<Job?> RunningJobAsync(ToadFlowDb db, Guid jobId, Guid agentId, TimeProvider time, CancellationToken ct, int? attempt = null,
        DateTimeOffset? startedBy = null)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.AgentId == agentId && j.Status == JobStatus.Running
                                                                         && j.ModuleKind == ModuleKinds.ClashExtract
                                                                         && (attempt == null || j.Attempt == attempt)
                                                                         && (startedBy == null || j.StartedAt <= startedBy), ct);
        if (job is null) return null;

        var leaseUntil = time.GetUtcNow() + AgentHub.Lease;
        await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running && (j.LeaseUntil == null || j.LeaseUntil < leaseUntil))
            .ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, leaseUntil), ct);
        return job;
    }

    private static Task<ClashUpload?> UploadAsync(ToadFlowDb db, Guid id, HttpContext http) =>
        db.Set<ClashUpload>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.AgentId == http.User.AgentId(), http.RequestAborted);

    private static IResult NotRunning() => Results.Conflict(new { error = "Задание не выполняется у этого агента: загрузка не принимается" });

    private static Guid? ModelIdOf(Job job)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ClashExtractPayload>(job.PayloadJson, ToadFlowDb.Json);
            return Guid.TryParseExact(payload?.ModelKey, "N", out var id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClashUploadState StateOf(ClashStorage storage, ClashUpload upload)
    {
        var folder = storage.UploadFolder(upload.Id);
        return new ClashUploadState(upload.Id, Length(Path.Combine(folder, ClashUploads.FileName(ClashUploads.Geometry)!)),
            Length(Path.Combine(folder, ClashUploads.FileName(ClashUploads.Parameters)!)));
    }

    private static long Length(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
