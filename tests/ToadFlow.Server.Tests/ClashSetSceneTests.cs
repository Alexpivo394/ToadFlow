using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Viewer;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>3D набора в редакторе: элементы набора из последних выгрузок черновика, окружение вокруг выбранного образца, «почему попал».</summary>
public class ClashSetSceneTests : ServerTest
{
    private static readonly Guid WallsId = Guid.NewGuid();

    private static ElementInfo Info(string uid, string category) => new(uid, uid.Length, category, "Семейство", "Тип", "Этаж 1", null, []);

    private static Condition Category(string value) => new(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, value);

    /// <summary>Набор «Стены» группы АР: стены — или по категории, или по семейству «Нет такого».</summary>
    private static ClashConfiguration Config() => new([ConfigurationDefaults.DisciplineGroup("АР"), ConfigurationDefaults.DisciplineGroup("ОВ")],
        [new SelectionSet(WallsId, ConfigurationDefaults.DisciplineGroupId("АР"), "Стены",
            [new ConditionGroup([Category("OST_Walls")]), new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Family), ConditionOperator.Equals, "Нет такого")])])],
        [], []);

    /// <summary>АР: стены w0 у начала, w1 в 1 м, w2 в 20 м и сколько надо ещё; near — не стена в 3 м, far — не стена в 8 м. ОВ: воздуховод у начала.</summary>
    private async Task<(HttpClient Admin, Guid PartId, string Ar)> SetupAsync(int extraWalls = 0)
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        ModelGeometry Geometry(string name, string key)
        {
            var header = new ModelHeader(key, name, 2022, DateTimeOffset.UtcNow, true, "Navisworks", 0, 0, 0);
            ElementGeometry[] elements = name.Contains("_AR")
                ?
                [
                    ClashFixtures.Box(Info("w0", "OST_Walls"), 0, 0, 0, 100), ClashFixtures.Box(Info("w1", "OST_Walls"), 1000, 0, 0, 100),
                    ClashFixtures.Box(Info("w2", "OST_Walls"), 20000, 0, 0, 100), ClashFixtures.Box(Info("near", "OST_GenericModel"), 3000, 0, 0, 100),
                    ClashFixtures.Box(Info("far", "OST_GenericModel"), 8000, 0, 0, 100),
                    .. Enumerable.Range(0, extraWalls).Select(i => ClashFixtures.Box(Info($"x{i:D5}", "OST_Walls"), 0, 200 + i, 0, 1))
                ]
                : [ClashFixtures.Box(Info("duct", "OST_DuctCurves"), 0, 0, 0, 100)];
            return new ModelGeometry(header, elements, elements.Select(e => e.Info.UniqueId).ToArray());
        }

        var partId = await PartAsync(Server, admin, agent, ["POL_K1_AR.rvt", "POL_K1_OV.rvt"], Geometry);
        var ar = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models.Single(m => m.Discipline == "АР").ModelId.ToString("N");
        return (admin, partId, ar);
    }

    private static StringContent Body(object? focus = null, string? model = null, string? uniqueId = null) => new(JsonSerializer.Serialize(new
    {
        configuration = JsonDocument.Parse(ConfigurationJson.Serialize(Config())).RootElement,
        setId = WallsId,
        focus,
        model,
        uniqueId
    }), Encoding.UTF8, "application/json");

    [Test]
    public async Task SetScene_FocusCentered_RolesSetAndContext()
    {
        var (admin, partId, ar) = await SetupAsync();

        var scene = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/scene", Body(new { model = ar, uniqueId = "w1" }))).ReadJsonAsync<ViewerSceneView>();

        (scene.X, scene.Y, scene.Z).ShouldBe((1050, 50, 50));
        scene.Total.ShouldBe(3);
        scene.Truncated.ShouldBeFalse();
        scene.Elements.Where(e => e.Role == "set").Select(e => e.UniqueId).ShouldBe(["w1", "w0", "w2"]); // ближние к началу первыми
        scene.Elements.Where(e => e.Role == "context").Select(e => e.UniqueId).ShouldBe(["near"]); // far дальше 5 м, воздуховод — другой модели
        scene.Elements.Single(e => e.UniqueId == "w1").Min.ShouldBe([-50f, -50f, -50f]);

        var whole = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/scene", Body())).ReadJsonAsync<ViewerSceneView>();
        (whole.X, whole.Y, whole.Z).ShouldBe((10050, 50, 50)); // центр габарита набора
        whole.Elements.ShouldAllBe(e => e.Role == "set");
    }

    [Test]
    public async Task SetScene_Over3000_TruncatedNearestKept()
    {
        var (admin, partId, ar) = await SetupAsync(extraWalls: 3000);

        var scene = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/scene", Body(new { model = ar, uniqueId = "w0" }))).ReadJsonAsync<ViewerSceneView>();

        scene.Total.ShouldBe(3003);
        scene.Truncated.ShouldBeTrue();
        var set = scene.Elements.Where(e => e.Role == "set").Select(e => e.UniqueId).ToList();
        set.Count.ShouldBe(3000);
        set.ShouldContain("w0");
        set.ShouldNotContain("w2"); // самая дальняя стена отброшена (дополнительные — в пределах 3,2 м)
    }

    [Test]
    public async Task SetScene_Meshes_OnlyPicked()
    {
        var (admin, partId, ar) = await SetupAsync();
        var scene = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/scene", Body(new { model = ar, uniqueId = "w1" }))).ReadJsonAsync<ViewerSceneView>();

        var response = await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/meshes", Body(new { model = ar, uniqueId = "w1" }));
        response.EnsureSuccessStatusCode();
        using var reader = new BinaryReader(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        reader.ReadUInt32().ShouldBe(ViewerMeshWriter.Magic);
        var count = reader.ReadInt32();
        var indices = new List<int>();
        for (var i = 0; i < count; i++)
        {
            indices.Add(reader.ReadInt32());
            var vertices = reader.ReadInt32();
            var triangles = reader.ReadInt32();
            reader.BaseStream.Position += vertices * 12L + triangles * 4L;
        }

        indices.ShouldBe(scene.Elements.Where(e => e.Mesh).Select(e => e.Index));
        indices.Count.ShouldBe(4); // три стены и near; far и воздуховод не в сцене
    }

    [Test]
    public async Task SetWhy_ListsMatchedConditions()
    {
        var (admin, partId, ar) = await SetupAsync();

        var why = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/why", Body(model: ar, uniqueId: "w1"))).ReadJsonAsync<string[]>();
        var none = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/sets/why", Body(model: ar, uniqueId: "near"))).ReadJsonAsync<string[]>();

        why.ShouldBe(["Категория = OST_Walls"]);
        none.ShouldBeEmpty();
    }
}
