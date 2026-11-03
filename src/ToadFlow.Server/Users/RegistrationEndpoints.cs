using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Users;

/// <param name="Disciplines">Разделы, которые человек ведёт; координатор при подтверждении может их поправить.</param>
public sealed record RegisterRequest(
    string Login,
    string DisplayName,
    string Password,
    string[]? Disciplines,
    string? Position = null,
    string? Email = null,
    string? Phone = null);

/// <summary>
///     Самостоятельная регистрация: учётная запись проектировщика с заявкой, которую подтверждает координатор
///     (<see cref="UserEndpoints" />). До подтверждения войти нельзя.
/// </summary>
public static class RegistrationEndpoints
{
    /// <summary>Защита от мусора без капчи: сверх этого числа ждущих заявок регистрация отвечает 429.</summary>
    public const int MaxPending = 50;

    /// <summary>Сколько живёт заявка: не подтверждённая за это время удаляется, логин освобождается.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(3);

    public static void MapRegistration(this RouteGroupBuilder members)
    {
        members.MapGet("/auth/disciplines", (NamingStore naming, IEnumerable<IDisciplineSource> sources, CancellationToken ct) =>
            Disciplines.ListAsync(naming, sources, ct)).AllowAnonymous();
        members.MapPost("/auth/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(Auth.AuthRateLimits.Register);
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest request, ToadFlowDb db, NamingStore naming, IEnumerable<IDisciplineSource> sources,
        TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        if (UserEndpoints.Validate(request.Login, request.DisplayName, request.Password) is { } error) return Results.BadRequest(new { error });
        if (ProfileEndpoints.Contacts(request.Position, request.Email, request.Phone, out var contacts) is { } contactError)
            return Results.BadRequest(new { error = contactError });
        var disciplines = Disciplines.Canonical(await Disciplines.ListAsync(naming, sources, ct), request.Disciplines, out var unknown);
        if (unknown.Count > 0) return Results.BadRequest(new { error = $"Нет таких разделов: {string.Join(", ", unknown)}" });

        var login = User.NormalizeLogin(request.Login);
        await DeleteExpiredAsync(db, time, events, ct);
        if (await db.Users.AnyAsync(u => u.Login == login, ct)) return Results.Conflict(new { error = $"Логин «{login}» уже занят" });
        if (await db.Users.CountAsync(u => u.IsPending, ct) >= MaxPending)
            return Results.Json(new { error = "Слишком много заявок ждут подтверждения — попробуйте позже" }, statusCode: StatusCodes.Status429TooManyRequests);

        var user = User.Create(login, request.DisplayName, UserRole.Designer, "", time.GetUtcNow());
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        user.IsPending = true;
        (user.Position, user.Email, user.Phone) = contacts;
        user.Disciplines = disciplines;
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        events.Publish(new UserChanged(new Actor(ActorKind.User, user.DisplayName), user.Id,
            $"Заявка на регистрацию: {user.DisplayName} ({user.Login}), разделы: {DisciplineText(user.Disciplines)}"));
        return Results.Accepted();
    }

    /// <summary>
    ///     Удаляет заявки старше <see cref="PendingLifetime" />. Зовётся там, где заявку видно (регистрация, список заявок,
    ///     подтверждение), и ежедневной уборкой — поэтому просроченная заявка нигде не показывается. Вход уборку не зовёт: просроченную
    ///     заявку он отклоняет сам.
    /// </summary>
    /// <remarks>
    ///     Каждая заявка удаляется отдельной командой с тем же условием: два запроса в момент истечения не мешают друг другу — удалит
    ///     и сообщит в журнал тот, у кого команда задела строку.
    /// </remarks>
    public static async Task DeleteExpiredAsync(ToadFlowDb db, TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - PendingLifetime;
        var expired = await db.Users.AsNoTracking().Where(u => u.IsPending && u.CreatedAt < cutoff)
            .Select(u => new { u.Id, u.DisplayName, u.Login }).ToListAsync(ct);
        foreach (var user in expired)
        {
            var deleted = await db.Users.Where(u => u.Id == user.Id && u.IsPending && u.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
            if (deleted == 0) continue;
            events.Publish(new UserChanged(Actor.System, user.Id,
                $"Заявка удалена: не подтверждена за {PendingLifetime.TotalDays:0} суток — {user.DisplayName} ({user.Login})"));
        }
    }

    /// <summary>Заявка просрочена: её нет, даже если уборка ещё не прошла.</summary>
    public static bool Expired(User user, DateTimeOffset now) => user.IsPending && user.CreatedAt < now - PendingLifetime;

    public static string DisciplineText(string[] disciplines) => disciplines.Length == 0 ? "не указаны" : string.Join(", ", disciplines);
}
