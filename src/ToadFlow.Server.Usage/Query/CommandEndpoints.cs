using ToadFlow.Server.Overview;
using ToadFlow.Server.Usage.Ingest;

namespace ToadFlow.Server.Usage.Query;

/// <param name="AvgMs">Средняя длительность запуска — только у команд ToadTools; запуск с ленты длительности не знает.</param>
public sealed record CommandRow(string Source, string CommandId, string Name, string? Tab, int Count, int People, int Failed, int Cancelled, double? AvgMs);

public sealed record UnusedCommand(string CommandId, string Name, DateOnly? LastRunAt);

/// <param name="RibbonOnly">Штатные команды считаются только по ленте: горячие клавиши и повтор команды сборщик не ловит.</param>
/// <param name="Unused">Команды ToadTools, которые запускали когда-то, но не за последние <see cref="CommandEndpoints.UnusedDays" /> дней.</param>
public sealed record CommandsView(UsageBasis Basis, bool RibbonOnly, IReadOnlyList<CommandRow> Rows, IReadOnlyList<UnusedCommand> Unused);

public sealed record CommandWeek(DateOnly Start, int Count, int People);

public sealed record CommandUser(Guid Id, string Name, int Count);

public sealed record CommandCard(string Name, IReadOnlyList<CommandWeek> Weeks, IReadOnlyList<CommandUser> Users);

/// <summary>Экран «Инструменты»: какими командами пользуются, кто и как часто; команды ToadTools, которые никто не запускает.</summary>
public static class CommandEndpoints
{
    public const int UnusedDays = 90;

    /// <summary>Итог пробы: сборщик ловит штатные команды только по нажатиям ленты.</summary>
    public const bool RibbonOnly = true;

    public static void MapUsageCommands(this RouteGroupBuilder usage)
    {
        usage.MapGet("/commands", ListAsync);
        usage.MapGet("/commands/{source}/{commandId}", CardAsync);
    }

    private static async Task<CommandsView> ListAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var filter = await query.FilterAsync(request, ct);
        var users = await filter.UsersAsync(query.Db, ct);
        var args = new UsageSql.Args { ["dayFrom"] = filter.From, ["dayTo"] = filter.To };
        var where = filter.Where("c", users, null, args);
        if (request.Query["source"].ToString() is { Length: > 0 } source)
        {
            args["source"] = UsageValues.CommandSources.FirstOrDefault(s => s.Equals(source, StringComparison.OrdinalIgnoreCase))
                             ?? throw new BadHttpRequestException($"Неизвестный источник «{source}»");
            where += " AND c.source = @source";
        }

        var selection = $"FROM usage_command_days c WHERE c.day >= @dayFrom AND c.day <= @dayTo AND {where}";
        var rows = await UsageSql.QueryAsync(query.Db, $"""
            SELECT c.source, c.command_id, (array_agg(c.name ORDER BY c.day DESC))[1], (array_agg(c.tab ORDER BY c.day DESC))[1],
                   sum(c.count)::int, count(DISTINCT c.user_id)::int, sum(c.failed)::int, sum(c.cancelled)::int, sum(c.duration_ms)::bigint
            {selection}
            GROUP BY c.source, c.command_id
            ORDER BY sum(c.count) DESC, c.command_id
            """, args, r =>
        {
            var count = r.Int(4);
            var duration = r.Long(8);
            var source = r.GetString(0);
            return new CommandRow(source, r.GetString(1), r.GetString(2), r.NullableString(3), count, r.Int(5), r.Int(6), r.Int(7),
                source == "ToadTools" && count > 0 ? Math.Round((double)duration / count) : null);
        }, ct);
        var basis = (await UsageSql.QueryAsync(query.Db, $"SELECT count(DISTINCT c.user_id)::int, count(DISTINCT c.machine_key)::int {selection}", args,
            r => new UsageBasis(r.Int(0), r.Int(1)), ct)).Single();

        // «Не запускали» — от сегодняшнего дня, а не от периода фильтра.
        var today = OverviewDays.DayOf(query.Now, await query.ZoneAsync(ct));
        var unused = await UsageSql.QueryAsync(query.Db, """
            SELECT command_id, (array_agg(name ORDER BY day DESC))[1], max(day) FROM usage_command_days
            WHERE source = 'ToadTools' GROUP BY command_id HAVING max(day) < @since ORDER BY max(day), command_id
            """, new UsageSql.Args { ["since"] = today.AddDays(-(UnusedDays - 1)) },
            r => new UnusedCommand(r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2)), ct);

        return new CommandsView(basis, RibbonOnly, rows, unused);
    }

    private static async Task<IResult> CardAsync(string source, string commandId, HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var any = await UsageSql.QueryAsync(query.Db,
            "SELECT (array_agg(name ORDER BY day DESC))[1] FROM usage_command_days WHERE source = @source AND command_id = @id HAVING count(*) > 0",
            new UsageSql.Args { ["source"] = source, ["id"] = commandId }, r => r.GetString(0), ct);
        if (any.Count == 0) return Results.NotFound();

        var filter = await query.FilterAsync(request, ct);
        var users = await filter.UsersAsync(query.Db, ct);
        var args = new UsageSql.Args { ["dayFrom"] = filter.From, ["dayTo"] = filter.To, ["source"] = source, ["id"] = commandId };
        var selection =
            $"FROM usage_command_days c WHERE c.day >= @dayFrom AND c.day <= @dayTo AND c.source = @source AND c.command_id = @id AND {filter.Where("c", users, null, args)}";
        var weekly = await UsageSql.QueryAsync(query.Db, $"""
            SELECT ((c.day - @dayFrom) / 7)::int AS week, sum(c.count)::int, count(DISTINCT c.user_id)::int {selection} GROUP BY week
            """, args, r => (Week: r.Int(0), Count: r.Int(1), People: r.Int(2)), ct);
        var weeks = Enumerable.Range(0, (filter.Days + 6) / 7).Select(i =>
        {
            var point = weekly.FirstOrDefault(w => w.Week == i);
            return new CommandWeek(filter.From.AddDays(7 * i), point.Count, point.People);
        }).ToList();
        var names = await query.PeopleAsync(ct);
        var people = (await UsageSql.QueryAsync(query.Db, $"SELECT c.user_id, sum(c.count)::int {selection} GROUP BY c.user_id ORDER BY sum(c.count) DESC",
                args, r => (Id: r.GetGuid(0), Count: r.Int(1)), ct))
            .Select(u => new CommandUser(u.Id, UsageQuery.Person(names, u.Id).Name, u.Count)).ToList();
        return Results.Ok(new CommandCard(any[0], weeks, people));
    }
}
