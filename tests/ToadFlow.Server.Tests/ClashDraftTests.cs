using System.Net;
using System.Text;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Templates;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashDraftTests : ServerTest
{
    private static readonly Condition AnyCategory = new(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null);

    private static Guid G(string discipline) => ConfigurationDefaults.DisciplineGroupId(discipline);

    private static DisciplineSet Set(string discipline, string name) => new(Guid.NewGuid(), discipline, name, [new ConditionGroup([AnyCategory])]);

    private static ClashConfiguration Config(IReadOnlyList<DisciplineSet> sets, IReadOnlyList<DisciplineCell> matrix, IReadOnlyList<SetPairExclusion>? exclusions = null,
        bool ignoreConnectedMep = true) =>
        ConfigurationDefaults.FromDisciplines(sets, matrix, exclusions ?? [], ignoreConnectedMep);

    /// <summary>Часть с моделями (по умолчанию АР и ОВ, разделы — по разбору имён).</summary>
    private async Task<(HttpClient Admin, Guid PartId, Guid[] ModelIds)> PartAsync(params string[] models)
    {
        var admin = await AdminAsync();
        var ids = await ModelsAsync(models.Length > 0 ? models : ["POL_K1_AR.rvt", "POL_K1_OV.rvt"]);
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "Часть" })).ReadJsonAsync<ClashPartView>();
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}/models", ids.Select(id => new { modelId = id, disciplineOverride = (string?)null }).ToArray()))
            .EnsureSuccessStatusCode();
        return (admin, part.Id, ids);
    }

    private async Task<Guid[]> ModelsAsync(params string[] names)
    {
        using var scope = Server.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
        var ids = new List<Guid>();
        foreach (var name in names) ids.Add((await storage.EnsureModelAsync(name, $"RSN://rs22/{name}", CancellationToken.None)).Id);
        return [.. ids];
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid partId, ClashConfiguration config) =>
        client.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json"));

    private static async Task<(ClashConfiguration Config, DraftView View)> DraftAsync(HttpClient client, Guid partId)
    {
        var view = await client.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft");
        return (ConfigurationJson.Deserialize(view.Configuration.GetRawText()), view);
    }

    [Test]
    public async Task SaveDraft_ValidConfiguration_RoundTrips()
    {
        var (admin, partId, ids) = await PartAsync();
        var walls = Set("АР", "Стены");
        var ducts = Set("ОВ", "Воздуховоды");
        var config = Config([walls, ducts], [new DisciplineCell("АР", "ОВ", true, 15)], [new SetPairExclusion(walls.Id, ducts.Id)], false);

        (await SaveAsync(admin, partId, config)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (saved, view) = await DraftAsync(admin, partId);
        view.Disciplines.ShouldBe(["АР", "ОВ"]);
        view.Models.Select(m => (m.ModelKey, m.Discipline)).ShouldBe([(ids[0].ToString("N"), "АР"), (ids[1].ToString("N"), "ОВ")]);
        view.Problems.ShouldBeEmpty();
        saved.Groups.Select(g => g.Name).ShouldBe(["АР", "ОВ"]);
        saved.Sets.Select(s => s.Name).ShouldBe(["Стены", "Воздуховоды"]);
        saved.Matrix.ShouldHaveSingleItem().ToleranceMm.ShouldBe(15);
        saved.Exclusions.ShouldHaveSingleItem().ShouldBe(new SetPairExclusion(walls.Id, ducts.Id));
        saved.IgnoreConnectedMep.ShouldBeFalse();
    }

    [Test]
    public async Task SaveDraft_InvalidConfiguration_ReturnsProblems()
    {
        var (admin, partId, _) = await PartAsync();
        var greater = new DisciplineSet(Guid.NewGuid(), "ОВ", "Толстые",
            [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Parameter, "Диаметр"), ConditionOperator.Greater, "сорок")])]);
        var config = Config([greater, Set("ВК", "Трубы")], [new DisciplineCell("АР", "ОВ", true, 0.01)]);

        var response = await SaveAsync(admin, partId, config);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("число");
        body.ShouldContain("«ВК»");
        body.ShouldContain("0,05");
        (await DraftAsync(admin, partId)).Config.Sets.ShouldBeEmpty();
    }

    [Test]
    public async Task SaveDraft_BrokenJson_Returns400()
    {
        var (admin, partId, _) = await PartAsync();

        var broken = await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent("{\"sets\":", Encoding.UTF8, "application/json"));
        var nulls = await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent("{\"sets\":null}", Encoding.UTF8, "application/json"));
        var nullGroups = await admin.PutAsync($"/api/clash/parts/{partId}/draft",
            new StringContent("{\"groups\":null,\"sets\":[],\"matrix\":[],\"exclusions\":[]}", Encoding.UTF8, "application/json"));

        broken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        nulls.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        nullGroups.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Draft_LegacyJson_OpensWithGroups()
    {
        var (admin, partId, _) = await PartAsync();
        const string legacy = """
            {"sets":[{"id":"6a1c1b8e-0000-4000-8000-000000000001","discipline":"АР","name":"Стены",
              "anyOf":[{"all":[{"property":{"kind":"category"},"operator":"isNotEmpty","value":null}]}]}],
             "matrix":[{"disciplineA":"АР","disciplineB":"ОВ","enabled":true,"toleranceMm":20}],"exclusions":[],"ignoreConnectedMep":true}
            """;
        await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId).ExecuteUpdateAsync(p => p.SetProperty(x => x.DraftJson, legacy)));

        var (config, view) = await DraftAsync(admin, partId);

        view.Problems.ShouldBeEmpty();
        config.Groups.Select(g => (g.Id, g.Name)).ShouldBe([(G("АР"), "АР"), (G("ОВ"), "ОВ")]);
        config.Sets.ShouldHaveSingleItem().GroupId.ShouldBe(G("АР"));
        config.Matrix.ShouldHaveSingleItem().ShouldBe(new MatrixCell(G("АР"), G("ОВ"), true));
    }

    [Test]
    public async Task Draft_AddsMissingDisciplineGroups()
    {
        var (admin, partId, _) = await PartAsync("POL_K1_AR.rvt", "POL_K1_OV.rvt", "POL_K1_VK.rvt");
        await SaveAsync(admin, partId, Config([Set("АР", "Стены")], []));

        var (config, _) = await DraftAsync(admin, partId);

        config.Groups.Select(g => g.Name).ShouldBe(["АР", "ВК", "ОВ"]);
    }

    [Test]
    public async Task FillMatrix_AddsAllGroupPairsAt20mm()
    {
        var (admin, partId, _) = await PartAsync("POL_K1_AR.rvt", "POL_K1_OV.rvt", "POL_K1_VK.rvt");
        await SaveAsync(admin, partId, Config([], [new DisciplineCell("АР", "ОВ", false, 50)]));

        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/matrix/fill", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var config = (await DraftAsync(admin, partId)).Config;
        config.Matrix.Count.ShouldBe(6); // 3 пары групп + 3 группы сами с собой
        config.CellFor(G("ОВ"), G("АР")).ShouldBe(new MatrixCell(G("АР"), G("ОВ"), false, 50)); // заданная не меняется
        config.Matrix.Where(c => c.ToleranceMm != 50).ShouldAllBe(c => c.Enabled && c.ToleranceMm == 20);
    }

    [Test]
    public async Task CreatePart_WithModelsAndDisciplineStart()
    {
        var admin = await AdminAsync();
        var ids = await ModelsAsync("POL_K1_AR.rvt", "POL_K1_OV.rvt");

        var created = await admin.PostJsonAsync("/api/clash/parts", new
        {
            name = "Корпус 1", @object = "Полковая", part = "К1",
            models = ids.Select(id => new { modelId = id, disciplineOverride = (string?)null }),
            start = new { kind = "disciplines" }
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var part = await created.ReadJsonAsync<ClashPartView>();
        part.Models.Count.ShouldBe(2);
        var (config, view) = await DraftAsync(admin, part.Id);
        view.Problems.ShouldBeEmpty();
        config.Groups.Select(g => g.Name).ShouldBe(["АР", "ОВ"]);
        config.Sets.Select(s => s.Name).ShouldBe(["Всё", "Всё"]);
        config.Matrix.Count.ShouldBe(3);
    }

    [Test]
    public async Task CreatePart_FromTemplate()
    {
        var (admin, source, _) = await PartAsync();
        await SaveAsync(admin, source, Config([Set("АР", "Стены"), Set("ОВ", "Воздуховоды")], [new DisciplineCell("АР", "ОВ", true, 30)]));
        var template = await (await admin.PostJsonAsync("/api/clash/templates", new { name = "Жилой дом", partId = source })).ReadJsonAsync<ClashTemplateView>();
        var ids = await ModelsAsync("ROD_K2_AR.rvt", "ROD_K2_OV.rvt");

        var part = await (await admin.PostJsonAsync("/api/clash/parts", new
        {
            name = "Родина", models = ids.Select(id => new { modelId = id }), start = new { kind = "template", templateId = template.Id }
        })).ReadJsonAsync<ClashPartView>();

        var config = (await DraftAsync(admin, part.Id)).Config;
        config.Sets.Select(s => s.Name).ShouldBe(["Стены", "Воздуховоды"]);
        config.Matrix.ShouldHaveSingleItem().ToleranceMm.ShouldBe(30);
    }

    [Test]
    public async Task CreatePart_CopyOfPart()
    {
        var (admin, source, ids) = await PartAsync();
        var walls = Set("АР", "Стены");
        await SaveAsync(admin, source, Config([walls], [new DisciplineCell("АР", "АР", true)]));

        var part = await (await admin.PostJsonAsync("/api/clash/parts", new
        {
            name = "Копия", models = ids.Select(id => new { modelId = id }), start = new { kind = "part", partId = source }
        })).ReadJsonAsync<ClashPartView>();

        var config = (await DraftAsync(admin, part.Id)).Config;
        config.Sets.ShouldHaveSingleItem().Name.ShouldBe("Стены");
        config.Sets[0].Id.ShouldNotBe(walls.Id);
    }

    [Test]
    public async Task Template_SaveAndApply_NewSetIdsAndRemappedExclusions()
    {
        var (admin, source, _) = await PartAsync();
        var walls = Set("АР", "Стены");
        var ducts = Set("ОВ", "Воздуховоды");
        await SaveAsync(admin, source, Config([walls, ducts], [new DisciplineCell("АР", "ОВ", true)], [new SetPairExclusion(walls.Id, ducts.Id)]));
        var (_, target, _) = await PartAsync("ROD_K2_AR.rvt", "ROD_K2_OV.rvt");

        var created = await admin.PostJsonAsync("/api/clash/templates", new { name = "Жилой дом", partId = source });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var template = await created.ReadJsonAsync<ClashTemplateView>();
        var listed = (await admin.GetJsonAsync<ClashTemplateView[]>("/api/clash/templates")).ShouldHaveSingleItem();
        listed.Sets.ShouldBe(2);
        listed.Groups.ShouldBe(["АР", "ОВ"]);
        var applied = await admin.PostAsync($"/api/clash/templates/{template.Id}/apply/{target}", null);

        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        var config = (await DraftAsync(admin, target)).Config;
        config.Sets.Select(s => s.Name).ShouldBe(["Стены", "Воздуховоды"]);
        config.Sets.ShouldAllBe(s => s.Id != walls.Id && s.Id != ducts.Id);
        var exclusion = config.Exclusions.ShouldHaveSingleItem();
        exclusion.SetA.ShouldBe(config.Sets[0].Id);
        exclusion.SetB.ShouldBe(config.Sets[1].Id);
    }

    [Test]
    public async Task Template_Apply_DropsCellsOfMissingDisciplines()
    {
        var (admin, source, _) = await PartAsync("POL_K1_AR.rvt", "POL_K1_OV.rvt", "POL_K1_VK.rvt");
        await SaveAsync(admin, source, Config([Set("АР", "Стены")], [new DisciplineCell("АР", "ОВ", true), new DisciplineCell("АР", "ВК", true)]));
        var template = await (await admin.PostJsonAsync("/api/clash/templates", new { name = "Три раздела", partId = source })).ReadJsonAsync<ClashTemplateView>();
        var (_, target, _) = await PartAsync("ROD_K2_AR.rvt", "ROD_K2_OV.rvt");

        var view = await (await admin.PostAsync($"/api/clash/templates/{template.Id}/apply/{target}", null)).ReadJsonAsync<DraftView>();

        var config = ConfigurationJson.Deserialize(view.Configuration.GetRawText());
        config.Matrix.ShouldHaveSingleItem().ShouldBe(new MatrixCell(G("АР"), G("ОВ"), true));
        config.Groups.Select(g => g.Name).ShouldBe(["АР", "ОВ"]);
        view.Problems.ShouldBeEmpty();
    }

    [Test]
    public async Task Template_Apply_MapsDisciplineGroupsKeepsCustom()
    {
        var (admin, source, sourceIds) = await PartAsync();
        var ramps = new SetGroup(Guid.NewGuid(), "Проезды", new GroupSource(["АР"], []));
        var pinned = new SetGroup(Guid.NewGuid(), "Только эта АР", new GroupSource([], [sourceIds[0].ToString("N")]));
        var rampSet = new SelectionSet(Guid.NewGuid(), ramps.Id, "Проезды", [new ConditionGroup([AnyCategory])]);
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР"), ConfigurationDefaults.DisciplineGroup("ОВ"), ramps, pinned],
            [rampSet], [new MatrixCell(ramps.Id, G("ОВ"), true, 50)], []);
        (await SaveAsync(admin, source, config)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var template = await (await admin.PostJsonAsync("/api/clash/templates", new { name = "С проездами", partId = source })).ReadJsonAsync<ClashTemplateView>();
        var (_, target, _) = await PartAsync("ROD_K2_AR.rvt", "ROD_K2_OV.rvt");

        var view = await (await admin.PostAsync($"/api/clash/templates/{template.Id}/apply/{target}", null)).ReadJsonAsync<DraftView>();

        var applied = ConfigurationJson.Deserialize(view.Configuration.GetRawText());
        applied.Groups.Select(g => g.Name).ShouldBe(["АР", "ОВ", "Проезды", "Только эта АР"]);
        applied.Groups.Single(g => g.Name == "Проезды").Id.ShouldBe(ramps.Id);
        applied.Groups.Single(g => g.Name == "Только эта АР").Source.ModelKeys.ShouldBeEmpty();
        applied.CellFor(ramps.Id, G("ОВ"))!.ToleranceMm.ShouldBe(50);
        view.Problems.ShouldContain(p => p.Contains("«Только эта АР»"));
    }

    [Test]
    public async Task StartDraft_GroupSourceMissing_Rejected()
    {
        var (admin, partId, _) = await PartAsync();
        var gone = new SetGroup(Guid.NewGuid(), "Проезды", new GroupSource([], [Guid.NewGuid().ToString("N")]));
        var config = new ClashConfiguration([gone], [new SelectionSet(Guid.NewGuid(), gone.Id, "Проезды", [new ConditionGroup([AnyCategory])])],
            [new MatrixCell(gone.Id, gone.Id, true)], []);
        await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == partId)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.DraftJson, ConfigurationJson.Serialize(config))));

        var response = await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("модель источника");
    }

    [Test]
    public async Task Template_DuplicateName_Rejected()
    {
        var (admin, partId, _) = await PartAsync();
        (await admin.PostJsonAsync("/api/clash/templates", new { name = "Жилой дом", partId })).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await admin.PostJsonAsync("/api/clash/templates", new { name = "жилой дом", partId })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Draft_ViewerCannotSave()
    {
        var (_, partId, _) = await PartAsync();
        var viewer = await ClientAsync(UserRole.Viewer);

        (await viewer.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Problems.ShouldBeEmpty();
        (await SaveAsync(viewer, partId, Config([], []))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/clash/parts/{partId}/draft/matrix/fill", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
