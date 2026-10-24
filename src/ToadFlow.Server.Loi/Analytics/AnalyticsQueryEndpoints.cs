using System.Text;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Loi.Analytics;

/// <summary>Фильтры аналитики. Пустое поле — без фильтра; <c>(нет)</c> — модели, у которых поле не распознано.</summary>
public sealed record AnalyticsFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Customer,
    string? Object,
    string? Discipline,
    string? Part,
    string? Profile,
    LoiCheckSource? Source,
    string? Search)
{
    public const string None = "(нет)";
}

public sealed record AnalyticsFilters(string[] Customers, AnalyticsObject[] Objects, string[] Disciplines, string[] Parts, string[] Profiles,
    DateTimeOffset? First, DateTimeOffset? Last);

public sealed record AnalyticsObject(string Name, string? Customer);

/// <summary>Сводка по группе моделей: доли — по последней проверке каждой модели.</summary>
public sealed record GroupStats(string? Key, string? Customer, int Models, double? FillRate, double? Completeness);

public sealed record MatrixCell(string? Object, string? Discipline, int Models, double? FillRate);

public sealed record TrendPoint(DateOnly Bucket, string? Series, int Checks, double? FillRate, double? Completeness);

public sealed record ModelRow(
    string ModelKey,
    string ModelName,
    string ModelPath,
    string? Customer,
    string? Object,
    string? Discipline,
    string? Part,
    DateTimeOffset CheckedAt,
    LoiCheckSource Source,
    string ProfileName,
    int Elements,
    double? FillRate,
    double? Completeness,
    double? PreviousFillRate,
    bool ProfileChanged);

public sealed record ParameterRow(string Name, int Models, int Total, int Filled, int Empty, int Missing, double? FillRate, string[] WorstModels);

/// <param name="Granularity"><c>day</c> или <c>week</c> — шаг графика.</param>
public sealed record AnalyticsOverview(
    int Models,
    int Checks,
    int ManualChecks,
    double? FillRate,
    double? Completeness,
    GroupStats[] ByObject,
    GroupStats[] ByDiscipline,
    MatrixCell[] Matrix,
    string Granularity,
    TrendPoint[] Trend,
    TrendPoint[] TrendByObject,
    ModelRow[] WorstModels,
    ParameterRow[] WorstParameters);

public sealed record CheckPoint(Guid Id, DateTimeOffset CheckedAt, LoiCheckSource Source, string? UserName, string ProfileName, string ProfileFingerprint,
    int Elements, double? FillRate, double? Completeness);

public sealed record RulePoint(Guid CheckId, int Position, string Title, int Elements, double? FillRate, double? Completeness);

public sealed record ParameterPoint(string Rule, string Name, int Filled, int Empty, int Missing, double? FillRate);

public sealed record ModelHistory(ModelRow Model, CheckPoint[] Checks, RulePoint[] Rules, ParameterPoint[] Parameters);

/// <summary>
///     Запросы страницы аналитики. «Состояние» группы — по последней проверке каждой модели в периоде; доли взвешены
///     (сумма заполненных к сумме проверенных), чтобы маленькая модель не весила как большая.
/// </summary>
internal static class AnalyticsQueryEndpoints
{
    private const int TopCount = 10;

    public static void MapLoiAnalyticsQueries(this RouteGroupBuilder loi)
    {
        var analytics = loi.MapGroup("/analytics");
        analytics.MapGet("/filters", FiltersAsync);
        analytics.MapGet("/overview", OverviewAsync);
        analytics.MapGet("/models", async ([AsParameters] AnalyticsFilter filter, ToadFlowDb db, CancellationToken ct) =>
            (await StateAsync(db, filter, ct)).Rows.OrderBy(r => r.Object).ThenBy(r => r.Discipline).ThenBy(r => r.ModelName).ToList());
        analytics.MapGet("/model", HistoryAsync);
        analytics.MapGet("/parameters", async ([AsParameters] AnalyticsFilter filter, ToadFlowDb db, CancellationToken ct) =>
            await ParametersAsync(db, await StateAsync(db, filter, ct), null, ct));
        analytics.MapGet("/models.csv", ExportAsync);
    }

