namespace ToadFlow.Server.Clash.Collisions;

/// <summary>
///     Пользователь открывал карточку коллизии. Коллизия новая для него, пока она открыта и строки нет или
///     <see cref="SeenAt" /> раньше <see cref="ClashCollision.OpenedAt" />.
/// </summary>
public sealed class ClashCollisionSeen
{
    public Guid UserId { get; set; }
    public Guid CollisionId { get; set; }

    /// <summary>Последнее открытие карточки.</summary>
    public DateTimeOffset SeenAt { get; set; }
}
