using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Notifications;

/// <summary>Отправка уведомлений людям.</summary>
public interface INotifications
{
    /// <summary>
    ///     Уведомляет пользователей, кроме <paramref name="actor" /> (своё действие человек уже видел), отключённых, ожидающих подтверждения
    ///     и тех, кто отключил этот вид. Непрочитанное с тем же <paramref name="groupKey" /> за последние 10 минут заменяется свежим со
    ///     счётчиком: три сообщения подряд — одно уведомление «(3)».
    /// </summary>
    Task NotifyAsync(IEnumerable<Guid> users, string kind, string title, string text, string? link, string? groupKey, Guid? actor, CancellationToken ct);
}

/// <summary>Личный хаб: только свои уведомления — его слушает и проектировщик, которому общий хаб событий не положен.</summary>
[Authorize(Policies.Member)]
public sealed class MeHub : Hub;

internal sealed class NotificationService(ToadFlowDb db, IHubContext<MeHub> hub, TimeProvider time) : INotifications
{
    private static readonly TimeSpan GlueWindow = TimeSpan.FromMinutes(10);
    private const int MaxText = 1000;

    public async Task NotifyAsync(IEnumerable<Guid> users, string kind, string title, string text, string? link, string? groupKey, Guid? actor, CancellationToken ct)
    {
        var ids = users.Where(u => u != actor).Distinct().ToList();
        if (ids.Count == 0) return;

        var recipients = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && !u.IsDisabled && !u.IsPending && !u.MutedNotifications.Contains(kind))
            .Select(u => u.Id).ToListAsync(ct);
        if (recipients.Count == 0) return;

        var now = time.GetUtcNow();
        var glued = groupKey is null
            ? []
            : await db.Set<Notification>().Where(n => recipients.Contains(n.UserId) && n.GroupKey == groupKey && n.ReadAt == null && n.At > now - GlueWindow)
                .ToListAsync(ct);
        foreach (var user in recipients)
        {
            var previous = glued.Where(n => n.UserId == user).ToList();
            db.Set<Notification>().RemoveRange(previous);
            db.Set<Notification>().Add(new Notification
            {
                UserId = user,
                Kind = kind,
                Title = title,
                Text = text.Length > MaxText ? text[..MaxText] + "…" : text,
                Link = link,
                GroupKey = groupKey,
                Count = 1 + previous.Sum(n => n.Count),
                At = now
            });
        }

        await db.SaveChangesAsync(ct);
        await hub.Clients.Users(recipients.Select(r => r.ToString())).SendAsync("notification", ct);
    }
}

/// <summary>Уборка: прочитанные уведомления старше 90 дней.</summary>
public static class NotificationCleanup
{
    private const int KeepDays = 90;

    public static Task<int> RunOnceAsync(ToadFlowDb db, DateTimeOffset now, CancellationToken ct) =>
        db.Set<Notification>().Where(n => n.ReadAt != null && n.At < now.AddDays(-KeepDays)).ExecuteDeleteAsync(ct);
}
