using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;

namespace ToadFlow.Server.Auth;

/// <summary>Политики по ролям: старшая роль проходит политики младших.</summary>
public static class Policies
{
    public const string Viewer = nameof(Viewer);
    public const string Operator = nameof(Operator);
    public const string Admin = nameof(Admin);

    /// <summary>Пользователь входит паролем (JWT в браузере) или личным токеном (программы вроде плагина ParamChecker).</summary>
    private static readonly string[] Schemes = [JwtBearerDefaults.AuthenticationScheme, ApiTokenAuthHandler.SchemeName];

    public static void Add(AuthorizationOptions options)
    {
        options.AddPolicy(Viewer, policy => policy.AddAuthenticationSchemes(Schemes).RequireRole(Roles(UserRole.Viewer)));
        options.AddPolicy(Operator, policy => policy.AddAuthenticationSchemes(Schemes).RequireRole(Roles(UserRole.Operator)));
        options.AddPolicy(Admin, policy => policy.AddAuthenticationSchemes(Schemes).RequireRole(Roles(UserRole.Admin)));
        options.AddPolicy(Agent, policy => policy.AddAuthenticationSchemes(AgentAuthHandler.SchemeName).RequireAuthenticatedUser());
    }

    /// <summary>Вызов от агента по его ключу (<see cref="AgentAuthHandler" />); пользовательские токены не подходят.</summary>
    public const string Agent = nameof(Agent);

    /// <summary>Агент, от имени которого пришёл запрос с политикой <see cref="Agent" />.</summary>
    public static Guid AgentId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Нет агента в запросе"));

    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Нет пользователя в токене"));

    private static string[] Roles(UserRole minimum) =>
        Enum.GetValues<UserRole>().Where(role => role >= minimum).Select(role => role.ToString()).ToArray();
}
