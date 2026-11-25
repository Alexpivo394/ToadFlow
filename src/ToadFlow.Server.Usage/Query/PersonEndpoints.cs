using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage.Query;

public sealed record PersonUser(Guid Id, string Name, string Role, string? Position, string[] Disciplines);

public sealed record PersonDay(DateOnly Day, int ActiveSeconds, int Sessions);

public sealed record PersonWaitKind(string Kind, int Count, double? MedianMs);

public sealed record PersonWaits(long TotalMs, IReadOnlyList<PersonWaitKind> Rows);

public sealed record PersonCommand(string Source, string CommandId, string Name, int Count);

public sealed record PersonSpace(string Space, int Visits);

public sealed record PersonArea(string Area, int Calls);

public sealed record PersonView(PersonUser User, IReadOnlyList<UsageMachineRef> Machines, IReadOnlyList<PersonDay> Days, PersonWaits Waits,
    IReadOnlyList<PersonCommand> Commands, IReadOnlyList<PersonSpace> Web, IReadOnlyList<PersonArea> Plugins);

/// <summary>Карточка человека: его машины, активное время по дням, ожидания, команды, веб и плагины за период фильтра.</summary>
public static class PersonEndpoints
{
    public const int TopCommands = 20;

    public static void MapUsagePeople(this RouteGroupBuilder usage) => usage.MapGet("/people/{id:guid}", PersonAsync);

    private static async Task<IResult> PersonAsync(Guid id, HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var db = query.Db;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id && !u.IsPending).SingleOrDefaultAsync(ct);
        if (user is null) return Results.NotFound();

        // Отбор людей на карточке не действует: она всегда про этого человека.
        var filter = await query.FilterAsync(request, ct) with { User = null, Discipline = null };
        Guid[] only = [id];
        var operations = await SummaryEndpoints.Operations(db, filter, only).Select(o => new { o.Kind, o.Outcome, o.DurationMs, o.MachineKey, Nested = o.Kind == "Link" && o.HostModelKey != null })
            .ToListAsync(ct);
        var sessions = await SummaryEndpoints.Sessions(db, filter, only).Select(s => new { s.StartedAt, s.MachineKey }).ToListAsync(ct);
        var activity = await db.Set<UsageSessionDay>().AsNoTracking()
            .Where(d => d.UserId == id && d.Day >= filter.From && d.Day <= filter.To && (filter.Machine == null || d.MachineKey == filter.Machine))
            .GroupBy(d => d.Day).Select(g => new { Day = g.Key, Seconds = g.Sum(d => d.ActiveSeconds) }).ToListAsync(ct);
        var commands = await db.Set<UsageCommandDay>().AsNoTracking()
            .Where(c => c.UserId == id && c.Day >= filter.From && c.Day <= filter.To && (filter.Machine == null || c.MachineKey == filter.Machine))
            .GroupBy(c => new { c.Source, c.CommandId }).Select(g => new { g.Key.Source, g.Key.CommandId, Name = g.Max(c => c.Name), Count = g.Sum(c => c.Count) })
            .OrderByDescending(c => c.Count).ThenBy(c => c.CommandId).Take(TopCommands).ToListAsync(ct);
        var web = await db.Set<UsageWebDay>().AsNoTracking().Where(w => w.UserId == id && w.Day >= filter.From && w.Day <= filter.To)
            .GroupBy(w => w.Space).Select(g => new { Space = g.Key, Visits = g.Sum(w => w.Count) }).OrderByDescending(s => s.Visits).ToListAsync(ct);
        var plugins = await db.Set<UsagePluginDay>().AsNoTracking().Where(p => p.UserId == id && p.Day >= filter.From && p.Day <= filter.To)
            .GroupBy(p => p.Area).Select(g => new { Area = g.Key, Calls = g.Sum(p => p.Count) }).OrderByDescending(a => a.Calls).ToListAsync(ct);

        var machines = await query.MachinesAsync(ct);
        var machineRefs = operations.Select(o => o.MachineKey).Concat(sessions.Select(s => s.MachineKey)).Distinct()
            .Select(key => UsageQuery.Machine(machines, key)).OrderBy(m => m.Name).ToList();
        var sessionsByDay = sessions.GroupBy(s => OverviewDays.DayOf(s.StartedAt, filter.Zone)).ToDictionary(g => g.Key, g => g.Count());
        var days = activity.Select(a => a.Day).Concat(sessionsByDay.Keys).Distinct().Order()
            .Select(day => new PersonDay(day, activity.FirstOrDefault(a => a.Day == day)?.Seconds ?? 0, sessionsByDay.GetValueOrDefault(day))).ToList();
        var succeeded = operations.Where(o => o.Outcome == "Succeeded").ToList();
        var waits = new PersonWaits(succeeded.Where(o => !o.Nested).Sum(o => o.DurationMs),
            operations.GroupBy(o => o.Kind).Select(g =>
            {
                var ok = g.Where(o => o.Outcome == "Succeeded").Select(o => o.DurationMs).ToList();
                return new PersonWaitKind(g.Key, g.Count(), ok.Count == 0 ? null : SummaryEndpoints.Median(ok));
            }).OrderByDescending(r => r.Count).ToList());

        return Results.Ok(new PersonView(
            new PersonUser(user.Id, user.DisplayName, user.Role.ToString(), user.Position, user.Disciplines), machineRefs, days, waits,
            commands.Select(c => new PersonCommand(c.Source, c.CommandId, c.Name ?? c.CommandId, c.Count)).ToList(),
            web.Select(w => new PersonSpace(w.Space, w.Visits)).ToList(), plugins.Select(p => new PersonArea(p.Area, p.Calls)).ToList()));
    }
}
