using ToadFlow.Contracts;

namespace ToadFlow.Server.Data.Entities;

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Логин в нижнем регистре — так он уникален без учёта регистра.</summary>
    public string Login { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public static string NormalizeLogin(string login) => login.Trim().ToLowerInvariant();

    public static User Create(string login, string displayName, UserRole role, string passwordHash, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Login = NormalizeLogin(login),
        DisplayName = displayName.Trim(),
        Role = role,
        PasswordHash = passwordHash,
        CreatedAt = now
    };
}
