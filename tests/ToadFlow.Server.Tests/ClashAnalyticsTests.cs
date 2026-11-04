using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Analytics;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>
///     Сводка коллизий: снимок по разделу пишет сверка утверждённого прогона; коллизия пары разделов считается за каждый раздел
///     пары, но один раз в итоге (раздел <c>""</c>).
/// </summary>
public class ClashAnalyticsTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashAnalyticsTests() => Time = _time;

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    private async Task<Guid> PartOfAsync(HttpClient admin, FakeAgent agent) => await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");

    private static async Task<Guid> RunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits,
        IReadOnlyList<EngineGonePair>? gone = null, bool approve = false)
    {
        var start = approve
            ? await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })
            : await admin.PostAsync($"/api/clash/parts/{partId}/approved/run", null);
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, hits.Select(h => h with { FingerprintA = 1, FingerprintB = 2 }).ToList(), Stats,
            gone ?? [], [])).EnsureSuccessStatusCode();
        return runId;
    }

    private static EngineGonePair Gone(int i) => new("a", $"ua-{i}", "b", $"ub-{i}", "InGeometry", "InGeometry", null);

    private Task<List<ClashCollisionStat>> StatsAsync(Guid runId) => DbAsync(db => db.Set<ClashCollisionStat>().Where(s => s.RunId == runId).ToListAsync());

    /// <summary>Сегодня по часовому поясу из «Общих» — по нему сводка режет дни.</summary>
    private async Task<DateOnly> TodayAsync(HttpClient admin)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await admin.GetJsonAsync<GeneralBody>("/api/settings")).TimeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).DateTime);
    }

    private static Task<ClashAnalyticsView> AnalyticsAsync(HttpClient client, string query = "") =>
        client.GetJsonAsync<ClashAnalyticsView>($"/api/clash/analytics{query}");

    [Test]
    public async Task Stats_WrittenOnReconcile_PerDiscipline()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;

        var runId = await RunAsync(admin, engine, partId, [Hit(1), Hit(2, disciplineB: "ВК")], approve: true);

        var stats = (await StatsAsync(runId)).ToDictionary(s => s.Discipline);
        stats.Keys.Order().ShouldBe(["", "АР", "ВК", "ОВ"]);
        (stats[""].Open, stats[""].New, stats[""].PartId).ShouldBe((2, 2, partId));
        (stats["АР"].Open, stats["АР"].New).ShouldBe((2, 2));
        (stats["ОВ"].Open, stats["ОВ"].New, stats["ВК"].Open).ShouldBe((1, 1, 1));
        stats.Values.ShouldAllBe(s => s.Fake == 0 && s.Fixed == 0 && s.Returned == 0 && s.GoneByRules == 0);
    }

    [Test]
    public async Task Stats_SameDisciplinePair_CountedOnce()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;

        var runId = await RunAsync(admin, engine, partId, [Hit(1, disciplineA: "ОВК", disciplineB: "ОВК"), Hit(2, disciplineA: "КР", disciplineB: "ОВК")], approve: true);

        var stats = (await StatsAsync(runId)).ToDictionary(s => s.Discipline);
        (stats["ОВК"].Open, stats["ОВК"].New).ShouldBe((2, 2));
        (stats["КР"].Open, stats[""].Open).ShouldBe((1, 2));
        var view = await AnalyticsAsync(admin, "?groupBy=discipline");
        view.StuckDays.ShouldBe(30); // порог «висят» — для подсказки в вебе
        view.Lagging.Single(l => l.Key == "ОВК").Open.ShouldBe(2);
    }

    [Test]
    public async Task Stats_CancelledRun_NotWritten()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await admin.PostAsync($"/api/clash/parts/{partId}/approved/run/cancel", null)).EnsureSuccessStatusCode();

        await PostApprovedResultAsync(engine, runId, 0, true, [Hit(1) with { FingerprintA = 1 }], Stats, [], []);

        (await DbAsync(db => db.Set<ClashCollisionStat>().CountAsync())).ShouldBe(0);
    }

    [Test]
    public async Task Analytics_SeriesCarriesForward()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var day0 = await TodayAsync(admin);
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2)], approve: true);
        _time.Advance(TimeSpan.FromDays(2));
        await RunAsync(admin, engine, partId, [Hit(1)], [Gone(2)]);

        var view = await AnalyticsAsync(admin, $"?from={day0.AddDays(-1):yyyy-MM-dd}&to={day0.AddDays(2):yyyy-MM-dd}");

        view.Series.Select(p => p.Date).ShouldBe([day0.AddDays(-1), day0, day0.AddDays(1), day0.AddDays(2)]);
        view.Series.Select(p => p.Open).ShouldBe([0, 2, 2, 1]);
        var ov = await AnalyticsAsync(admin, $"?from={day0:yyyy-MM-dd}&to={day0.AddDays(2):yyyy-MM-dd}&discipline=ОВ");
        ov.Series.Select(p => p.Open).ShouldBe([2, 2, 1]);
    }

    [Test]
    public async Task Analytics_PeriodSumsTransitions()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var day0 = await TodayAsync(admin);
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2)], approve: true);
        _time.Advance(TimeSpan.FromDays(1));
        await RunAsync(admin, engine, partId, [Hit(1), Hit(3)], [Gone(2)]);
        _time.Advance(TimeSpan.FromDays(1));
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3)]);

        var all = await AnalyticsAsync(admin, $"?from={day0:yyyy-MM-dd}&to={day0.AddDays(2):yyyy-MM-dd}");
        var last = await AnalyticsAsync(admin, $"?from={day0.AddDays(1):yyyy-MM-dd}&to={day0.AddDays(2):yyyy-MM-dd}");

        (all.Period.New, all.Period.Fixed, all.Period.Returned).ShouldBe((3, 1, 1));
        (last.Period.New, last.Period.Fixed, last.Period.Returned).ShouldBe((1, 1, 1));
        all.Current.Open.ShouldBe(3);
    }

    [Test]
    public async Task Analytics_LaggingByDiscipline_SortedByOpen()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var day0 = await TodayAsync(admin);
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3, disciplineB: "ВК")], approve: true);
        var designer = await ClientAsync(UserRole.Designer);

        var view = await AnalyticsAsync(designer, $"?from={day0:yyyy-MM-dd}&to={day0:yyyy-MM-dd}&groupBy=discipline");

        view.Lagging.Select(l => (l.Key, l.Open)).ShouldBe([("АР", 3), ("ОВ", 2), ("ВК", 1)]);
        view.Lagging.Single(l => l.Key == "ОВ").Growth.ShouldBe(2); // в начале периода коллизий не было
        view.Lagging.ShouldAllBe(l => l.Stuck == 0);
        var byPart = (await AnalyticsAsync(designer, "?groupBy=part")).Lagging.ShouldHaveSingleItem();
        (byPart.Open, byPart.PartId).ShouldBe((3, partId)); // строка ведёт на страницу проверки
    }

    [Test]
    public async Task Analytics_FilterByObjectAndDiscipline()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var other = await PartOfAsync(admin, agent);
        await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId).ExecuteUpdateAsync(p => p.SetProperty(x => x.Object, "DON")));
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2, disciplineB: "ВК")], approve: true);
        await RunAsync(admin, engine, other, [Hit(1), Hit(2), Hit(3)], approve: true);
        var id = await DbAsync(db => db.Set<ClashCollision>().Where(c => c.PartId == partId && c.Number == 1).Select(c => c.Id).SingleAsync());
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake", note = "Гильза" })).EnsureSuccessStatusCode();

        var don = await AnalyticsAsync(admin, "?object=DON");
        var vk = await AnalyticsAsync(admin, "?object=DON&discipline=ВК");

        (don.Current.Open, don.Current.Fake, don.Current.FakeShare).ShouldBe((1, 1, 0.5));
        don.Current.AgeBuckets.Sum(b => b.Count).ShouldBe(1);
        don.Current.AgeBuckets[0].Count.ShouldBe(1); // до 7 дней
        vk.Current.Open.ShouldBe(1);
        (await AnalyticsAsync(admin, "?groupBy=object")).Lagging.Select(l => (l.Key, l.Open)).ShouldBe([("Без проекта", 3), ("DON", 1)]);
        (await AnalyticsAsync(admin, $"?partId={other}")).Current.Open.ShouldBe(3);
    }

    /// <summary>Срез «пара»: «кто отстаёт» по парам разделов; рост — переходы пары за период.</summary>
    [Test]
    public async Task Analytics_PairSlice()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3, disciplineB: "ВК")], approve: true);

        var view = await AnalyticsAsync(admin, "?groupBy=pair");

        view.Lagging.Select(l => (l.Key, l.Open, l.Growth, l.Stuck)).ShouldBe([("АР × ОВ", 2, 2, 0), ("АР × ВК", 1, 1, 0)]);
        (await AnalyticsAsync(admin, "?groupBy=pair&discipline=ВК")).Lagging.Select(l => l.Key).ShouldBe(["АР × ВК"]);
    }

    /// <summary>«На ком коллизии»: по разделу — сколько он правит сам и сколько ещё не решено, кто правит.</summary>
    [Test]
    public async Task Analytics_LoadByDiscipline()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3, disciplineB: "ВК"), Hit(4, disciplineA: "ОВ")], approve: true);
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 1).ExecuteUpdateAsync(c => c.SetProperty(x => x.Assignee, CollisionAssignee.SideB)));
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 3).ExecuteUpdateAsync(c => c.SetProperty(x => x.Assignee, CollisionAssignee.SideA)));

        var current = (await AnalyticsAsync(admin, "")).Current;

        // №1 АР×ОВ — правит ОВ; №2 АР×ОВ — не решено; №3 АР×ВК — правит АР; №4 ОВ×ОВ — правит ОВ (обе стороны — один раздел).
        current.Undecided.ShouldBe(1);
        current.ByDiscipline.Select(d => (d.Discipline, d.Assigned, d.Undecided)).ShouldBe([("ОВ", 2, 1), ("АР", 1, 1)]);
    }

    private sealed record GeneralBody(string TimeZoneId);
}
