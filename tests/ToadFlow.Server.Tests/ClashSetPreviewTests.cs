using System.Net;
using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashSetPreviewTests : ServerTest
{
    private static SelectionSet Walls(string discipline = "АР") =>
        new(Guid.NewGuid(), ConfigurationDefaults.DisciplineGroupId(discipline), "Стены", [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, "ost_walls")])]);

    private static readonly ClashConfiguration Groups = new([ConfigurationDefaults.DisciplineGroup("АР"), ConfigurationDefaults.DisciplineGroup("КР")], [], [], []);

    private async Task<FakeAgent> AgentAsync()
    {
        var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return agent;
    }

    private async Task PublishAsync(FakeAgent agent, string model, int padding = 0)
    {
        var published = await ClashFixtures.PublishAsync(Server, agent, model, padding: padding);
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(published.Result, TestJson.Options));
    }

    private async Task<Guid> PartAsync(HttpClient admin, params string[] models)
    {
        var known = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "Часть" })).ReadJsonAsync<ClashPartView>();
        var ids = models.Select(name => known.Single(m => m.Name == name).Id);
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}/models", ids.Select(id => new { modelId = id, disciplineOverride = (string?)null }).ToArray()))
            .EnsureSuccessStatusCode();
        return part.Id;
    }

    private static async Task<SetPreviewResult> PreviewAsync(HttpClient client, Guid partId, SelectionSet set)
    {
        var body = JsonSerializer.Serialize(new { configuration = JsonDocument.Parse(ConfigurationJson.Serialize(Groups with { Sets = [set] })).RootElement, setId = set.Id });
        var response = await client.PostAsync($"/api/clash/parts/{partId}/preview", new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync<SetPreviewResult>();
    }


    [Test]
    public async Task Preview_CountsElementsOfSetDiscipline()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "POL_K1_AR.rvt", padding: 3);
        await PublishAsync(agent, "POL_K1_OV.rvt");
        var admin = await AdminAsync();
        var partId = await PartAsync(admin, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var viewer = await ClientAsync(UserRole.Viewer);

        var result = await PreviewAsync(viewer, partId, Walls());

        result.Matched.ShouldBe(1);
        result.Total.ShouldBe(5); // стена, труба и три заполнителя модели АР; модель ОВ не в счёт
        var wall = result.Sample.ShouldHaveSingleItem();
        wall.UniqueId.ShouldBe("wall-1");
        wall.Category.ShouldBe("OST_Walls");
    }

    [Test]
    public async Task Preview_UsesLatestSnapshot()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "POL_K1_AR.rvt");
        await PublishAsync(agent, "POL_K1_AR.rvt", padding: 10);
        var admin = await AdminAsync();
        var partId = await PartAsync(admin, "POL_K1_AR.rvt");

        (await PreviewAsync(admin, partId, Walls())).Total.ShouldBe(12);
    }

    [Test]
    public async Task Preview_SecondCallServedFromCache()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "POL_K1_AR.rvt");
        var admin = await AdminAsync();
        var partId = await PartAsync(admin, "POL_K1_AR.rvt");
        var cache = Server.Services.GetRequiredService<PartElementsCache>();
        var before = cache.Loads;

        await PreviewAsync(admin, partId, Walls());
        await PreviewAsync(admin, partId, Walls());

        (cache.Loads - before).ShouldBe(1);
    }

    /// <summary>Слепок model.tcg и model.tcp в новом временном каталоге.</summary>
    private static string SnapshotFolder(int padding)
    {
        var folder = Path.Combine(Path.GetTempPath(), "toadflow-clash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var files = ClashFixtures.ModelFiles("model", padding: padding);
        File.WriteAllBytes(Path.Combine(folder, "model.tcg"), files.Geometry);
        File.WriteAllBytes(Path.Combine(folder, "model.tcp"), files.Parameters);
        return folder;
    }

    [Test]
    public async Task ElementsCache_ConcurrentColdGet_LoadedOnce()
    {
        var folder = SnapshotFolder(padding: 5000);
        using var cache = new PartElementsCache();
        var snapshotId = Guid.NewGuid();
        using var start = new Barrier(16);

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            return cache.Get(snapshotId, folder);
        }, TaskCreationOptions.LongRunning)));

        cache.Loads.ShouldBe(1);
        snapshots.Distinct().Count().ShouldBe(1);
        cache.Get(snapshotId, folder).ShouldBeSameAs(snapshots[0]);
        cache.Loads.ShouldBe(1);
        Directory.Delete(folder, recursive: true);
    }

    [Test]
    public void ElementsCache_FailedLoad_NotCached()
    {
        var folder = SnapshotFolder(padding: 0);
        using var cache = new PartElementsCache();
        var snapshotId = Guid.NewGuid();
        var geometry = Path.Combine(folder, "model.tcg");
        var bytes = File.ReadAllBytes(geometry);
        File.Delete(geometry);

        Should.Throw<FileNotFoundException>(() => cache.Get(snapshotId, folder));
        File.WriteAllBytes(geometry, bytes);

        cache.Get(snapshotId, folder).Elements.Count.ShouldBe(2);
        cache.Loads.ShouldBe(2);
        Directory.Delete(folder, recursive: true);
    }

    [Test]
    public async Task Preview_ModelWithoutSnapshotSkipped()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "POL_K1_AR.rvt");
        using (var scope = Server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ClashStorage>().EnsureModelAsync("POL_K1_KR.rvt", "RSN://rs22/POL_K1_KR.rvt", CancellationToken.None);
        var admin = await AdminAsync();
        var partId = await PartAsync(admin, "POL_K1_AR.rvt", "POL_K1_KR.rvt");

        (await PreviewAsync(admin, partId, Walls("КР"))).Total.ShouldBe(0);
        (await PreviewAsync(admin, partId, Walls())).Matched.ShouldBe(1);
    }

    [Test]
    public async Task Preview_NotOneSet_Returns400()
    {
        var admin = await AdminAsync();
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "Часть" })).ReadJsonAsync<ClashPartView>();
        var body = ConfigurationJson.Serialize(Groups with { Sets = [Walls(), Walls()] });

        var response = await admin.PostAsync($"/api/clash/parts/{part.Id}/preview", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static StringContent GroupBody(object extra) => new(JsonSerializer.Serialize(extra), Encoding.UTF8, "application/json");

    private static JsonElement GroupsJson(ClashConfiguration config) => JsonDocument.Parse(ConfigurationJson.Serialize(config)).RootElement;

    [Test]
    public async Task Values_ReturnsCategoryCounts()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "POL_K1_AR.rvt", padding: 3);
        var admin = await AdminAsync();
        var partId = await PartAsync(admin, "POL_K1_AR.rvt");
        var viewer = await ClientAsync(UserRole.Viewer);

        var response = await viewer.PostAsync($"/api/clash/parts/{partId}/values", GroupBody(new
        {
            configuration = GroupsJson(Groups), groupId = ConfigurationDefaults.DisciplineGroupId("АР"), property = new { kind = "category" }
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var values = await response.ReadJsonAsync<ValueCount[]>();
        values.ShouldBe([new ValueCount("OST_GenericModel", 3), new ValueCount("OST_PipeCurves", 1), new ValueCount("OST_Walls", 1)]);
    }

    [Test]
    public async Task Unassigned_ReturnsByCategory()
    {
        await using var agent = await AgentAsync();
        await PublishAsync(agent, "POL_K1_AR.rvt", padding: 2);
        var admin = await AdminAsync();
        var partId = await PartAsync(admin, "POL_K1_AR.rvt");

        var response = await admin.PostAsync($"/api/clash/parts/{partId}/unassigned", GroupBody(new
        {
            configuration = GroupsJson(Groups with { Sets = [Walls()] }), groupId = ConfigurationDefaults.DisciplineGroupId("АР")
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.ReadJsonAsync<UnassignedResult>();
        result.Count.ShouldBe(3);
        result.ByCategory.ShouldBe([new ValueCount("OST_GenericModel", 2), new ValueCount("OST_PipeCurves", 1)]);
    }
}
