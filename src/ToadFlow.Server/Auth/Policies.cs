using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ToadFlow.Contracts;

namespace ToadFlow.Server.Auth;

/// <summary>Политики по ролям: старшая роль проходит политики младших.</summary>
public static class Policies
{
    public const string Viewer = nameof(Viewer);
    public const string Operator = nameof(Operator);
    public const string Admin = nameof(Admin);

    public static void Add(AuthorizationOptions options)
    {
        options.AddPolicy(Viewer, policy => policy.RequireRole(Roles(UserRole.Viewer)));
        options.AddPolicy(Operator, policy => policy.RequireRole(Roles(UserRole.Operator)));
        options.AddPolicy(Admin, policy => policy.RequireRole(Roles(UserRole.Admin)));
    }

    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Нет пользователя в токене"));

    private static string[] Roles(UserRole minimum) =>
        Enum.GetValues<UserRole>().Where(role => role >= minimum).Select(role => role.ToString()).ToArray();
}
