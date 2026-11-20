using ToadFlow.Server.Data;
using ToadFlow.Server.Loi.Analytics;
using ToadFlow.Server.Overview;

namespace ToadFlow.Server.Loi;

/// <summary>
///     Плитка LOI: заполненность по последней проверке каждой модели — тот же расчёт, что у страницы аналитики, — изменение к
///     состоянию неделю назад и число объектов.
/// </summary>
internal sealed class LoiOverview : IOverviewSource
{
    public string Module => "loi";

    public async Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct)
    {
        const string link = "/loi/analytics";
        var db = services.GetRequiredService<ToadFlowDb>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var current = await AnalyticsQueryEndpoints.SummaryAsync(db, null, ct);
        if (current.FillRate is not { } rate) return new OverviewTile(Module, "—", "данных пока нет", null, [], link);

        var weekAgo = await AnalyticsQueryEndpoints.SummaryAsync(db, now.AddDays(-7), ct);
        string? trend = null;
        if (weekAgo.FillRate is { } before)
        {
            var change = (int)Math.Round((rate - before) * 100, MidpointRounding.AwayFromZero);
            trend = change > 0 ? $"▲ {change}" : change < 0 ? $"▼ {-change}" : "0";
        }

        return new OverviewTile(Module, $"{Math.Round(rate * 100, MidpointRounding.AwayFromZero):0} %", "заполнено", trend, [$"объектов: {current.Objects}"], link);
    }
}
