using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Analytics;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;
using CoreOverview = ToadFlow.Server.Overview;

namespace ToadFlow.Server.Clash.Overview;

/// <summary>
///     Плитка коллизий: открытые сейчас и новые и закрытые за неделю — тот же расчёт, что у сводки коллизий за 7 дней. Ряд — открытые
///     на конец каждого дня за две недели; фраза — сколько закрыто сегодня.
/// </summary>
internal sealed class ClashOverviewSource : CoreOverview.IOverviewSource
{
    public string Module => "clash";

    public async Task<CoreOverview.OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct)
    {
        const string link = "/clash/summary";
        var db = services.GetRequiredService<ToadFlowDb>();
        if (!await db.Set<ClashPart>().AnyAsync(p => p.ApprovedVersion != null, ct))
            return new CoreOverview.OverviewTile(Module, "—", "данных пока нет", null, [], link);

        var general = services.GetRequiredService<ISettingsStore>();
        var settings = services.GetRequiredService<ClashSettingsStore>();
        var time = services.GetRequiredService<TimeProvider>();
        var today = CoreOverview.OverviewDays.DayOf(time.GetUtcNow(), (await general.GetAsync(ct)).TimeZone);
        Task<ClashAnalyticsView> AnalyticsAsync(DateOnly from) => AnalyticsEndpoints.AnalyticsAsync(null, null, null, from, today, null, db, general, settings, time, ct);

        var week = await AnalyticsAsync(today.AddDays(-6));
        var series = await AnalyticsAsync(today.AddDays(-(CoreOverview.OverviewDays.Count - 1)));
        var fixedToday = (await AnalyticsAsync(today)).Period.Fixed;
        return new CoreOverview.OverviewTile(Module, week.Current.Open.ToString(), "открыто", null,
            [$"новых за неделю: {week.Period.New}", $"закрыто за неделю: {week.Period.Fixed}"], link,
            series.Series.Select(p => (double)p.Open).ToList(),
            fixedToday == 0 ? null : CoreOverview.Russian.Counted(fixedToday, "закрыта", "закрыто", "коллизия", "коллизии", "коллизий"));
    }

    /// <summary>«Для вас»: новые коллизии в разделах человека (ещё не открытые им) и сколько ждут его исправления — как в «Моих коллизиях».</summary>
    public async Task<IReadOnlyList<CoreOverview.PersonalItem>> PersonalAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var (disciplines, state) = await Collisions.MyEndpoints.UserAsync(db, userId, ct);
        if (disciplines.Length == 0) return [];
        var parts = await Collisions.MyCollisions.PartsAsync(db, ct);
        var mark = Collisions.MyCollisions.Mark(state, services.GetRequiredService<TimeProvider>().GetUtcNow());
        var unseen = await Collisions.MyCollisions.Query(db, disciplines, parts, new Collisions.MyFilter(Scope: "all", Since: "unseen"), mark, userId).CountAsync(ct);
        var toFix = await Collisions.MyCollisions.Query(db, disciplines, parts, new Collisions.MyFilter(), mark, userId).CountAsync(ct);
        return
        [
            new CoreOverview.PersonalItem(Module, "Новые коллизии в ваших разделах", "/clash/my?since=unseen", CoreOverview.PersonalTone.Action, unseen),
            new CoreOverview.PersonalItem(Module, "Коллизии ждут исправления", "/clash/my", CoreOverview.PersonalTone.Info, toFix)
        ];
    }
}
