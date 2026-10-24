namespace ToadFlow.Server.Data.Entities;

/// <summary>
///     Личный токен доступа: даёт программе (например, плагину ParamChecker) те же права, что у пользователя, без его пароля.
///     Сам токен показывается один раз при создании; хранится только его хеш.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Для чего токен, например «ParamChecker на ноутбуке».</summary>
    public string Name { get; set; } = "";

    /// <summary>Начало токена — чтобы узнать его в списке.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>SHA-256 токена в hex.</summary>
    public string Hash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
