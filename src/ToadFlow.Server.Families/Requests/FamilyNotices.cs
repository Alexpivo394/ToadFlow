using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Notifications;

namespace ToadFlow.Server.Families.Requests;

/// <summary>
///     Уведомления по итогам обхода библиотеки: у семейства новая действующая версия — тем, кто загружал его в проекты за 180 дней (с перечнем
///     проектов); появилась версия на проверке — координаторам направления её раздела.
/// </summary>
public sealed class FamilyNotices(ToadFlowDb db, INotifications notifications, TimeProvider time)
{
    private const int LoadsDays = 180;
    private const int MaxProjects = 5;

    /// <param name="currentBefore">Действующая версия каждой карточки до обхода.</param>
    /// <param name="reviewBefore">Версии на проверке до обхода.</param>
    public async Task AfterScanAsync(IReadOnlyDictionary<Guid, Guid?> currentBefore, IReadOnlySet<Guid> reviewBefore, IReadOnlyDictionary<Guid, FamilyCard> cards,
        IReadOnlyDictionary<Guid, FamilyVersion> versions, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-LoadsDays);
        foreach (var card in cards.Values)
        {
            if (!currentBefore.TryGetValue(card.Id, out var before) || before is null || card.CurrentVersionId is not { } now || now == before) continue;
            if (!versions.TryGetValue(now, out var version) || version.State != VersionState.Approved) continue;

            var loads = await db.Set<FamilyLoad>().AsNoTracking().Where(l => l.CardId == card.Id && l.At >= since)
                .Select(l => new { l.UserId, l.Project }).ToListAsync(ct);
            foreach (var user in loads.GroupBy(l => l.UserId))
            {
                var projects = user.Select(l => l.Project).Where(p => p.Length > 0).Distinct().ToList();
                var text = projects.Count == 0
                    ? "Новая версия в каталоге — обновите семейство в своих проектах"
                    : $"Новая версия. Загружено в проекты: {string.Join(", ", projects.Take(MaxProjects))}{(projects.Count > MaxProjects ? $" и ещё {projects.Count - MaxProjects}" : "")}";
                await notifications.NotifyAsync([user.Key], NotificationKinds.FamilyUpdated, $"Обновлено: {card.Name}", text,
                    $"/families/catalog?card={card.Id}", $"family:{card.Id}:updated", null, ct);
            }
        }

        foreach (var version in versions.Values.Where(v => v.State == VersionState.InReview && !reviewBefore.Contains(v.Id)))
        {
            if (!cards.TryGetValue(version.CardId, out var card)) continue;
            var coordinators = await Directions.CoordinatorsAsync(db, await Directions.BySectionAsync(db, card.Section, ct), ct);
            await notifications.NotifyAsync(coordinators, NotificationKinds.FamilyReview, $"На проверку: {card.Name}",
                card.Section.Length > 0 ? $"Раздел «{card.Section}»" : "Новое семейство или версия", "/families/review", "family.review", version.AuthorUserId, ct);
        }
    }
}
