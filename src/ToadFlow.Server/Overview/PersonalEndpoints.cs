using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Schedules;

namespace ToadFlow.Server.Overview;

/// <summary>
///     «Для вас» — любой роли, в том числе проектировщику: строки ядра (расписания, за которыми человек следит) и включённых модулей. Сбой
///     модуля не ломает блок — его строк просто нет.
/// </summary>
public static class PersonalEndpoints
{
    public static void MapPersonal(this RouteGroupBuilder members) => members.MapGet("/me/today", TodayAsync);

    private static async Task<PersonalView> TodayAsync(HttpContext http, ToadFlowDb db, ModuleSet modules, IEnumerable<IOverviewSource> sources, TimeProvider time,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var me = http.User.UserId();
        var items = new List<PersonalItem>(await FollowedFailuresAsync(db, modules, me, time.GetUtcNow(), ct));
        foreach (var source in sources)
        {
            try
            {
                items.AddRange(await source.PersonalAsync(http.RequestServices, me, ct));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggers.CreateLogger(typeof(PersonalEndpoints)).LogError(exception, "«Для вас»: модуль {Module} не ответил", source.Module);
            }
        }

        return new PersonalView(items.Where(i => i.Count > 0).OrderBy(i => i.Tone).ToList());
    }

    /// <summary>Расписания, за которыми человек следит, с ошибками за сутки — строкой на модуль.</summary>
    private static async Task<List<PersonalItem>> FollowedFailuresAsync(ToadFlowDb db, ModuleSet modules, Guid me, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-1);
        var followed = db.Set<ScheduleFollower>().Where(f => f.UserId == me).Select(f => f.ScheduleId);
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.ScheduleId != null && followed.Contains(r.ScheduleId.Value) && r.FinishedAt >= since
                        && db.Jobs.Any(j => j.RunId == r.Id && j.Status == JobStatus.Failed))
            .Select(r => new { r.ScheduleId, r.ModuleName }).ToListAsync(ct);
        return runs.GroupBy(r => r.ModuleName).Select(g =>
        {
            var module = modules.Enabled.FirstOrDefault(m => m.Name == g.Key);
            return new PersonalItem(null, $"{module?.Title ?? "Ферма"}: расписания с ошибками за сутки", module?.HistoryPath ?? "/farm/queue", PersonalTone.Action,
                g.Select(r => r.ScheduleId).Distinct().Count());
        }).ToList();
    }
}
