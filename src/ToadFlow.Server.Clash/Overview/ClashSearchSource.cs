using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Search;

namespace ToadFlow.Server.Clash.Overview;

/// <summary>Быстрый поиск: утверждённые проверки (части) коллизий — от наблюдателя.</summary>
internal sealed class ClashSearchSource : ISearchSource
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(IServiceProvider services, ClaimsPrincipal user, string q, CancellationToken ct)
    {
        if (user.IsInRole(nameof(UserRole.Designer))) return [];
        var like = GlobalSearch.Like(q);
        return await services.GetRequiredService<ToadFlowDb>().Set<ClashPart>().AsNoTracking()
            .Where(p => EF.Functions.ILike(p.Name, like, @"\") || (p.Object != null && EF.Functions.ILike(p.Object, like, @"\")))
            .OrderBy(p => p.Name).Take(GlobalSearch.PerKind)
            .Select(p => new SearchHit("part", p.Name, p.Object != null ? $"Проверка коллизий · {p.Object}" : "Проверка коллизий",
                p.ApprovedVersion != null ? $"/clash/checks/{p.Id}" : $"/clash/setup/parts/{p.Id}"))
            .ToListAsync(ct);
    }
}
