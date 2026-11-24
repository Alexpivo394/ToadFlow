using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage.Query;

/// <summary>Строка парка: железо — последнее присланное, показатели — за период фильтра.</summary>
public sealed record MachineRow(string Key, string Name, string? Cpu, int? Cores, int? RamMb, string? Gpu, int? GpuMemoryMb, string DiskKind, int? DiskTotalGb,
    int? DiskFreeGb, string? Os, int[] RevitYears, string? ToadTools, DateTimeOffset? LastSeenAt, UsagePerson? LastUser, double? OpenMedianMs,
    double? SyncMedianMs, int Sessions, int Abnormal, double ActiveHours);

public sealed record MachinesView(UsageBasis Basis, IReadOnlyList<MachineRow> Rows);

public sealed record MachineUser(Guid Id, string Name, int Sessions);

/// <summary>Аварийная сессия: вылет, снятие процесса или выключение компьютера — различить нельзя. Конец — последняя отметка.</summary>
public sealed record AbnormalSession(Guid Id, DateTimeOffset StartedAt, DateTimeOffset SeenAt, UsagePerson User);

public sealed record MachineWeek(DateOnly Start, double? OpenMedianMs, double? SyncMedianMs);

public sealed record MachineCard(string Key, string Name, string? Cpu, int? Cores, int? RamMb, string? Gpu, int? GpuMemoryMb, string DiskKind, int? DiskTotalGb,
    int? DiskFreeGb, string? Os, int[] RevitYears, string? ToadTools, DateTimeOffset? FirstSeenAt, DateTimeOffset? LastSeenAt, UsagePerson? LastUser,
    double? OpenMedianMs, double? SyncMedianMs, int Sessions, int Abnormal, double ActiveHours, IReadOnlyList<MachineUser> Users,
    IReadOnlyList<AbnormalSession> AbnormalSessions, IReadOnlyList<MachineWeek> Weeks);

/// <summary>Экран «Машины»: парк компьютеров проектировщиков и карточка машины.</summary>
public static class MachineEndpoints
{
    public static void MapUsageMachines(this RouteGroupBuilder usage)
    {
        usage.MapGet("/machines", ListAsync);
        usage.MapGet("/machines/{key}", CardAsync);
    }

    private sealed record Stats(double? Open, double? Sync, int Sessions, int Abnormal, double ActiveHours);

    /// <summary>Показатели машин за период: медианы открытия и синхронизации, сессии, аварии, активные часы.</summary>
    private static async Task<Dictionary<string, Stats>> StatsAsync(UsageQuery query, UsageFilter filter, Guid[]? users, CancellationToken ct)
    {
        var db = query.Db;
        var args = new UsageSql.Args { ["from"] = filter.FromUtc, ["to"] = filter.ToUtc, ["dayFrom"] = filter.From, ["dayTo"] = filter.To };
        var ops = await UsageSql.QueryAsync(db, $"""
            SELECT o.machine_key,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY o.duration_ms) FILTER (WHERE o.kind = 'Open'),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY o.duration_ms) FILTER (WHERE o.kind = 'Sync')
            FROM usage_operations o
            WHERE o.started_at >= @from AND o.started_at < @to AND o.outcome = 'Succeeded' AND {filter.Where("o", users, "revit_year", args)}
            GROUP BY o.machine_key
            """, args, r => (Key: r.GetString(0), Open: r.NullableDouble(1), Sync: r.NullableDouble(2)), ct);
        var sessions = await UsageSql.QueryAsync(db, $"""
            SELECT s.machine_key, count(*)::int, count(*) FILTER (WHERE s."end" = 'Abnormal')::int
            FROM usage_sessions s
            WHERE s.started_at >= @from AND s.started_at < @to AND {filter.Where("s", users, "revit_year", args)}
            GROUP BY s.machine_key
            """, args, r => (Key: r.GetString(0), Count: r.Int(1), Abnormal: r.Int(2)), ct);
        var active = await UsageSql.QueryAsync(db, $"""
            SELECT d.machine_key, coalesce(sum(d.active_seconds), 0)::bigint
            FROM usage_session_days d
            WHERE d.day >= @dayFrom AND d.day <= @dayTo AND {filter.Where("d", users, null, args)}
            GROUP BY d.machine_key
            """, args, r => (Key: r.GetString(0), Seconds: r.Long(1)), ct);

        var keys = ops.Select(o => o.Key).Concat(sessions.Select(s => s.Key)).Concat(active.Select(a => a.Key)).Distinct();
        return keys.ToDictionary(key => key, key =>
        {
            var op = ops.FirstOrDefault(o => o.Key == key);
            var session = sessions.FirstOrDefault(s => s.Key == key);
            var seconds = active.FirstOrDefault(a => a.Key == key).Seconds;
            return new Stats(op.Open, op.Sync, session.Count, session.Abnormal, Math.Round(seconds / 3600.0, 1));
        });
    }

    /// <summary>Люди, работавшие на машинах за период (сессии и операции), — для «построено по N людям».</summary>
    private static async Task<List<(string Machine, Guid User, int Sessions)>> PeopleAsync(UsageQuery query, UsageFilter filter, Guid[]? users,
        CancellationToken ct)
    {
        var args = new UsageSql.Args { ["from"] = filter.FromUtc, ["to"] = filter.ToUtc };
        return await UsageSql.QueryAsync(query.Db, $"""
            SELECT machine_key, user_id, sum(sessions)::int FROM (
                SELECT s.machine_key, s.user_id, 1 AS sessions FROM usage_sessions s
                WHERE s.started_at >= @from AND s.started_at < @to AND {filter.Where("s", users, "revit_year", args)}
                UNION ALL
                SELECT o.machine_key, o.user_id, 0 FROM usage_operations o
                WHERE o.started_at >= @from AND o.started_at < @to AND {filter.Where("o", users, "revit_year", args)}
            ) x GROUP BY machine_key, user_id
            """, args, r => (r.GetString(0), r.GetGuid(1), r.Int(2)), ct);
    }

