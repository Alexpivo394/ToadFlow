using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Collisions;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Архив HTML-выгрузки раздела или всех пар: запуск, ход, скачивание и отмена. Открыто любой роли, как отчёт.</summary>
public static class ReportArchiveEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashReportArchives(this RouteGroupBuilder members)
    {
        members.MapPost("/clash/reports/{runId:guid}/archive", StartAsync);
        var archives = members.MapGroup("/clash/report-archives");
        archives.MapGet("/current", (HttpContext http, ReportArchives jobs) =>
            jobs.Current(http.User.UserId()) is { } view ? Results.Ok(view) : Results.NoContent());
        archives.MapGet("/{id:guid}/download", (Guid id, HttpContext http, ReportArchives jobs) =>
        {
            if (jobs.Download(http.User.UserId(), id, out var gone) is { } file)
                return Results.File(file.Path, "application/zip", file.FileName, enableRangeProcessing: true);
            return gone ? Results.Problem("Архив устарел, выгрузите заново", statusCode: StatusCodes.Status410Gone) : Results.NotFound();
        });
        archives.MapDelete("/{id:guid}", (Guid id, HttpContext http, ReportArchives jobs) =>
            jobs.Cancel(http.User.UserId(), id) ? Results.NoContent() : Results.NotFound());
    }

    /// <param name="scope"><c>changes</c> (по умолчанию) или <c>open</c>.</param>
    /// <param name="sort">Сортировка строк внутри пары, как у списка отчёта.</param>
    private static async Task<IResult> StartAsync(Guid runId, string? scope, string? sort, string? baseUrl, HttpContext http, RunReports reports,
        ReportArchives jobs, CancellationToken ct)
    {
        if (ReportEndpoints.WebBase(baseUrl) is not { } web) return ReportEndpoints.BadBaseUrl();
        var filter = CollisionFilter.From(http.Request);
        if (filter.Pair is not null) return Results.BadRequest(new { error = "Пара выгружается одним файлом HTML, не архивом" });
        if (await reports.ContextAsync(runId, ct) is null) return Results.NotFound();
        return Results.Ok(jobs.Start(http.User.UserId(), runId, filter.Discipline, ReportEndpoints.Scope(scope), filter, sort, web));
    }
}
