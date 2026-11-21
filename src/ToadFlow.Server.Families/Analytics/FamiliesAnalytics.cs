using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Files;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Families.Analytics;

/// <param name="Week">Понедельник недели (пояс из настроек).</param>
public sealed record WeekPoint(DateOnly Week, int Created, int Closed);

/// <param name="Accepted">Принятых за период.</param>
/// <param name="MedianHours">Медиана от подачи до приёмки по принятым за период; null — таких нет.</param>
/// <param name="DeclinedShare">Доля принятых, которые хотя бы раз возвращали («Не то»); 0 — если принятых нет.</param>
public sealed record DirectionStat(string Name, int Open, int Overdue, int Accepted, double? MedianHours, double DeclinedShare);

/// <summary>Нагрузка, а не рейтинг: сколько открытых заявок у исполнителя.</summary>
public sealed record AssigneeLoad(string Name, int Open);

public sealed record CardCount(Guid CardId, string Name, string Section, int Loads);

public sealed record FamiliesAnalyticsView(IReadOnlyList<WeekPoint> Weeks, IReadOnlyList<DirectionStat> ByDirection, IReadOnlyList<AssigneeLoad> ByAssignee,
    IReadOnlyList<CardCount> TopLoaded, IReadOnlyList<CardCount> Unused, IReadOnlyList<MissRow> TopMisses);

/// <summary>Как работает отдел: заявки по неделям, сроки и возвраты по направлениям, нагрузка, востребованность каталога, промахи поиска.</summary>
internal static class FamiliesAnalytics
{
    private const string CommonQueue = "Общая очередь";
    private const int UnusedDays = 180;
    private const int Top = 10;
    private const int MaxUnused = 50;

    public static void MapFamiliesAnalytics(this RouteGroupBuilder families)
    {
        families.MapGet("/analytics", (ToadFlowDb db, ISettingsStore settings, TimeProvider time, int? days, CancellationToken ct) =>
            BuildAsync(db, settings, time, days, ct)).RequireAuthorization(Policies.Operator);
        families.MapGet("/analytics.xlsx", XlsxAsync).RequireAuthorization(Policies.Operator);
    }

    public static async Task<FamiliesAnalyticsView> BuildAsync(ToadFlowDb db, ISettingsStore settings, TimeProvider time, int? days, CancellationToken ct)
    {
        var period = Math.Clamp(days ?? 90, 7, 365);
        var now = time.GetUtcNow();
        var zone = (await settings.GetAsync(ct)).TimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var since = now.AddDays(-period);
        var open = FamilyRequest.OpenStates;

        var requests = await db.Set<FamilyRequest>().AsNoTracking()
            .Where(r => open.Contains(r.State) || r.CreatedAt >= since || r.ClosedAt >= since)
            .Select(r => new { r.State, r.CreatedAt, r.ClosedAt, r.DirectionId, r.AssigneeUserId, r.NeededBy, r.DeclineCount }).ToListAsync(ct);
        bool Accepted(RequestState state) => state is RequestState.Done or RequestState.Exists;

        DateOnly WeekOf(DateTimeOffset at)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
            return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        }

        var thisWeek = WeekOf(now);
        var weeks = Enumerable.Range(0, (period + 6) / 7).Select(i => thisWeek.AddDays(-7 * i)).Reverse()
            .Select(week => new WeekPoint(week, requests.Count(r => WeekOf(r.CreatedAt) == week),
                requests.Count(r => Accepted(r.State) && r.ClosedAt is { } c && WeekOf(c) == week)))
            .ToList();

        var directions = await db.Set<FamilyDirection>().AsNoTracking().OrderBy(d => d.Order).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var groups = directions.Select(d => ((Guid?)d.Id, d.Name)).Append((null, CommonQueue));
        var byDirection = groups.Select(g =>
        {
            var mine = requests.Where(r => r.DirectionId == g.Item1).ToList();
            var accepted = mine.Where(r => Accepted(r.State) && r.ClosedAt >= since).ToList();
            var hours = accepted.Select(r => (r.ClosedAt!.Value - r.CreatedAt).TotalHours).Order().ToList();
            return new DirectionStat(g.Name, mine.Count(r => open.Contains(r.State)), mine.Count(r => open.Contains(r.State) && r.NeededBy < today), accepted.Count,
                hours.Count == 0 ? null : Math.Round(hours[hours.Count / 2], 1),
                accepted.Count == 0 ? 0 : Math.Round((double)accepted.Count(r => r.DeclineCount > 0) / accepted.Count, 2));
        }).Where(d => d.Open > 0 || d.Accepted > 0 || d.Name != CommonQueue).ToList();

