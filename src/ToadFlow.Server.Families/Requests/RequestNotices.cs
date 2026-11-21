using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Notifications;

namespace ToadFlow.Server.Families.Requests;

/// <summary>Кому и что сообщить о заявке. Автору действия уведомление не приходит (<see cref="INotifications" />).</summary>
public sealed class RequestNotices(ToadFlowDb db, INotifications notifications)
{
    /// <summary>Новая заявка или переданная в направление — координаторам направления (без направления — всем координаторам).</summary>
    public async Task NewAsync(FamilyRequest request, Guid? actor, CancellationToken ct) =>
        await SendAsync(await Directions.CoordinatorsAsync(db, request.DirectionId, ct), NotificationKinds.RequestNew, request,
            request.BlocksIssue ? $"«{request.Title}» — блокирует выдачу" : $"«{request.Title}»", actor, ct);

    /// <summary>Автору и присоединившимся.</summary>
    public async Task MembersAsync(FamilyRequest request, string kind, string text, Guid? actor, CancellationToken ct) =>
        await SendAsync(await db.Set<FamilyRequestMember>().Where(m => m.RequestId == request.Id).Select(m => m.UserId).ToListAsync(ct), kind, request, text, actor, ct);

    /// <summary>Исполнителю, если он есть.</summary>
    public Task AssigneeAsync(FamilyRequest request, string kind, string text, Guid? actor, CancellationToken ct) =>
        request.AssigneeUserId is { } assignee ? SendAsync([assignee], kind, request, text, actor, ct) : Task.CompletedTask;

    /// <summary>Участникам и исполнителю.</summary>
    public async Task PeopleAsync(FamilyRequest request, string kind, string text, Guid? actor, CancellationToken ct) =>
        await SendAsync(await RequestMaintenance.RequestPeopleAsync(db, request, ct), kind, request, text, actor, ct);

    private Task SendAsync(IEnumerable<Guid> users, string kind, FamilyRequest request, string text, Guid? actor, CancellationToken ct) =>
        notifications.NotifyAsync(users, kind, RequestMaintenance.Title(request), text, RequestMaintenance.Link(request), $"request:{request.Id}:{kind}", actor, ct);
}
