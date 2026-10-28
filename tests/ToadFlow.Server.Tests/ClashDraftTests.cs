using System.Net;
using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
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

    private static SelectionSet Set(string discipline, string name) => new(Guid.NewGuid(), discipline, name, [new ConditionGroup([AnyCategory])]);

    /// <summary>Часть с моделями АР и ОВ (разделы — по разбору имён).</summary>
    private async Task<(HttpClient Admin, Guid PartId)> PartAsync(params string[] models)
    {
        var admin = await AdminAsync();
        var ids = new List<Guid>();
        using (var scope = Server.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
            foreach (var name in models.Length > 0 ? models : ["POL_K1_AR.rvt", "POL_K1_OV.rvt"])
                ids.Add((await storage.EnsureModelAsync(name, $"RSN://rs22/{name}", CancellationToken.None)).Id);
        }

        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "Часть" })).ReadJsonAsync<ClashPartView>();
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}/models", ids.Select(id => new { modelId = id, disciplineOverride = (string?)null }).ToArray()))
            .EnsureSuccessStatusCode();
        return (admin, part.Id);
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
        var (admin, partId) = await PartAsync();
        var walls = Set("АР", "Стены");
        var ducts = Set("ОВ", "Воздуховоды");
        var config = new ClashConfiguration([walls, ducts], [new MatrixCell("АР", "ОВ", true, 15)], [new SetPairExclusion(walls.Id, ducts.Id)], false);

        (await SaveAsync(admin, partId, config)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (saved, view) = await DraftAsync(admin, partId);
        view.Disciplines.ShouldBe(["АР", "ОВ"]);
        view.Problems.ShouldBeEmpty();
        saved.Sets.Select(s => s.Name).ShouldBe(["Стены", "Воздуховоды"]);
        saved.Matrix.ShouldHaveSingleItem().ToleranceMm.ShouldBe(15);
        saved.Exclusions.ShouldHaveSingleItem().ShouldBe(new SetPairExclusion(walls.Id, ducts.Id));
        saved.IgnoreConnectedMep.ShouldBeFalse();
    }

    [Test]
    public async Task SaveDraft_InvalidConfiguration_ReturnsProblems()
    {
        var (admin, partId) = await PartAsync();
        var greater = new SelectionSet(Guid.NewGuid(), "ОВ", "Толстые",
            [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Parameter, "Диаметр"), ConditionOperator.Greater, "сорок")])]);
        var config = new ClashConfiguration([greater, Set("ВК", "Трубы")], [new MatrixCell("АР", "ОВ", true, 0.01)], []);

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
        var (admin, partId) = await PartAsync();

        var broken = await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent("{\"sets\":", Encoding.UTF8, "application/json"));
        var nulls = await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent("{\"sets\":null}", Encoding.UTF8, "application/json"));

        broken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        nulls.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task FillMatrix_AddsAllDisciplinePairsAt20mm()
    {
        var (admin, partId) = await PartAsync("POL_K1_AR.rvt", "POL_K1_OV.rvt", "POL_K1_VK.rvt");
        await SaveAsync(admin, partId, new ClashConfiguration([], [new MatrixCell("АР", "ОВ", false, 50)], []));

        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/matrix/fill", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var matrix = (await DraftAsync(admin, partId)).Config.Matrix;
        matrix.Count.ShouldBe(6); // 3 пары разделов + 3 раздела сами с собой
        matrix.Single(c => c.DisciplineA == "АР" && c.DisciplineB == "ОВ").ShouldBe(new MatrixCell("АР", "ОВ", false, 50)); // заданная не меняется
        matrix.Where(c => c.DisciplineA != "АР" || c.DisciplineB != "ОВ").ShouldAllBe(c => c.Enabled && c.ToleranceMm == 20);
    }

    [Test]
    public async Task Template_SaveAndApply_NewSetIdsAndRemappedExclusions()
    {
        var (admin, source) = await PartAsync();
        var walls = Set("АР", "Стены");
        var ducts = Set("ОВ", "Воздуховоды");
        await SaveAsync(admin, source, new ClashConfiguration([walls, ducts], [new MatrixCell("АР", "ОВ", true)], [new SetPairExclusion(walls.Id, ducts.Id)]));
        var (_, target) = await PartAsync("ROD_K2_AR.rvt", "ROD_K2_OV.rvt");

        var created = await admin.PostJsonAsync("/api/clash/templates", new { name = "Жилой дом", partId = source });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var template = await created.ReadJsonAsync<ClashTemplateView>();
        (await admin.GetJsonAsync<ClashTemplateView[]>("/api/clash/templates")).ShouldHaveSingleItem().Sets.ShouldBe(2);
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
        var (admin, source) = await PartAsync("POL_K1_AR.rvt", "POL_K1_OV.rvt", "POL_K1_VK.rvt");
        await SaveAsync(admin, source, new ClashConfiguration([Set("АР", "Стены")], [new MatrixCell("АР", "ОВ", true), new MatrixCell("АР", "ВК", true)], []));
        var template = await (await admin.PostJsonAsync("/api/clash/templates", new { name = "Три раздела", partId = source })).ReadJsonAsync<ClashTemplateView>();
        var (_, target) = await PartAsync("ROD_K2_AR.rvt", "ROD_K2_OV.rvt");

        var view = await (await admin.PostAsync($"/api/clash/templates/{template.Id}/apply/{target}", null)).ReadJsonAsync<DraftView>();

        ConfigurationJson.Deserialize(view.Configuration.GetRawText()).Matrix.ShouldHaveSingleItem().DisciplineB.ShouldBe("ОВ");
        view.Problems.ShouldBeEmpty();
    }

    [Test]
    public async Task Template_DuplicateName_Rejected()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostJsonAsync("/api/clash/templates", new { name = "Жилой дом", partId })).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await admin.PostJsonAsync("/api/clash/templates", new { name = "жилой дом", partId })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Draft_ViewerCannotSave()
    {
        var (_, partId) = await PartAsync();
        var viewer = await ClientAsync(UserRole.Viewer);

        (await viewer.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Problems.ShouldBeEmpty();
        (await SaveAsync(viewer, partId, new ClashConfiguration([], [], []))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/clash/parts/{partId}/draft/matrix/fill", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
