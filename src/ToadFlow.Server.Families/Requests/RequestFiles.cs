using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Families.Requests;

public sealed record UploadedFile(Guid Id, string FileName, long Size, string ContentType);

/// <summary>
///     Вложения заявки. Файл загружается заранее «ничейным» (форма подачи, композер) и привязывается при подаче или отправке сообщения —
///     так миниатюры видны до отправки. Браузер показывает сам только картинки и PDF, распознанные по первым байтам; остальное — скачивание.
/// </summary>
internal static partial class RequestEndpoints
{
    public const long MaxFileSize = 25L * 1024 * 1024;
    public const int MaxFiles = 30;
    private const int MaxFileName = 200;

    private static void MapFiles(RouteGroupBuilder requests)
    {
        requests.MapPost("/uploads", UploadOrphanAsync).RequireAuthorization(Policies.Designer).DisableAntiforgery();
        requests.MapGet("/uploads/{fileId:guid}", OrphanAsync);
        requests.MapGet("/{id:guid}/files/{fileId:guid}", FileAsync);
        requests.MapPost("/{id:guid}/files", UploadAsync).RequireAuthorization(Policies.Designer).DisableAntiforgery();
        requests.MapDelete("/{id:guid}/files/{fileId:guid}", DeleteFileAsync).RequireAuthorization(Policies.Designer);
    }

