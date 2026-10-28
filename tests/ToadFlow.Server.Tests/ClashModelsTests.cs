using System.Net;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashModelsTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashModelsTests() => Time = _time;

    private async Task<FakeAgent> AgentAsync()
    {
        var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return agent;
    }

    /// <summary>Выгрузка с завершением задания: агент снова свободен для следующего.</summary>
    private async Task<(Guid JobId, ClashModel Model, ClashExtractResult Result)> PublishAsync(FakeAgent agent, string model, int hoursAgo, int padding = 0)
    {
        var published = await ClashFixtures.PublishAsync(Server, agent, model, DateTimeOffset.UtcNow.AddHours(-hoursAgo), padding);
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(published.Result, TestJson.Options));
        return published;
    }

    [Test]
    public async Task Models_ListsLatestSnapshot()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "АР.rvt", 5);
        var latest = await PublishAsync(agent, "АР.rvt", 1);
        await PublishAsync(agent, "ОВ.rvt", 2);
        var viewer = await ClientAsync(UserRole.Viewer);

        var models = await viewer.GetJsonAsync<ClashModelView[]>("/api/clash/models");

        models.Select(m => m.Name).Order().ShouldBe(["АР.rvt", "ОВ.rvt"]);
        var ar = models.Single(m => m.Name == "АР.rvt");
        ar.SnapshotCount.ShouldBe(2);
        ar.Latest.ShouldNotBeNull().Id.ShouldBe(latest.Result.SnapshotId);
        ar.Latest.Elements.ShouldBe(latest.Result.Elements);
        ar.ModelPath.ShouldBe("RSN://rs22/ЖК/АР.rvt");

        var snapshots = await viewer.GetJsonAsync<ClashSnapshotView[]>($"/api/clash/models/{ar.Id}/snapshots");
        snapshots.Length.ShouldBe(2);
        snapshots[0].Id.ShouldBe(latest.Result.SnapshotId);
        snapshots[0].ExtractedAt.ShouldBeGreaterThan(snapshots[1].ExtractedAt);
        snapshots[0].JobId.ShouldBe(latest.JobId);
    }

    [Test]
    public async Task Catalog_MergesLatestSnapshotsOfSelectedModels()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "АР.rvt", 5, padding: 7); // старый слепок: в каталог не попадает
        var ar = await PublishAsync(agent, "АР.rvt", 1);
        var ov = await PublishAsync(agent, "ОВ.rvt", 2, padding: 3);
        var viewer = await ClientAsync(UserRole.Viewer);

        var single = await viewer.GetJsonAsync<ModelCatalog>($"/api/clash/catalog?modelId={ar.Model.Id}");
        var both = await viewer.GetJsonAsync<ModelCatalog>($"/api/clash/catalog?modelId={ar.Model.Id}&modelId={ov.Model.Id}");
        var all = await viewer.GetJsonAsync<ModelCatalog>("/api/clash/catalog");

        single.Categories.Select(c => c.Key).ShouldBe(["OST_PipeCurves", "OST_Walls"]);
        single.Categories.Single(c => c.Key == "OST_Walls").Count.ShouldBe(1);
        both.Categories.Single(c => c.Key == "OST_Walls").Count.ShouldBe(2);
        both.Categories.Single(c => c.Key == "OST_GenericModel").Count.ShouldBe(3);
        all.Categories.ShouldBe(both.Categories);
        both.Parameters.Select(p => p.Name).ShouldBe(["Диаметр", "Марка"]);
    }

    [Test]
    public async Task Settings_ReduceKeep_PrunesImmediately()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "АР.rvt", 3);
        await PublishAsync(agent, "АР.rvt", 2);
        var newest = await PublishAsync(agent, "АР.rvt", 1);
        var admin = await AdminAsync();

        (await admin.PutJsonAsync("/api/clash/settings", new ClashStorageSettings(1))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var snapshots = await DbAsync(db => db.Set<ClashSnapshot>().AsNoTracking().ToListAsync());
        snapshots.ShouldHaveSingleItem().Id.ShouldBe(newest.Result.SnapshotId);
        Directory.GetDirectories(Path.Combine(Server.ClashStorageRoot, newest.Model.Id.ToString("N"))).ShouldHaveSingleItem();
        (await admin.GetJsonAsync<ClashStorageSettings>("/api/clash/settings")).KeepSnapshots.ShouldBe(1);
    }

    [Test]
    public async Task Settings_RequireAdmin()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var @operator = await ClientAsync(UserRole.Operator);
        var admin = await AdminAsync();

        (await viewer.GetJsonAsync<ClashStorageSettings>("/api/clash/settings")).KeepSnapshots.ShouldBe(3);
        (await @operator.PutJsonAsync("/api/clash/settings", new ClashStorageSettings(5))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var invalid = await admin.PutJsonAsync("/api/clash/settings", new ClashStorageSettings(0));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).ShouldContain("от 1 до 20");
    }

    [Test]
    public async Task Cleaner_RemovesStaleUploads()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);
        var started = await ClashFixtures.AgentClient(Server, agent).PostJsonAsync("/api/agent/clash/uploads", files.Request(jobId));
        var upload = await started.ReadJsonAsync<ClashUploadState>();
        var uploadFolder = Path.Combine(Server.ClashStorageRoot, "uploads", upload.UploadId.ToString("N"));
        var orphan = Path.Combine(Server.ClashStorageRoot, model.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
        var cleaner = ActivatorUtilities.CreateInstance<ClashUploadCleaner>(Server.Services);

        await cleaner.CleanAsync(CancellationToken.None);
        Directory.Exists(uploadFolder).ShouldBeTrue(); // свежая загрузка ещё может продолжиться
        Directory.Exists(orphan).ShouldBeFalse(); // папка без слепка в базе

        _time.Advance(TimeSpan.FromHours(25));
        await cleaner.CleanAsync(CancellationToken.None);

        Directory.Exists(uploadFolder).ShouldBeFalse();
        (await DbAsync(db => db.Set<ClashUpload>().CountAsync())).ShouldBe(0);
    }

    [Test]
    public async Task JobCompleted_PublishesEvent()
    {
        await using var agent = await AgentAsync();

        await PublishAsync(agent, "АР.rvt", 1);

        await EventuallyAsync(async () => await DbAsync(db => db.SystemEvents.AnyAsync(e => e.Text.Contains("Выгружена модель АР.rvt"))));
    }
}
