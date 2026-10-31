using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Images;

/// <summary>Картинка коллизии для веба: из кеша или нарисованная по запросу.</summary>
public static class ImageEndpoints
{
    public const string CacheControl = "private, max-age=31536000, immutable";

    /// <summary>
    ///     Картинка коллизии: из кеша или нарисованная по запросу. Навсегда кешируется только ответ на адрес с текущей версией
    ///     (<c>?v=</c>); без версии или с устаревшей — <c>no-cache</c>. Нет картинки — 404 или 410 с причиной.
    /// </summary>
    internal static async Task<IResult> ImageAsync(Guid id, string? v, ToadFlowDb db, CollisionImages images, HttpContext http, CancellationToken ct)
    {
        if (!(await ImageTargets.LoadAsync(db, [id], ct)).TryGetValue(id, out var target))
            return Results.NotFound(new { error = "коллизия не найдена" });

        // Файл версии может удалить запись более новой версии между ответом кеша и чтением: тогда спросить ещё раз.
        for (var attempt = 0; ; attempt++)
        {
            var result = await images.GetAsync(target, ct);
            if (result.Status != StatusCodes.Status200OK) return Results.Json(new { error = result.Error }, statusCode: result.Status);
            FileStream file;
            try
            {
                file = new FileStream(result.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            }
            catch (Exception exception) when (attempt == 0 && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            http.Response.Headers.CacheControl = v == target.Version ? CacheControl : "no-cache";
            return Results.Stream(file, "image/png");
        }
    }
}
