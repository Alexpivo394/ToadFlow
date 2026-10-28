using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashEngineQueueTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashEngineQueueTests() => Time = _time;

    private async Task<(HttpClient Admin, FakeAgent Agent)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return (admin, agent);
    }

    private static async Task<DraftRunSummary> StartAsync(HttpClient admin, Guid partId)
    {
        var response = await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<DraftRunSummary>();
    }

    private static async Task<EngineRunAssignment?> ClaimAsync(HttpClient engine)
    {
        var response = await engine.PostAsync("/api/engine/runs/claim", null);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync<EngineRunAssignment>();
    }

    private Task<EngineRun> RunAsync(Guid id) => DbAsync(db => db.Set<EngineRun>().AsNoTracking().SingleAsync(r => r.Id == id));

    private Task<List<ClashDraftHit>> HitsAsync(Guid partId) => DbAsync(async db =>
    {
        var resultRun = await db.Set<ClashPart>().Where(p => p.Id == partId).Select(p => p.ResultRunId).SingleAsync();
        return await db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == resultRun).OrderBy(h => h.Number).ToListAsync();
    });

    [Test]
    public async Task StartDraft_ModelWithoutSnapshotOrDiscipline_Rejected()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var published = await ClashFixtures.PublishAsync(Server, agent, "POL_K1_SOMETHING.rvt");
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded);
        using (var scope = Server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ClashStorage>().EnsureModelAsync("POL_K1_KR.rvt", "RSN://rs22/POL_K1_KR.rvt", CancellationToken.None);
        var known = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        await admin.PutJsonAsync($"/api/clash/parts/{partId}/models", known.Select(m => new { modelId = m.Id, disciplineOverride = (string?)null }).ToArray());

        var response = await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldContain("POL_K1_KR.rvt");
        text.ShouldContain("POL_K1_SOMETHING.rvt");
        (await DbAsync(db => db.Set<EngineRun>().CountAsync())).ShouldBe(0);
    }

    [Test]
    public async Task StartDraft_Twice_ReplacesQueuedRun()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");

        var first = await StartAsync(admin, partId);
        var second = await StartAsync(admin, partId);

        (await RunAsync(first.RunId)).State.ShouldBe(EngineRunState.Cancelled);
        second.State.ShouldBe(EngineRunState.Queued);
        (await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run")).RunId.ShouldBe(second.RunId);
    }

    [Test]
    public async Task Claim_OneRunAtATime()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partA = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var partB = await PartAsync(Server, admin, agent, "POL_K2_AR.rvt");
        var runA = await StartAsync(admin, partA);
        await StartAsync(admin, partB);
        var engine = EngineClient(Server);

        var assignment = (await ClaimAsync(engine)).ShouldNotBeNull();

        assignment.RunId.ShouldBe(runA.RunId);
        assignment.Attempt.ShouldBe(1);
        assignment.Models.ShouldHaveSingleItem().Discipline.ShouldBe("АР");
        (await ClaimAsync(engine)).ShouldBeNull();
        (await RunAsync(runA.RunId)).State.ShouldBe(EngineRunState.Running);
    }

    [Test]
    public async Task Result_InChunks_ReplacesPreviousDraftAtomically()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        var first = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await PostResultAsync(engine, first.RunId, 0, true, [Hit(1)], Stats)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HitsAsync(partId)).ShouldHaveSingleItem().UniqueIdA.ShouldBe("ua-1");

        var second = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await PostResultAsync(engine, second.RunId, 0, false, [Hit(10), Hit(11)])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostResultAsync(engine, second.RunId, 0, false, [Hit(10), Hit(11)])).StatusCode.ShouldBe(HttpStatusCode.OK); // повтор части после обрыва
        (await HitsAsync(partId)).ShouldHaveSingleItem().UniqueIdA.ShouldBe("ua-1"); // прошлый результат виден до последней части
        (await PostResultAsync(engine, second.RunId, 1, true, [Hit(12)], Stats)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var hits = await HitsAsync(partId);
        hits.Select(h => h.UniqueIdA).ShouldBe(["ua-10", "ua-11", "ua-12"]);
        hits[0].DisciplinePair.ShouldBe("АР × ОВ");
        var run = await RunAsync(second.RunId);
        run.State.ShouldBe(EngineRunState.Succeeded);
        run.HitCount.ShouldBe(3);
        run.Stats.ShouldNotBeNull().Elements.ShouldBe(10);
        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == first.RunId))).ShouldBe(0);
    }

    [Test]
    public async Task EngineRun_LeaseExpired_RequeuedThenFailed()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var run = await StartAsync(admin, partId);
        var engine = EngineClient(Server);
        var sweeper = ActivatorUtilities.CreateInstance<EngineLeaseSweeper>(Server.Services);
        await ClaimAsync(engine);

        _time.Advance(TimeSpan.FromMinutes(3));
        await sweeper.SweepAsync(CancellationToken.None);
        (await RunAsync(run.RunId)).State.ShouldBe(EngineRunState.Queued);
        (await ClaimAsync(engine)).ShouldNotBeNull().Attempt.ShouldBe(2);
        _time.Advance(TimeSpan.FromMinutes(1));
        (await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/progress", new EngineProgress(run.RunId, "Поиск", "пары"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        _time.Advance(TimeSpan.FromMinutes(1.5));
        await sweeper.SweepAsync(CancellationToken.None);
        (await RunAsync(run.RunId)).State.ShouldBe(EngineRunState.Running); // ход продлил аренду
        _time.Advance(TimeSpan.FromMinutes(3));
        await sweeper.SweepAsync(CancellationToken.None);

        var failed = await RunAsync(run.RunId);
        failed.State.ShouldBe(EngineRunState.Failed);
        failed.Error.ShouldNotBeNull().ShouldContain("перестал отвечать");
    }

    [Test]
    public async Task Fail_KeepsPreviousResult()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var engine = EngineClient(Server);
        var first = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        await PostResultAsync(engine, first.RunId, 0, true, [Hit(1)], Stats);
        var second = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        await PostResultAsync(engine, second.RunId, 0, false, [Hit(2)]);

        (await engine.PostJsonAsync($"/api/engine/runs/{second.RunId}/fail", new EngineFailure(second.RunId, "слепок повреждён", false))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var run = await RunAsync(second.RunId);
        run.State.ShouldBe(EngineRunState.Failed);
        run.Error.ShouldBe("слепок повреждён");
        (await HitsAsync(partId)).ShouldHaveSingleItem().UniqueIdA.ShouldBe("ua-1");
        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == second.RunId))).ShouldBe(0);
    }

    [Test]
    public async Task Fail_WithRetry_Requeued()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var run = await StartAsync(admin, partId);
        var engine = EngineClient(Server);
        await ClaimAsync(engine);

        await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/fail", new EngineFailure(run.RunId, "сеть", true));

        (await RunAsync(run.RunId)).State.ShouldBe(EngineRunState.Queued);
    }

    [Test]
    public async Task Cancel_RunningRun_WorkerGets409()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var run = await StartAsync(admin, partId);
        var engine = EngineClient(Server);
        await ClaimAsync(engine);

        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RunAsync(run.RunId)).State.ShouldBe(EngineRunState.Cancelled);
        (await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/progress", new EngineProgress(run.RunId, "Поиск", "пары"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostResultAsync(engine, run.RunId, 0, true, [Hit(1)], Stats)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Snapshot_Download_SupportsRange()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        await StartAsync(admin, partId);
        var engine = EngineClient(Server);
        var model = (await ClaimAsync(engine)).ShouldNotBeNull().Models.Single();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/engine/snapshots/{model.SnapshotId}/geometry");
        request.Headers.Range = new RangeHeaderValue(4, 13);
        var partial = await engine.SendAsync(request);
        var whole = await engine.GetAsync($"/api/engine/snapshots/{model.SnapshotId}/parameters");

        partial.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Length.ShouldBe(10);
        (await whole.Content.ReadAsByteArrayAsync()).LongLength.ShouldBe(model.ParametersBytes);
    }

    [Test]
    public async Task EngineEndpoints_RequireEngineKey()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;

        (await Server.CreateClient().PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await EngineClient(Server, "wrong").PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ClashFixtures.AgentClient(Server, agent).PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await admin.PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DraftResult_LargeHitCount_WrittenQuickly()
    {
        var (admin, agent) = await SetupAsync();
        await using var _ = agent;
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var run = await StartAsync(admin, partId);
        var engine = EngineClient(Server);
        await ClaimAsync(engine);
        var watch = Stopwatch.StartNew();

        (await PostResultAsync(engine, run.RunId, 0, false, Enumerable.Range(0, EngineApi.ResultChunkHits).Select(i => Hit(i)).ToList())).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await PostResultAsync(engine, run.RunId, 1, true, Enumerable.Range(EngineApi.ResultChunkHits, EngineApi.ResultChunkHits).Select(i => Hit(i)).ToList(), Stats))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        (await RunAsync(run.RunId)).HitCount.ShouldBe(100_000);
    }
}
