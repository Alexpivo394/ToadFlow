using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Overview;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Projects;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Clash.Analytics;

public sealed record AssigneeCount(CollisionAssignee Assignee, int Count);

/// <param name="Label">«до 7 дней», «7–30 дней», «30–90 дней», «больше 90 дней».</param>
public sealed record AgeBucket(string Label, int Count);

/// <summary>Сейчас — прямо по коллизиям: открытые, фиктивные, «кто правит» и возраст открытых.</summary>
/// <param name="FakeShare">Доля фиктивных среди открытых и фиктивных, от 0 до 1.</param>
public sealed record AnalyticsCurrent(int Open, int Fake, double FakeShare, IReadOnlyList<AssigneeCount> ByAssignee, IReadOnlyList<AgeBucket> AgeBuckets);

/// <summary>Переходы за период — сумма снимков прогонов периода.</summary>
public sealed record AnalyticsPeriod(int New, int Fixed, int Returned);

public sealed record AnalyticsPoint(DateOnly Date, int Open);

/// <param name="Key">Раздел, проект или проверка — смотря по разбивке.</param>
/// <param name="Growth">Открытых сейчас минус открытых в начале периода.</param>
/// <param name="Stuck">Открытые без движения дольше порога «висят» из настроек.</param>
/// <param name="PartId">Проверка — при разбивке по проверкам: строка ведёт на её страницу.</param>
public sealed record AnalyticsLagging(string Key, int Open, int Growth, int Stuck, Guid? PartId = null);

/// <summary>Сводка коллизий утверждённых проверок за период по часовому поясу из «Общих».</summary>
public sealed record ClashAnalyticsView(
    DateOnly From,
    DateOnly To,
    AnalyticsCurrent Current,
    AnalyticsPeriod Period,
    IReadOnlyList<AnalyticsPoint> Series,
    IReadOnlyList<AnalyticsLagging> Lagging);

/// <summary>
///     Сводка коллизий (аналитика): «сейчас» и «кто отстаёт» — по коллизиям, переходы и динамика — по снимкам сверки
///     (<see cref="ClashCollisionStat" />). Коллизия пары разделов считается за каждый раздел пары; без фильтра по разделу —
///     один раз.
/// </summary>
public static class AnalyticsEndpoints
{
    private const int MaxDays = 3 * 366;

    /// <param name="members">Группа участников: сводку видит любая роль.</param>
    public static void MapClashAnalytics(this RouteGroupBuilder members) => members.MapGet("/clash/analytics", AnalyticsAsync);

