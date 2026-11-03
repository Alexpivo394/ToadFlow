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

    /// <summary>Заявка на регистрацию ждёт координатора: войти нельзя.</summary>
    public bool IsPending { get; set; }

    public string? Position { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>Разделы, которые человек ведёт, — как в списке <c>Disciplines.ListAsync</c>; по ним считаются «Мои коллизии».</summary>
    public string[] Disciplines { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    ///     Меняется при смене пароля, роли и отключении: выданные раньше JWT (claim <c>stamp</c>) перестают действовать сразу, а не через
    ///     12 часов, и refresh-токены пользователя отзываются.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

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
