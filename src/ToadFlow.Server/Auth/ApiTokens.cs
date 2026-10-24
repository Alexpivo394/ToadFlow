using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Auth;

/// <summary>
///     Вход личным токеном: заголовок <c>X-ToadFlow-Token</c>. Пользователь получает свои обычные права; отключённого
///     пользователя токен не пускает.
/// </summary>
public sealed class ApiTokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ToadFlowDb db,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string Header = "X-ToadFlow-Token";

    /// <summary>Есть у пользователя, вошедшего токеном: токенами нельзя управлять токеном.</summary>
    public const string TokenClaim = "toadflow:token";

    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        var hash = ApiTokenEndpoints.Hash(token.Trim());
        var found = await db.ApiTokens.AsNoTracking().Where(t => t.Hash == hash)
            .Join(db.Users, t => t.UserId, u => u.Id, (t, u) => new { Token = t, User = u })
            .SingleOrDefaultAsync();
        if (found is null) return AuthenticateResult.Fail("Неверный токен");
        if (found.User.IsDisabled) return AuthenticateResult.Fail("Пользователь отключён");

        var now = time.GetUtcNow();
        if (found.Token.LastUsedAt is not { } used || now - used > TouchEvery)
            await db.ApiTokens.Where(t => t.Id == found.Token.Id).ExecuteUpdateAsync(t => t.SetProperty(x => x.LastUsedAt, now));

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, found.User.Id.ToString()),
            new Claim(ClaimTypes.Name, found.User.DisplayName),
            new Claim(ClaimTypes.Role, found.User.Role.ToString()),
            new Claim(TokenClaim, found.Token.Id.ToString())
        ], SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public sealed record ApiTokenView(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <param name="Token">Сам токен — показывается один раз.</param>
public sealed record ApiTokenCreated(ApiTokenView View, string Token);

public sealed record ApiTokenRequest(string Name);

/// <summary>Свои токены: список, создание, отзыв. Только после входа паролем — не другим токеном.</summary>
public static class ApiTokenEndpoints
{
    private const string Prefix = "tf_";

    public static void MapApiTokens(this RouteGroupBuilder api)
    {
        var tokens = api.MapGroup("/auth/tokens");
        tokens.MapGet("/", async (HttpContext http, ToadFlowDb db) =>
        {
            if (ByToken(http)) return Results.Forbid();
            var userId = http.User.UserId();
            return Results.Ok(await db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt)
                .Select(t => new ApiTokenView(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt)).ToListAsync());
        });

        tokens.MapPost("/", async (ApiTokenRequest request, HttpContext http, ToadFlowDb db, TimeProvider time, IDomainEvents events) =>
        {
            if (ByToken(http)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Укажите, для чего токен" });

            var token = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var entity = new ApiToken
            {
                Id = Guid.CreateVersion7(),
                UserId = http.User.UserId(),
                Name = request.Name.Trim(),
                Prefix = token[..(Prefix.Length + 6)],
                Hash = Hash(token),
                CreatedAt = time.GetUtcNow()
            };
            db.ApiTokens.Add(entity);
            await db.SaveChangesAsync();
            events.Publish(new UserChanged(http.User.Actor(), entity.UserId, $"Создан токен доступа «{entity.Name}»"));
            return Results.Ok(new ApiTokenCreated(new ApiTokenView(entity.Id, entity.Name, entity.Prefix, entity.CreatedAt, null), token));
        });

        tokens.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ToadFlowDb db, IDomainEvents events) =>
        {
            if (ByToken(http)) return Results.Forbid();
            var userId = http.User.UserId();
            var entity = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id && t.UserId == userId);
            if (entity is null) return Results.NotFound();

            db.ApiTokens.Remove(entity);
            await db.SaveChangesAsync();
            events.Publish(new UserChanged(http.User.Actor(), userId, $"Отозван токен доступа «{entity.Name}»"));
            return Results.NoContent();
        });
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool ByToken(HttpContext http) => http.User.HasClaim(c => c.Type == ApiTokenAuthHandler.TokenClaim);
}
