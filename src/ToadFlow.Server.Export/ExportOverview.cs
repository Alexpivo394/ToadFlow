using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Export;

/// <summary>Плитка выгрузки: сколько моделей выгружено за сутки, ошибки, ближайший запуск; ряд — выгруженные по дням за две недели.</summary>
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

        var zone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        var firstDay = OverviewDays.Start(OverviewDays.Of(now, zone).First, zone);
        var from = firstDay < dayAgo ? firstDay : dayAgo;
        var finished = await jobs.Where(j => j.FinishedAt >= from && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning || j.Status == JobStatus.Failed))
            .Select(j => new { At = j.FinishedAt!.Value, j.Status }).ToListAsync(ct);
        var done = finished.Where(j => j.Status != JobStatus.Failed).ToList();
        var doneDay = done.Count(j => j.At >= dayAgo);
        var failed = finished.Count(j => j.Status == JobStatus.Failed && j.At >= dayAgo);
        var next = await db.Schedules.AsNoTracking().Where(s => s.IsEnabled && s.NextRunAt != null && kinds.Contains(s.ModuleKind))
            .MinAsync(s => s.NextRunAt, ct);
        return new OverviewTile(Module, doneDay.ToString(), "за сутки", null,
        [
            failed == 0 ? "без ошибок" : $"ошибок: {failed}",
            next is { } at ? $"следующий запуск {TimeZoneInfo.ConvertTime(at, zone):dd.MM HH:mm}" : "расписаний нет"
        ], link,
            OverviewDays.CountByDay(done.Select(j => j.At), now, zone),
            doneDay == 0 ? null : Russian.Counted(doneDay, "выгружена", "выгружено", "модель", "модели", "моделей"));
    }
}
