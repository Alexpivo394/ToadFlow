using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage.Query;

public sealed record UsageFilterPerson(Guid Id, string Name, string[] Disciplines);

/// <summary>Варианты отборов общего верха экранов.</summary>
public sealed record UsageFilters(UsageFilterPerson[] People, UsageMachineRef[] Machines, string[] Disciplines, int[] RevitYears);

public static class FilterEndpoints
{
    private static readonly StringComparer Russian = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), false);

    /// <summary>
    ///     <c>GET /api/usage/filters</c> — люди (учётки, кроме ожидающих регистрации), машины, разделы людей (последний —
    ///     «Без раздела»), годы Revit из сессий.
    /// </summary>
    public static void MapUsageFilters(this RouteGroupBuilder usage) =>
        usage.MapGet("/filters", async (UsageQuery query, CancellationToken ct) =>
        {
            var db = query.Db;
            var people = (await db.Users.AsNoTracking().Where(u => !u.IsPending).Select(u => new UsageFilterPerson(u.Id, u.DisplayName, u.Disciplines))
                .ToListAsync(ct)).OrderBy(p => p.Name, Russian).ToArray();
            var machines = (await db.Set<UsageMachine>().AsNoTracking().Select(m => new UsageMachineRef(m.Key, m.Name)).ToListAsync(ct))
                .OrderBy(m => m.Name, Russian).ToArray();
            var disciplines = people.SelectMany(p => p.Disciplines).Distinct().Order(Russian).Append(UsageFilter.NoDiscipline).ToArray();
            var years = await db.Set<UsageSession>().AsNoTracking().Where(s => s.RevitYear > 0).Select(s => s.RevitYear).Distinct().OrderBy(y => y)
                .ToArrayAsync(ct);
            return new UsageFilters(people, machines, disciplines, years);
        });
}
