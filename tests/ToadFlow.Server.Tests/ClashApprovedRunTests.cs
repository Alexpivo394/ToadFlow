using System.Diagnostics;
using System.Net;
using System.Text;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashApprovedRunTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    private static async Task<Guid> ApproveAndRunAsync(HttpClient admin, Guid partId)
    {
        var response = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
    }

    private async Task<Guid> RerunAsync(Guid partId, IReadOnlyCollection<Guid>? unchecked_ = null)
    {
        using var scope = Server.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, null, unchecked_ ?? [], Actor.System, CancellationToken.None);
        return run.ShouldNotBeNull().Id;
    }

    private static async Task<EngineRunAssignment> ClaimAsync(HttpClient engine)
    {
        var response = await engine.PostAsync("/api/engine/runs/claim", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync<EngineRunAssignment>();
    }

    /// <summary>Работник берёт прогон и сдаёт его одной частью.</summary>
    private static async Task FinishAsync(HttpClient engine, Guid runId, IReadOnlyList<EngineHit> hits, IReadOnlyList<EngineGonePair>? gone = null,
        IReadOnlyList<EngineIdentityMatch>? matches = null)
    {
        (await ClaimAsync(engine)).RunId.ShouldBe(runId);
        var response = await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, gone ?? [], matches ?? []);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static EngineHit Fp(EngineHit hit, long a = 11, long b = 22) => hit with { FingerprintA = a, FingerprintB = b };

    private static EngineGonePair Gone(int i, string presenceA = "InGeometry", string presenceB = "InGeometry", bool? wouldHit = null) =>
        new("a", $"ua-{i}", "b", $"ub-{i}", presenceA, presenceB, wouldHit);

    private Task<List<ClashCollision>> CollisionsAsync(Guid partId) =>
        DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).OrderBy(c => c.Number).ToListAsync());

    private Task<EngineRun> RunAsync(Guid id) => DbAsync(db => db.Set<EngineRun>().AsNoTracking().SingleAsync(r => r.Id == id));

    [Test]
    public async Task Approve_WithRun_QueuesApprovedRun()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;

        var runId = await ApproveAndRunAsync(admin, partId);

        var run = await RunAsync(runId);
        (run.Kind, run.Version, run.State).ShouldBe((EngineRunKind.Approved, (int?)1, EngineRunState.Queued));
        var assignment = await ClaimAsync(engine);
        assignment.Kind.ShouldBe(EngineApi.Approved);
        var approved = await DbAsync(db => db.Set<ClashPartVersion>().SingleAsync(v => v.PartId == partId));
        ApproveEndpoints.SameConfiguration(assignment.ConfigurationJson, approved.ConfigurationJson).ShouldBeTrue();
    }

    [Test]
    public async Task Approved_FirstRun_AllNewNumbered1toN()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await ApproveAndRunAsync(admin, partId);

        await FinishAsync(engine, runId, [Fp(Hit(1)), Fp(Hit(2)), Fp(Hit(3))]);

        var collisions = await CollisionsAsync(partId);
        collisions.Select(c => (c.Number, c.Status, c.FoundNow, c.FirstVersion)).ShouldBe(
            [(1, CollisionStatus.New, true, 1), (2, CollisionStatus.New, true, 1), (3, CollisionStatus.New, true, 1)]);
        collisions[0].FingerprintA.ShouldBe(11);
        collisions[0].DisciplinePair.ShouldBe("АР × ОВ");
        (await DbAsync(db => db.Set<ClashCollisionSample>().CountAsync())).ShouldBe(3);
        (await DbAsync(db => db.Set<ClashCollisionEvent>().CountAsync(e => e.Kind == CollisionEventKind.Found && e.Version == 1))).ShouldBe(3);
        var run = await RunAsync(runId);
        run.State.ShouldBe(EngineRunState.Succeeded);
        run.Summary.ShouldNotBeNull().ShouldBe(new ApprovedRunSummary(3, 3, 0, 0, 0, 0, 0, 0, 0));
        (await DbAsync(db => db.Set<ClashPart>().SingleAsync(p => p.Id == partId))).ApprovedRunId.ShouldBe(runId);
        (await DbAsync(db => db.Set<ClashApprovedHit>().CountAsync())).ShouldBe(0);
        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.RunId == runId))).ShouldBe(0);
    }

    [Test]
    public async Task Approved_RerunSameGeometry_NewBecomesActive_NoSamples()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1)), Fp(Hit(2))]);

        var second = await RerunAsync(partId);
        await FinishAsync(engine, second, [Fp(Hit(2)), Fp(Hit(1))]);

        var collisions = await CollisionsAsync(partId);
        collisions.Select(c => (c.UniqueIdA, c.Number, c.Status)).ShouldBe([("ua-1", 1, CollisionStatus.Active), ("ua-2", 2, CollisionStatus.Active)]);
        collisions.ShouldAllBe(c => c.LastFoundRunId == second);
        (await DbAsync(db => db.Set<ClashCollisionSample>().CountAsync())).ShouldBe(2);
        (await RunAsync(second)).Summary.ShouldNotBeNull().ShouldBe(new ApprovedRunSummary(2, 0, 2, 0, 0, 0, 0, 0, 0));
    }

    [Test]
    public async Task Approved_DepthChanged_NewSample()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1))]);

        await FinishAsync(engine, await RerunAsync(partId), [Fp(Hit(1, depth: 45))]);

        (await DbAsync(db => db.Set<ClashCollisionSample>().OrderBy(s => s.At).Select(s => s.DepthMm).ToListAsync())).ShouldBe([30f, 45f]);
        (await CollisionsAsync(partId)).Single().DepthMm.ShouldBe(45f);
    }

    [Test]
    public async Task Approved_PairGone_Deleted_Fixed()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1)), Fp(Hit(2))]);

        var second = await RerunAsync(partId);
        await FinishAsync(engine, second, [Fp(Hit(1))], [Gone(2, presenceB: "Absent")]);

        var fixedOne = (await CollisionsAsync(partId)).Single(c => c.Number == 2);
        (fixedOne.Status, fixedOne.FoundNow).ShouldBe((CollisionStatus.Fixed, false));
        var status = await DbAsync(db => db.Set<ClashCollisionEvent>().SingleAsync(e => e.CollisionId == fixedOne.Id && e.Kind == CollisionEventKind.Status));
        (status.FromStatus, status.ToStatus, status.UserId).ShouldBe((CollisionStatus.New, CollisionStatus.Fixed, (Guid?)null));
        (await RunAsync(second)).Summary.ShouldNotBeNull().Fixed.ShouldBe(1);
    }

    [Test]
    public async Task Approved_FixedFoundAgain_Returned()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1))]);
        await FinishAsync(engine, await RerunAsync(partId), [], [Gone(1)]);
        (await CollisionsAsync(partId)).Single().Status.ShouldBe(CollisionStatus.Fixed);

        var third = await RerunAsync(partId);
        await FinishAsync(engine, third, [Fp(Hit(1))]);

        var collision = (await CollisionsAsync(partId)).Single();
        (collision.Number, collision.Status, collision.FoundNow).ShouldBe((1, CollisionStatus.Returned, true));
        (await RunAsync(third)).Summary.ShouldNotBeNull().Returned.ShouldBe(1);
    }

    [Test]
    public async Task Approved_ToleranceRaised_GoneByRules()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1)), Fp(Hit(2))]);
        var draft = ConfigurationJson.Deserialize((await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Configuration.GetRawText());
        (await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(
            ConfigurationJson.Serialize(draft with { Matrix = draft.Matrix.Select(c => c with { ToleranceMm = 50 }).ToList() }), Encoding.UTF8,
            "application/json"))).EnsureSuccessStatusCode();

        var second = await ApproveAndRunAsync(admin, partId);
        await FinishAsync(engine, second, [Fp(Hit(2))], [Gone(1, wouldHit: true)]);

        var collisions = await CollisionsAsync(partId);
        collisions.Select(c => c.Status).ShouldBe([CollisionStatus.GoneByRules, CollisionStatus.Active]);
        collisions[1].LastFoundVersion.ShouldBe(2);
        (await DbAsync(db => db.Set<ClashCollisionEvent>().CountAsync(e => e.CollisionId == collisions[1].Id && e.Kind == CollisionEventKind.Found)))
            .ShouldBe(2); // найдена версией 1 и версией 2
    }

    [Test]
    public async Task Approved_UncheckedModel_Unchecked()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1))]);

        // Гибель выгрузки модели B в цикле: пару, которую не нашли, не судим.
        var modelB = Guid.NewGuid();
        await DbAsync(db => db.Set<ClashCollision>().ExecuteUpdateAsync(c => c.SetProperty(x => x.ModelB, modelB.ToString("N"))));
        await FinishAsync(engine, await RerunAsync(partId, [modelB]), [], [new EngineGonePair("a", "ua-1", modelB.ToString("N"), "ub-1", "InGeometry", "Absent", null)]);

        (await CollisionsAsync(partId)).Single().Status.ShouldBe(CollisionStatus.Unchecked);
    }

    [Test]
    public async Task Approved_FakeNotFound_StaysFakeFoundNowFalse()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1))]);
        await DbAsync(db => db.Set<ClashCollision>().ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fake)));

        await FinishAsync(engine, await RerunAsync(partId), [], [Gone(1, presenceA: "Absent")]);

        var collision = (await CollisionsAsync(partId)).Single();
        (collision.Status, collision.FoundNow).ShouldBe((CollisionStatus.Fake, false));
    }

    [Test]
    public async Task Approved_Suggestion_SetOnNewCollision()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1))]);

        var recreated = Fp(Hit(1)) with { UniqueIdA = "ua-1-new" };
        await FinishAsync(engine, await RerunAsync(partId), [recreated], [Gone(1, presenceA: "Absent")], [new EngineIdentityMatch("a", "ua-1", "ua-1-new")]);

        var collisions = await CollisionsAsync(partId);
        collisions.Select(c => (c.Number, c.Status)).ShouldBe([(1, CollisionStatus.Fixed), (2, CollisionStatus.New)]);
        collisions[1].SuggestedPredecessorId.ShouldBe(collisions[0].Id);
        (await DbAsync(db => db.Set<ClashCollisionEvent>().SingleAsync(e => e.CollisionId == collisions[1].Id && e.Kind == CollisionEventKind.Suggestion)))
            .Text.ShouldContain("№1");
    }

    [Test]
    public async Task Basis_ReturnsOpenPairsAndPreviousConfig()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await ApproveAndRunAsync(admin, partId);
        await FinishAsync(engine, first, [Fp(Hit(1)), Fp(Hit(2))]);
        await FinishAsync(engine, await RerunAsync(partId), [Fp(Hit(1))], [Gone(2)]);

        var third = await RerunAsync(partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(third);
        var basis = await engine.GetJsonAsync<EngineBasis>($"/api/engine/runs/{third}/basis?attempt=1");

        var open = basis.Open.ShouldHaveSingleItem();
        (open.UniqueIdA, open.FoundByPrevious, open.FingerprintA).ShouldBe(("ua-1", true, 11L));
        basis.PreviousConfigurationJson.ShouldNotBeNull();
        basis.PreviousModels.Count.ShouldBe(2);
        (await RunAsync(third)).PreviousRunId.ShouldNotBeNull();
    }

    [Test]
    public async Task Basis_ReturnsPreviousEngineVersion()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await ApproveAndRunAsync(admin, partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(first);
        (await PostApprovedResultAsync(engine, first, 0, true, [Fp(Hit(1))], Stats with { EngineVersion = 7 }, [], [])).EnsureSuccessStatusCode();

        var second = await RerunAsync(partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(second);

        (await engine.GetJsonAsync<EngineBasis>($"/api/engine/runs/{second}/basis?attempt=1")).PreviousEngineVersion.ShouldBe(7);
    }

    [Test]
    public async Task Basis_FirstRun_Empty()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await ApproveAndRunAsync(admin, partId);
        await ClaimAsync(engine);

        var basis = await engine.GetJsonAsync<EngineBasis>($"/api/engine/runs/{runId}/basis?attempt=1");

        basis.PreviousConfigurationJson.ShouldBeNull();
        basis.PreviousModels.ShouldBeEmpty();
        basis.Open.ShouldBeEmpty();
    }

    [Test]
    public async Task ApprovedRun_QueuedTwice_OneRun()
    {
        var (admin, agent, partId, _) = await SetupAsync();
        await using var __ = agent;
        var first = await ApproveAndRunAsync(admin, partId);

        var second = await RerunAsync(partId);

        second.ShouldBe(first);
        (await DbAsync(db => db.Set<EngineRun>().CountAsync(r => r.Kind == EngineRunKind.Approved))).ShouldBe(1);
    }

    [Test]
    public async Task DraftRun_DoesNotCancelQueuedApproved()
    {
        var (admin, agent, partId, _) = await SetupAsync();
        await using var __ = agent;
        var approved = await ApproveAndRunAsync(admin, partId);

        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run/cancel", null)).EnsureSuccessStatusCode();

        (await RunAsync(approved)).State.ShouldBe(EngineRunState.Queued);
        (await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run")).RunId.ShouldNotBe(approved);
    }

    [Test]
    public async Task Prune_KeepsApprovedRunSnapshots()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        (await admin.PutJsonAsync("/api/clash/settings", new { storage = new { keepSnapshots = 1 } })).EnsureSuccessStatusCode();
        var runId = await ApproveAndRunAsync(admin, partId);
        await FinishAsync(engine, runId, [Fp(Hit(1))]);
        var kept = (await RunAsync(runId)).Models.Select(m => m.SnapshotId).ToList();

        foreach (var model in new[] { "POL_K1_AR.rvt", "POL_K1_OV.rvt" })
        {
            var published = await ClashFixtures.PublishAsync(Server, agent, model);
            await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded);
        }

        foreach (var id in kept) (await engine.GetAsync($"/api/engine/snapshots/{id}/geometry")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    ///     Прогон ждёт в очереди, а модели выгрузились заново: тот же прогон берёт новые слепки и запуск нового цикла. Модель, не
    ///     проверенная раньше и выгрузившаяся теперь, перестаёт быть не проверенной; модель, не выгрузившаяся в новом цикле, — становится.
    /// </summary>
    [Test]
    public async Task Enqueue_QueuedSameVersion_RefreshesSnapshotsUncheckedTrigger()
    {
        var (admin, agent, partId, _) = await SetupAsync();
        await using var __ = agent;
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models;
        var (ar, ov) = (models.Single(m => m.Name == "POL_K1_AR.rvt").ModelId, models.Single(m => m.Name == "POL_K1_OV.rvt").ModelId);
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false })).EnsureSuccessStatusCode();
        var first = await RerunAsync(partId, [ar]);

        var published = await ClashFixtures.PublishAsync(Server, agent, "POL_K1_AR.rvt");
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded);
        var trigger = Guid.NewGuid();
        EngineRun? second;
        using (var scope = Server.Services.CreateScope())
            second = await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, trigger, [ov], Actor.System, CancellationToken.None);

        second.ShouldNotBeNull().Id.ShouldBe(first);
        var run = await RunAsync(first);
        run.Models.Single(m => m.ModelId == ar).SnapshotId.ShouldBe(published.Result.SnapshotId);
        run.UncheckedModels.ShouldBe([ov]);
        run.TriggerRunId.ShouldBe(trigger);
    }

    [Test]
    [Explicit]
    public async Task Reconcile_200kHits_Under30s()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var hits = Enumerable.Range(1, 200_000).Select(i => Fp(Hit(i))).ToList();
        var first = await ApproveAndRunAsync(admin, partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(first);
        var clock = Stopwatch.StartNew();
        for (var part = 0; part < 4; part++)
            (await PostApprovedResultAsync(engine, first, part, part == 3, hits.Skip(part * 50_000).Take(50_000).ToList(), part == 3 ? Stats : null, [], []))
                .EnsureSuccessStatusCode();
        var firstSeconds = clock.Elapsed.TotalSeconds;

        var second = await RerunAsync(partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(second);
        clock.Restart();
        for (var part = 0; part < 4; part++)
            (await PostApprovedResultAsync(engine, second, part, part == 3, hits.Skip(part * 50_000).Take(50_000).ToList(), part == 3 ? Stats : null, [], []))
                .EnsureSuccessStatusCode();
        var secondSeconds = clock.Elapsed.TotalSeconds;

        Console.WriteLine($"Сверка 200 тыс.: первый прогон {firstSeconds:0.0} с, повтор {secondSeconds:0.0} с");
        (await DbAsync(db => db.Set<ClashCollision>().CountAsync(c => c.Status == CollisionStatus.Active))).ShouldBe(200_000);
        firstSeconds.ShouldBeLessThan(30);
        secondSeconds.ShouldBeLessThan(30);
    }

    [Test]
    public async Task Approved_QueuedWhileRunning_BasisIsRunFinishedBeforeClaim()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1))]);
        var running = await RerunAsync(partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(running);
        var queued = await RerunAsync(partId);
        (await PostApprovedResultAsync(engine, running, 0, true, [Fp(Hit(1)), Fp(Hit(2))], Stats, [], [])).EnsureSuccessStatusCode();

        (await ClaimAsync(engine)).RunId.ShouldBe(queued);
        var basis = await engine.GetJsonAsync<EngineBasis>($"/api/engine/runs/{queued}/basis?attempt=1");

        (await RunAsync(queued)).PreviousRunId.ShouldBe(running);
        basis.Open.Where(p => p.FoundByPrevious).Select(p => p.UniqueIdA).Order().ShouldBe(["ua-1", "ua-2"]);
    }

    [Test]
    public async Task Approved_CancelledBeforeReconcile_CollisionsUntouched()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await FinishAsync(engine, await ApproveAndRunAsync(admin, partId), [Fp(Hit(1)), Fp(Hit(2))]);
        var rerun = await RerunAsync(partId);
        (await ClaimAsync(engine)).RunId.ShouldBe(rerun);
        using var scope = Server.Services.CreateScope();
        // Как в приёме последней части: прогон прочитан ещё в работе, отмена приходит до сверки.
        var run = await scope.ServiceProvider.GetRequiredService<ToadFlow.Server.Data.ToadFlowDb>().Set<EngineRun>().SingleAsync(r => r.Id == rerun);
        (await admin.PostAsync($"/api/clash/parts/{partId}/approved/run/cancel", null)).EnsureSuccessStatusCode();

        var summary = await scope.ServiceProvider.GetRequiredService<CollisionReconciler>().ApplyAsync(run, [], [], Stats, CancellationToken.None);

        summary.ShouldBeNull();
        (await CollisionsAsync(partId)).ShouldAllBe(c => c.Status == CollisionStatus.New && c.FoundNow);
        (await RunAsync(rerun)).State.ShouldBe(EngineRunState.Cancelled);
    }
}
