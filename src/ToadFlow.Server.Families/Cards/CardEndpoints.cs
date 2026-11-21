using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;
using ToadFlow.Server.Families.Scan;
using ToadFlow.Server.Families.Search;

namespace ToadFlow.Server.Families.Cards;

internal static class CardEndpoints
{
    private const int MaxTags = 30;
    private const int MaxText = 2000;

    /// <summary>Маршруты участников: карточку видит и проектировщик (по правилам <see cref="CardVisibility" />).</summary>
    public static void MapFamiliesCards(this RouteGroupBuilder families)
    {
        families.MapGet("/{id:guid}", CardAsync);
        families.MapGet("/{id:guid}/preview", PreviewAsync);
    }

    /// <summary>
    ///     Картинка версии <paramref name="version" /> (её хеш) или действующей. С хешем в адресе ответ не меняется никогда — кешируется
    ///     на год. Видна тем, кому видна карточка.
    /// </summary>
    private static async Task<IResult> PreviewAsync(Guid id, string? version, bool? unreviewed, ToadFlowDb db, PreviewStore previews, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (card is null || !user.CanSee(card, unreviewed == true)) return Results.NotFound();

        var versions = db.Set<FamilyVersion>().AsNoTracking().Where(v => v.CardId == id && v.HasPreview);
        var hash = version is { Length: > 0 }
            ? await versions.Where(v => v.Hash == version).Select(v => v.Hash).FirstOrDefaultAsync(ct)
            : await versions.Where(v => v.Id == card.CurrentVersionId).Select(v => v.Hash).FirstOrDefaultAsync(ct);
        if (hash is null || !previews.Exists(hash)) return Results.NotFound();

        http.Response.Headers.CacheControl = version is { Length: > 0 } ? "private, max-age=31536000, immutable" : "private, no-cache";
        return Results.File(previews.PathOf(hash), "image/png");
    }

    /// <summary>Маршруты наблюдателя и старше: «На проверке», «Нужно решение», правка карточки координатором.</summary>
    public static void MapFamiliesReview(this RouteGroupBuilder families)
    {
        families.MapGet("/review", ReviewAsync);
        families.MapGet("/issues", IssuesAsync);
        families.MapPut("/{id:guid}/notes", NotesAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/issues/{id:guid}/resolve", ResolveAsync).RequireAuthorization(Policies.Operator);
    }

    /// <summary>
    ///     «Посмотрел» — закрыть случай «изменено без проверки». Остальные случаи закрываются сами, когда их причина ушла из библиотеки:
    ///     закрыть их руками значило бы спрятать то, что в папке осталось.
    /// </summary>
    private static async Task<IResult> ResolveAsync(Guid id, ToadFlowDb db, TimeProvider time, HttpContext http, IDomainEvents events, CancellationToken ct)
    {
        var issue = await db.Set<FamilyIssue>().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null) return Results.NotFound();
        if (issue.Kind != IssueKind.UnreviewedChange)
            return Results.BadRequest(new { error = "Этот случай закроется сам, когда его причину исправят в папке библиотеки" });
        if (issue.ResolvedAt is null)
        {
            issue.ResolvedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            events.Publish(new CatalogChanged(http.User.Actor(), ScanApplier.UiEntity, issue.CardId ?? Guid.Empty,
                $"Семейства: просмотрена правка без проверки «{issue.RelativePath}»"));
        }

        return Results.Ok();
    }

