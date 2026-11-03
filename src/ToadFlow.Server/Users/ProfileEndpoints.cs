using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Users;

/// <summary>Свои имя и контакты. Роли и разделов здесь нет: их меняют координатор и администратор.</summary>
public sealed record ProfileRequest(string DisplayName, string? Position = null, string? Email = null, string? Phone = null);

public sealed record ChangePasswordRequest(string Current, string New);

/// <summary>Контакты коллеги — для ленты коллизии: по имени в записи видно, как связаться.</summary>
public sealed record UserCard(Guid Id, string DisplayName, string? Position, string? Email, string? Phone, string[] Disciplines);

/// <summary>Личный кабинет: есть у любой роли.</summary>
public static class ProfileEndpoints
{
    private const int MaxContactLength = 200;

    public static void MapProfile(this RouteGroupBuilder members)
    {
        members.MapGet("/profile", async (HttpContext http, ToadFlowDb db, CancellationToken ct) =>
            UserView.From(await db.Users.SingleAsync(u => u.Id == http.User.UserId(), ct)));
        members.MapPut("/profile", UpdateAsync);
        members.MapPost("/profile/password", ChangePasswordAsync);
        members.MapGet("/users/{id:guid}/card", async (Guid id, ToadFlowDb db, CancellationToken ct) =>
            await db.Users.AsNoTracking().Where(u => u.Id == id && !u.IsPending)
                    .Select(u => new UserCard(u.Id, u.DisplayName, u.Position, u.Email, u.Phone, u.Disciplines)).SingleOrDefaultAsync(ct) is { } card
                ? Results.Ok(card)
                : Results.NotFound());
    }

    /// <summary>Должность, почта и телефон: пустое — null, длина не больше 200.</summary>
    /// <returns>Ошибка для показа; null — всё в порядке.</returns>
    public static string? Contacts(string? position, string? email, string? phone, out (string? Position, string? Email, string? Phone) contacts)
    {
        contacts = (Clean(position), Clean(email), Clean(phone));
        if (new[] { contacts.Position, contacts.Email, contacts.Phone }.Any(v => v?.Length > MaxContactLength))
            return $"Должность, почта и телефон — не длиннее {MaxContactLength} символов";
        return contacts.Email is { } mail && !mail.Contains('@') ? "Почта указана неверно" : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<IResult> UpdateAsync(ProfileRequest request, HttpContext http, ToadFlowDb db, IDomainEvents events, CancellationToken ct)
    {
        if (UserEndpoints.DisplayNameError(request.DisplayName) is { } nameError) return Results.BadRequest(new { error = nameError });
        if (Contacts(request.Position, request.Email, request.Phone, out var contacts) is { } error) return Results.BadRequest(new { error });

        var user = await db.Users.SingleAsync(u => u.Id == http.User.UserId(), ct);
        user.DisplayName = request.DisplayName.Trim();
        (user.Position, user.Email, user.Phone) = contacts;
        await db.SaveChangesAsync(ct);
        events.Publish(new UserChanged(http.User.Actor(), user.Id, $"{user.DisplayName} изменил(а) свой профиль"));
        return Results.Ok(UserView.From(user));
    }

    /// <summary>
    ///     Смена своего пароля закрывает все сессии пользователя (другие браузеры, плагин), а этой выдаёт новый JWT — человек
    ///     продолжает работать, не входя заново.
    /// </summary>
    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, HttpContext http, ToadFlowDb db, IDomainEvents events,
        RefreshTokens refresh, TokenService tokens, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == http.User.UserId(), ct);
        var hasher = new PasswordHasher<User>();
        if (string.IsNullOrEmpty(request.Current) || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Current) == PasswordVerificationResult.Failed)
            return Results.BadRequest(new { error = "Текущий пароль неверный" });
        if (UserEndpoints.PasswordError(request.New) is { } error) return Results.BadRequest(new { error });

        user.PasswordHash = hasher.HashPassword(user, request.New);
        await UserSessions.EndAllAsync(user, refresh, ct);
        await db.SaveChangesAsync(ct);
        cache.ForgetUser(user.Id);
        events.Publish(new UserChanged(http.User.Actor(), user.Id, $"{user.DisplayName} сменил(а) свой пароль"));
        return Results.Ok(new LoginResponse(tokens.Issue(user), UserView.From(user)));
    }
}
