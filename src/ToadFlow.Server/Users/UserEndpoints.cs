using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Users;

public sealed record CreateUserRequest(string Login, string DisplayName, UserRole Role, string Password);

public sealed record UpdateUserRequest(string DisplayName, UserRole Role, bool IsDisabled);

public sealed record PasswordRequest(string Password);

/// <param name="Disciplines">Разделы проектировщика — как в заявке или поправленные координатором.</param>
public sealed record ApproveUserRequest(string[]? Disciplines);

/// <summary>
///     Пользователи. Список, заявки на регистрацию и разделы — координатор; создание, роль, отключение, пароль и удаление —
///     администратор.
/// </summary>
public static class UserEndpoints
{
    private const int MinPasswordLength = 6;
    private const string LastAdmin = "Нельзя убрать последнего активного администратора";

    public static void MapUsers(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(Policies.Operator);

        users.MapGet("/", async (ToadFlowDb db) =>
            (await db.Users.OrderBy(u => u.DisplayName).ToListAsync()).Select(UserView.From));
        users.MapGet("/pending", async (ToadFlowDb db, CancellationToken ct) =>
            (await db.Users.Where(u => u.IsPending).OrderBy(u => u.CreatedAt).ToListAsync(ct)).Select(UserView.From));
        users.MapPost("/{id:guid}/approve", ApproveAsync);
        users.MapPost("/{id:guid}/reject", RejectAsync);
        users.MapPut("/{id:guid}/disciplines", SetDisciplinesAsync);

        var admin = users.MapGroup("").RequireAuthorization(Policies.Admin);
        admin.MapPost("/", CreateAsync);
        admin.MapPut("/{id:guid}", UpdateAsync);
        admin.MapPost("/{id:guid}/password", SetPasswordAsync);
        admin.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> ApproveAsync(Guid id, ApproveUserRequest request, ToadFlowDb db, NamingStore naming,
        IEnumerable<IDisciplineSource> sources, IMemoryCache cache, HttpContext http, IDomainEvents events, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound();
        if (!user.IsPending) return Results.BadRequest(new { error = "Заявки нет: пользователь уже подтверждён" });
        var disciplines = Disciplines.Canonical(await Disciplines.ListAsync(naming, sources, ct), request.Disciplines, out var unknown);
        if (unknown.Count > 0) return Results.BadRequest(new { error = $"Нет таких разделов: {string.Join(", ", unknown)}" });

        user.IsPending = false;
        user.Disciplines = disciplines;
        await db.SaveChangesAsync(ct);
        cache.ForgetUser(id);
        events.Publish(new UserChanged(http.User.Actor(), id,
            $"Подтверждена заявка {user.DisplayName} ({user.Login}), разделы: {RegistrationEndpoints.DisciplineText(disciplines)}"));
        return Results.Ok(UserView.From(user));
    }

    /// <summary>Отклонённая заявка удаляется: логин освобождается, человек может подать заявку заново.</summary>
    private static async Task<IResult> RejectAsync(Guid id, ToadFlowDb db, HttpContext http, IDomainEvents events, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound();
        if (!user.IsPending) return Results.BadRequest(new { error = "Заявки нет: пользователь уже подтверждён" });

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        events.Publish(new UserChanged(http.User.Actor(), id, $"Отклонена заявка {user.DisplayName} ({user.Login})"));
        return Results.NoContent();
    }

    private static async Task<IResult> SetDisciplinesAsync(Guid id, string[]? request, ToadFlowDb db, NamingStore naming,
        IEnumerable<IDisciplineSource> sources, HttpContext http, IDomainEvents events, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return Results.NotFound();
        var disciplines = Disciplines.Canonical(await Disciplines.ListAsync(naming, sources, ct), request, out var unknown);
        if (unknown.Count > 0) return Results.BadRequest(new { error = $"Нет таких разделов: {string.Join(", ", unknown)}" });

        user.Disciplines = disciplines;
        await db.SaveChangesAsync(ct);
        events.Publish(new UserChanged(http.User.Actor(), id, $"Разделы {user.DisplayName}: {RegistrationEndpoints.DisciplineText(disciplines)}"));
        return Results.Ok(UserView.From(user));
    }

    private static async Task<IResult> CreateAsync(CreateUserRequest request, ToadFlowDb db, TimeProvider time, HttpContext http, IDomainEvents events)
    {
        if (Validate(request.Login, request.DisplayName, request.Password) is { } error) return Results.BadRequest(new { error });

        var login = User.NormalizeLogin(request.Login);
        if (await db.Users.AnyAsync(u => u.Login == login)) return Results.Conflict(new { error = $"Логин «{login}» уже занят" });

        var user = User.Create(login, request.DisplayName, request.Role, "", time.GetUtcNow());
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        events.Publish(new UserChanged(http.User.Actor(), user.Id, $"Добавлен пользователь {user.DisplayName} ({user.Login}), роль {user.Role}"));
        return Results.Created($"/api/users/{user.Id}", UserView.From(user));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateUserRequest request, ToadFlowDb db, IMemoryCache cache, HttpContext http, IDomainEvents events)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return Results.BadRequest(new { error = "Укажите имя" });
        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound();

        var losesAdmin = user is { Role: UserRole.Admin, IsDisabled: false } && (request.Role != UserRole.Admin || request.IsDisabled);
        if (losesAdmin && await IsLastActiveAdminAsync(db, id)) return Results.Conflict(new { error = LastAdmin });

        user.DisplayName = request.DisplayName.Trim();
        user.Role = request.Role;
        user.IsDisabled = request.IsDisabled;
        await db.SaveChangesAsync();
        cache.ForgetUser(id);
        events.Publish(new UserChanged(http.User.Actor(), id,
            $"Изменён пользователь {user.DisplayName}: роль {user.Role}{(user.IsDisabled ? ", отключён" : "")}"));
        return Results.Ok(UserView.From(user));
    }

    private static async Task<IResult> SetPasswordAsync(Guid id, PasswordRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events)
    {
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinPasswordLength)
            return Results.BadRequest(new { error = $"Пароль — не короче {MinPasswordLength} символов" });
        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound();

        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        await db.SaveChangesAsync();
        events.Publish(new UserChanged(http.User.Actor(), id, $"Сброшен пароль пользователя {user.DisplayName}"));
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, IMemoryCache cache, HttpContext http, IDomainEvents events)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return Results.NotFound();
        if (user is { Role: UserRole.Admin, IsDisabled: false } && await IsLastActiveAdminAsync(db, id))
            return Results.Conflict(new { error = LastAdmin });

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        cache.ForgetUser(id);
        events.Publish(new UserChanged(http.User.Actor(), id, $"Удалён пользователь {user.DisplayName} ({user.Login})"));
        return Results.NoContent();
    }

    private static Task<bool> IsLastActiveAdminAsync(ToadFlowDb db, Guid id) =>
        db.Users.AllAsync(u => u.Id == id || u.Role != UserRole.Admin || u.IsDisabled);

    public static string? Validate(string login, string displayName, string password)
    {
        if (string.IsNullOrWhiteSpace(login)) return "Укажите логин";
        if (string.IsNullOrWhiteSpace(displayName)) return "Укажите имя";
        return PasswordError(password);
    }

    public static string? PasswordError(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? $"Пароль — не короче {MinPasswordLength} символов" : null;
}
