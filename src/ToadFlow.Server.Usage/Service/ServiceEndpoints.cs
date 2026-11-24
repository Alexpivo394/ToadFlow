using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Usage.Query;

namespace ToadFlow.Server.Usage.Service;

public sealed record WebDay(DateOnly Day, int People);

public sealed record WebSpace(string Space, int Visits, int People);

public sealed record WebPage(string Space, string Page, int Visits, int People);

public sealed record WebRole(string Role, int People, int Visits);

public sealed record WebUsage(IReadOnlyList<WebDay> Days, IReadOnlyList<WebSpace> Spaces, IReadOnlyList<WebPage> Pages, IReadOnlyList<WebRole> Roles);

public sealed record PluginArea(string Area, int Calls, int People);

public sealed record PluginUserArea(string Area, int Calls);

public sealed record PluginUser(Guid Id, string Name, IReadOnlyList<PluginUserArea> Areas);

public sealed record PluginUsage(IReadOnlyList<PluginArea> Areas, IReadOnlyList<PluginUser> Users);

public sealed record ServiceView(UsageBasis Basis, WebUsage Web, PluginUsage Plugins);

/// <summary>Экран «Сервис»: кто и как пользуется вебом и плагинами. Отборы — период, раздел и человек; машина и год не действуют.</summary>
public static class ServiceEndpoints
{
    public static void MapUsageService(this RouteGroupBuilder usage) => usage.MapGet("/service", ServiceAsync);

    private static async Task<ServiceView> ServiceAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var db = query.Db;
        var filter = await query.FilterAsync(request, ct) with { Machine = null, RevitYear = null };
        var users = await filter.UsersAsync(db, ct);
        var web = db.Set<Data.UsageWebDay>().AsNoTracking().Where(w => w.Day >= filter.From && w.Day <= filter.To);
        var plugins = db.Set<Data.UsagePluginDay>().AsNoTracking().Where(p => p.Day >= filter.From && p.Day <= filter.To);
        if (users is not null)
        {
            web = web.Where(w => users.Contains(w.UserId));
            plugins = plugins.Where(p => users.Contains(p.UserId));
        }

        var webRows = await web.ToListAsync(ct);
        var pluginRows = await plugins.ToListAsync(ct);
        var roles = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Role.ToString(), ct);
        var names = await query.PeopleAsync(ct);

        var webView = new WebUsage(
            webRows.GroupBy(w => w.Day).OrderBy(g => g.Key).Select(g => new WebDay(g.Key, g.Select(w => w.UserId).Distinct().Count())).ToList(),
            webRows.GroupBy(w => w.Space).Select(g => new WebSpace(g.Key, g.Sum(w => w.Count), g.Select(w => w.UserId).Distinct().Count()))
                .OrderByDescending(s => s.Visits).ToList(),
            webRows.GroupBy(w => (w.Space, w.Page)).Select(g => new WebPage(g.Key.Space, g.Key.Page, g.Sum(w => w.Count), g.Select(w => w.UserId).Distinct().Count()))
                .OrderByDescending(p => p.Visits).ToList(),
            webRows.GroupBy(w => roles.GetValueOrDefault(w.UserId, "—"))
                .Select(g => new WebRole(g.Key, g.Select(w => w.UserId).Distinct().Count(), g.Sum(w => w.Count))).OrderByDescending(r => r.Visits).ToList());
        var pluginView = new PluginUsage(
            pluginRows.GroupBy(p => p.Area).Select(g => new PluginArea(g.Key, g.Sum(p => p.Count), g.Select(p => p.UserId).Distinct().Count()))
                .OrderByDescending(a => a.Calls).ToList(),
            pluginRows.GroupBy(p => p.UserId)
                .Select(g => new PluginUser(g.Key, UsageQuery.Person(names, g.Key).Name,
                    g.GroupBy(p => p.Area).Select(a => new PluginUserArea(a.Key, a.Sum(p => p.Count))).OrderByDescending(a => a.Calls).ToList()))
                .OrderByDescending(u => u.Areas.Sum(a => a.Calls)).ToList());

        var people = webRows.Select(w => w.UserId).Concat(pluginRows.Select(p => p.UserId)).Distinct().Count();
        return new ServiceView(new UsageBasis(people, 0), webView, pluginView);
    }
}