        var assigneeIds = requests.Where(r => open.Contains(r.State) && r.AssigneeUserId != null).Select(r => r.AssigneeUserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => assigneeIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var byAssignee = requests.Where(r => open.Contains(r.State) && r.AssigneeUserId != null).GroupBy(r => r.AssigneeUserId!.Value)
            .Select(g => new AssigneeLoad(names.GetValueOrDefault(g.Key, "?"), g.Count())).OrderByDescending(a => a.Open).ThenBy(a => a.Name).ToList();

        var cards = db.Set<FamilyCard>().AsNoTracking().Where(c => c.State == CardState.Approved);
        var topLoaded = await cards.Where(c => c.LoadCount > 0).OrderByDescending(c => c.LoadCount).ThenBy(c => c.Name).Take(Top)
            .Select(c => new CardCount(c.Id, c.Name, c.Section, c.LoadCount)).ToListAsync(ct);
        var unusedSince = now.AddDays(-UnusedDays);
        var loads = db.Set<FamilyLoad>();
        var unused = await cards.Where(c => c.CreatedAt < unusedSince && !loads.Any(l => l.CardId == c.Id && l.At >= unusedSince))
            .OrderBy(c => c.Section).ThenBy(c => c.Name).Take(MaxUnused).Select(c => new CardCount(c.Id, c.Name, c.Section, c.LoadCount)).ToListAsync(ct);

        var misses = (await SearchMisses.ListAsync(db, time, Math.Min(period, 180), ct)).Take(Top).ToList();
        return new FamiliesAnalyticsView(weeks, byDirection, byAssignee, topLoaded, unused, misses);
    }

    private static async Task XlsxAsync(HttpContext http, ToadFlowDb db, ISettingsStore settings, TimeProvider time, int? days, CancellationToken ct)
    {
        var view = await BuildAsync(db, settings, time, days, ct);
        http.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"families-analytics.xlsx\"; filename*=UTF-8''" + Uri.EscapeDataString("Аналитика семейств.xlsx");
        await using var buffer = new MemoryStream();
        await using (var writer = XlsxWriter.Create(buffer))
        {
            var weeks = writer.AddSheet("Заявки по неделям", [("Неделя с", 14), ("Подано", 10), ("Принято", 10)]);
            foreach (var w in view.Weeks)
                await weeks.WriteRowAsync([XlsxCell.Text(w.Week.ToString("dd.MM.yyyy")), XlsxCell.Number(w.Created), XlsxCell.Number(w.Closed)], ct);

            var directions = writer.AddSheet("Направления", [("Направление", 24), ("Открыто", 10), ("Просрочено", 12), ("Принято", 10), ("Медиана, ч", 12), ("Доля «Не то»", 14)]);
            foreach (var d in view.ByDirection)
                await directions.WriteRowAsync([XlsxCell.Text(d.Name), XlsxCell.Number(d.Open), XlsxCell.Number(d.Overdue), XlsxCell.Number(d.Accepted),
                    d.MedianHours is { } m ? XlsxCell.Number(m) : XlsxCell.Empty, XlsxCell.Number(d.DeclinedShare)], ct);

            var load = writer.AddSheet("Нагрузка", [("Исполнитель", 28), ("Открытых", 10)]);
            foreach (var a in view.ByAssignee) await load.WriteRowAsync([XlsxCell.Text(a.Name), XlsxCell.Number(a.Open)], ct);

            var top = writer.AddSheet("Востребованные", [("Семейство", 40), ("Раздел", 16), ("Загрузок", 10)]);
            foreach (var c in view.TopLoaded) await top.WriteRowAsync([XlsxCell.Text(c.Name), XlsxCell.Text(c.Section), XlsxCell.Number(c.Loads)], ct);

            var unused = writer.AddSheet("Без загрузок 180 дней", [("Семейство", 40), ("Раздел", 16), ("Загрузок всего", 14)]);
            foreach (var c in view.Unused) await unused.WriteRowAsync([XlsxCell.Text(c.Name), XlsxCell.Text(c.Section), XlsxCell.Number(c.Loads)], ct);

            var misses = writer.AddSheet("Искали и не нашли", [("Запрос", 40), ("Раз", 8), ("Людей", 8)]);
            foreach (var m in view.TopMisses) await misses.WriteRowAsync([XlsxCell.Text(m.Query), XlsxCell.Number(m.Count), XlsxCell.Number(m.Users)], ct);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(http.Response.Body, ct);
    }
}
