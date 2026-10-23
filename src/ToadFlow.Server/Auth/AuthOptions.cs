using System.ComponentModel.DataAnnotations;

namespace ToadFlow.Server.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    [Required, MinLength(32, ErrorMessage = "Auth:JwtKey должен быть не короче 32 символов")]
    public string JwtKey { get; set; } = "";

    [Range(1, 24 * 30)]
    public int TokenLifetimeHours { get; set; } = 12;
}

/// <summary>Первый администратор: создаётся при старте, если пользователей ещё нет.</summary>
public sealed class AdminSeedOptions
{
    public const string Section = "Admin";

    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}
