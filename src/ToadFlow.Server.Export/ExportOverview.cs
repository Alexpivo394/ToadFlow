using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Export;

/// <summary>Плитка выгрузки: сколько моделей выгружено за сутки, ошибки, ближайший запуск.</summary>
internal sealed class ExportOverview : IOverviewSource
{
    public string Module => "export";

    public async Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct)
    {
        const string link = "/export/schedules";
        var db = services.GetRequiredService<ToadFlowDb>();
        var kinds = services.GetRequiredService<ModuleSet>().KindsOf(Module);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var dayAgo = now.AddDays(-1);
        var jobs = db.Jobs.AsNoTracking().Where(j => kinds.Contains(j.ModuleKind));
        if (!await jobs.AnyAsync(ct)) return new OverviewTile(Module, "—", "данных пока нет", null, [], link);

        var done = await jobs.CountAsync(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning) && j.FinishedAt >= dayAgo, ct);
        var failed = await jobs.CountAsync(j => j.Status == JobStatus.Failed && j.FinishedAt >= dayAgo, ct);
        var next = await db.Schedules.AsNoTracking().Where(s => s.IsEnabled && s.NextRunAt != null && kinds.Contains(s.ModuleKind))
            .MinAsync(s => s.NextRunAt, ct);
        var zone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        return new OverviewTile(Module, done.ToString(), "за сутки", null,
        [
            failed == 0 ? "без ошибок" : $"ошибок: {failed}",
            next is { } at ? $"следующий запуск {TimeZoneInfo.ConvertTime(at, zone):dd.MM HH:mm}" : "расписаний нет"
        ], link);
    }
}
