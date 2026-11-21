using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Families.Scan;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Families;

/// <summary>
///     Плитка «Семейства»: сколько семейств в каталоге, что ждёт проверки и решения. Строки внимания: библиотека недоступна,
///     нет агента для обхода, случаи «Нужно решение», файлы, которые ферма не прочитала.
/// </summary>
internal sealed class FamiliesOverview : IOverviewSource
{
    /// <summary>Полного обхода нет дольше стольких интервалов — библиотека считается недоступной.</summary>
    private const int MissedIntervals = 3;

    public string Module => "families";

    public async Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = await services.GetRequiredService<FamiliesSettingsStore>().GetAsync(ct);
        if (!settings.Configured) return new OverviewTile(Module, "—", "папка не задана", null, [], "/families/settings");

        var db = services.GetRequiredService<ToadFlowDb>();
        var cards = db.Set<FamilyCard>().AsNoTracking();
        var approved = await cards.CountAsync(c => c.State == CardState.Approved, ct);
        var inReview = await db.Set<FamilyVersion>().AsNoTracking()
            .CountAsync(v => v.State == VersionState.InReview && cards.Any(c => c.Id == v.CardId && c.State != CardState.Archived), ct);
        var issues = await db.Set<FamilyIssue>().CountAsync(i => i.ResolvedAt == null, ct);
        var waiting = await db.Set<FamilyRequest>().CountAsync(r => r.State == RequestState.New || r.State == RequestState.InWork, ct);
        return new OverviewTile(Module, approved.ToString(), "в каталоге", null,
            [$"заявок ждёт: {waiting}", $"на проверке: {inReview}", $"нужно решение: {issues}"], "/families/catalog");
    }

    public async Task<IReadOnlyList<AttentionItem>> AttentionAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = await services.GetRequiredService<FamiliesSettingsStore>().GetAsync(ct);
        if (!settings.Configured) return [];

        var db = services.GetRequiredService<ToadFlowDb>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var items = new List<AttentionItem>();

        var agent = await services.GetRequiredService<LibraryScanner>().AgentAsync(settings, ct);
        if (agent is not { Online: true })
            items.Add(new AttentionItem(AttentionLevel.Problem, "Семейства: нет агента для обхода библиотеки", "/families/settings", Module));

        // Отсчёт — от последнего полного обхода, а если его не было — от первого обхода: только что заданная библиотека строку не даёт.
        var scans = db.Set<FamilyScan>().AsNoTracking();
        var lastComplete = await scans.Where(s => s.Complete == true).MaxAsync(s => s.FinishedAt, ct);
        var since = lastComplete ?? await scans.MinAsync(s => (DateTimeOffset?)s.StartedAt, ct);
        if (since is { } at && now - at > TimeSpan.FromMinutes(settings.ScanIntervalMinutes * MissedIntervals))
        {
            var when = lastComplete is { } complete ? $"последний полный обход {await LocalAsync(services, complete, ct)}" : "полного обхода ещё не было";
            items.Add(new AttentionItem(AttentionLevel.Problem, $"Семейства: библиотека недоступна — {when}", "/families/settings", Module));
        }

        var fresh = await db.Set<FamilyRequest>().CountAsync(r => r.State == RequestState.New, ct);
        if (fresh > 0)
            items.Add(new AttentionItem(AttentionLevel.Todo, $"Семейства: {fresh} {Russian.Plural(fresh, "новая заявка ждёт", "новые заявки ждут", "новых заявок ждут")} исполнителя",
                "/families/requests?scope=all&state=New", Module, UserRole.Operator));

        var issues = await db.Set<FamilyIssue>().CountAsync(i => i.ResolvedAt == null, ct);
        if (issues > 0)
            items.Add(new AttentionItem(AttentionLevel.Todo, $"Семейства: {issues} {Russian.Plural(issues, "случай ждёт", "случая ждут", "случаев ждут")} решения",
                "/families/issues", Module, UserRole.Operator));

        var cards = db.Set<FamilyCard>().AsNoTracking();
        var failed = await db.Set<FamilyVersion>().AsNoTracking()
            .CountAsync(v => v.ReadState == ReadState.Failed && v.State != VersionState.Rejected && cards.Any(c => c.Id == v.CardId && c.State != CardState.Archived), ct);
        if (failed > 0)
            items.Add(new AttentionItem(AttentionLevel.Problem,
                $"Семейства: {failed} {Russian.Plural(failed, "файл", "файла", "файлов")} не удалось прочитать", "/families/issues", Module, UserRole.Operator));

        return items;
    }

    private static async Task<string> LocalAsync(IServiceProvider services, DateTimeOffset at, CancellationToken ct)
    {
        var zone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        return $"{TimeZoneInfo.ConvertTime(at, zone):dd.MM HH:mm}";
    }
}
