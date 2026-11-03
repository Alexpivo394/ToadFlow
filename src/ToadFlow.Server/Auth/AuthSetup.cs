using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Auth;

public static class AuthSetup
{
    public static void AddToadFlowAuth(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<AdminSeedOptions>().BindConfiguration(AdminSeedOptions.Section);
        builder.Services.AddSingleton<TokenService>();
        builder.Services.AddMemoryCache();
        builder.Services.AddHostedService<AdminSeeder>();

        var jwtKey = builder.Configuration[$"{AuthOptions.Section}:JwtKey"] ?? "";
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenService.Issuer,
                    ValidAudience = TokenService.Issuer,
                    IssuerSigningKey = TokenService.Key(jwtKey.Length > 0 ? jwtKey : new string('-', 32)),
                    NameClaimType = System.Security.Claims.ClaimTypes.Name,
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role
                };
                options.Events = new JwtBearerEvents
                {
                    // SignalR в браузере передаёт токен в строке запроса.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/ui"))
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context => { if (await RejectionAsync(context) is { } reason) context.Fail(reason); }
                };
            });
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(ApiTokenAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(Policies.Add);
        builder.Services.AddScoped<RefreshTokens>();
        builder.AddAuthRateLimits();
    }

    private sealed record UserState(UserRole Role, bool Blocked, Guid Stamp);

    /// <summary>
    ///     JWT действует, пока пользователь тот же, что при выдаче: не отключён и не ждёт подтверждения, роль та же, штамп тот же (пароль
    ///     не меняли). Состояние кешируется на 30 секунд и сбрасывается при изменении пользователя (<see cref="ForgetUser" />). JWT,
    ///     выданный до штампов (без claim <c>stamp</c>), проверяется только по роли.
    /// </summary>
    /// <returns>null — токен действует; иначе причина отказа.</returns>
    private static async Task<string?> RejectionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var userId = principal.UserId();
        var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
        var state = await cache.GetOrCreateAsync(StateKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var db = context.HttpContext.RequestServices.GetRequiredService<ToadFlowDb>();
            return await db.Users.Where(u => u.Id == userId).Select(u => new UserState(u.Role, u.IsDisabled || u.IsPending, u.SecurityStamp)).SingleOrDefaultAsync();
        });
        if (state is null || state.Blocked) return "Пользователь отключён";
        if (principal.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value != state.Role.ToString()) return "Роль пользователя изменилась — войдите снова";
        if (principal.FindFirst(TokenService.StampClaim)?.Value is { } stamp && stamp != state.Stamp.ToString()) return "Сессия закрыта — войдите снова";
        return null;
    }

    /// <summary>Сбросить кеш состояния после изменения пользователя — чтобы отключение, смена роли и пароля действовали сразу.</summary>
    public static void ForgetUser(this IMemoryCache cache, Guid userId) => cache.Remove(StateKey(userId));

    private static string StateKey(Guid userId) => $"user-state:{userId}";
}
