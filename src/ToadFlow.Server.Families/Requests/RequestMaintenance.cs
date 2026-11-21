using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Notifications;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Families.Requests;

/// <summary>
///     Раз в час (и при старте): автоприёмка заявок, о которых автор молчит дольше <c>AutoAcceptDays</c>, напоминание за сутки до неё,
///     одно уведомление о просрочке «нужно к» и уборка «ничейных» загрузок старше суток.
/// </summary>
internal sealed class RequestMaintenance(IServiceScopeFactory scopes, TimeProvider time, ILogger<RequestMaintenance> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider, time.GetUtcNow(), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Обслуживание заявок на семейства не удалось");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>Один проход; <paramref name="now" /> — для тестов.</summary>
    public static async Task RunOnceAsync(IServiceProvider services, DateTimeOffset now, CancellationToken ct)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var notifications = services.GetRequiredService<INotifications>();
        var events = services.GetRequiredService<IDomainEvents>();
        var days = (await services.GetRequiredService<FamiliesSettingsStore>().GetAsync(ct)).AutoAcceptDays;
        var zone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        var acceptBefore = now.AddDays(-days);
        foreach (var request in await db.Set<FamilyRequest>().Where(r => r.State == RequestState.Review && r.ReviewSince <= acceptBefore).ToListAsync(ct))
        {
            RequestEndpoints.Accept(request, now);
            request.UpdatedAt = now;
            db.Set<FamilyRequestMessage>().Add(new FamilyRequestMessage
            {
                Id = Guid.CreateVersion7(),
                RequestId = request.Id,
                Text = $"принята автоматически: автор не ответил за {days} дн.",
                IsEvent = true,
                At = now
            });
            await db.SaveChangesAsync(ct);
            await notifications.NotifyAsync(await RequestPeopleAsync(db, request, ct), NotificationKinds.RequestClosed, Title(request),
                $"«{request.Title}» принята автоматически", Link(request), null, null, ct);
            events.Publish(new CatalogChanged(Actor.System, Requests.UiEntity, request.Id, $"Семейства: заявка № {request.Number} принята автоматически"));
        }

        var remindBefore = now.AddDays(-(days - 1));
        foreach (var request in await db.Set<FamilyRequest>().Where(r => r.State == RequestState.Review && !r.ReminderSent && r.ReviewSince <= remindBefore)
                     .ToListAsync(ct))
        {
            request.ReminderSent = true;
            await db.SaveChangesAsync(ct);
            await notifications.NotifyAsync([request.AuthorUserId], NotificationKinds.RequestRemind, Title(request),
                $"«{request.Title}» примется сама {request.ReviewSince!.Value.AddDays(days):dd.MM} — проверьте, подходит ли", Link(request), null, null, ct);
        }

        await RequestEndpoints.RemoveOrphansAsync(db, services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FamiliesStorageOptions>>().Value.Root, now, ct);

        await Search.SearchMisses.CleanAsync(db, now, ct);

        var open = FamilyRequest.OpenStates;
        foreach (var request in await db.Set<FamilyRequest>().Where(r => open.Contains(r.State) && !r.OverdueNotified && r.NeededBy < today).ToListAsync(ct))
        {
            request.OverdueNotified = true;
            await db.SaveChangesAsync(ct);
            IReadOnlyList<Guid> to = request.AssigneeUserId is { } assignee ? [assignee] : await Directions.CoordinatorsAsync(db, request.DirectionId, ct);
            await notifications.NotifyAsync(to, NotificationKinds.RequestOverdue, Title(request),
                $"«{request.Title}» нужна была к {request.NeededBy:dd.MM.yyyy}", Link(request), null, null, ct);
        }
    }

    internal static string Title(FamilyRequest request) => $"Заявка № {request.Number}";

    internal static string Link(FamilyRequest request) => $"/families/requests/{request.Id}";

    /// <summary>Участники заявки и исполнитель.</summary>
    internal static async Task<List<Guid>> RequestPeopleAsync(ToadFlowDb db, FamilyRequest request, CancellationToken ct)
    {
        var people = await db.Set<FamilyRequestMember>().Where(m => m.RequestId == request.Id).Select(m => m.UserId).ToListAsync(ct);
        if (request.AssigneeUserId is { } assignee) people.Add(assignee);
        return people;
    }
}