    private static async Task<AnalyticsFilters> FiltersAsync(ToadFlowDb db, CancellationToken ct)
    {
        var checks = db.Set<LoiCheck>().AsNoTracking();
        var objects = await checks.Where(c => c.Object != null).Select(c => new { c.Object, c.Customer }).Distinct().ToListAsync(ct);
        return new AnalyticsFilters(
            await Distinct(checks.Select(c => c.Customer), ct),
            objects.GroupBy(o => o.Object!).Select(g => new AnalyticsObject(g.Key, g.Select(o => o.Customer).FirstOrDefault(c => c != null)))
                .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            await Distinct(checks.Select(c => c.Discipline), ct),
            await Distinct(checks.Select(c => c.Part), ct),
            await Distinct(checks.Select(c => c.ProfileName), ct),
            await checks.MinAsync(c => (DateTimeOffset?)c.CheckedAt, ct),
            await checks.MaxAsync(c => (DateTimeOffset?)c.CheckedAt, ct));
    }

    private static async Task<AnalyticsOverview> OverviewAsync([AsParameters] AnalyticsFilter filter, ToadFlowDb db, ISettingsStore settings, CancellationToken ct)
    {
        var state = await StateAsync(db, filter, ct);
        var latest = state.Latest;
        var timeZone = (await settings.GetAsync(ct)).TimeZone;
        var period = state.InPeriod;
        var first = period.Count == 0 ? DateTimeOffset.UtcNow : period.Min(c => c.CheckedAt);
        var last = period.Count == 0 ? DateTimeOffset.UtcNow : period.Max(c => c.CheckedAt);
        var weekly = (filter.To ?? last) - (filter.From ?? first) > TimeSpan.FromDays(62);

        DateOnly Bucket(DateTimeOffset at)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timeZone).DateTime);
            return weekly ? day.AddDays(-(((int)day.DayOfWeek + 6) % 7)) : day;
        }

        var topObjects = latest.GroupBy(c => c.Object).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key).ToHashSet();
        return new AnalyticsOverview(
            latest.Count,
            period.Count,
            period.Count(c => c.Source == LoiCheckSource.Manual),
            Ratio(latest.Sum(c => c.Filled), latest.Sum(c => c.Values)),
            Ratio(latest.Sum(c => c.CompleteElements), latest.Sum(c => c.Elements)),
            Group(latest, c => c.Object).ToArray(),
            Group(latest, c => c.Discipline).ToArray(),
            latest.GroupBy(c => (c.Object, c.Discipline))
                .Select(g => new MatrixCell(g.Key.Object, g.Key.Discipline, g.Count(), Ratio(g.Sum(c => c.Filled), g.Sum(c => c.Values)))).ToArray(),
            weekly ? "week" : "day",
            Trend(period, Bucket, _ => null).ToArray(),
            Trend(period.Where(c => topObjects.Contains(c.Object)), Bucket, c => c.Object ?? AnalyticsFilter.None).ToArray(),
            state.Rows.Where(r => r.FillRate is not null).OrderBy(r => r.FillRate).Take(TopCount).ToArray(),
            (await ParametersAsync(db, state, TopCount, ct)).ToArray());
    }

    private static async Task<IResult> HistoryAsync(string key, ToadFlowDb db, CancellationToken ct)
    {
        var checks = await db.Set<LoiCheck>().AsNoTracking().Where(c => c.ModelKey == key).OrderBy(c => c.CheckedAt).ToListAsync(ct);
        if (checks.Count == 0) return Results.NotFound();

        var ids = checks.Select(c => c.Id).ToList();
        var userIds = checks.Where(c => c.UserId != null).Select(c => c.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var rules = await db.Set<LoiCheckRule>().AsNoTracking().Where(r => ids.Contains(r.CheckId)).ToListAsync(ct);
        var latest = checks[^1];
        var latestRules = rules.Where(r => r.CheckId == latest.Id).ToDictionary(r => r.Id, r => r.Title);
        var parameters = await db.Set<LoiCheckParameter>().AsNoTracking().Where(p => p.CheckId == latest.Id).ToListAsync(ct);
        var previous = checks.Count > 1 ? checks[^2] : null;

        return Results.Ok(new ModelHistory(
            Row(latest, previous),
            checks.Select(c => new CheckPoint(c.Id, c.CheckedAt, c.Source, c.UserId is { } id ? users.GetValueOrDefault(id, "удалённый пользователь") : null,
                c.ProfileName, c.ProfileFingerprint, c.Elements, Ratio(c.Filled, c.Values), Ratio(c.CompleteElements, c.Elements))).ToArray(),
            rules.Select(r => new RulePoint(r.CheckId, r.Position, r.Title, r.Elements, Ratio(r.Filled, r.Values), Ratio(r.CompleteElements, r.Elements)))
                .OrderBy(r => r.Position).ToArray(),
            parameters.Select(p => new ParameterPoint(latestRules.GetValueOrDefault(p.RuleId, ""), p.Name, p.Filled, p.Empty, p.Missing,
                Ratio(p.Filled, p.Filled + p.Empty + p.Missing))).OrderBy(p => p.FillRate ?? 1).ToArray()));
    }

    /// <summary>Таблица моделей для Excel: точка с запятой и BOM — так её открывает русский Excel.</summary>
    private static async Task<IResult> ExportAsync([AsParameters] AnalyticsFilter filter, ToadFlowDb db, CancellationToken ct)
    {
        var rows = (await StateAsync(db, filter, ct)).Rows.OrderBy(r => r.Object).ThenBy(r => r.Discipline).ThenBy(r => r.ModelName);
        var text = new StringBuilder("Заказчик;Объект;Раздел;Часть;Модель;Путь;Проверена;Источник;Профиль;Элементов;Заполненность;Полнота;Изменение\r\n");
        foreach (var r in rows)
        {
            text.AppendJoin(';', Csv(r.Customer), Csv(r.Object), Csv(r.Discipline), Csv(r.Part), Csv(r.ModelName), Csv(r.ModelPath),
                r.CheckedAt.ToString("yyyy-MM-dd HH:mm"), r.Source == LoiCheckSource.Manual ? "вручную" : "по расписанию", Csv(r.ProfileName), r.Elements,
                Percent(r.FillRate), Percent(r.Completeness), r.FillRate is { } now && r.PreviousFillRate is { } before ? Percent(now - before) : "");
            text.Append("\r\n");
        }

        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray(), "text/csv; charset=utf-8",
            "LOI_модели.csv");
    }

    /// <summary>Лёгкие строки проверок под фильтром (без начала периода — нужна и предыдущая проверка) и состояние моделей.</summary>
    private static async Task<State> StateAsync(ToadFlowDb db, AnalyticsFilter filter, CancellationToken ct)
    {
        var query = db.Set<LoiCheck>().AsNoTracking();
        if (filter.To is { } to) query = query.Where(c => c.CheckedAt <= to.ToUniversalTime());
        if (Value(filter.Customer) is { } customer) query = query.Where(c => c.Customer == customer);
        else if (filter.Customer == AnalyticsFilter.None) query = query.Where(c => c.Customer == null);
        if (Value(filter.Object) is { } obj) query = query.Where(c => c.Object == obj);
        else if (filter.Object == AnalyticsFilter.None) query = query.Where(c => c.Object == null);
        if (Value(filter.Discipline) is { } discipline) query = query.Where(c => c.Discipline == discipline);
        else if (filter.Discipline == AnalyticsFilter.None) query = query.Where(c => c.Discipline == null);
        if (Value(filter.Part) is { } part) query = query.Where(c => c.Part == part);
        else if (filter.Part == AnalyticsFilter.None) query = query.Where(c => c.Part == null);
        if (Value(filter.Profile) is { } profile) query = query.Where(c => c.ProfileName == profile);
        if (filter.Source is { } source) query = query.Where(c => c.Source == source);
        if (!string.IsNullOrWhiteSpace(filter.Search)) query = query.Where(c => EF.Functions.ILike(c.ModelName, $"%{filter.Search.Trim()}%"));

        var checks = await query.Select(c => new Light(c.Id, c.ModelKey, c.ModelName, c.ModelPath, c.Customer, c.Object, c.Discipline, c.Part, c.CheckedAt,
            c.Source, c.ProfileName, c.ProfileFingerprint, c.Elements, c.CompleteElements, c.Values, c.Filled)).ToListAsync(ct);

        var from = filter.From?.ToUniversalTime();
        var inPeriod = from is null ? checks : checks.Where(c => c.CheckedAt >= from).ToList();
        var latest = new List<Light>();
        var rows = new List<ModelRow>();
        foreach (var model in checks.GroupBy(c => c.ModelKey))
        {
            var ordered = model.OrderByDescending(c => c.CheckedAt).ToList();
            if (from is not null && ordered[0].CheckedAt < from) continue; // в периоде модель не проверялась
            latest.Add(ordered[0]);
            rows.Add(Row(ordered[0], ordered.Count > 1 ? ordered[1] : null));
        }

        return new State(inPeriod, latest, rows);
    }

    private static async Task<List<ParameterRow>> ParametersAsync(ToadFlowDb db, State state, int? top, CancellationToken ct)
    {
        var ids = state.Latest.Select(c => c.Id).ToList();
        if (ids.Count == 0) return [];

        var names = state.Latest.ToDictionary(c => c.Id, c => c.ModelName);
        var parameters = await db.Set<LoiCheckParameter>().AsNoTracking().Where(p => ids.Contains(p.CheckId))
            .Select(p => new { p.CheckId, p.Name, p.Filled, p.Empty, p.Missing }).ToListAsync(ct);
        var rows = parameters.GroupBy(p => p.Name).Select(g =>
        {
            var total = g.Sum(p => p.Filled + p.Empty + p.Missing);
            var worst = g.GroupBy(p => p.CheckId)
                .Select(m => (Model: names[m.Key], Rate: Ratio(m.Sum(p => p.Filled), m.Sum(p => p.Filled + p.Empty + p.Missing))))
                .Where(m => m.Rate < 1).OrderBy(m => m.Rate).Take(3).Select(m => m.Model).ToArray();
            return new ParameterRow(g.Key, g.Select(p => p.CheckId).Distinct().Count(), total, g.Sum(p => p.Filled), g.Sum(p => p.Empty), g.Sum(p => p.Missing),
                Ratio(g.Sum(p => p.Filled), total), worst);
        }).Where(r => r.Total > 0).OrderBy(r => r.FillRate).ThenByDescending(r => r.Total);
        return (top is { } count ? rows.Take(count) : rows).ToList();
    }

    private static IEnumerable<GroupStats> Group(IEnumerable<Light> latest, Func<Light, string?> key) =>
        latest.GroupBy(key).Select(g => new GroupStats(g.Key, g.Select(c => c.Customer).FirstOrDefault(c => c != null), g.Count(),
                Ratio(g.Sum(c => c.Filled), g.Sum(c => c.Values)), Ratio(g.Sum(c => c.CompleteElements), g.Sum(c => c.Elements))))
            .OrderBy(g => g.FillRate ?? 2);

    private static IEnumerable<TrendPoint> Trend(IEnumerable<Light> checks, Func<DateTimeOffset, DateOnly> bucket, Func<Light, string?> series) =>
        checks.GroupBy(c => (Bucket: bucket(c.CheckedAt), Series: series(c)))
            .Select(g => new TrendPoint(g.Key.Bucket, g.Key.Series, g.Count(), Ratio(g.Sum(c => c.Filled), g.Sum(c => c.Values)),
                Ratio(g.Sum(c => c.CompleteElements), g.Sum(c => c.Elements))))
            .OrderBy(p => p.Bucket);

    private static ModelRow Row(Light latest, Light? previous) => new(latest.ModelKey, latest.ModelName, latest.ModelPath, latest.Customer, latest.Object,
        latest.Discipline, latest.Part, latest.CheckedAt, latest.Source, latest.ProfileName, latest.Elements, Ratio(latest.Filled, latest.Values),
        Ratio(latest.CompleteElements, latest.Elements), previous is null ? null : Ratio(previous.Filled, previous.Values),
        previous is not null && previous.ProfileFingerprint != latest.ProfileFingerprint);

    private static ModelRow Row(LoiCheck latest, LoiCheck? previous) => Row(Light.Of(latest), previous is null ? null : Light.Of(previous));

    private static async Task<string[]> Distinct(IQueryable<string?> values, CancellationToken ct) =>
        (await values.Where(v => v != null).Distinct().ToListAsync(ct)).Select(v => v!).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private static string? Value(string? filter) => string.IsNullOrWhiteSpace(filter) || filter == AnalyticsFilter.None ? null : filter;

    private static double? Ratio(int part, int total) => total == 0 ? null : (double)part / total;

    /// <summary>Проценты с запятой — CSV через точку с запятой открывают в русском Excel.</summary>
    private static string Percent(double? share) =>
        share is { } value ? (value * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') : "";

    private static string Csv(string? value) =>
        value is null ? "" : value.IndexOfAny([';', '"', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private sealed record Light(Guid Id, string ModelKey, string ModelName, string ModelPath, string? Customer, string? Object, string? Discipline, string? Part,
        DateTimeOffset CheckedAt, LoiCheckSource Source, string ProfileName, string ProfileFingerprint, int Elements, int CompleteElements, int Values, int Filled)
    {
        public static Light Of(LoiCheck c) => new(c.Id, c.ModelKey, c.ModelName, c.ModelPath, c.Customer, c.Object, c.Discipline, c.Part, c.CheckedAt, c.Source,
            c.ProfileName, c.ProfileFingerprint, c.Elements, c.CompleteElements, c.Values, c.Filled);
    }

    /// <param name="InPeriod">Проверки периода — для графика и счётчиков.</param>
    /// <param name="Latest">Последняя проверка каждой модели, проверенной в периоде.</param>
    private sealed record State(List<Light> InPeriod, List<Light> Latest, List<ModelRow> Rows);
}
