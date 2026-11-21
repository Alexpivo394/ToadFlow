using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Runs;

namespace ToadFlow.Server.Families.Search;

public sealed record FamilyHit(
    Guid Id,
    string Name,
    string Section,
    string RelativePath,
    string Category,
    int? RevitYear,
    int TypeCount,
    CardState State,
    bool HasPreview,
    DateTimeOffset UpdatedAt);

public sealed record FacetValue(string Value, int Count);

public sealed record FamilyFacets(IReadOnlyList<FacetValue> Sections, IReadOnlyList<FacetValue> Categories, IReadOnlyList<FacetValue> Years,
    IReadOnlyList<FacetValue> Placements);

/// <summary>Кто какие карточки видит: проектировщик — только утверждённые (и на проверке, если сам попросил), архив — никогда.</summary>
public static class CardVisibility
{
    public static bool IsDesigner(this ClaimsPrincipal user) => user.IsInRole(nameof(UserRole.Designer));

    public static IQueryable<FamilyCard> Visible(this IQueryable<FamilyCard> cards, ClaimsPrincipal user, CardState? state, bool unreviewed)
    {
        if (!user.IsDesigner()) return cards.Where(c => c.State == (state ?? CardState.Approved));
        return unreviewed ? cards.Where(c => c.State == CardState.Approved || c.State == CardState.InReview) : cards.Where(c => c.State == CardState.Approved);
    }

    public static bool CanSee(this ClaimsPrincipal user, FamilyCard card, bool unreviewed) =>
        !user.IsDesigner() || card.State == CardState.Approved || (unreviewed && card.State == CardState.InReview);
}

internal static class SearchEndpoints
{
    private const int DefaultPageSize = 40;
    private const int MaxPageSize = 200;
    private const int MaxWords = 10;

    public static void MapFamiliesSearch(this RouteGroupBuilder families)
    {
        families.MapGet("/search", SearchAsync);
        families.MapGet("/facets", FacetsAsync);
    }

    private static async Task<Page<FamilyHit>> SearchAsync(ToadFlowDb db, ClaimsPrincipal user, string? q, string? section, string? category, int? year,
        CardState? state, string? placement, bool? unreviewed, int? page, int? pageSize, CancellationToken ct)
    {
        var cards = db.Set<FamilyCard>().AsNoTracking().Visible(user, state, unreviewed == true);
        var patterns = (q ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().Take(MaxWords).Select(word => "%" + EscapeLike(word) + "%").ToList();
        foreach (var pattern in patterns) cards = cards.Where(c => EF.Functions.Like(c.SearchText, pattern, @"\"));
        if (!string.IsNullOrEmpty(section)) cards = cards.Where(c => c.Section == section);
        if (!string.IsNullOrEmpty(category)) cards = cards.Where(c => c.Category == category);
        if (year is { } revitYear) cards = cards.Where(c => c.RevitYear == revitYear);
        if (!string.IsNullOrEmpty(placement)) cards = cards.Where(c => c.Placement == placement);

        var total = await cards.CountAsync(ct);
        var ordered = patterns.Count > 0
            ? cards.OrderByDescending(NameMatches(patterns)).ThenByDescending(c => c.UpdatedAt)
            : cards.OrderByDescending(c => c.UpdatedAt);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(page ?? 1, 1);
        var versions = db.Set<FamilyVersion>();
        var items = await ordered.ThenBy(c => c.Name).ThenBy(c => c.Id).Skip((number - 1) * size).Take(size)
            .Select(c => new FamilyHit(c.Id, c.Name, c.Section, c.RelativePath, c.Category, c.RevitYear, c.TypeCount, c.State,
                versions.Any(v => v.Id == c.CurrentVersionId && v.HasPreview), c.UpdatedAt))
            .ToListAsync(ct);
        return new Page<FamilyHit>(items, total);
    }

    private static async Task<FamilyFacets> FacetsAsync(ToadFlowDb db, ClaimsPrincipal user, CardState? state, bool? unreviewed, CancellationToken ct)
    {
        var cards = db.Set<FamilyCard>().AsNoTracking().Visible(user, state, unreviewed == true);
        var sections = await cards.Where(c => c.Section != "").GroupBy(c => c.Section).Select(g => new FacetValue(g.Key, g.Count())).ToListAsync(ct);
        var categories = await cards.Where(c => c.Category != "").GroupBy(c => c.Category).Select(g => new FacetValue(g.Key, g.Count())).ToListAsync(ct);
        var years = await cards.Where(c => c.RevitYear != null).GroupBy(c => c.RevitYear!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var placements = await cards.Where(c => c.Placement != "").GroupBy(c => c.Placement).Select(g => new FacetValue(g.Key, g.Count())).ToListAsync(ct);
        return new FamilyFacets(Sort(sections), Sort(categories), years.OrderBy(y => y.Key).Select(y => new FacetValue(y.Key.ToString(), y.Count)).ToList(),
            Sort(placements));
    }

    private static List<FacetValue> Sort(List<FacetValue> values) =>
        values.OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Все слова запроса входят в имя: такие карточки — выше.</summary>
    private static Expression<Func<FamilyCard, bool>> NameMatches(IReadOnlyList<string> patterns)
    {
        var card = Expression.Parameter(typeof(FamilyCard), "c");
        var like = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;
        Expression body = Expression.Constant(true);
        foreach (var pattern in patterns)
        {
            // Через объект, а не константу: EF передаёт значение параметром запроса.
            var value = Expression.Property(Expression.Constant(new Box(pattern)), nameof(Box.Value));
            body = Expression.AndAlso(body, Expression.Call(like, Expression.Constant(EF.Functions), Expression.Property(card, nameof(FamilyCard.NameKey)), value,
                Expression.Constant(@"\")));
        }

        return Expression.Lambda<Func<FamilyCard, bool>>(body, card);
    }

    /// <summary>Запрос ищется буквально: знаки шаблона LIKE экранируются.</summary>
    private static string EscapeLike(string word) => word.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed class Box(string value)
    {
        public string Value { get; } = value;
    }
}
