using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Финиш и отмена черновика не должны перезаписывать друг друга: оба берут блокировку строки прогона.</summary>
public class DraftRunRaceTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public DraftRunRaceTests()
    {
        Time = _time;
        Services = services => services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EngineLeaseSweeper)));
    }

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return (admin, agent, await PartAsync(Server, admin, agent, "POL_K1_AR.rvt"));
    }

    private static async Task<DraftRunSummary> StartAsync(HttpClient admin, Guid partId)
    {
        var response = await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<DraftRunSummary>();
    }

    private static Task ClaimAsync(HttpClient engine) => engine.PostAsync("/api/engine/runs/claim", null);

    [Test]
    public async Task Draft_CancelBeforeLastPart_StaysCancelled_PreviousResultKept()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        var engine = EngineClient(Server);
        var first = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await PostResultAsync(engine, first.RunId, 0, true, [Hit(1), Hit(2)], Stats)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await PostResultAsync(engine, second.RunId, 0, false, [Hit(3)])).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var last = await PostResultAsync(engine, second.RunId, 1, true, [Hit(4)], Stats);

        last.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DbAsync(db => db.Set<EngineRun>().Where(r => r.Id == second.RunId).Select(r => r.State).SingleAsync())).ShouldBe(EngineRunState.Cancelled);
        (await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId).Select(p => p.ResultRunId).SingleAsync())).ShouldBe(first.RunId);
        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == first.RunId))).ShouldBe(2);
        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == second.RunId))).ShouldBe(0);
    }

    [Test]
    public async Task Draft_Complete_AfterSweeperRequeue_409()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        var engine = EngineClient(Server);
        var run = await StartAsync(admin, partId);
        await ClaimAsync(engine);
        (await PostResultAsync(engine, run.RunId, 0, false, [Hit(1)])).StatusCode.ShouldBe(HttpStatusCode.OK);
        _time.Advance(TimeSpan.FromMinutes(3));
        await ActivatorUtilities.CreateInstance<EngineLeaseSweeper>(Server.Services).SweepAsync(CancellationToken.None);
        (await DbAsync(db => db.Set<EngineRun>().Where(r => r.Id == run.RunId).Select(r => r.State).SingleAsync())).ShouldBe(EngineRunState.Queued);

        var last = await PostResultAsync(engine, run.RunId, 1, true, [Hit(2)], Stats);

        last.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId).Select(p => p.ResultRunId).SingleAsync())).ShouldBeNull();
        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == run.RunId))).ShouldBe(0);
    }

    /// <summary>Отмена проходит между проверкой состояния и финишем: финиш ждёт блокировку и видит отмену.</summary>
    [Test]
    public async Task Draft_CompleteWaitingOnLock_CancelledMeanwhile_ReturnsFalse()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        var run = await StartAsync(admin, partId);
        await ClaimAsync(EngineClient(Server));

        await using var scope = Server.Services.CreateAsyncScope();
        var stale = await scope.ServiceProvider.GetRequiredService<ToadFlowDb>().Set<EngineRun>().SingleAsync(r => r.Id == run.RunId);
        var hits = scope.ServiceProvider.GetRequiredService<DraftHits>();

        Task<bool> complete;
        await using (var other = Server.Services.CreateAsyncScope())
        {
            var db = other.ServiceProvider.GetRequiredService<ToadFlowDb>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM clash_engine_runs WHERE id = {run.RunId} FOR UPDATE");
            complete = hits.CompleteAsync(stale, Stats, CancellationToken.None);
            await Task.Delay(500);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE clash_engine_runs SET state = {nameof(EngineRunState.Cancelled)} WHERE id = {run.RunId}");
            await transaction.CommitAsync();
        }

        (await complete).ShouldBeFalse();
        (await DbAsync(db => db.Set<EngineRun>().Where(r => r.Id == run.RunId).Select(r => r.State).SingleAsync())).ShouldBe(EngineRunState.Cancelled);
        (await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId).Select(p => p.ResultRunId).SingleAsync())).ShouldBeNull();
    }
}
