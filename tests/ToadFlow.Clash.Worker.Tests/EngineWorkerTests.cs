using System.Text;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Clash.Worker.Tests;

public class EngineWorkerTests : ServerTest
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-tests", Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void CleanCache()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
    }

    private (EngineWorker Worker, EngineClient Client) Worker()
    {
        var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = _cache, Threads = 2 };
        return (new EngineWorker(client, new SnapshotCache(client, options), options, NullLogger<EngineWorker>.Instance), client);
    }

    /// <summary>
    ///     Модели АР и ОВ из одинаковых файлов фикстуры: стена и труба одной модели совпадают со стеной и трубой другой —
    ///     две коллизии АР × ОВ глубиной 100 мм (коробки 100 мм).
    /// </summary>
    private async Task<(HttpClient Admin, Guid PartId)> PartAsync()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return (admin, await ClashDraftFixtures.PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt"));
    }

    private static async Task StartAsync(HttpClient admin, Guid partId) =>
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();

    [Test]
    public async Task Worker_RunsDraft_ResultVisibleInSummary()
    {
        var (admin, partId) = await PartAsync();
        await StartAsync(admin, partId);
        var (worker, _) = Worker();

        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);

        var run = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run");
        run.State.ShouldBe(EngineRunState.Succeeded);
        run.Stats.ShouldNotBeNull().Elements.ShouldBe(4);
        run.Stats.PeakWorkingSetBytes.ShouldBeGreaterThan(0);
        var summary = await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary");
        summary.Total.ShouldBe(2);
        summary.Checks.Where(c => c.Count > 0).ShouldHaveSingleItem().Count.ShouldBe(2);
        summary.Checks.Single(c => c.Count > 0).GroupAName.ShouldBe("АР");
        (await admin.GetJsonAsync<SetPairCount[]>($"/api/clash/parts/{partId}/draft/checks/{summary.Checks.Single(c => c.Count > 0).Key}/setpairs"))
            .ShouldHaveSingleItem().SetAName.ShouldBe("Все");
        var hits = (await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits")).Items;
        hits.Select(h => (h.CategoryA, h.CategoryB)).Order().ShouldBe([("OST_PipeCurves", "OST_PipeCurves"), ("OST_Walls", "OST_Walls")]);
        hits.ShouldAllBe(h => h.DepthMm > 20 && h.LevelA == "Этаж 1" && h.ModelNameA != h.ModelNameB && h.Checks.Count == 1);
        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBeNull(); // очередь пуста
    }

    [Test]
    public async Task Worker_ToleranceAboveDepth_NoHit()
    {
        var (admin, partId) = await PartAsync();
        var view = await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft");
        var config = ConfigurationJson.Deserialize(view.Configuration.GetRawText());
        config = config with { Matrix = config.Matrix.Select(c => c with { ToleranceMm = 200 }).ToList() };
        (await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
        await StartAsync(admin, partId);

        (await Worker().Worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);

        (await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary")).Total.ShouldBe(0);
    }

    [Test]
    public async Task Worker_SecondRun_UsesCache()
    {
        var (admin, partId) = await PartAsync();
        var (worker, client) = Worker();
        await StartAsync(admin, partId);
        await worker.RunOnceAsync(CancellationToken.None);
        var downloads = client.Downloads;

        await StartAsync(admin, partId);
        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);

        downloads.ShouldBe(4); // два слепка по два файла
        client.Downloads.ShouldBe(downloads);
    }

    [Test]
    public async Task Worker_CorruptSnapshot_FailsWithoutRetry()
    {
        var (admin, partId) = await PartAsync();
        foreach (var file in Directory.GetFiles(Server.ClashStorageRoot, "model.tcg", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Array.Fill(bytes, (byte)0xAB, 0, 16);
            await File.WriteAllBytesAsync(file, bytes);
        }

        await StartAsync(admin, partId);

        (await Worker().Worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Failed);

        var run = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run");
        run.State.ShouldBe(EngineRunState.Failed);
        run.Error.ShouldNotBeNull().ShouldContain("повреждён");
    }

    [Test]
    public async Task Worker_MisalignedModels_FailsWithoutRetry()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await ClashDraftFixtures.PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        // ОВ без общих координат: та же геометрия, но в 6 000 км от АР.
        var published = await ClashFixtures.PublishAsync(Server, agent, "POL_K1_OV.rvt", custom: key =>
        {
            var header = new ModelHeader(key, @"C:\models\ОВ.rvt", 2022, DateTimeOffset.UtcNow, true, "Navisworks", 6e9, 6e9, 0);
            return new ModelGeometry(header,
                [ClashFixtures.Box(new ElementInfo("wall-1", 1, "OST_Walls", "Стена", "200", "Этаж 1", "АР", []), 0, 0, 0, 100)], ["wall-1"]);
        });
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded);
        await StartAsync(admin, partId);

        (await Worker().Worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Failed);

        var run = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run");
        run.State.ShouldBe(EngineRunState.Failed);
        run.Error.ShouldNotBeNull().ShouldContain("POL_K1_OV");
        run.Error.ShouldContain("общие координаты");
        run.Error.ShouldContain("км");
    }

    [Test]
    public async Task Worker_ServerCancels_StopsRun()
    {
        var (admin, partId) = await PartAsync();
        await StartAsync(admin, partId);
        var (worker, client) = Worker();
        var assignment = (await client.ClaimAsync(CancellationToken.None)).ShouldNotBeNull();
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run/cancel", null)).EnsureSuccessStatusCode();

        (await worker.ProcessAsync(assignment, CancellationToken.None)).ShouldBe(RunOutcome.Cancelled);

        (await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run")).State.ShouldBe(EngineRunState.Cancelled);
        (await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary")).Total.ShouldBe(0);
    }
}
