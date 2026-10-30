namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Когда пользователь открывал «Мои коллизии»: от прошлой отметки считаются «новые с прошлого захода».</summary>
public sealed class ClashUserState
{
    /// <summary>Отметка сдвигается не чаще: иначе новые обнулялись бы при каждом открытии страницы.</summary>
    public static readonly TimeSpan VisitStep = TimeSpan.FromMinutes(30);

    /// <summary>Захода ещё не было — новыми считаются коллизии этого срока.</summary>
    public static readonly TimeSpan FirstVisitWindow = TimeSpan.FromDays(7);

    public Guid UserId { get; set; }

    /// <summary>Последняя отметка захода.</summary>
    public DateTimeOffset SeenAt { get; set; }

    /// <summary>Отметка до последней: от неё считаются новые; null — заход был один.</summary>
    public DateTimeOffset? PreviousSeenAt { get; set; }
}
