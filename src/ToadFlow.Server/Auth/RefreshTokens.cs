using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Auth;

/// <summary>Токен «Оставаться в системе». В базе — только SHA-256 токена: утечка базы не даёт войти.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>SHA-256 токена, 64 шестнадцатеричных символа.</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
///     Выдача и смена refresh-токенов. Каждое обновление отзывает предъявленный токен и выдаёт новый на 30 дней. Отозванный токен,
///     предъявленный снова, значит, что его украли: отзываются все токены пользователя.
/// </summary>
public sealed class RefreshTokens(ToadFlowDb db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Новый токен пользователя; возвращается сам токен — в базе остаётся только его хеш.</summary>
    public async Task<string> IssueAsync(Guid userId, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        db.RefreshTokens.Add(new RefreshToken { Id = Guid.CreateVersion7(), UserId = userId, TokenHash = Hash(token), CreatedAt = now, ExpiresAt = now + Lifetime });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>
    ///     Обменять токен на новый. Отзыв старого — условным обновлением: из двух одновременных обменов одного токена выиграет один,
    ///     второй увидит отозванный токен.
    /// </summary>
    /// <returns>Пользователь и новый токен; null — токен неизвестен, истёк, отозван (тогда отозваны и все токены пользователя) или пользователь не может войти.</returns>
    public async Task<(User User, string Token)?> RotateAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        var stored = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return null;
        var now = time.GetUtcNow();
        var revoked = stored.RevokedAt is null
            ? await db.RefreshTokens.Where(t => t.Id == stored.Id && t.RevokedAt == null).ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, now), ct)
            : 0;
        if (revoked == 0)
        {
            await RevokeAllAsync(stored.UserId, ct);
            return null;
        }

        if (stored.ExpiresAt <= now) return null;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || user.IsDisabled || user.IsPending) return null;
        return (user, await IssueAsync(user.Id, ct));
    }

    /// <summary>Выход: предъявленный токен больше не действует.</summary>
    public Task RevokeAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        var now = time.GetUtcNow();
        return db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null).ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, now), ct);
    }

    public Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, now), ct);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

/// <summary>Закрыть все сессии пользователя: смена пароля, роли, отключение.</summary>
public static class UserSessions
{
    /// <summary>Новый штамп (старые JWT перестают действовать) и отзыв refresh-токенов. Сохраняет и сбрасывает кеш (ForgetUser) вызывающий — после сохранения.</summary>
    public static async Task EndAllAsync(User user, RefreshTokens tokens, CancellationToken ct)
    {
        user.SecurityStamp = Guid.NewGuid();
        await tokens.RevokeAllAsync(user.Id, ct);
    }
}
