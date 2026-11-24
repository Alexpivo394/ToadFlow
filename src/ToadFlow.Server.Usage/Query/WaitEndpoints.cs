using ToadFlow.Server.Usage.Ingest;

namespace ToadFlow.Server.Usage.Query;

/// <param name="Trend">Медиана успешных операций по неделям периода (от его начала); null — в неделю операций не было.</param>
public sealed record WaitRow(string Key, string Title, int Count, double? MedianMs, double? P90Ms, long TotalMs, int Failed, double?[] Trend);

public sealed record WaitsView(UsageBasis Basis, IReadOnlyList<WaitRow> Rows);

/// <param name="HostModel">У связи — ключ модели, в которую она загружена.</param>
public sealed record WaitCase(Guid Id, string Kind, DateTimeOffset StartedAt, long DurationMs, string Outcome, UsagePerson User, UsageMachineRef Machine,
    string ModelName, string ModelPath, string? HostModel, int? FreeMemoryMb, long? FileSizeBytes);

/// <summary>
///     Экран «Ожидание»: операции с моделями, сгруппированные по модели, машине, человеку, серверу Revit или модели-связи. Медиана,
///     90-й процентиль и сумма — только по успешным операциям; неуспешные (ошибка, отмена) считаются в <c>failed</c>.
/// </summary>
public static class WaitEndpoints
{
    public const int MaxCases = 200;

    private static readonly string[] Groups = ["model", "machine", "user", "server", "link"];

    public static void MapUsageWaits(this RouteGroupBuilder usage)
    {
        usage.MapGet("/waits", WaitsAsync);
        usage.MapGet("/waits/cases", CasesAsync);
    }

    /// <summary>Выборка операций экрана: CTE <c>ops</c> с ключом группы <c>gkey</c>.</summary>
    private static async Task<(string Cte, UsageSql.Args Args, string Group)> SelectionAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var filter = await query.FilterAsync(request, ct);
        var group = request.Query["group"].ToString() is { Length: > 0 } g ? g.ToLowerInvariant() : "model";
        if (!Groups.Contains(group)) throw new BadHttpRequestException($"Неизвестная группировка «{group}»");
        string? kind = null;
        if (request.Query["kind"].ToString() is { Length: > 0 } k)
            kind = UsageValues.OperationKinds.FirstOrDefault(known => known.Equals(k, StringComparison.OrdinalIgnoreCase))
                   ?? throw new BadHttpRequestException($"Неизвестный вид операции «{k}»");

        var args = new UsageSql.Args { ["from"] = filter.FromUtc, ["to"] = filter.ToUtc };
        var users = await filter.UsersAsync(query.Db, ct);
        var where = filter.Where("o", users, "revit_year", args);
        var kindCondition = group == "link" ? "o.kind = 'Link'" : kind is null ? "o.kind <> 'Link'" : "o.kind = @kind";
        if (group != "link" && kind is not null) args["kind"] = kind;
        var key = group switch
        {
            "machine" => "o.machine_key",
            "user" => "o.user_id::text",
            "server" => "o.server_host",
            _ => "o.model_key"
        };
        var server = group == "server" ? " AND o.server_host IS NOT NULL" : "";
        var cte = $"""
            WITH ops AS (
                SELECT o.*, {key} AS gkey FROM usage_operations o
                WHERE o.started_at >= @from AND o.started_at < @to AND {kindCondition} AND {where}{server}
            )
            """;
        args["weekFrom"] = filter.FromUtc;
        args["weeks"] = (filter.Days + 6) / 7;
        return (cte, args, group);
    }

    private static async Task<WaitsView> WaitsAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var (cte, args, group) = await SelectionAsync(request, query, ct);
        var db = query.Db;
        var weeks = (int)args["weeks"]!;

        var rows = await UsageSql.QueryAsync(db, cte + """
            SELECT gkey, (array_agg(model_name ORDER BY started_at DESC))[1], count(*)::int,
                   count(*) FILTER (WHERE outcome <> 'Succeeded')::int,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms) FILTER (WHERE outcome = 'Succeeded'),
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY duration_ms) FILTER (WHERE outcome = 'Succeeded'),
                   coalesce(sum(duration_ms) FILTER (WHERE outcome = 'Succeeded'), 0)::bigint
            FROM ops GROUP BY gkey
            """, args, r => new
        {
            Key = r.GetString(0), ModelName = r.GetString(1), Count = r.Int(2), Failed = r.Int(3), Median = r.NullableDouble(4), P90 = r.NullableDouble(5),
            Total = r.Long(6)
        }, ct);

        var trend = await UsageSql.QueryAsync(db, cte + """
            SELECT gkey, floor(extract(epoch FROM (started_at - @weekFrom)) / 604800)::int AS week,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms)
            FROM ops WHERE outcome = 'Succeeded' GROUP BY gkey, week
            """, args, r => (Key: r.GetString(0), Week: r.Int(1), Median: r.NullableDouble(2)), ct);
        var trendByKey = trend.ToLookup(t => t.Key);

        var basis = (await UsageSql.QueryAsync(db, cte + "SELECT count(DISTINCT user_id)::int, count(DISTINCT machine_key)::int FROM ops", args,
            r => new UsageBasis(r.Int(0), r.Int(1)), ct)).Single();

        var people = group == "user" ? await query.PeopleAsync(ct) : [];
        var machines = group == "machine" ? await query.MachinesAsync(ct) : [];
        return new WaitsView(basis, rows.Select(row =>
        {
            var title = group switch
            {
                "machine" => machines.GetValueOrDefault(row.Key, row.Key),
                "user" => UsageQuery.Person(people, Guid.Parse(row.Key)).Name,
                "server" => row.Key,
                _ => row.ModelName
            };
            var series = new double?[weeks];
            foreach (var point in trendByKey[row.Key])
                if (point.Week >= 0 && point.Week < weeks) series[point.Week] = point.Median;
            return new WaitRow(row.Key, title, row.Count, row.Median, row.P90, row.Total, row.Failed, series);
        }).OrderByDescending(r => r.TotalMs).ThenByDescending(r => r.Count).ToList());
    }

    private static async Task<IReadOnlyList<WaitCase>> CasesAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var (cte, args, _) = await SelectionAsync(request, query, ct);
        args["key"] = request.Query["key"].ToString();
        args["limit"] = MaxCases;
        var rows = await UsageSql.QueryAsync(query.Db, cte + """
            SELECT id, kind, started_at, duration_ms, outcome, user_id, machine_key, model_name, model_path, host_model_key, free_memory_mb, file_size_bytes
            FROM ops WHERE gkey = @key ORDER BY started_at DESC LIMIT @limit
            """, args, r => new
        {
            Id = r.GetGuid(0), Kind = r.GetString(1), StartedAt = r.GetFieldValue<DateTimeOffset>(2), Duration = r.Long(3), Outcome = r.GetString(4),
            User = r.GetGuid(5), Machine = r.GetString(6), Name = r.GetString(7), Path = r.GetString(8), Host = r.NullableString(9),
            Free = r.NullableInt(10), Size = r.NullableLong(11)
        }, ct);
        var people = await query.PeopleAsync(ct);
        var machines = await query.MachinesAsync(ct);
        return rows.Select(r => new WaitCase(r.Id, r.Kind, r.StartedAt, r.Duration, r.Outcome, UsageQuery.Person(people, r.User),
            UsageQuery.Machine(machines, r.Machine), r.Name, r.Path, r.Host, r.Free, r.Size)).ToList();
    }
}
