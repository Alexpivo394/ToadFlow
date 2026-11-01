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
        report.MapGet("/levels", LevelsAsync);
        report.MapGet("/chat", ChatAsync);
        report.MapGet("/export.xlsx", (Guid runId, string? scope, string? sort, string? baseUrl, HttpContext http, RunReports reports, ISettingsStore general,
                CancellationToken ct) => ExportAsync(runId, scope, sort, baseUrl, "xlsx", http, reports, general,
                (export, body) => ReportXlsx.WriteAsync(body, export.View, ReportPairFile.Rows(export.Selection, export.Context, ct), export.Web, export.Zone, ct),
                ct));
        report.MapGet("/export.html", (Guid runId, string? scope, string? sort, string? baseUrl, HttpContext http, RunReports reports, ISettingsStore general,
            Data.ToadFlowDb db, Images.CollisionImages images, CancellationToken ct) =>
        {
            if (CollisionFilter.From(http.Request).Pair is null)
                return Task.FromResult(Results.BadRequest(new { error = "HTML выгружается по паре; раздел или всё — архивом" }));
            return ExportAsync(runId, scope, sort, baseUrl, "html", http, reports, general,
                (export, body) => ReportPairFile.WriteAsync(body, export.View, export.Context, export.Selection, export.Filter.Pair!, db, images, export.Web,
                    export.Zone, ct), ct);
        });
        members.MapGet("/clash/parts/{id:guid}/approved/runs", async (Guid id, RunReports reports, CancellationToken ct) =>
            Results.Ok(await reports.RunsAsync(id, ct)));
    }

    /// <param name="scope"><c>changes</c> (по умолчанию) — изменения прогона; <c>open</c> — открытые сейчас.</param>
    private static async Task<IResult> CollisionsAsync(Guid runId, string? scope, string? sort, int? page, int? pageSize, HttpRequest request,
        RunReports reports, Data.ToadFlowDb db, CancellationToken ct)
    {
        if (await reports.ContextAsync(runId, ct) is not { } context) return Results.NotFound();
        var filter = CollisionFilter.From(request);
        var ranks = await reports.RanksAsync(context.Run.PartId, ct);
        var order = await reports.OrderAsync(context, Scope(scope), filter, ranks, ct);
        var size = Math.Clamp(pageSize ?? DraftResultEndpoints.DefaultPageSize, 1, DraftResultEndpoints.MaxPageSize);
        var (total, items) = await reports.PageAsync(context, Scope(scope), filter, sort, order, Math.Max(1, page ?? 1), size, ct);
        var suggested = await CollisionEndpoints.NumbersAsync(db, items.Select(r => r.Collision.SuggestedPredecessorId).OfType<Guid>().ToList(), ct);
        return Results.Ok(new ReportPage(total, items.Select(r => RunReports.Row(r, context.ModelNames, suggested)).ToList()));
    }

    /// <summary>Уровни выборки по парам, в порядке строк, с числами — заголовки групп и фильтр уровня.</summary>
    private static async Task<IResult> LevelsAsync(Guid runId, string? scope, HttpRequest request, RunReports reports, CancellationToken ct)
    {
        if (await reports.ContextAsync(runId, ct) is not { } context) return Results.NotFound();
        var ranks = await reports.RanksAsync(context.Run.PartId, ct);
        return Results.Ok((await reports.OrderAsync(context, Scope(scope), CollisionFilter.From(request), ranks, ct)).LevelViews);
    }

    private static async Task<IResult> ChatAsync(Guid runId, string? baseUrl, RunReports reports, ISettingsStore general, CancellationToken ct)
    {
        if (ReportUrl(baseUrl, runId) is not { } url) return BadBaseUrl();
        if (await reports.ViewAsync(runId, ct) is not { } view) return Results.NotFound();
        var zone = (await general.GetAsync(ct)).TimeZone;
        return Results.Ok(new ChatMessageView(ReportChatMessage.Compose(view, url, zone)));
    }

    /// <summary>Что пишет выгрузка: отчёт, строки по фильтрам страницы, адрес веба и часовой пояс.</summary>
    private sealed record ExportSource(ReportView View, ReportContext Context, ReportSelection Selection, CollisionFilter Filter, string Web, TimeZoneInfo Zone);

    /// <summary>
    ///     Выгрузка отчёта с фильтрами страницы: строки читаются из базы потоком и пишутся в тело ответа, не собираясь в памяти.
    /// </summary>
    /// <param name="write">Пишет файл формата в тело ответа.</param>
    private static async Task<IResult> ExportAsync(Guid runId, string? scope, string? sort, string? baseUrl, string format, HttpContext http,
        RunReports reports, ISettingsStore general, Func<ExportSource, Stream, Task> write, CancellationToken ct)
    {
        if (WebBase(baseUrl) is not { } web) return BadBaseUrl();
        if (await reports.ContextAsync(runId, ct) is not { } context || await reports.ViewAsync(runId, ct) is not { } view) return Results.NotFound();
        var zone = (await general.GetAsync(ct)).TimeZone;
        var filter = CollisionFilter.From(http.Request);
        var selection = await reports.SelectAsync(context, Scope(scope), filter, sort, ct);

        var day = TimeZoneInfo.ConvertTime(view.Run.FinishedAt, zone).ToString("dd.MM.yyyy");
        var pair = filter.Pair is { } key ? ReportPairFile.Pair(view, selection.Ranks, key).Label : null;
        var name = ReportPairFile.FileName(new[] { "Коллизии", view.Check.Object, view.Check.Name, pair, day }) + "." + format;
        http.Response.ContentType = format == "xlsx" ? ReportXlsx.ContentType : ReportHtml.ContentType;
        http.Response.Headers.ContentDisposition =
            new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = $"\"report.{format}\"", FileNameStar = name }.ToString();

        await write(new ExportSource(view, context, selection, filter, web, zone), http.Response.Body);
        return Results.Empty;
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
