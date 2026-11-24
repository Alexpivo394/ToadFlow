using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage.Query;

/// <param name="WaitMs">Сколько ждали: сумма успешных операций всех видов.</param>
/// <param name="People">Люди, у которых за период была сессия Revit.</param>
public sealed record SummaryTotals(long WaitMs, int AbnormalSessions, int People, int ToadToolsRuns);

/// <param name="SyncMs">Синхронизация и «Обновить до последней» — обмен с хранилищем.</param>
public sealed record SummaryDay(DateOnly Day, long OpenMs, long LinkMs, long SyncMs, long SaveMs);

public sealed record SlowModel(string Key, string Title, double MedianMs, int Count);

public sealed record SlowMachine(string Key, string Name, long TotalMs);

public sealed record CrashingMachine(string Key, string Name, int Abnormal);

public sealed record SummaryView(UsageBasis Basis, SummaryTotals Totals, SummaryTotals Previous, IReadOnlyList<SummaryDay> Days,
    IReadOnlyList<SlowModel> SlowModels, IReadOnlyList<SlowMachine> SlowMachines, IReadOnlyList<CrashingMachine> CrashingMachines);

/// <summary>«Обзор» модуля: четыре итога с прошлым периодом той же длины, ожидание по дням и «Что болит» — по пять строк.</summary>
public static class SummaryEndpoints
{
    public const int Top = 5;

    public static void MapUsageSummary(this RouteGroupBuilder usage) => usage.MapGet("/summary", SummaryAsync);

    private static async Task<SummaryView> SummaryAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var db = query.Db;
        var filter = await query.FilterAsync(request, ct);
        var users = await filter.UsersAsync(db, ct);
        var previous = filter with { From = filter.From.AddDays(-filter.Days), To = filter.From.AddDays(-1) };

        var operations = await Operations(db, filter, users).Where(o => o.Outcome == "Succeeded")
            .Select(o => new { o.Kind, o.StartedAt, o.DurationMs, o.ModelKey, o.ModelName, o.MachineKey, o.UserId }).ToListAsync(ct);
        var sessions = await Sessions(db, filter, users).Select(s => new { s.UserId, s.MachineKey, s.End }).ToListAsync(ct);
        var totals = await TotalsAsync(db, filter, users, ct);
        var before = await TotalsAsync(db, previous, users, ct);

        var byDay = operations.ToLookup(o => OverviewDays.DayOf(o.StartedAt, filter.Zone));
        var days = Enumerable.Range(0, filter.Days).Select(i =>
        {
            var day = filter.From.AddDays(i);
            var ops = byDay[day].ToList();
            long Sum(params string[] kinds) => ops.Where(o => kinds.Contains(o.Kind)).Sum(o => o.DurationMs);
            return new SummaryDay(day, Sum("Open"), Sum("Link"), Sum("Sync", "Reload"), Sum("Save"));
        }).ToList();

        var machines = await query.MachinesAsync(ct);
        var slowModels = operations.Where(o => o.Kind == "Open").GroupBy(o => o.ModelKey)
            .Select(g => new SlowModel(g.Key, g.OrderByDescending(o => o.StartedAt).First().ModelName, Median(g.Select(o => o.DurationMs)), g.Count()))
            .OrderByDescending(m => m.MedianMs).Take(Top).ToList();
        var slowMachines = operations.GroupBy(o => o.MachineKey)
            .Select(g => new SlowMachine(g.Key, machines.GetValueOrDefault(g.Key, g.Key), g.Sum(o => o.DurationMs)))
            .OrderByDescending(m => m.TotalMs).Take(Top).ToList();
        var crashing = sessions.Where(s => s.End == "Abnormal").GroupBy(s => s.MachineKey)
            .Select(g => new CrashingMachine(g.Key, machines.GetValueOrDefault(g.Key, g.Key), g.Count()))
            .OrderByDescending(m => m.Abnormal).Take(Top).ToList();

        var basis = new UsageBasis(operations.Select(o => o.UserId).Concat(sessions.Select(s => s.UserId)).Distinct().Count(),
            operations.Select(o => o.MachineKey).Concat(sessions.Select(s => s.MachineKey)).Distinct().Count());
        return new SummaryView(basis, totals, before, days, slowModels, slowMachines, crashing);
    }

    internal static IQueryable<UsageOperation> Operations(ToadFlowDb db, UsageFilter filter, Guid[]? users)
    {
        var from = filter.FromUtc;
        var to = filter.ToUtc;
        var query = db.Set<UsageOperation>().AsNoTracking().Where(o => o.StartedAt >= from && o.StartedAt < to);
        if (users is not null) query = query.Where(o => users.Contains(o.UserId));
        if (filter.Machine is { } machine) query = query.Where(o => o.MachineKey == machine);
        if (filter.RevitYear is { } year) query = query.Where(o => o.RevitYear == year);
        return query;
    }

    /// <summary>Сессии, которые шли в период: начались до его конца и были на связи после его начала.</summary>
    internal static IQueryable<UsageSession> Sessions(ToadFlowDb db, UsageFilter filter, Guid[]? users)
    {
        var from = filter.FromUtc;
        var to = filter.ToUtc;
        var query = db.Set<UsageSession>().AsNoTracking().Where(s => s.StartedAt < to && s.SeenAt >= from);
        if (users is not null) query = query.Where(s => users.Contains(s.UserId));
        if (filter.Machine is { } machine) query = query.Where(s => s.MachineKey == machine);
        if (filter.RevitYear is { } year) query = query.Where(s => s.RevitYear == year);
        return query;
    }

    private static async Task<SummaryTotals> TotalsAsync(ToadFlowDb db, UsageFilter filter, Guid[]? users, CancellationToken ct)
    {
        var wait = await Operations(db, filter, users).Where(o => o.Outcome == "Succeeded").SumAsync(o => (long?)o.DurationMs, ct) ?? 0;
        var from = filter.FromUtc;
        var to = filter.ToUtc;
        var abnormal = await Sessions(db, filter, users).CountAsync(s => s.End == "Abnormal" && s.StartedAt >= from && s.StartedAt < to, ct);
        var people = await Sessions(db, filter, users).Select(s => s.UserId).Distinct().CountAsync(ct);
        var commands = db.Set<UsageCommandDay>().AsNoTracking().Where(c => c.Source == "ToadTools" && c.Day >= filter.From && c.Day <= filter.To);
        if (users is not null) commands = commands.Where(c => users.Contains(c.UserId));
        if (filter.Machine is { } machine) commands = commands.Where(c => c.MachineKey == machine);
        var runs = await commands.SumAsync(c => (int?)c.Count, ct) ?? 0;
        return new SummaryTotals(wait, abnormal, people, runs);
    }

    internal static double Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
