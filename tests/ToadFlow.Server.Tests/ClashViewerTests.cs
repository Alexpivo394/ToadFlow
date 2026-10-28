using System.Net;
using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Viewer;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashViewerTests : ServerTest
{
    private sealed record Setup(HttpClient Admin, Guid PartId, Guid RunId, string Ar, string Ov, double X, double Y, double Z);

    private static ElementInfo Info(string uid, string category = "OST_GenericModel") => new(uid, uid.Length, category, "Семейство", "Тип", "Этаж 1", null, []);

    /// <summary>
    ///     Часть АР + ОВ после прогона с одной коллизией A × B. В АР: A — куб 100 мм у начала модели, near — в 3 м, far — в 8 м.
    ///     В ОВ: B — куб 100 мм, сдвинутый на 50 мм от A по X в общей системе.
    /// </summary>
    private async Task<Setup> SceneAsync(double originAr = 0, double originOv = 0)
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        ModelGeometry Geometry(string name, string key)
        {
            var ar = name.Contains("_AR");
            var origin = ar ? originAr : originOv;
            var header = new ModelHeader(key, name, 2022, DateTimeOffset.UtcNow, true, "Navisworks", origin, 0, 0);
            ElementGeometry[] elements = ar
                ? [ClashFixtures.Box(Info("a", "OST_Walls"), 0, 0, 0, 100), ClashFixtures.Box(Info("near"), 3000, 0, 0, 100), ClashFixtures.Box(Info("far"), 8000, 0, 0, 100)]
                : [ClashFixtures.Box(Info("b", "OST_DuctCurves"), (float)(originAr - originOv + 50), 0, 0, 100)];
            return new ModelGeometry(header, elements, elements.Select(e => e.Info.UniqueId).ToArray());
        }

        var partId = await PartAsync(Server, admin, agent, ["POL_K1_AR.rvt", "POL_K1_OV.rvt"], Geometry);
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models;
        var ar = models.Single(m => m.Discipline == "АР").ModelId.ToString("N");
        var ov = models.Single(m => m.Discipline == "ОВ").ModelId.ToString("N");
        var (x, y, z) = (originAr + 75.25, 50.0, 50.0);
        var runId = await RunWithAsync(admin, partId, Hit(1, ar, ov) with { UniqueIdA = "a", UniqueIdB = "b", X = x, Y = y, Z = z, DepthMm = 50 });
        return new Setup(admin, partId, runId, ar, ov, x, y, z);
    }

    private async Task<Guid> RunWithAsync(HttpClient admin, Guid partId, params EngineHit[] hits)
    {
        var run = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).ReadJsonAsync<DraftRunSummary>();
        var engine = EngineClient(Server);
        (await engine.PostAsync("/api/engine/runs/claim", null)).EnsureSuccessStatusCode();
        (await PostResultAsync(engine, run.RunId, 0, true, hits, Stats)).EnsureSuccessStatusCode();
        return run.RunId;
    }

    private static Dictionary<int, (float[] Positions, uint[] Indices)> ParseMeshes(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.ReadUInt32().ShouldBe(0x31564354u);
        var count = reader.ReadInt32();
        var result = new Dictionary<int, (float[], uint[])>();
        for (var i = 0; i < count; i++)
        {
            var index = reader.ReadInt32();
            var vertices = reader.ReadInt32();
            var indices = reader.ReadInt32();
            var positions = Enumerable.Range(0, vertices * 3).Select(_ => reader.ReadSingle()).ToArray();
            result[index] = (positions, Enumerable.Range(0, indices).Select(_ => reader.ReadUInt32()).ToArray());
        }

        reader.BaseStream.Position.ShouldBe(bytes.Length);
        return result;
    }

    [Test]
    public async Task Scene_ReturnsAAndBAndNeighbours()
    {
        var setup = await SceneAsync();

        var scene = await setup.Admin.GetJsonAsync<ViewerSceneView>($"/api/clash/runs/{setup.RunId}/hits/1/scene");

        scene.RadiusMm.ShouldBe(5000);
        scene.DepthMm.ShouldBe(50);
        scene.Elements.Select(e => (e.Role, e.UniqueId)).ShouldBe([("A", "a"), ("B", "b"), ("context", "near")]);
        scene.Elements.ShouldAllBe(e => e.Mesh && e.Triangles == 12);
        scene.Elements[0].ModelName.ShouldBe("POL_K1_AR.rvt");
        scene.Elements[1].Discipline.ShouldBe("ОВ");
        scene.Elements[1].Category.ShouldBe("OST_DuctCurves");
        scene.MeshTriangles.ShouldBe(36);
        scene.Truncated.ShouldBeFalse();

        var wide = await setup.Admin.GetJsonAsync<ViewerSceneView>($"/api/clash/runs/{setup.RunId}/hits/1/scene?radius=10000");
        wide.Elements.Select(e => e.UniqueId).ShouldBe(["a", "b", "near", "far"]);
    }

    [Test]
    public async Task Meshes_ParseBackToSceneElements()
    {
        var setup = await SceneAsync();
        var scene = await setup.Admin.GetJsonAsync<ViewerSceneView>($"/api/clash/runs/{setup.RunId}/hits/1/scene");

        var response = await setup.Admin.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/meshes");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        var meshes = ParseMeshes(await response.Content.ReadAsByteArrayAsync());
        meshes.Keys.Order().ShouldBe(scene.Elements.Where(e => e.Mesh).Select(e => e.Index));
        meshes.Values.ShouldAllBe(m => m.Positions.Length == 24 && m.Indices.Length == 36);
        // near: куб 3000..3100 по X в системе АР, точка коллизии — 75.25.
        meshes[2].Positions.Where((_, i) => i % 3 == 0).Min().ShouldBe(3000 - 75.25f, 0.01f);
    }

    [Test]
    public async Task Meshes_FarFromOrigin_RelativeToHitPrecisely()
    {
        var setup = await SceneAsync(originAr: 7_000_000, originOv: 7_000_000);

        var meshes = ParseMeshes(await setup.Admin.GetByteArrayAsync($"/api/clash/runs/{setup.RunId}/hits/1/meshes"));

        var xs = meshes[0].Positions.Where((_, i) => i % 3 == 0).ToArray();
        xs.Min().ShouldBe(-75.25f, 0.01f);
        xs.Max().ShouldBe(24.75f, 0.01f);
    }

    [Test]
    public async Task Scene_TwoModelsDifferentOrigins_Aligned()
    {
        var setup = await SceneAsync(originAr: 7_000_000, originOv: 8_000_000);

        var scene = await setup.Admin.GetJsonAsync<ViewerSceneView>($"/api/clash/runs/{setup.RunId}/hits/1/scene");
        var meshes = ParseMeshes(await setup.Admin.GetByteArrayAsync($"/api/clash/runs/{setup.RunId}/hits/1/meshes"));

        var b = scene.Elements.Single(e => e.Role == "B");
        b.Min[0].ShouldBe(-25.25f, 0.01f);
        b.Max[0].ShouldBe(74.75f, 0.01f);
        meshes[b.Index].Positions.Where((_, i) => i % 3 == 0).Min().ShouldBe(-25.25f, 0.01f);
        scene.Elements.Single(e => e.Role == "A").Min[0].ShouldBe(-75.25f, 0.01f);
    }

    [Test]
    public async Task Scene_RunNoLongerResult_NotFound()
    {
        var setup = await SceneAsync();
        await RunWithAsync(setup.Admin, setup.PartId, Hit(1, setup.Ar, setup.Ov) with { UniqueIdA = "a", UniqueIdB = "b" });

        var stale = await setup.Admin.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/scene");
        var missing = await setup.Admin.GetAsync($"/api/clash/runs/{Guid.NewGuid()}/hits/1/scene");
        var noHit = await setup.Admin.GetAsync($"/api/clash/runs/{setup.RunId}/hits/99/meshes");

        stale.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stale.Content.ReadAsStringAsync()).ShouldContain("результат черновика обновился");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        noHit.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Scene_SnapshotFolderDeleted_Gone()
    {
        var setup = await SceneAsync();
        var storage = Server.Services.CreateScope().ServiceProvider.GetRequiredService<ClashStorage>();
        foreach (var folder in Directory.GetDirectories(storage.ModelFolder(Guid.ParseExact(setup.Ov, "N")))) Directory.Delete(folder, true);

        var response = await setup.Admin.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/scene");

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).ShouldContain("выгрузка модели удалена");
    }

    [Test]
    public async Task Scene_RadiusOutOfRange_BadRequest()
    {
        var setup = await SceneAsync();

        (await setup.Admin.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/scene?radius=999")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await setup.Admin.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/meshes?radius=20001")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ElementProperties_ReturnsInfoAndParameters()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models;
        var ar = models.Single(m => m.Discipline == "АР").ModelId.ToString("N");
        var ov = models.Single(m => m.Discipline == "ОВ").ModelId.ToString("N");
        var runId = await RunWithAsync(admin, partId, Hit(1, ar, ov) with { UniqueIdA = "wall-1", UniqueIdB = "pipe-1" });

        var wall = await admin.GetJsonAsync<ViewerElementProperties>($"/api/clash/runs/{runId}/elements/{ar}/wall-1");
        var unknown = await admin.GetAsync($"/api/clash/runs/{runId}/elements/{ar}/nothing");
        var otherModel = await admin.GetAsync($"/api/clash/runs/{runId}/elements/{Guid.NewGuid():N}/wall-1");

        wall.ModelName.ShouldBe("POL_K1_AR.rvt");
        wall.Discipline.ShouldBe("АР");
        (wall.Category, wall.Family, wall.Type, wall.Level, wall.Workset, wall.ElementId).ShouldBe(("OST_Walls", "Стена", "200", "Этаж 1", "АР", 1L));
        wall.Parameters.ShouldHaveSingleItem().ShouldBe(new ViewerParameter("Марка", "С1", null));
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        otherModel.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Viewer_AnonymousUnauthorized_ViewerAllowed()
    {
        var setup = await SceneAsync();
        var viewer = await ClientAsync(UserRole.Viewer);

        (await Server.CreateClient().GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/scene")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await viewer.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/scene")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/clash/runs/{setup.RunId}/hits/1/meshes")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/clash/runs/{setup.RunId}/elements/{setup.Ar}/a")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task DraftHits_CarryRunId()
    {
        var setup = await SceneAsync();

        var page = await setup.Admin.GetJsonAsync<HitPage>($"/api/clash/parts/{setup.PartId}/draft/hits");

        page.Items.ShouldHaveSingleItem().RunId.ShouldBe(setup.RunId);
    }
}
