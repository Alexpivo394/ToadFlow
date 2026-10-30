using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Auth;

public sealed record LoginRequest(string Login, string Password);

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

public sealed record LoginResponse(string Token, UserView User);

public static class AuthEndpoints
{
    public const string PendingError = "Заявка ждёт подтверждения координатора";

    public static void MapAuth(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        api.MapGet("/auth/me", async (HttpContext http, ToadFlowDb db) =>
            UserView.From(await db.Users.SingleAsync(u => u.Id == http.User.UserId())));
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, ToadFlowDb db, TokenService tokens, TimeProvider time)
    {
        var login = User.NormalizeLogin(request.Login ?? "");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Login == login);
        if (user is null || user.IsDisabled || string.IsNullOrEmpty(request.Password)) return Results.Unauthorized();

        var hasher = new PasswordHasher<User>();
        var check = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (check == PasswordVerificationResult.Failed) return Results.Unauthorized();
        // Только после верного пароля: иначе по ответу можно узнать, чья заявка ждёт.
        if (user.IsPending) return Results.Json(new { error = PendingError }, statusCode: StatusCodes.Status403Forbidden);
        if (check == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, request.Password);

        user.LastLoginAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        return Results.Ok(new LoginResponse(tokens.Issue(user), UserView.From(user)));
    }
}
