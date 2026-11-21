using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;

namespace ToadFlow.Server.Families.Scan;

public sealed record ScanStarted(Guid ScanId);

internal static class ScanEndpoints
{
    /// <summary><c>POST /api/families/scan</c> — «Обойти сейчас»; <c>GET /api/families/status</c> — состояние библиотеки.</summary>
    public static void MapFamiliesScan(this RouteGroupBuilder families)
    {
        families.MapPost("/scan", async (LibraryScanner scanner, CancellationToken ct) =>
        {
            var result = await scanner.StartAsync(ct);
            return result.Outcome switch
            {
                ScanStart.Started => Results.Ok(new ScanStarted(result.ScanId!.Value)),
                ScanStart.NotConfigured => Results.BadRequest(new { error = "Корень библиотеки не задан" }),
                ScanStart.Running => Results.Conflict(new { error = "Обход уже идёт — дождитесь его отчёта" }),
                ScanStart.NoAgent => Results.Conflict(new { error = "Нет агента в сети, который умеет обходить библиотеку: нужен агент новой версии" }),
                ScanStart.AgentBusy => Results.Conflict(new { error = "Агент ещё занят прошлым обходом" }),
                _ => Results.Problem($"Агент не начал обход: {result.Error}", statusCode: StatusCodes.Status502BadGateway)
            };
        }).RequireAuthorization(Policies.Operator);

        families.MapGet("/status", StatusAsync);
    }

    /// <summary><c>POST /api/agent/families/scans/{scanId}</c> — отчёт обхода от агента, тело сжато gzip.</summary>
    public static void MapFamiliesAgent(this RouteGroupBuilder agentApi) =>
        agentApi.MapPost("/families/scans/{scanId:guid}", async (Guid scanId, FamilyScanReportDto report, HttpContext http, ScanApplier applier, FamilyReadJobs reads,
            ILogger<ScanApplier> logger, CancellationToken ct) =>
        {
            var applied = await applier.ApplyAsync(scanId, http.User.AgentId(), report, ct);
            if (applied is null) logger.LogInformation("Отчёт обхода {Scan} не применён: обход неизвестен или уже завершён", scanId);
            else await reads.EnqueueAsync(applied.VersionsToRead, ct);
            return Results.Ok();
        });

    private static async Task<LibraryStatus> StatusAsync(FamiliesSettingsStore store, LibraryScanner scanner, ToadFlowDb db, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        var agent = await scanner.AgentAsync(settings, ct);
        var last = await db.Set<FamilyScan>().AsNoTracking().Where(s => s.Complete != null).OrderByDescending(s => s.FinishedAt).FirstOrDefaultAsync(ct);
        var live = db.Set<FamilyVersion>().Where(v => db.Set<FamilyCard>().Any(c => c.Id == v.CardId && c.State != CardState.Archived) && v.State != VersionState.Rejected);
        return new LibraryStatus(settings.Configured, agent?.MachineName, agent?.Online == true, last?.FinishedAt, last?.Complete, last?.Errors ?? [],
            await live.CountAsync(v => v.ReadState == ReadState.Pending && v.ReadError == null, ct), await live.CountAsync(v => v.ReadState == ReadState.Failed, ct));
    }
}
