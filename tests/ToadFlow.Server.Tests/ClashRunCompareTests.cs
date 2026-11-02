using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Счётчики прогона по парам разделов пишутся при сверке; по ним сравниваются два прогона.</summary>
public class ClashRunCompareTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashRunCompareTests() => Time = _time;

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    private async Task<Guid> RunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits,
        IReadOnlyList<EngineGonePair>? gone = null, bool approve = false)
    {
        _time.Advance(TimeSpan.FromMinutes(10));
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

    private Task DropStatsAsync(Guid runId) =>
        DbAsync(db => db.Set<ClashRunPairStat>().Where(s => s.RunId == runId).ExecuteDeleteAsync());

    [Test]
    public async Task PairStats_WrittenOnReconcile()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;

        var runId = await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3, disciplineA: "КР", disciplineB: "ОВ")], approve: true);

        var stats = await DbAsync(db => db.Set<ClashRunPairStat>().Where(s => s.RunId == runId).ToListAsync());
        var arOv = stats.Single(s => s.DisciplinePair == "АР × ОВ");
        (arOv.PartId, arOv.Found, arOv.New, arOv.Returned, arOv.Fixed, arOv.GoneByRules).ShouldBe((partId, 2, 2, 0, 0, 0));
        stats.Single(s => s.DisciplinePair == "КР × ОВ").Found.ShouldBe(1);
    }

    [Test]
    public async Task Compare_SumsBetweenRuns()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Hit(1), Hit(2)], approve: true);
        await RunAsync(admin, engine, partId, [Hit(1)], [Gone(2)]);
        var third = await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3)]);

        var compare = await admin.GetJsonAsync<RunCompare>($"/api/clash/parts/{partId}/approved/runs/compare?a={third}&b={first}");

        (compare.RunA, compare.RunB).ShouldBe((first, third)); // раньше — тот, что раньше по времени
        var row = compare.Rows.ShouldHaveSingleItem();
        row.ShouldBe(new RunCompareRow("АР × ОВ", 2, 3, 1, 1, 1, 0));
    }

    [Test]
    public async Task Compare_OldRun_409()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var second = await RunAsync(admin, engine, partId, [Hit(1)]);
        await DropStatsAsync(first);

        var response = await admin.GetAsync($"/api/clash/parts/{partId}/approved/runs/compare?a={first}&b={second}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("был до обновления — сравнение недоступно");
    }

    [Test]
    public async Task Runs_ComparableFlag()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var second = await RunAsync(admin, engine, partId, [Hit(1)]);
        await DropStatsAsync(first);

        var runs = await admin.GetJsonAsync<ReportRunView[]>($"/api/clash/parts/{partId}/approved/runs");

        runs.Single(r => r.RunId == first).Comparable.ShouldBeFalse();
        runs.Single(r => r.RunId == second).Comparable.ShouldBeTrue();
    }
}