    /// <summary>Загрузка «ничейного» файла: ещё не привязан к заявке, виден только загрузившему.</summary>
    private static async Task<IResult> UploadOrphanAsync(HttpRequest http, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time,
        IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        var (upload, name, refusal) = await ReadUploadAsync(http, ct);
        if (refusal is not null) return Results.BadRequest(new { error = refusal });

        var file = new FamilyRequestFile { Id = Guid.CreateVersion7(), FileName = name, Size = upload!.Length, UploadedBy = user.UserId(), At = time.GetUtcNow() };
        file.ContentType = await SaveAsync(upload, OrphanPath(storage.Value.Root, file.Id), ct);
        db.Set<FamilyRequestFile>().Add(file);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new UploadedFile(file.Id, file.FileName, file.Size, file.ContentType));
    }

    private static async Task<IResult> OrphanAsync(Guid fileId, HttpContext http, ToadFlowDb db, ClaimsPrincipal user, IOptions<FamiliesStorageOptions> storage,
        CancellationToken ct)
    {
        var me = user.UserId();
        var file = await db.Set<FamilyRequestFile>().AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.RequestId == null && f.UploadedBy == me, ct);
        return Serve(http, file, file is null ? null : OrphanPath(storage.Value.Root, file.Id), true);
    }

    /// <summary><c>?inline=1</c> — показать в браузере, если это картинка или PDF; иначе и по умолчанию — скачать.</summary>
    private static async Task<IResult> FileAsync(Guid id, Guid fileId, string? inline, HttpContext http, ToadFlowDb db, IOptions<FamiliesStorageOptions> storage,
        CancellationToken ct)
    {
        var file = await db.Set<FamilyRequestFile>().AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.RequestId == id, ct);
        return Serve(http, file, file is null ? null : FilePath(storage.Value.Root, id, fileId), inline is "1" or "true");
    }

    private static IResult Serve(HttpContext http, FamilyRequestFile? file, string? path, bool inline)
    {
        if (file is null || !File.Exists(path)) return Results.NotFound();
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (inline && RequestFileTypes.IsInline(file.ContentType))
        {
            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(file.FileName);
            http.Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
            return Results.File(path, file.ContentType, enableRangeProcessing: true);
        }

        return Results.File(path, "application/octet-stream", file.FileName);
    }

    /// <summary>Прежний способ (панель и веб 3.0): файл сразу к заявке.</summary>
    private static async Task<IResult> UploadAsync(Guid id, HttpRequest http, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        var (upload, name, refusal) = await ReadUploadAsync(http, ct);
        if (refusal is not null) return Results.BadRequest(new { error = refusal });

        var fileId = Guid.CreateVersion7();
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (!await CanWriteAsync(db, user, request, ct)) return "Прикладывать файлы могут участники заявки и координаторы — присоединитесь к ней";
            if (await db.Set<FamilyRequestFile>().CountAsync(f => f.RequestId == id, ct) >= MaxFiles) return $"У заявки уже {MaxFiles} вложений";
            var type = await SaveAsync(upload!, FilePath(storage.Value.Root, id, fileId), ct);
            db.Set<FamilyRequestFile>().Add(new FamilyRequestFile
                { Id = fileId, RequestId = id, FileName = name, ContentType = type, Size = upload!.Length, UploadedBy = me, At = now });
            return null;
        }, $"приложил файл «{name}»");
    }

    private static async Task<IResult> DeleteFileAsync(Guid id, Guid fileId, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        string? name = null;
        var result = await ChangeAsync(id, db, user, time, events, ct, async (request, me, _) =>
        {
            var file = await db.Set<FamilyRequestFile>().SingleOrDefaultAsync(f => f.Id == fileId && f.RequestId == id, ct);
            if (file is null) return "Файл не найден";
            if (!request.IsOpen) return "Заявка закрыта — вложения не меняются";
            if (file.UploadedBy != me && !user.IsStaff()) return "Удалить файл может тот, кто его приложил, или координатор";
            db.Set<FamilyRequestFile>().Remove(file);
            name = file.FileName;
            return null;
        }, () => $"удалил файл «{name}»");
        if (name is not null) File.Delete(FilePath(storage.Value.Root, id, fileId));
        return result;
    }

    /// <summary>
    ///     Привязывает свои «ничейные» загрузки к заявке (и сообщению): переносит файлы в папку заявки. Изменения сохраняет вызывающий.
    /// </summary>
    /// <returns>Отказ для показа; null — привязано.</returns>
    private static async Task<string?> AttachAsync(ToadFlowDb db, string root, Guid requestId, Guid? messageId, IReadOnlyCollection<Guid>? fileIds, Guid me,
        CancellationToken ct)
    {
        if (fileIds is not { Count: > 0 }) return null;
        var ids = fileIds.Distinct().ToList();
        var files = await db.Set<FamilyRequestFile>().Where(f => ids.Contains(f.Id) && f.RequestId == null && f.UploadedBy == me).ToListAsync(ct);
        if (files.Count != ids.Count) return "Файл не найден — загрузите его ещё раз";
        if (await db.Set<FamilyRequestFile>().CountAsync(f => f.RequestId == requestId, ct) + files.Count > MaxFiles) return $"У заявки — до {MaxFiles} вложений";

        foreach (var file in files)
        {
            var target = FilePath(root, requestId, file.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(OrphanPath(root, file.Id), target, true);
            file.RequestId = requestId;
            file.MessageId = messageId;
        }

        return null;
    }

    private static async Task<(IFormFile? Upload, string Name, string? Refusal)> ReadUploadAsync(HttpRequest http, CancellationToken ct)
    {
        if (!http.HasFormContentType) return (null, "", "Нужен файл");
        var form = await http.ReadFormAsync(ct);
        if (form.Files.Count != 1) return (null, "", "Один файл за раз");
        var upload = form.Files[0];
        if (upload.Length == 0 || upload.Length > MaxFileSize) return (null, "", $"Файл — до {MaxFileSize / 1024 / 1024} МБ");
        var name = Path.GetFileName(upload.FileName.Replace('\\', '/')).Trim();
        if (name.Length == 0 || name.Length > MaxFileName) return (null, "", $"Имя файла — до {MaxFileName} знаков");
        return (upload, name, null);
    }

    /// <summary>Сохраняет файл и возвращает его тип по первым байтам: картинка, PDF или <c>application/octet-stream</c>.</summary>
    private static async Task<string> SaveAsync(IFormFile upload, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var target = File.Create(path))
        await using (var source = upload.OpenReadStream())
            await source.CopyToAsync(target, ct);

        var head = new byte[16];
        await using var read = File.OpenRead(path);
        var count = await read.ReadAtLeastAsync(head, head.Length, false, ct);
        return RequestFileTypes.Sniff(head.AsSpan(0, count));
    }

    public static string FilePath(string root, Guid requestId, Guid fileId) => Path.Combine(root, "requests", requestId.ToString("N"), fileId.ToString("N"));

    public static string OrphanPath(string root, Guid fileId) => Path.Combine(root, "requests", "_uploads", fileId.ToString("N"));

    /// <summary>«Ничейные» загрузки старше суток: из базы и с диска.</summary>
    internal static async Task RemoveOrphansAsync(ToadFlowDb db, string root, DateTimeOffset now, CancellationToken ct)
    {
        var before = now.AddDays(-1);
        var orphans = await db.Set<FamilyRequestFile>().Where(f => f.RequestId == null && f.At < before).ToListAsync(ct);
        if (orphans.Count == 0) return;
        db.Set<FamilyRequestFile>().RemoveRange(orphans);
        await db.SaveChangesAsync(ct);
        foreach (var orphan in orphans) File.Delete(OrphanPath(root, orphan.Id));
    }
}

/// <summary>Какие файлы браузер может показать сам: картинки и PDF. Тип определяется по первым байтам, а не по заявленному.</summary>
public static class RequestFileTypes
{
    private static readonly string[] Inline = ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf"];

    public static bool IsInline(string contentType) => Inline.Contains(contentType);

    public static string Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return "image/gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (head.StartsWith("%PDF-"u8)) return "application/pdf";
        return "application/octet-stream";
    }
}
