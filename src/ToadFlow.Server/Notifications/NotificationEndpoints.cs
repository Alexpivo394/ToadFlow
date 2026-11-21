using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Notifications;

public sealed record NotificationView(long Id, string Kind, string Title, string Text, string? Link, int Count, DateTimeOffset At, bool Read);

public sealed record NotificationCount(int Unread);

public sealed record NotificationLink(string Link);

public sealed record NotificationKindView(string Kind, string Group, string Title, bool Muted);

public sealed record NotificationSettingsView(IReadOnlyList<NotificationKindView> Kinds);

public sealed record NotificationSettingsInput(string[] Muted);

/// <summary>Свои уведомления — любой роли, в том числе проектировщику.</summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder members)
    {
        var group = members.MapGroup("/notifications");
        group.MapGet("", ListAsync);
        group.MapGet("/count", async (ToadFlowDb db, ClaimsPrincipal user, CancellationToken ct) =>
            new NotificationCount(await Mine(db, user).CountAsync(n => n.ReadAt == null, ct)));
        group.MapPost("/{id:long}/read", ReadAsync);
        group.MapPost("/read-all", async (ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct) =>
        {
            await Mine(db, user).Where(n => n.ReadAt == null).ExecuteUpdateAsync(n => n.SetProperty(x => x.ReadAt, time.GetUtcNow()), ct);
            return Results.Ok();
        });
        group.MapPost("/read-link", async (NotificationLink body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct) =>
        {
            await Mine(db, user).Where(n => n.ReadAt == null && n.Link == body.Link).ExecuteUpdateAsync(n => n.SetProperty(x => x.ReadAt, time.GetUtcNow()), ct);
            return Results.Ok();
        });
        group.MapGet("/settings", SettingsAsync);
        group.MapPut("/settings", SaveSettingsAsync);
    }

    private static IQueryable<Notification> Mine(ToadFlowDb db, ClaimsPrincipal user)
    {
        var me = user.UserId();
        return db.Set<Notification>().Where(n => n.UserId == me);
    }

    /// <summary>Новые сверху; <c>before</c> — листать дальше от id, <c>take</c> — до 100.</summary>
    private static async Task<List<NotificationView>> ListAsync(ToadFlowDb db, ClaimsPrincipal user, long? before, int? take, CancellationToken ct)
    {
        var query = Mine(db, user).AsNoTracking();
        if (before is { } id) query = query.Where(n => n.Id < id);
        return await query.OrderByDescending(n => n.Id).Take(Math.Clamp(take ?? 30, 1, 100))
            .Select(n => new NotificationView(n.Id, n.Kind, n.Title, n.Text, n.Link, n.Count, n.At, n.ReadAt != null)).ToListAsync(ct);
    }

    private static async Task<IResult> ReadAsync(long id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct)
    {
        var changed = await Mine(db, user).Where(n => n.Id == id).ExecuteUpdateAsync(n => n.SetProperty(x => x.ReadAt, x => x.ReadAt ?? time.GetUtcNow()), ct);
        return changed == 0 ? Results.NotFound() : Results.Ok();
    }

    private static async Task<NotificationSettingsView> SettingsAsync(ToadFlowDb db, ClaimsPrincipal user, CancellationToken ct)
    {
        var me = user.UserId();
        var muted = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.MutedNotifications).SingleAsync(ct);
        return new NotificationSettingsView(NotificationKinds.All.Select(k => new NotificationKindView(k.Kind, k.Group, k.Title, muted.Contains(k.Kind))).ToList());
    }

    private static async Task<IResult> SaveSettingsAsync(NotificationSettingsInput body, ToadFlowDb db, ClaimsPrincipal user, CancellationToken ct)
    {
        var muted = (body.Muted ?? []).Distinct().ToArray();
        if (muted.FirstOrDefault(k => !NotificationKinds.IsKnown(k)) is { } unknown) return Results.BadRequest(new { error = $"Неизвестный вид уведомлений «{unknown}»" });
        var me = user.UserId();
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(u => u.SetProperty(x => x.MutedNotifications, muted), ct);
        return Results.Ok(await SettingsAsync(db, user, ct));
    }

    public static void AddNotifications(this IServiceCollection services) => services.AddScoped<INotifications, NotificationService>();
}
