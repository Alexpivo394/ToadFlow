using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;

namespace ToadFlow.Server.Auth;

/// <summary>
///     Политики — явные наборы ролей. Наблюдатель, оператор и администратор старшинством проходят политики младших;
///     проектировщик в эту лестницу не входит: ему открыты только <see cref="Member" /> и <see cref="Designer" />.
/// </summary>
public static class Policies
{
    /// <summary>Любой вошедший пользователь, в том числе проектировщик.</summary>
    public const string Member = nameof(Member);

    public const string Viewer = nameof(Viewer);

    /// <summary>Правит коллизии: статусы, «кто правит», примечания.</summary>
    public const string Designer = nameof(Designer);

    public const string Operator = nameof(Operator);
    public const string Admin = nameof(Admin);

    /// <summary>Пользователь входит паролем (JWT в браузере) или личным токеном (программы вроде плагина ParamChecker).</summary>
    private static readonly string[] Schemes = [JwtBearerDefaults.AuthenticationScheme, ApiTokenAuthHandler.SchemeName];

    public static void Add(AuthorizationOptions options)
    {
        Role(options, Member, UserRole.Viewer, UserRole.Designer, UserRole.Operator, UserRole.Admin);
        Role(options, Viewer, UserRole.Viewer, UserRole.Operator, UserRole.Admin);
        Role(options, Designer, UserRole.Designer, UserRole.Operator, UserRole.Admin);
        Role(options, Operator, UserRole.Operator, UserRole.Admin);
        Role(options, Admin, UserRole.Admin);
        options.AddPolicy(Agent, policy => policy.AddAuthenticationSchemes(AgentAuthHandler.SchemeName).RequireAuthenticatedUser());
    }

    /// <summary>Координатор или администратор — тот, кто проходит политику <see cref="Operator" />.</summary>
    public static bool IsStaff(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Operator)) || principal.IsInRole(nameof(UserRole.Admin));

    /// <summary>Вызов от агента по его ключу (<see cref="AgentAuthHandler" />); пользовательские токены не подходят.</summary>
    public const string Agent = nameof(Agent);

    /// <summary>Агент, от имени которого пришёл запрос с политикой <see cref="Agent" />.</summary>
    public static Guid AgentId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Нет агента в запросе"));

    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Нет пользователя в токене"));

    private static void Role(AuthorizationOptions options, string name, params UserRole[] roles) =>
        options.AddPolicy(name, policy => policy.AddAuthenticationSchemes(Schemes).RequireRole(roles.Select(role => role.ToString())));
}
