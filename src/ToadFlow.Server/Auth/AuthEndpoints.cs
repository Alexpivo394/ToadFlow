using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Users;

namespace ToadFlow.Server.Auth;

/// <param name="Remember">«Оставаться в системе»: выдать refresh-токен на 30 дней.</param>
public sealed record LoginRequest(string Login, string Password, bool Remember = false);

public sealed record RefreshRequest(string? RefreshToken);

/// <param name="IsPending">Заявка на регистрацию ждёт подтверждения.</param>
/// <param name="Disciplines">Разделы, которые человек ведёт; по ним — «Мои коллизии».</param>
public sealed record UserView(
    Guid Id,
    string Login,
    string DisplayName,
    UserRole Role,
    bool IsDisabled,
    DateTimeOffset? LastLoginAt,
    bool IsPending,
    string? Position,
    string? Email,
    string? Phone,
    string[] Disciplines,
    DateTimeOffset CreatedAt)
{
    public static UserView From(User user) => new(user.Id, user.Login, user.DisplayName, user.Role, user.IsDisabled, user.LastLoginAt, user.IsPending,
        user.Position, user.Email, user.Phone, user.Disciplines, user.CreatedAt);
}

/// <param name="RefreshToken">Только при «Оставаться в системе»: им обновляется JWT после его истечения.</param>
public sealed record LoginResponse(string Token, UserView User, string? RefreshToken = null);

public static class AuthEndpoints
{
    public const string PendingError = "Заявка ждёт подтверждения координатора";

    public static void MapAuth(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting(AuthRateLimits.Login);
        api.MapPost("/auth/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(AuthRateLimits.Login);
        api.MapPost("/auth/logout", LogoutAsync).AllowAnonymous();
        api.MapGet("/auth/me", async (HttpContext http, ToadFlowDb db) =>
            UserView.From(await db.Users.SingleAsync(u => u.Id == http.User.UserId())));
    }

    /// <summary>Новый JWT по refresh-токену; предъявленный токен заменяется новым.</summary>
    private static async Task<IResult> RefreshAsync(RefreshRequest request, RefreshTokens refresh, TokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.RefreshToken) || await refresh.RotateAsync(request.RefreshToken, ct) is not { } rotated)
            return Results.Unauthorized();
        return Results.Ok(new LoginResponse(tokens.Issue(rotated.User), UserView.From(rotated.User), rotated.Token));
    }

    /// <summary>Выход: refresh-токен этой сессии больше не действует. JWT не нужен — он мог уже истечь.</summary>
    private static async Task<IResult> LogoutAsync(RefreshRequest request, RefreshTokens refresh, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.RefreshToken)) await refresh.RevokeAsync(request.RefreshToken, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, ToadFlowDb db, TokenService tokens, RefreshTokens refresh, TimeProvider time,
        CancellationToken ct)
    {
        var login = User.NormalizeLogin(request.Login ?? "");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Login == login);
        // Просроченная заявка — как несуществующий пользователь; удалит её уборка, вход в базу не пишет.
        if (user is null || user.IsDisabled || string.IsNullOrEmpty(request.Password) || RegistrationEndpoints.Expired(user, time.GetUtcNow()))
            return Results.Unauthorized();

        var hasher = new PasswordHasher<User>();
        var check = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (check == PasswordVerificationResult.Failed) return Results.Unauthorized();
        // Только после верного пароля: иначе по ответу можно узнать, чья заявка ждёт.
        if (user.IsPending) return Results.Json(new { error = PendingError }, statusCode: StatusCodes.Status403Forbidden);
        if (check == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, request.Password);

        user.LastLoginAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var refreshToken = request.Remember ? await refresh.IssueAsync(user.Id, ct) : null;
        return Results.Ok(new LoginResponse(tokens.Issue(user), UserView.From(user), refreshToken));
    }
}
