using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Отчёт утверждённого прогона, список прогонов проверки и текст для чата. Открыто любой роли, как «Проверки».</summary>
public static class ReportEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashReports(this RouteGroupBuilder members)
    {
        var report = members.MapGroup("/clash/reports/{runId:guid}");
        report.MapGet("", async (Guid runId, RunReports reports, CancellationToken ct) =>
            await reports.ViewAsync(runId, ct) is { } view ? Results.Ok(view) : Results.NotFound());
        report.MapGet("/collisions", CollisionsAsync);
        report.MapGet("/chat", ChatAsync);
        report.MapGet("/export.xlsx", (Guid runId, string? scope, string? sort, string? baseUrl, HttpContext http, RunReports reports, ISettingsStore general,
            Data.ToadFlowDb db, Images.CollisionImages collisionImages, CancellationToken ct) => ExportAsync(runId, scope, sort, baseUrl, "xlsx", http, reports, general, db, collisionImages, ct));
        report.MapGet("/export.html", (Guid runId, string? scope, string? sort, string? baseUrl, HttpContext http, RunReports reports, ISettingsStore general,
            Data.ToadFlowDb db, Images.CollisionImages collisionImages, CancellationToken ct) => ExportAsync(runId, scope, sort, baseUrl, "html", http, reports, general, db, collisionImages, ct));
        members.MapGet("/clash/parts/{id:guid}/approved/runs", async (Guid id, RunReports reports, CancellationToken ct) =>
            Results.Ok(await reports.RunsAsync(id, ct)));
    }

    /// <param name="scope"><c>changes</c> (по умолчанию) — изменения прогона; <c>open</c> — открытые сейчас.</param>
    private static async Task<IResult> CollisionsAsync(Guid runId, string? scope, string? sort, int? page, int? pageSize, HttpRequest request,
        RunReports reports, Data.ToadFlowDb db, CancellationToken ct)
    {
        if (await reports.ContextAsync(runId, ct) is not { } context) return Results.NotFound();
        var rows = reports.Rows(context, Scope(scope), CollisionFilter.From(request), sort);
        var total = await rows.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? DraftResultEndpoints.DefaultPageSize, 1, DraftResultEndpoints.MaxPageSize);
        var items = await rows.Skip((Math.Max(1, page ?? 1) - 1) * size).Take(size).ToListAsync(ct);
        var suggested = await CollisionEndpoints.NumbersAsync(db, items.Select(r => r.Collision.SuggestedPredecessorId).OfType<Guid>().ToList(), ct);
        return Results.Ok(new ReportPage(total, items.Select(r => RunReports.Row(r, context.ModelNames, suggested)).ToList()));
    }

    private static async Task<IResult> ChatAsync(Guid runId, string? baseUrl, RunReports reports, ISettingsStore general, CancellationToken ct)
    {
        if (ReportUrl(baseUrl, runId) is not { } url) return BadBaseUrl();
        if (await reports.ViewAsync(runId, ct) is not { } view) return Results.NotFound();
        var zone = (await general.GetAsync(ct)).TimeZone;
        return Results.Ok(new ChatMessageView(ReportChatMessage.Compose(view, url, zone)));
    }

    /// <summary>
    ///     Выгрузка отчёта с фильтрами страницы: строки читаются из базы потоком и пишутся в тело ответа, не собираясь в памяти.
    /// </summary>
    private static async Task<IResult> ExportAsync(Guid runId, string? scope, string? sort, string? baseUrl, string format, HttpContext http,
        RunReports reports, ISettingsStore general, Data.ToadFlowDb db, Images.CollisionImages collisionImages, CancellationToken ct)
    {
        if (WebBase(baseUrl) is not { } web) return BadBaseUrl();
        if (await reports.ContextAsync(runId, ct) is not { } context || await reports.ViewAsync(runId, ct) is not { } view) return Results.NotFound();
        var zone = (await general.GetAsync(ct)).TimeZone;
        var rows = reports.Rows(context, Scope(scope), CollisionFilter.From(http.Request), sort);

        var day = TimeZoneInfo.ConvertTime(view.Run.FinishedAt, zone).ToString("dd.MM.yyyy");
        var title = string.Join(' ', new[] { "Коллизии", view.Check.Object, view.Check.Name, day }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + "." + format;
        http.Response.ContentType = format == "xlsx" ? ReportXlsx.ContentType : ReportHtml.ContentType;
        http.Response.Headers.ContentDisposition =
            new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = $"\"report.{format}\"", FileNameStar = name }.ToString();

        var body = http.Response.Body;
        if (format == "xlsx") await ReportXlsx.WriteAsync(body, view, Export(rows, context, ct), web, zone, ct);
        else
        {
            var total = await rows.CountAsync(ct);
            var images = await RenderImagesAsync(rows, db, collisionImages, ct);
            await ReportHtml.WriteAsync(body, view, Export(rows, context, ct), total,
                (row, _) => Task.FromResult(images.GetValueOrDefault(row.Id)), web, zone, ct);
        }

        return Results.Empty;
    }

    /// <summary>
    ///     Картинки первых <see cref="ReportHtml.MaxImages" /> строк выборки: готовые берутся из кеша, недостающие рисуются (число
    ///     одновременных отрисовок ограничено в <see cref="Images.CollisionImages" />). Строки без картинки (404, 410) в словаре не появляются.
    /// </summary>
    private static async Task<Dictionary<Guid, byte[]>> RenderImagesAsync(IQueryable<ReportRowSource> rows, Data.ToadFlowDb db,
        Images.CollisionImages images, CancellationToken ct)
    {
        var ids = await rows.Select(r => r.Collision.Id).Take(ReportHtml.MaxImages).ToListAsync(ct);
        var targets = await Images.ImageTargets.LoadAsync(db, ids, ct);
        var results = await Task.WhenAll(targets.Values.Select(async target => (target.CollisionId, Result: await images.GetAsync(target, ct))));
        var bytes = new Dictionary<Guid, byte[]>(results.Length);
        foreach (var (id, result) in results)
            if (result.Status == StatusCodes.Status200OK)
                bytes[id] = await File.ReadAllBytesAsync(result.Path!, ct);
        return bytes;
    }

    private static async IAsyncEnumerable<ReportExportRow> Export(IQueryable<ReportRowSource> rows, ReportContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var none = new Dictionary<Guid, int>();
        await foreach (var source in rows.AsAsyncEnumerable().WithCancellation(ct))
            yield return new ReportExportRow(RunReports.Row(source, context.ModelNames, none), source.Collision.CreatedAt);
    }

    internal static string Scope(string? scope) => scope == RunReports.Open ? RunReports.Open : RunReports.Changes;

    /// <summary>
    ///     Адрес веба для ссылок: публичного адреса в настройках нет, его передаёт веб (<c>window.location.origin</c>). Только
    ///     абсолютный http или https; иначе null.
    /// </summary>
    internal static string? WebBase(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? baseUrl!.TrimEnd('/')
            : null;

    internal static string? ReportUrl(string? baseUrl, Guid runId) => WebBase(baseUrl) is { } web ? $"{web}/clash/reports/{runId}" : null;

    internal static IResult BadBaseUrl() => Results.BadRequest(new { error = "Нужен адрес веба (baseUrl): абсолютный http или https" });
}
