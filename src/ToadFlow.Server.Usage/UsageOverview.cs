using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Settings;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage;

/// <summary>
///     Плитка «Аналитики» на общем «Обзоре»: сколько людей на связи за неделю, сколько они ждали, сколько было аварий; ряд — люди по
///     дням. В «Требует внимания» и «Для вас» модуль ничего не пишет. Проектировщику «Обзор» закрыт целиком.
/// </summary>
internal sealed class UsageOverview : IOverviewSource
{
    private const int WeekDays = 7;

    public string Module => "usage";

    public async Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var zone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        var weekAgo = now.AddDays(-WeekDays);

        var people = await db.Set<UsageSession>().AsNoTracking().Where(s => s.SeenAt >= weekAgo).Select(s => s.UserId).Distinct().CountAsync(ct);
        var waitMs = await Query.SummaryEndpoints.OwnWaits(db.Set<UsageOperation>().AsNoTracking()).Where(o => o.StartedAt >= weekAgo && o.Outcome == "Succeeded")
            .SumAsync(o => (long?)o.DurationMs, ct) ?? 0;
        var abnormal = await db.Set<UsageSession>().AsNoTracking().CountAsync(s => s.StartedAt >= weekAgo && s.End == "Abnormal", ct);

        var (first, _) = OverviewDays.Of(now, zone);
        var daily = await db.Set<UsageSessionDay>().AsNoTracking().Where(d => d.Day >= first)
            .GroupBy(d => d.Day).Select(g => new { Day = g.Key, People = g.Select(d => d.UserId).Distinct().Count() }).ToListAsync(ct);
        var series = Enumerable.Range(0, OverviewDays.Count)
            .Select(i => (double)(daily.FirstOrDefault(d => d.Day == first.AddDays(i))?.People ?? 0)).ToList();

        var hours = Math.Round(waitMs / 3_600_000.0, waitMs < 36_000_000 ? 1 : 0);
        return new OverviewTile(Module, people.ToString(), "на связи за неделю", null,
            [$"ожидание: {hours.ToString(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} ч за 7 дней", $"аварийных сессий: {abnormal}"], "/usage", series);
    }
}
