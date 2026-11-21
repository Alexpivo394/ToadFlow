using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Families.Requests;

/// <summary>Вложения заявки.</summary>
internal static partial class RequestEndpoints
{
    public const long MaxFileSize = 25L * 1024 * 1024;
    public const int MaxFiles = 30;

    private static void MapFiles(RouteGroupBuilder requests)
    {
        requests.MapGet("/{id:guid}/files/{fileId:guid}", FileAsync);
        requests.MapPost("/{id:guid}/files", UploadAsync).RequireAuthorization(Policies.Designer);
    }

    private static async Task<IResult> FileAsync(Guid id, Guid fileId, ToadFlowDb db, IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        var file = await db.Set<FamilyRequestFile>().AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.RequestId == id, ct);
        var path = file is null ? null : FilePath(storage.Value.Root, id, fileId);
        if (file is null || !File.Exists(path)) return Results.NotFound();
        return Results.File(path, "application/octet-stream", file.FileName);
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpRequest http, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        if (!http.HasFormContentType) return Results.BadRequest(new { error = "Нужен файл" });
        var form = await http.ReadFormAsync(ct);
        if (form.Files.Count != 1) return Results.BadRequest(new { error = "Один файл за раз" });
        var upload = form.Files[0];
        if (upload.Length == 0 || upload.Length > MaxFileSize) return Results.BadRequest(new { error = $"Файл — до {MaxFileSize / 1024 / 1024} МБ" });
        var name = Path.GetFileName(upload.FileName.Replace('\\', '/')).Trim();
        if (name.Length == 0 || name.Length > 200) return Results.BadRequest(new { error = "Имя файла — до 200 знаков" });

        var fileId = Guid.CreateVersion7();
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (!await CanWriteAsync(db, user, request, ct)) return "Прикладывать файлы могут участники заявки и координаторы — присоединитесь к ней";
            if (await db.Set<FamilyRequestFile>().CountAsync(f => f.RequestId == id, ct) >= MaxFiles) return $"У заявки уже {MaxFiles} вложений";

            var path = FilePath(storage.Value.Root, id, fileId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var target = File.Create(path))
            await using (var source = upload.OpenReadStream())
                await source.CopyToAsync(target, ct);

            db.Set<FamilyRequestFile>().Add(new FamilyRequestFile
            {
                Id = fileId,
                RequestId = id,
                FileName = name,
                ContentType = upload.ContentType is { Length: > 0 and <= 200 } type ? type : "application/octet-stream",
                Size = upload.Length,
                UploadedBy = me,
                At = now
            });
            return null;
        }, $"приложил файл «{name}»");
    }

    public static string FilePath(string root, Guid requestId, Guid fileId) => Path.Combine(root, "requests", requestId.ToString("N"), fileId.ToString("N"));
}

/// <summary>Какие файлы браузер может показать сам: картинки и PDF. Тип определяется по первым байтам, а не по заявленному.</summary>
public static class RequestFileTypes
{
    private static readonly string[] Inline = ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf"];

    public static bool IsInline(string contentType) => Inline.Contains(contentType);
}