    private static async Task<MachinesView> ListAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var filter = await query.FilterAsync(request, ct);
        var users = await filter.UsersAsync(query.Db, ct);
        var stats = await StatsAsync(query, filter, users, ct);
        var people = await PeopleAsync(query, filter, users, ct);
        var known = await query.Db.Set<UsageMachine>().AsNoTracking().ToDictionaryAsync(m => m.Key, ct);
        var names = await query.PeopleAsync(ct);

        // Отбор по людям или году Revit — только машины, на которых они работали; без него — весь парк.
        var keys = stats.Keys.Concat(people.Select(p => p.Machine));
        if (users is null && filter.RevitYear is null) keys = keys.Concat(known.Keys);
        if (filter.Machine is { } only) keys = keys.Where(k => k == only);

        var rows = keys.Distinct().Select(key => Row(key, known.GetValueOrDefault(key), stats.GetValueOrDefault(key), names))
            .OrderByDescending(r => r.OpenMedianMs ?? -1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = rows.Select(r => r.Key).ToHashSet();
        var basis = new UsageBasis(people.Where(p => shown.Contains(p.Machine)).Select(p => p.User).Distinct().Count(), rows.Count);
        return new MachinesView(basis, rows);
    }

    private static MachineRow Row(string key, UsageMachine? machine, Stats? stats, Dictionary<Guid, string> people) => new(
        key, machine?.Name ?? key, machine?.Cpu, machine?.Cores, machine?.RamMb, machine?.Gpu, machine?.GpuMemoryMb, machine?.DiskKind ?? "Unknown",
        machine?.DiskTotalGb, machine?.DiskFreeGb, machine?.Os, machine?.RevitYears ?? [], machine?.ToadTools, machine?.LastSeenAt,
        machine?.LastUserId is { } user ? UsageQuery.Person(people, user) : null, stats?.Open, stats?.Sync, stats?.Sessions ?? 0, stats?.Abnormal ?? 0,
        stats?.ActiveHours ?? 0);

    private static async Task<IResult> CardAsync(string key, HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        key = key.ToLowerInvariant();
        var db = query.Db;
        var machine = await db.Set<UsageMachine>().AsNoTracking().SingleOrDefaultAsync(m => m.Key == key, ct);
        if (machine is null && !await db.Set<UsageSession>().AnyAsync(s => s.MachineKey == key, ct)
                            && !await db.Set<UsageOperation>().AnyAsync(o => o.MachineKey == key, ct))
            return Results.NotFound();

        var filter = await query.FilterAsync(request, ct) with { Machine = key };
        var users = await filter.UsersAsync(db, ct);
        var stats = (await StatsAsync(query, filter, users, ct)).GetValueOrDefault(key);
        var names = await query.PeopleAsync(ct);
        var row = Row(key, machine, stats, names);

        var workers = (await PeopleAsync(query, filter, users, ct))
            .Select(p => new MachineUser(p.User, UsageQuery.Person(names, p.User).Name, p.Sessions))
            .OrderByDescending(u => u.Sessions).ThenBy(u => u.Name).ToList();

        var args = new UsageSql.Args { ["from"] = filter.FromUtc, ["to"] = filter.ToUtc };
        var abnormal = (await UsageSql.QueryAsync(db, $"""
                SELECT s.id, s.started_at, s.seen_at, s.user_id FROM usage_sessions s
                WHERE s.started_at >= @from AND s.started_at < @to AND s."end" = 'Abnormal' AND {filter.Where("s", users, "revit_year", args)}
                ORDER BY s.started_at DESC
                """, args, r => (Id: r.GetGuid(0), Started: r.GetFieldValue<DateTimeOffset>(1), Seen: r.GetFieldValue<DateTimeOffset>(2), User: r.GetGuid(3)), ct))
            .Select(s => new AbnormalSession(s.Id, s.Started, s.Seen, UsageQuery.Person(names, s.User))).ToList();

        var weeks = (filter.Days + 6) / 7;
        var weekly = await UsageSql.QueryAsync(db, $"""
            SELECT floor(extract(epoch FROM (o.started_at - @from)) / 604800)::int AS week,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY o.duration_ms) FILTER (WHERE o.kind = 'Open'),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY o.duration_ms) FILTER (WHERE o.kind = 'Sync')
            FROM usage_operations o
            WHERE o.started_at >= @from AND o.started_at < @to AND o.outcome = 'Succeeded' AND {filter.Where("o", users, "revit_year", args)}
            GROUP BY week
            """, args, r => (Week: r.Int(0), Open: r.NullableDouble(1), Sync: r.NullableDouble(2)), ct);
        var series = Enumerable.Range(0, weeks).Select(i =>
        {
            var point = weekly.FirstOrDefault(w => w.Week == i);
            return new MachineWeek(filter.From.AddDays(7 * i), point.Open, point.Sync);
        }).ToList();

        return Results.Ok(new MachineCard(row.Key, row.Name, row.Cpu, row.Cores, row.RamMb, row.Gpu, row.GpuMemoryMb, row.DiskKind, row.DiskTotalGb,
            row.DiskFreeGb, row.Os, row.RevitYears, row.ToadTools, machine?.FirstSeenAt, row.LastSeenAt, row.LastUser, row.OpenMedianMs, row.SyncMedianMs,
            row.Sessions, row.Abnormal, row.ActiveHours, workers, abnormal, series));
    }
}
