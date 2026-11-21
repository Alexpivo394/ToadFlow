using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Requests;

/// <summary>Общие действия с заявками: проверки и закрытие публикацией из обхода.</summary>
public static class Requests
{
    public const string UiEntity = "family-requests";

    public static Task<bool> IsOpenAsync(ToadFlowDb db, Guid id, CancellationToken ct) =>
        db.Set<FamilyRequest>().AnyAsync(r => r.Id == id && FamilyRequest.OpenStates.Contains(r.State), ct);

    /// <summary>
    ///     Публикации с заявкой, которые обход привязал к версиям: заявка уходит на приёмку автору со ссылкой на карточку. Уже закрытую заявку публикация не
    ///     трогает. Изменения сохраняет вызывающий — в транзакции обхода.
    /// </summary>
    /// <param name="cards">Карточки обхода по id: у привязанной версии карточка всегда среди них.</param>
    /// <param name="versions">Версии обхода по id.</param>
    /// <returns>Закрытые заявки и строки для журнала действий.</returns>
    public static async Task<List<(Guid Id, string Text)>> CloseByPublicationsAsync(ToadFlowDb db, IEnumerable<FamilyPublication> published,
        IReadOnlyDictionary<Guid, FamilyCard> cards, IReadOnlyDictionary<Guid, FamilyVersion> versions, DateTimeOffset now, CancellationToken ct)
    {
        var closed = new List<(Guid, string)>();
        foreach (var publication in published.Where(p => p is { RequestId: not null, VersionId: not null }))
        {
            if (!versions.TryGetValue(publication.VersionId!.Value, out var version) || !cards.TryGetValue(version.CardId, out var card)) continue;
            var request = await db.Set<FamilyRequest>().SingleOrDefaultAsync(r => r.Id == publication.RequestId, ct);
            if (request is not { IsOpen: true }) continue;

            RequestEndpoints.ToReview(request, card.Id, RequestResolution.Made, publication.AuthorUserId, now);
            request.UpdatedAt = now;
            db.Set<FamilyRequestMessage>().Add(new FamilyRequestMessage
            {
                Id = Guid.CreateVersion7(),
                RequestId = request.Id,
                AuthorUserId = publication.AuthorUserId,
                Text = $"Готово: опубликовано семейство «{card.Name}» — на приёмке у автора",
                IsEvent = true,
                At = now
            });
            closed.Add((request.Id, $"Семейства: заявка № {request.Number} на приёмке — «{card.Name}»"));
        }

        return closed;
    }
}
