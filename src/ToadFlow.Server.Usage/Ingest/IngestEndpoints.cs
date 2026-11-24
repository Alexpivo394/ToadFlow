using System.Text.Json;
using ToadFlow.Server.Auth;

namespace ToadFlow.Server.Usage.Ingest;

public static class IngestEndpoints
{
    /// <summary>
    ///     <c>POST /api/usage/events</c> — пачка от ToadTools любой роли, включая проектировщика. Человек — из токена, в теле его нет.
    ///     400 — тело не пачка или в ней больше <see cref="IngestBatch.MaxItems" /> записей; неверные записи внутри пачки 400 не дают.
    /// </summary>
    public static void MapUsageIngest(this RouteGroupBuilder usage) =>
        usage.MapPost("/events", async (HttpContext http, UsageIngest ingest) =>
        {
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Тело запроса — не JSON" });
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Нет списка записей items" });
                if (items.GetArrayLength() > IngestBatch.MaxItems)
                    return Results.BadRequest(new { error = $"В пачке больше {IngestBatch.MaxItems} записей" });

                var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) ? number : 1;
                var batch = new IngestBatch(version, items.EnumerateArray().ToList());
                return Results.Ok(await ingest.ApplyAsync(http.User.UserId(), batch, http.RequestAborted));
            }
        });
}
