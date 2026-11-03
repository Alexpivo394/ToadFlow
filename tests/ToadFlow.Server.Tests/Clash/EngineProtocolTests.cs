using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Протокол работника движка ограждён попыткой: запрос не той попытки получает 409 и ничего не пишет и не стирает.</summary>
public class EngineProtocolTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public EngineProtocolTests()
    {
        Time = _time;
        Services = services => services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EngineLeaseSweeper)));
    }

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return (admin, agent, await PartAsync(Server, admin, agent, "POL_K1_AR.rvt"), EngineClient(Server));
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

    /// <summary>Прогон вернулся в очередь, а строки прежней попытки остались (запоздавшая часть успела закоммититься).</summary>
    private Task RequeueKeepingHitsAsync(Guid runId) => DbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE clash_engine_runs SET state = {nameof(EngineRunState.Queued)}, lease_until = NULL WHERE id = {runId}"));

    private Task<EngineRun> RunAsync(Guid id) => DbAsync(db => db.Set<EngineRun>().AsNoTracking().SingleAsync(r => r.Id == id));

    private Task<int> DraftHitsAsync(Guid runId) => DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == runId));

    private Task<int> ApprovedHitsAsync(Guid runId) => DbAsync(db => db.Set<ClashApprovedHit>().CountAsync(h => h.RunId == runId));

    [Test]
    public async Task Engine_StaleAttemptResult_409()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await StartAsync(admin, partId);
        (await ClaimAsync(engine))!.Attempt.ShouldBe(1);
        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(1), Hit(2)])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RequeueKeepingHitsAsync(run.RunId);
        (await ClaimAsync(engine))!.Attempt.ShouldBe(2);

        // Запоздавший работник первой попытки: ничего не принято, ничего не стёрто, прогон продолжает идти.
        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(9)], attempt: 1)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/progress", new EngineProgress(run.RunId, "Поиск", "", 1))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await engine.GetAsync($"/api/engine/runs/{run.RunId}/basis?attempt=1")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/fail", new EngineFailure(run.RunId, "поздно", false, 1))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        (await RunAsync(run.RunId)).State.ShouldBe(EngineRunState.Running);
        (await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/progress", new EngineProgress(run.RunId, "Поиск", "", 2))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(3)], attempt: 2)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostResultAsync(engine, run.RunId, 1, true, [Hit(9)], Stats, attempt: 1)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DraftHitsAsync(run.RunId)).ShouldBe(1);
        (await RunAsync(run.RunId)).State.ShouldBe(EngineRunState.Running);
    }

    [Test]
    public async Task Engine_Claim_DeletesHitsOfPreviousAttempt()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(1), Hit(2), Hit(3)])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostResultAsync(engine, run.RunId, 1, false, [Hit(4)])).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RequeueKeepingHitsAsync(run.RunId);

        await ClaimAsync(engine);

        (await DraftHitsAsync(run.RunId)).ShouldBe(0);
    }

    /// <summary>Устаревшая попытка утверждённого прогона не стирает части новой, ни на последней части, ни на сбое.</summary>
    [Test]
    public async Task Engine_StaleAttemptApprovedLastPart_409_KeepsNewAttemptHits()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var approve = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        var runId = (await approve.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        (await ClaimAsync(engine))!.Attempt.ShouldBe(1);
        await RequeueKeepingHitsAsync(runId);
        (await ClaimAsync(engine))!.Attempt.ShouldBe(2);
        (await PostApprovedResultAsync(engine, runId, 0, false, [Hit(1), Hit(2)], null, [], [], attempt: 2)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var late = await PostApprovedResultAsync(engine, runId, 1, true, [Hit(3)], Stats, [], [], attempt: 1);

        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await engine.PostJsonAsync($"/api/engine/runs/{runId}/fail", new EngineFailure(runId, "поздно", false, 1))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ApprovedHitsAsync(runId)).ShouldBe(2);
        (await RunAsync(runId)).State.ShouldBe(EngineRunState.Running);
        (await PostApprovedResultAsync(engine, runId, 1, true, [Hit(3)], Stats, [], [], attempt: 2)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RunAsync(runId)).State.ShouldBe(EngineRunState.Succeeded);
    }

    /// <summary>
    ///     Предпроверка в эндпоинте пропускает запрос, а прогон возвращают в очередь и отдают второй попытке до блокировки: ограждение
    ///     под FOR UPDATE в самом DraftHits и сверке отказывает устаревшей сущности и ничего не меняет.
    /// </summary>
    [Test]
    public async Task Engine_StaleEntityUnderLock_DraftHits_Refused_NewAttemptRowsKept()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        var stale = await DbAsync(db => db.Set<EngineRun>().AsNoTracking().SingleAsync(r => r.Id == run.RunId));
        await RequeueKeepingHitsAsync(run.RunId);
        (await ClaimAsync(engine))!.Attempt.ShouldBe(2);
        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(1), Hit(2)], attempt: 2)).StatusCode.ShouldBe(HttpStatusCode.OK);
        stale.Attempt.ShouldBe(1);

        await using var scope = Server.Services.CreateAsyncScope();
        var hits = scope.ServiceProvider.GetRequiredService<DraftHits>();
        (await hits.WriteChunkAsync(stale, 0, [Hit(7)], CancellationToken.None)).ShouldBeFalse();
        (await hits.CompleteAsync(stale, Stats, CancellationToken.None)).ShouldBeFalse();

        (await DraftHitsAsync(run.RunId)).ShouldBe(2);
        (await DbAsync(db => db.Set<ClashDraftHit>().Where(h => h.RunId == run.RunId).Select(h => h.ElementIdA).OrderBy(x => x).ToListAsync())).ShouldBe([1L, 2L]);
        var current = await RunAsync(run.RunId);
        (current.State, current.Attempt).ShouldBe((EngineRunState.Running, 2));
        (await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId).Select(p => p.ResultRunId).SingleAsync())).ShouldBeNull();
    }

    [Test]
    public async Task Engine_StaleEntityUnderLock_Reconciler_Refused_NewAttemptRowsKept()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var approve = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        var runId = (await approve.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        await ClaimAsync(engine);
        var stale = await DbAsync(db => db.Set<EngineRun>().AsNoTracking().SingleAsync(r => r.Id == runId));
        await RequeueKeepingHitsAsync(runId);
        (await ClaimAsync(engine))!.Attempt.ShouldBe(2);
        (await PostApprovedResultAsync(engine, runId, 0, false, [Hit(1), Hit(2)], null, [], [], attempt: 2)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = Server.Services.CreateAsyncScope();
        var hits = scope.ServiceProvider.GetRequiredService<DraftHits>();
        (await hits.WriteChunkAsync(stale, 1, [Hit(7)], CancellationToken.None)).ShouldBeFalse();
        (await scope.ServiceProvider.GetRequiredService<CollisionReconciler>().ApplyAsync(stale, [], [], Stats, CancellationToken.None)).ShouldBeNull();

        (await ApprovedHitsAsync(runId)).ShouldBe(2);
        (await RunAsync(runId)).State.ShouldBe(EngineRunState.Running);
        (await DbAsync(db => db.Set<ClashCollision>().CountAsync(c => c.PartId == partId))).ShouldBe(0);
    }

    /// <summary>Часть, пришедшая после отмены, не записывается: ограждение проверяет состояние под блокировкой строки прогона.</summary>
    [Test]
    public async Task Engine_ResultAfterCancel_409_NothingWritten()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(1)])).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await DraftHitsAsync(run.RunId)).ShouldBe(0);
    }

    [Test]
    public async Task Engine_TwoWorkersClaim_OneRun()
    {
        var (admin, agent, firstPart, _) = await SetupAsync();
        await using var __ = agent;
        var secondPart = await PartAsync(Server, admin, agent, "POL_K1_OV.rvt");
        await StartAsync(admin, firstPart);
        await StartAsync(admin, secondPart);

        for (var round = 0; round < 3; round++)
        {
            await DbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE clash_engine_runs SET state = {nameof(EngineRunState.Queued)}, lease_until = NULL"));
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EngineClient(Server).PostAsync("/api/engine/runs/claim", null)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            (await DbAsync(db => db.Set<EngineRun>().CountAsync(r => r.State == EngineRunState.Running))).ShouldBe(1);
        }
    }

    /// <summary>Выдача берёт advisory-блокировку, которую тест держит: пока она занята, выдача ждёт.</summary>
    [Test]
    public async Task Engine_Claim_WaitsForAdvisoryLock()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await StartAsync(admin, partId);

        Task<HttpResponseMessage> claim;
        await using (var scope = Server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({EngineEndpoints.ClaimLockKey})");
            claim = engine.PostAsync("/api/engine/runs/claim", null);
            await Task.Delay(700);
            claim.IsCompleted.ShouldBeFalse();
            await transaction.CommitAsync();
        }

        (await claim).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
