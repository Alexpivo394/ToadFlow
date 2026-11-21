using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Schedules;

namespace ToadFlow.Server.Notifications;

/// <summary>Запуск закончился с ошибками — тем, кто следит за расписанием, и тому, кто запустил вручную.</summary>
internal sealed class RunFailedNotifier(ToadFlowDb db, INotifications notifications, ModuleSet modules) : IDomainEventHandler
{
    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent is not RunFinished { Failed: > 0 } finished) return;
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == finished.RunId, ct);
        if (run is null) return;

        var people = run.ScheduleId is { } scheduleId
            ? await db.Set<ScheduleFollower>().Where(f => f.ScheduleId == scheduleId).Select(f => f.UserId).ToListAsync(ct)
            : [];
        if (run.TriggeredByUserId is { } trigger) people.Add(trigger);

        var module = modules.Enabled.FirstOrDefault(m => m.Name == run.ModuleName);
        var link = module is null ? $"/farm/queue/runs/{run.Id}" : $"{module.HistoryPath}/runs/{run.Id}";
        var total = finished.Succeeded + finished.Warnings + finished.Failed + finished.Cancelled;
        await notifications.NotifyAsync(people, NotificationKinds.RunFailed, $"Ошибки: {run.Name}",
            $"Моделей с ошибкой: {finished.Failed} из {total}", link, $"run:{run.Id}", null, ct);
    }
}
