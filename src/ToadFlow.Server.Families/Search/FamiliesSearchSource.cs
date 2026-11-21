using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Search;

namespace ToadFlow.Server.Families.Search;

/// <summary>Быстрый поиск: семейства, видимые человеку в каталоге, и заявки — по номеру или тексту.</summary>
internal sealed class FamiliesSearchSource : ISearchSource
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(IServiceProvider services, ClaimsPrincipal user, string q, CancellationToken ct)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var like = GlobalSearch.Like(q.ToLowerInvariant());
        var hits = await db.Set<FamilyCard>().AsNoTracking().Visible(user, null, false).Where(c => EF.Functions.Like(c.SearchText, like, @"\"))
            .OrderByDescending(c => c.LoadCount).ThenBy(c => c.Name).Take(GlobalSearch.PerKind)
            .Select(c => new SearchHit("family", c.Name, c.Section.Length > 0 ? $"Семейство · {c.Section}" : "Семейство", "/families/catalog?card=" + c.Id))
            .ToListAsync(ct);

        var number = int.TryParse(q.TrimStart('№', ' '), out var n) ? n : (int?)null;
        var requests = await db.Set<FamilyRequest>().AsNoTracking().Where(r => r.Number == number || EF.Functions.Like(r.SearchText, like, @"\"))
            .OrderBy(r => r.Number == number ? 0 : 1).ThenByDescending(r => r.UpdatedAt).Take(GlobalSearch.PerKind)
            .Select(r => new { r.Id, r.Number, r.Title, r.State }).ToListAsync(ct);
        hits.AddRange(requests.Select(r => new SearchHit("request", $"№ {r.Number} · {r.Title}", "Заявка", $"/families/requests/{r.Id}")));
        return hits;
    }
}
