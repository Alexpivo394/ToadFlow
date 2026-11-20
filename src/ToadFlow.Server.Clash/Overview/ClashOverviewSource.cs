using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Analytics;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;
using CoreOverview = ToadFlow.Server.Overview;

namespace ToadFlow.Server.Clash.Overview;

/// <summary>Плитка коллизий: открытые сейчас и новые и закрытые за неделю — тот же расчёт, что у сводки коллизий за 7 дней.</summary>
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
        var time = services.GetRequiredService<TimeProvider>();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), (await general.GetAsync(ct)).TimeZone).DateTime);
        var week = await AnalyticsEndpoints.AnalyticsAsync(null, null, null, today.AddDays(-6), today, null, db, general,
            services.GetRequiredService<ClashSettingsStore>(), time, ct);
        return new CoreOverview.OverviewTile(Module, week.Current.Open.ToString(), "открыто", null,
            [$"новых за неделю: {week.Period.New}", $"закрыто за неделю: {week.Period.Fixed}"], link);
    }
}
