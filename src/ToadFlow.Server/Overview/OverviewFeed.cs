using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Overview;

/// <summary>Строка ленты «Что происходит».</summary>
/// <param name="Module">Модуль — для значка и цвета; null — ядро.</param>
/// <param name="Tone"><c>ok</c>, <c>warn</c>, <c>fail</c> или <c>info</c>.</param>
public sealed record FeedItem(DateTimeOffset At, string? Module, string Text, string Tone);

/// <summary>
///     Лента «Обзора»: завершённые задания включённых модулей и рабочие события журнала. Видна всем, кто видит «Обзор», поэтому
///     события берутся по списку разрешённых: пользователи, настройки, источники, пресеты и лимиты агентов в неё не попадают.
/// </summary>
public static class OverviewFeed
{
    public const int Take = 15;

    /// <summary>
    ///     Модуль «Семейства» пишет свою работу как <c>CatalogChanged</c> с этим началом текста; остальные <c>CatalogChanged</c> —
    ///     настройка (источники, пресеты, справочники).
    /// </summary>
    private const string FamiliesPrefix = "Семейства:";

    private static readonly Dictionary<string, (string? Module, string Tone)> Allowed = new()
    {
        ["AgentWentOnline"] = (null, "ok"),
        ["AgentWentOffline"] = (null, "fail"),
        ["InstanceKilled"] = (null, "fail"),
        ["RunFinished"] = (null, "info"),
        ["ClashPartApproved"] = ("clash", "info"),
        ["ClashApprovedRunSkipped"] = ("clash", "warn")
    };

    public static void MapOverviewFeed(this RouteGroupBuilder api) =>
        api.MapGet("/overview/feed", (ToadFlowDb db, ModuleSet modules, CancellationToken ct) => ReadAsync(db, modules, ct));

    public static async Task<IReadOnlyList<FeedItem>> ReadAsync(ToadFlowDb db, ModuleSet modules, CancellationToken ct)
    {
        var kinds = modules.EnabledKinds;
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => kinds.Contains(j.ModuleKind) && j.FinishedAt != null &&
                        (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning || j.Status == JobStatus.Failed))
            .OrderByDescending(j => j.FinishedAt).Take(Take)
            .Select(j => new { j.ModuleKind, j.ModelName, j.Status, At = j.FinishedAt!.Value, j.AgentId }).ToListAsync(ct);
        var agentIds = jobs.Where(j => j.AgentId != null).Select(j => j.AgentId!.Value).Distinct().ToList();
        var machines = await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.MachineName, ct);

        var allowed = Allowed.Keys.ToList();
        var events = await db.SystemEvents.AsNoTracking()
            .Where(e => allowed.Contains(e.Kind) || (e.Kind == "CatalogChanged" && e.Text.StartsWith(FamiliesPrefix)))
            .OrderByDescending(e => e.Id).Take(Take).ToListAsync(ct);

        var items = jobs.Select(j => new FeedItem(j.At, modules.NameOfKind(j.ModuleKind),
            $"{j.ModelName} — {Outcome(j.Status)}{(j.AgentId is { } id && machines.TryGetValue(id, out var machine) ? $" · {machine}" : "")}",
            j.Status switch { JobStatus.Failed => "fail", JobStatus.Warning => "warn", _ => "ok" }));
        items = items.Concat(events.Select(e => Allowed.TryGetValue(e.Kind, out var known)
            ? new FeedItem(e.At, known.Module, e.Text, known.Tone)
            : new FeedItem(e.At, modules.IsEnabled("families") ? "families" : null, e.Text, "info")));
        return items.OrderByDescending(i => i.At).Take(Take).ToList();
    }

    private static string Outcome(JobStatus status) => status switch
    {
        JobStatus.Failed => "ошибка",
        JobStatus.Warning => "с предупреждением",
        _ => "готово"
    };
}
