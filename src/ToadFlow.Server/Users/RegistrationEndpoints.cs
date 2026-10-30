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

    public static void MapRegistration(this RouteGroupBuilder members)
    {
        members.MapGet("/auth/disciplines", (NamingStore naming, IEnumerable<IDisciplineSource> sources, CancellationToken ct) =>
            Disciplines.ListAsync(naming, sources, ct)).AllowAnonymous();
        members.MapPost("/auth/register", RegisterAsync).AllowAnonymous();
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

    public static string DisciplineText(string[] disciplines) => disciplines.Length == 0 ? "не указаны" : string.Join(", ", disciplines);
}
