using System.Text;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Clash.Worker.Tests;

/// <summary>
///     Утверждённые прогоны работником на моделях АР и ОВ из фикстуры: стена и труба одной модели совпадают со стеной и трубой
///     другой — две коллизии глубиной 100 мм.
/// </summary>
public class ApprovedRunTests : ServerTest
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-tests", Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void CleanCache()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
    }

    private EngineWorker Worker()
    {
        var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = _cache, Threads = 2 };
        return new EngineWorker(client, new SnapshotCache(client, options), options, NullLogger<EngineWorker>.Instance);
    }

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> PartAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return (admin, agent, await ClashDraftFixtures.PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt"));
    }

    private static async Task ApproveAsync(HttpClient admin, Guid partId) =>
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();

    private async Task RerunAsync(Guid partId)
    {
        using var scope = Server.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, null, [], Actor.System, CancellationToken.None)).ShouldNotBeNull();
    }

    private async Task<EngineRun> RunOnceAsync()
    {
        (await Worker().RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
        return await DbAsync(db => db.Set<EngineRun>().AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstAsync(r => r.Kind == EngineRunKind.Approved));
    }

    private Task<List<ClashCollision>> CollisionsAsync(Guid partId) =>
        DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).OrderBy(c => c.Number).ToListAsync());

    /// <summary>Новая выгрузка ОВ: стена как была, труба — по <paramref name="pipe" /> (null — нет в геометрии), опись — <paramref name="inventory" />.</summary>
    private Task PublishOvAsync(FakeAgent agent, string? pipe, params string[] inventory) =>
        PublishAsync(agent, key =>
        {
            var header = new ModelHeader(key, @"C:\models\ОВ.rvt", 2022, DateTimeOffset.UtcNow, true, "Navisworks", 0, 0, 0);
            var elements = new List<ElementGeometry> { ClashFixtures.Box(new ElementInfo("wall-1", 1, "OST_Walls", "Стена", "200", "Этаж 1", "АР", []), 0, 0, 0, 100) };
            if (pipe is not null)
                elements.Add(ClashFixtures.Box(new ElementInfo(pipe, 2, "OST_PipeCurves", "Труба", "Сталь", "Этаж 1", "ВК", []), 1000, 0, 0, 100));
            return new ModelGeometry(header, elements, ["wall-1", .. inventory]);
        });

    private async Task PublishAsync(FakeAgent agent, Func<string, ModelGeometry> custom)
    {
        var published = await ClashFixtures.PublishAsync(Server, agent, "POL_K1_OV.rvt", custom: custom);
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded);
    }

    [Test]
    public async Task Worker_Approved_SecondRun_ReusesPairs()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        var first = await RunOnceAsync();

        await RerunAsync(partId);
        var second = await RunOnceAsync();

        first.Stats.ShouldNotBeNull().ReusedPairs.ShouldBe(0);
        second.Stats.ShouldNotBeNull().ReusedPairs.ShouldBe(second.Stats.CandidatePairs);
        second.Stats.ReusedPairs.ShouldBeGreaterThan(0);
        var collisions = await CollisionsAsync(partId);
        collisions.Select(c => (c.Number, c.Status)).ShouldBe([(1, CollisionStatus.Active), (2, CollisionStatus.Active)]);
        collisions.ShouldAllBe(c => c.FingerprintA != 0 && c.DepthMm > 20);
    }

    [Test]
    public async Task Worker_Approved_OtherEngineVersion_FullRun()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        var first = await RunOnceAsync();
        first.Stats.ShouldNotBeNull().EngineVersion.ShouldBe(Alexpivo394.Revit.Clash.Engine.ClashEngine.AlgorithmVersion);
        // Прошлый прогон считал прежний алгоритм: его пары не переиспользуются.
        await DbAsync(async db =>
        {
            var run = await db.Set<EngineRun>().SingleAsync(r => r.Id == first.Id);
            run.Stats = run.Stats! with { EngineVersion = 0 };
            await db.SaveChangesAsync();
        });

        await RerunAsync(partId);
        var second = await RunOnceAsync();

        second.Stats.ShouldNotBeNull().ReusedPairs.ShouldBe(0);
        second.Stats.CandidatePairs.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Worker_Approved_DeletedElement_GoneAbsent()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        await RunOnceAsync();

        await PublishOvAsync(agent, pipe: null);
        await RerunAsync(partId);
        await RunOnceAsync();

        var pipes = (await CollisionsAsync(partId)).Single(c => c.CategoryA == "OST_PipeCurves");
        (pipes.Status, pipes.FoundNow).ShouldBe((CollisionStatus.Fixed, false));
        (await CollisionsAsync(partId)).Single(c => c.CategoryA == "OST_Walls").Status.ShouldBe(CollisionStatus.Active);
    }

    [Test]
    public async Task Worker_Approved_HiddenElement_GoneInventoryOnly()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        await RunOnceAsync();

        await PublishOvAsync(agent, pipe: null, "pipe-1");
        await RerunAsync(partId);
        await RunOnceAsync();

        (await CollisionsAsync(partId)).Single(c => c.CategoryA == "OST_PipeCurves").Status.ShouldBe(CollisionStatus.GoneByRules);
    }

    [Test]
    public async Task Worker_Approved_ToleranceRaised_PreviousWouldHit()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        await RunOnceAsync();
        var config = ConfigurationJson.Deserialize((await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Configuration.GetRawText());
        config = config with { Matrix = config.Matrix.Select(c => c with { ToleranceMm = 200 }).ToList() };
        (await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();

        await ApproveAsync(admin, partId);
        var second = await RunOnceAsync();

        second.Stats.ShouldNotBeNull().ReusedPairs.ShouldBe(0); // версия сменилась — полный пересчёт
        (await CollisionsAsync(partId)).ShouldAllBe(c => c.Status == CollisionStatus.GoneByRules);
    }

    /// <summary>
    ///     Раздел модели ОВ сменён на ВК, а в новой матрице клетки АР × ВК нет: пары ушли из-за правил. Перепроверка прошлой конфигурацией
    ///     идёт с прошлыми разделами — иначе элементы ВК не попали бы в группу ОВ и пары стали бы «исправленными».
    /// </summary>
    [Test]
    public async Task Worker_Approved_DisciplineChanged_GoneByRules()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        await RunOnceAsync();

        var known = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        (await admin.PutJsonAsync($"/api/clash/parts/{partId}/models", new[]
        {
            new { modelId = known.Single(m => m.Name == "POL_K1_AR.rvt").Id, disciplineOverride = (string?)null },
            new { modelId = known.Single(m => m.Name == "POL_K1_OV.rvt").Id, disciplineOverride = (string?)"ВК" }
        })).EnsureSuccessStatusCode();
        var disciplines = (await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Disciplines;
        var any = new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null);
        var config = ConfigurationDefaults.FromDisciplines(disciplines.Select(d => new DisciplineSet(Guid.NewGuid(), d, "Все", [new ConditionGroup([any])])).ToList(),
            [], []);
        config = config with { Matrix = config.Groups.Select(g => new MatrixCell(g.Id, g.Id, true)).ToList() };
        (await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();

        await ApproveAsync(admin, partId);
        await RunOnceAsync();

        (await CollisionsAsync(partId)).ShouldAllBe(c => c.Status == CollisionStatus.GoneByRules);
    }

    [Test]
    public async Task Worker_Approved_RecreatedElement_MatchSuggested()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        await RunOnceAsync();

        await PublishOvAsync(agent, pipe: "pipe-1-new", "pipe-1-new");
        await RerunAsync(partId);
        await RunOnceAsync();

        var collisions = await CollisionsAsync(partId);
        var old = collisions.Single(c => c.UniqueIdA == "pipe-1" && c.UniqueIdB == "pipe-1");
        var recreated = collisions.Single(c => c.UniqueIdA == "pipe-1-new" || c.UniqueIdB == "pipe-1-new");
        old.Status.ShouldBe(CollisionStatus.Fixed);
        (recreated.Status, recreated.SuggestedPredecessorId).ShouldBe((CollisionStatus.New, old.Id));
    }

    [Test]
    public async Task Worker_Approved_PreviousSnapshotMissing_FullRun()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        var first = await RunOnceAsync();
        await PublishOvAsync(agent, pipe: "pipe-1");

        // Слепок ОВ прошлого прогона пропал с сервера и из кеша работника.
        var ov = first.Models.Single(m => m.ModelName == "POL_K1_OV.rvt");
        using (var scope = Server.Services.CreateScope())
            Directory.Delete(scope.ServiceProvider.GetRequiredService<ClashStorage>().SnapshotFolder(ov.ModelId, ov.SnapshotId), true);
        CleanCache();

        await RerunAsync(partId);
        var second = await RunOnceAsync();

        second.State.ShouldBe(EngineRunState.Succeeded);
        second.Stats.ShouldNotBeNull().ReusedPairs.ShouldBe(0);
        (await CollisionsAsync(partId)).ShouldAllBe(c => c.Status == CollisionStatus.Active);
    }
}
