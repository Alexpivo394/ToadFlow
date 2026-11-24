using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Overview;

namespace ToadFlow.Server.Usage.Query;

/// <summary>По скольким людям и машинам построен ответ экрана.</summary>
public sealed record UsageBasis(int People, int Machines);

/// <summary>Человек в ответах чтения.</summary>
public sealed record UsagePerson(Guid Id, string Name);

/// <summary>Машина в ответах чтения: ключ и имя, как его прислал ToadTools.</summary>
public sealed record UsageMachineRef(string Key, string Name);

/// <summary>
///     Общий фильтр экранов: период <c>from</c>–<c>to</c> (дни <c>yyyy-MM-dd</c> включительно, в поясе из «Общих»; по умолчанию
///     последние <see cref="DefaultDays" /> дней), раздел, человек, машина, год Revit.
/// </summary>
public sealed record UsageFilter(DateOnly From, DateOnly To, string? Discipline, Guid? User, string? Machine, int? RevitYear, TimeZoneInfo Zone)
{
    public const int DefaultDays = 30;

    /// <summary>Строка отбора людей без разделов.</summary>
    public const string NoDiscipline = "Без раздела";

    /// <summary>Начало периода — момент UTC.</summary>
    public DateTimeOffset FromUtc => OverviewDays.Start(From, Zone);

    /// <summary>Конец периода (не включая) — начало дня после <see cref="To" />, момент UTC.</summary>
    public DateTimeOffset ToUtc => OverviewDays.Start(To.AddDays(1), Zone);

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>Разбирает строку запроса; неверная дата или число — 400.</summary>
    public static UsageFilter Parse(HttpRequest request, TimeZoneInfo zone, DateTimeOffset now)
    {
        var query = request.Query;
        var today = OverviewDays.DayOf(now, zone);
        var to = Date(query["to"]) ?? today;
        var from = Date(query["from"]) ?? to.AddDays(-(DefaultDays - 1));
        if (from > to) throw new BadHttpRequestException("Начало периода позже конца");

        Guid? user = null;
        if (Text(query["user"]) is { } userText)
            user = Guid.TryParse(userText, out var id) ? id : throw new BadHttpRequestException("Неверный id человека");
        int? year = null;
        if (Text(query["revitYear"]) is { } yearText)
            year = int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : throw new BadHttpRequestException("Неверный год Revit");

        return new UsageFilter(from, to, Text(query["discipline"]), user, Text(query["machine"])?.ToLowerInvariant(), year, zone);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? Date(string? value) => Text(value) is not { } text
        ? null
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new BadHttpRequestException($"Неверная дата «{text}»: нужна yyyy-MM-dd");

    /// <summary>Люди, подходящие под отбор человека и раздела; null — отбора по людям нет.</summary>
    public async Task<Guid[]?> UsersAsync(ToadFlowDb db, CancellationToken ct)
    {
        if (Discipline is null) return User is { } only ? [only] : null;

        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Disciplines }).ToListAsync(ct);
        return users.Where(u => Discipline == NoDiscipline ? u.Disciplines.Length == 0 : u.Disciplines.Contains(Discipline))
            .Where(u => User is null || u.Id == User)
            .Select(u => u.Id).ToArray();
    }

    /// <summary>
    ///     Условие SQL для таблицы с <c>user_id</c>, <c>machine_key</c> и (если <paramref name="yearColumn" /> задан) годом Revit, без
    ///     периода. Параметры — <c>@users</c>, <c>@machine</c>, <c>@year</c>.
    /// </summary>
    internal string Where(string alias, Guid[]? users, string? yearColumn, UsageSql.Args args)
    {
        var parts = new List<string>();
        if (users is not null)
        {
            parts.Add($"{alias}.user_id = ANY(@users)");
            args["users"] = users;
        }

        if (Machine is not null)
        {
            parts.Add($"{alias}.machine_key = @machine");
            args["machine"] = Machine;
        }

        if (RevitYear is not null)
        {
            parts.Add(yearColumn is null
                ? $"{alias}.session_id IN (SELECT id FROM usage_sessions WHERE revit_year = @year)"
                : $"{alias}.{yearColumn} = @year");
            args["year"] = RevitYear;
        }

        return parts.Count == 0 ? "TRUE" : string.Join(" AND ", parts);
    }
}
