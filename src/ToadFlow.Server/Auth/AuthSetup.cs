using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
                    OnTokenValidated = async context => { if (await IsDisabledAsync(context)) context.Fail("Пользователь отключён"); }
                };
            });
        builder.Services.AddAuthorization(Policies.Add);
    }

    /// <summary>Отключённого пользователя не пускаем и со старым токеном; флаг кешируется на 30 секунд.</summary>
    private static async Task<bool> IsDisabledAsync(TokenValidatedContext context)
    {
        var userId = context.Principal!.UserId();
        var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
        return await cache.GetOrCreateAsync(DisabledKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var db = context.HttpContext.RequestServices.GetRequiredService<ToadFlowDb>();
            return await db.Users.Where(u => u.Id == userId).Select(u => (bool?)u.IsDisabled).SingleOrDefaultAsync() ?? true;
        });
    }

    /// <summary>Сбросить кеш флага после изменения пользователя — чтобы отключение действовало сразу.</summary>
    public static void ForgetUser(this IMemoryCache cache, Guid userId) => cache.Remove(DisabledKey(userId));

    private static string DisabledKey(Guid userId) => $"user-disabled:{userId}";
}
