using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Requests;

/// <summary>Общие действия с заявками: проверки и закрытие публикацией из обхода.</summary>
public static class Requests
{
    public const string UiEntity = "family-requests";

    public static Task<bool> IsOpenAsync(ToadFlowDb db, Guid id, CancellationToken ct) =>
        db.Set<FamilyRequest>().AnyAsync(r => r.Id == id && (r.State == RequestState.New || r.State == RequestState.InWork), ct);

    /// <summary>
    ///     Публикация с заявкой увидена обходом: заявка «готова» со ссылкой на карточку. Уже закрытую заявку публикация не трогает.
    ///     Сохраняет изменения сама.
    /// </summary>
    public static async Task CloseByPublicationsAsync(ToadFlowDb db, IEnumerable<FamilyPublication> published, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var withRequest = published.Where(p => p is { RequestId: not null, VersionId: not null }).ToList();
        if (withRequest.Count == 0) return;

        var now = time.GetUtcNow();
        foreach (var publication in withRequest)
        {
            var request = await db.Set<FamilyRequest>().SingleOrDefaultAsync(r => r.Id == publication.RequestId, ct);
            if (request is not { IsOpen: true }) continue;
            var card = await db.Set<FamilyVersion>().Where(v => v.Id == publication.VersionId)
                .Join(db.Set<FamilyCard>(), v => v.CardId, c => c.Id, (v, c) => new { c.Id, c.Name }).SingleOrDefaultAsync(ct);
            if (card is null) continue;

            request.State = RequestState.Done;
            request.CardId = card.Id;
            request.AssigneeUserId ??= publication.AuthorUserId;
            request.ClosedAt = now;
            request.UpdatedAt = now;
            db.Set<FamilyRequestMessage>().Add(new FamilyRequestMessage
            {
                Id = Guid.CreateVersion7(),
                RequestId = request.Id,
                AuthorUserId = publication.AuthorUserId,
                Text = $"Готова: опубликовано семейство «{card.Name}»",
                IsEvent = true,
                At = now
            });
            await db.SaveChangesAsync(ct);
            events.Publish(new CatalogChanged(Actor.System, UiEntity, request.Id, $"Семейства: заявка № {request.Number} готова — «{card.Name}»"));
        }
    }
}