    private static async Task<IResult> CardAsync(Guid id, bool? unreviewed, ToadFlowDb db, FamiliesSettingsStore store, ClaimsPrincipal user, CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (card is null || !user.CanSee(card, unreviewed == true)) return Results.NotFound();

        var versions = await db.Set<FamilyVersion>().AsNoTracking().Where(v => v.CardId == id).OrderByDescending(v => v.SeenAt).ThenByDescending(v => v.Id)
            .ToListAsync(ct);
        // Проектировщик получает действующую версию и утверждённые; кандидаты и отклонённые — только если сам включил непроверенные.
        if (user.IsDesigner())
            versions = versions.Where(v => v.Id == card.CurrentVersionId || v.State == VersionState.Approved || (unreviewed == true && v.State == VersionState.InReview))
                .ToList();
        var current = versions.FirstOrDefault(v => v.Id == card.CurrentVersionId);
        var root = (await store.GetAsync(ct)).Root;
        return Results.Ok(new FamilyCardView(card.Id, card.Name, card.RelativePath, FullPath(root, card.RelativePath), card.Section, card.State, card.Category,
            card.Placement, card.RevitYear, card.TypeCount, card.Description, card.Tags, card.Manufacturer, card.CreatedAt, card.UpdatedAt, card.ArchivedAt,
            card.CurrentVersionId, current?.ReadState, current?.ReadError, current?.HasPreview == true, SearchText.ContentOf(current),
            versions.Select(v => new FamilyVersionView(v.Id, v.Hash, v.RevitYear, v.State, v.Note, v.RelativePath, v.SeenAt, v.ReadState, v.ReadError, v.HasPreview,
                v.Id == card.CurrentVersionId)).ToList()));
    }

    public static string FullPath(string root, string relativePath) =>
        root.Length == 0 ? relativePath : root.EndsWith('\\') ? root + relativePath : root + @"\" + relativePath;

    private static async Task<List<FamilyReviewItem>> ReviewAsync(ToadFlowDb db, CancellationToken ct)
    {
        var cards = db.Set<FamilyCard>().AsNoTracking();
        var items = await db.Set<FamilyVersion>().AsNoTracking()
            .Where(v => v.State == VersionState.InReview)
            .Join(cards.Where(c => c.State != CardState.Archived), v => v.CardId, c => c.Id, (v, c) => new { v, c })
            .Where(x => x.c.State == CardState.InReview ? x.c.CurrentVersionId == x.v.Id : x.c.CurrentVersionId != x.v.Id)
            .OrderByDescending(x => x.v.SeenAt)
            .Select(x => new FamilyReviewItem(x.c.Id, x.v.Id, x.c.Name, x.v.RelativePath, x.c.State == CardState.InReview, x.v.RevitYear, x.v.SeenAt))
            .ToListAsync(ct);
        return items;
    }

    private static async Task<List<FamilyIssueView>> IssuesAsync(ToadFlowDb db, bool? open, CancellationToken ct)
    {
        var issues = db.Set<FamilyIssue>().AsNoTracking();
        issues = open == false ? issues.Where(i => i.ResolvedAt != null) : issues.Where(i => i.ResolvedAt == null);
        var cards = db.Set<FamilyCard>();
        return await issues.OrderByDescending(i => i.CreatedAt).Take(1000)
            .Select(i => new FamilyIssueView(i.Id, i.Kind, i.CardId, cards.Where(c => c.Id == i.CardId).Select(c => c.Name).FirstOrDefault(), i.RelativePath, i.Text,
                i.CreatedAt, i.ResolvedAt))
            .ToListAsync(ct);
    }

    private static async Task<IResult> NotesAsync(Guid id, FamilyNotesRequest request, ToadFlowDb db, TimeProvider time, HttpContext http, IDomainEvents events,
        CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return Results.NotFound();

        var description = Clean(request.Description);
        var manufacturer = Clean(request.Manufacturer);
        var tags = (request.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).DistinctBy(t => t.ToLowerInvariant()).ToArray();
        if (description?.Length > MaxText || manufacturer?.Length > 200 || tags.Length > MaxTags || tags.Any(t => t.Length > 100))
            return Results.BadRequest(new { error = $"Описание — до {MaxText} знаков, производитель — до 200, меток — до {MaxTags} по 100 знаков" });

        card.Description = description;
        card.Manufacturer = manufacturer;
        card.Tags = tags;
        card.UpdatedAt = time.GetUtcNow();
        var current = card.CurrentVersionId is { } currentId ? await db.Set<FamilyVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == currentId, ct) : null;
        card.SearchText = SearchText.Build(card, SearchText.ContentOf(current));
        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(http.User.Actor(), ScanApplier.UiEntity, card.Id, $"Семейства: изменены описание и метки «{card.Name}»"));
        return Results.Ok();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
