using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Users;

public sealed record CreateUserRequest(string Login, string DisplayName, UserRole Role, string Password);

public sealed record UpdateUserRequest(string DisplayName, UserRole Role, bool IsDisabled);

public sealed record PasswordRequest(string Password);

public static class UserEndpoints
{
    private const int MinPasswordLength = 6;
    private const string LastAdmin = "Нельзя убрать последнего активного администратора";

    public static void MapUsers(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(Policies.Admin);

        users.MapGet("/", async (ToadFlowDb db) =>
            (await db.Users.OrderBy(u => u.DisplayName).ToListAsync()).Select(UserView.From));

        users.MapPost("/", CreateAsync);
        users.MapPut("/{id:guid}", UpdateAsync);
        users.MapPost("/{id:guid}/password", SetPasswordAsync);
        users.MapDelete("/{id:guid}", DeleteAsync);
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

    private static string? Validate(string login, string displayName, string password)
    {
        if (string.IsNullOrWhiteSpace(login)) return "Укажите логин";
        if (string.IsNullOrWhiteSpace(displayName)) return "Укажите имя";
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength) return $"Пароль — не короче {MinPasswordLength} символов";
        return null;
    }
}