    /// <param name="object">Проект; <see cref="ProjectEndpoints.Unparsed" /> — части без проекта.</param>
    /// <param name="from">Первый день периода; по умолчанию — 30 дней до <paramref name="to" />.</param>
    /// <param name="to">Последний день периода; по умолчанию — сегодня.</param>
    /// <param name="groupBy"><c>discipline</c> (по умолчанию), <c>object</c>, <c>part</c> — разбивка «кто отстаёт».</param>
    private static async Task<ClashAnalyticsView> AnalyticsAsync(string? @object, Guid? partId, string? discipline, DateOnly? from, DateOnly? to,
        string? groupBy, ToadFlowDb db, ISettingsStore general, ClashSettingsStore settings, TimeProvider time, CancellationToken ct)
    {
        var zone = (await general.GetAsync(ct)).TimeZone;
        var now = time.GetUtcNow();
        var last = to ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = from ?? last.AddDays(-29);
        if (first > last) (first, last) = (last, first);
        if (last.DayNumber - first.DayNumber > MaxDays) first = last.AddDays(-MaxDays);
        var start = Start(first, zone);
        var end = Start(last.AddDays(1), zone);
        var key = string.IsNullOrWhiteSpace(discipline) ? null : discipline.Trim();

        var parts = (await db.Set<ClashPart>().AsNoTracking().Where(p => p.ApprovedVersion != null)
                .Select(p => new { p.Id, p.Name, p.Object }).ToListAsync(ct))
            .Where(p => partId is not { } wanted || p.Id == wanted)
            .Where(p => string.IsNullOrWhiteSpace(@object) || ProjectOf(p.Object) == @object.Trim())
            .ToList();
        var ids = parts.Select(p => p.Id).ToArray();

        var collisions = db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.PartId));
        if (key is not null) collisions = collisions.Where(c => c.DisciplineA == key || c.DisciplineB == key);
        var open = collisions.Where(c => CollisionEndpoints.Open.Contains(c.Status));
        var current = await CurrentAsync(collisions, open, now, ct);

        // Без разбивки по разделам — строки итога; иначе — выбранного раздела или всех разделов (для «кто отстаёт» по разделам).
        var byDiscipline = (groupBy ?? "discipline") == "discipline";
        var stats = db.Set<ClashCollisionStat>().AsNoTracking().Where(s => ids.Contains(s.PartId) && s.At < end);
        stats = byDiscipline && key is null
            ? stats.Where(s => s.Discipline != ClashCollisionStat.Total)
            : stats.Where(s => s.Discipline == (key ?? ClashCollisionStat.Total));
        var rows = await stats.OrderBy(s => s.At).Select(s => new StatPoint(s.PartId, s.Discipline, s.At, s.Open, s.New, s.Fixed, s.Returned))
            .ToListAsync(ct);
        var totals = byDiscipline && key is null
            ? await db.Set<ClashCollisionStat>().AsNoTracking()
                .Where(s => ids.Contains(s.PartId) && s.At < end && s.Discipline == ClashCollisionStat.Total).OrderBy(s => s.At)
                .Select(s => new StatPoint(s.PartId, s.Discipline, s.At, s.Open, s.New, s.Fixed, s.Returned)).ToListAsync(ct)
            : rows;

        var inPeriod = totals.Where(r => r.At >= start).ToList();
        var period = new AnalyticsPeriod(inPeriod.Sum(r => r.New), inPeriod.Sum(r => r.Fixed), inPeriod.Sum(r => r.Returned));
        var series = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays)
            .Select(day => new AnalyticsPoint(day, OpenAt(totals, Start(day.AddDays(1), zone)).Values.Sum())).ToList();

        var stuckBefore = now - TimeSpan.FromDays((await settings.OverviewAsync(ct)).StuckDays);
        var stuck = OverviewEndpoints.Stuck(db, collisions, stuckBefore);
        var names = parts.ToDictionary(p => p.Id, p => (Part: p.Name, Project: ProjectOf(p.Object)));
        IReadOnlyList<AnalyticsLagging> lagging = groupBy switch
        {
            "object" => await LaggingByPartAsync(open, stuck, OpenAt(totals, start), id => (names[id].Project, null), ct),
            "part" => await LaggingByPartAsync(open, stuck, OpenAt(totals, start), id => (names[id].Part, id), ct),
            _ => await LaggingByDisciplineAsync(open, stuck, rows, start, key, ct)
        };

        return new ClashAnalyticsView(first, last, current, period, series, lagging);
    }

    private sealed record StatPoint(Guid PartId, string Discipline, DateTimeOffset At, int Open, int New, int Fixed, int Returned);

    private static string ProjectOf(string? @object) => string.IsNullOrWhiteSpace(@object) ? ProjectEndpoints.Unparsed : @object.Trim();

    /// <summary>Начало дня по часовому поясу — в UTC.</summary>
    private static DateTimeOffset Start(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Открытых у каждой пары «часть × раздел» на момент: последний снимок до него, с переносом вперёд.</summary>
    private static Dictionary<(Guid PartId, string Discipline), int> OpenAt(IReadOnlyList<StatPoint> rows, DateTimeOffset moment)
    {
        var result = new Dictionary<(Guid, string), int>();
        foreach (var row in rows)
        {
            if (row.At >= moment) break; // строки по времени
            result[(row.PartId, row.Discipline)] = row.Open;
        }

        return result;
    }

    private static async Task<AnalyticsCurrent> CurrentAsync(IQueryable<ClashCollision> collisions, IQueryable<ClashCollision> open, DateTimeOffset now,
        CancellationToken ct)
    {
        var openCount = await open.CountAsync(ct);
        var fake = await collisions.CountAsync(c => c.Status == CollisionStatus.Fake, ct);
        var byAssignee = await open.GroupBy(c => c.Assignee).Select(g => new AssigneeCount(g.Key, g.Count())).ToListAsync(ct);
        var (d7, d30, d90) = (now.AddDays(-7), now.AddDays(-30), now.AddDays(-90));
        var ages = await open.GroupBy(c => c.CreatedAt > d7 ? 0 : c.CreatedAt > d30 ? 1 : c.CreatedAt > d90 ? 2 : 3)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        AgeBucket[] buckets =
        [
            new("до 7 дней", ages.GetValueOrDefault(0)), new("7–30 дней", ages.GetValueOrDefault(1)),
            new("30–90 дней", ages.GetValueOrDefault(2)), new("больше 90 дней", ages.GetValueOrDefault(3))
        ];
        return new AnalyticsCurrent(openCount, fake, openCount + fake == 0 ? 0 : (double)fake / (openCount + fake),
            Enum.GetValues<CollisionAssignee>().Select(a => byAssignee.FirstOrDefault(x => x.Assignee == a) ?? new AssigneeCount(a, 0)).ToList(), buckets);
    }

    private static async Task<IReadOnlyList<AnalyticsLagging>> LaggingByPartAsync(IQueryable<ClashCollision> open, IQueryable<ClashCollision> stuck,
        Dictionary<(Guid PartId, string Discipline), int> atStart, Func<Guid, (string Key, Guid? PartId)> keyOf, CancellationToken ct)
    {
        var openByPart = await open.GroupBy(c => c.PartId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var stuckByPart = await stuck.GroupBy(c => c.PartId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var startByPart = atStart.GroupBy(s => s.Key.PartId).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
        return openByPart.Keys.Concat(startByPart.Keys).Concat(stuckByPart.Keys).Distinct()
            .GroupBy(keyOf)
            .Select(g => new AnalyticsLagging(g.Key.Key, g.Sum(openByPart.GetValueOrDefault), g.Sum(id => openByPart.GetValueOrDefault(id) - startByPart.GetValueOrDefault(id)),
                g.Sum(stuckByPart.GetValueOrDefault), g.Key.PartId))
            .OrderByDescending(l => l.Open).ThenBy(l => l.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>По разделам: коллизия за каждый раздел пары, внутри раздела — один раз (UNION сторон).</summary>
    private static async Task<IReadOnlyList<AnalyticsLagging>> LaggingByDisciplineAsync(IQueryable<ClashCollision> open, IQueryable<ClashCollision> stuck,
        IReadOnlyList<StatPoint> rows, DateTimeOffset start, string? only, CancellationToken ct)
    {
        var openBy = await BySideAsync(open, ct);
        var stuckBy = await BySideAsync(stuck, ct);
        var startBy = OpenAt(rows, start).GroupBy(s => s.Key.Discipline).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
        return openBy.Keys.Concat(startBy.Keys).Concat(stuckBy.Keys).Distinct()
            .Where(d => d != ClashCollisionStat.Total && (only is null || d == only))
            .Select(d => new AnalyticsLagging(d, openBy.GetValueOrDefault(d), openBy.GetValueOrDefault(d) - startBy.GetValueOrDefault(d), stuckBy.GetValueOrDefault(d)))
            .OrderByDescending(l => l.Open).ThenBy(l => l.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static Task<Dictionary<string, int>> BySideAsync(IQueryable<ClashCollision> query, CancellationToken ct) =>
        query.Select(c => new { c.Id, Discipline = c.DisciplineA }).Union(query.Select(c => new { c.Id, Discipline = c.DisciplineB }))
            .GroupBy(x => x.Discipline).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
}
