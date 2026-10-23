using System.Security.Claims;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Events;

public static class ActorContext
{
    /// <summary>Текущий пользователь запроса как автор события.</summary>
    public static Actor Actor(this ClaimsPrincipal principal) =>
        new(ActorKind.User, principal.FindFirstValue(ClaimTypes.Name) ?? "неизвестный пользователь");
}
