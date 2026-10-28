using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Naming;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashPartTests : ServerTest
{
    private async Task<ClashModel> ModelAsync(string name)
    {
        using var scope = Server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ClashStorage>().EnsureModelAsync(name, $"RSN://rs22/ПОЛ/{name}", CancellationToken.None);
    }

    private static async Task<ClashPartView> CreateAsync(HttpClient client, string name = "Полковая, корпус 1", string? obj = "Полковая", string? part = "корпус 1")
    {
        var response = await client.PostJsonAsync("/api/clash/parts", new { name, @object = obj, part });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadJsonAsync<ClashPartView>();
    }

    private static Task<HttpResponseMessage> SetModelsAsync(HttpClient client, Guid partId, params (Guid ModelId, string? Override)[] models) =>
        client.PutJsonAsync($"/api/clash/parts/{partId}/models", models.Select(m => new { modelId = m.ModelId, disciplineOverride = m.Override }).ToArray());

    [Test]
    public async Task CreatePart_AddModels_DisciplineFromNaming()
    {
        var admin = await AdminAsync();
        var ar = await ModelAsync("POL_K1_AR.rvt");
        var ov = await ModelAsync("POL_K1_OV.rvt");
        var part = await CreateAsync(admin);

        (await SetModelsAsync(admin, part.Id, (ar.Id, null), (ov.Id, null))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var view = await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{part.Id}");
        view.Models.Select(m => (m.Name, m.ParsedDiscipline, m.Discipline)).ShouldBe([("POL_K1_AR.rvt", "АР", "АР"), ("POL_K1_OV.rvt", "ОВ", "ОВ")]);
        view.Models.ShouldAllBe(m => m.Latest == null);
        var list = await admin.GetJsonAsync<ClashPartSummary[]>("/api/clash/parts");
        list.ShouldHaveSingleItem().Models.ShouldBe(2);
    }

    [Test]
    public async Task DisciplineOverride_WinsOverNaming()
    {
        var admin = await AdminAsync();
        var unknown = await ModelAsync("POL_K1_SOMETHING.rvt");
        var ar = await ModelAsync("POL_K1_AR.rvt");
        var part = await CreateAsync(admin);

        await SetModelsAsync(admin, part.Id, (unknown.Id, "ЭОМ"), (ar.Id, " "));

        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{part.Id}")).Models;
        var eom = models.Single(m => m.ModelId == unknown.Id);
        eom.ParsedDiscipline.ShouldBeNull();
        eom.Discipline.ShouldBe("ЭОМ");
        models.Single(m => m.ModelId == ar.Id).DisciplineOverride.ShouldBeNull(); // пробелы — не поправка
    }

    [Test]
    public async Task SetModels_UnknownModel_Rejected()
    {
        var admin = await AdminAsync();
        var part = await CreateAsync(admin);

        (await SetModelsAsync(admin, part.Id, (Guid.NewGuid(), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Suggest_ByObjectAndPart()
    {
        var admin = await AdminAsync();
        using (var scope = Server.Services.CreateScope())
        {
            var naming = NamingDefaults.Create();
            naming.Entries.Add(new NamingEntry(NamingFields.Object, "POL", "Полковая"));
            await scope.ServiceProvider.GetRequiredService<NamingStore>().SaveAsync(naming);
        }

        await ModelAsync("POL_K1_AR.rvt");
        await ModelAsync("POL_K1_OV.rvt");
        await ModelAsync("POL_K2_AR.rvt");
        await ModelAsync("ZZZ_K1_AR.rvt");

        var suggested = await admin.GetJsonAsync<PartModelView[]>("/api/clash/parts/suggest?object=полковая&part=КОРПУС 1");

        suggested.Select(m => m.Name).Order().ShouldBe(["POL_K1_AR.rvt", "POL_K1_OV.rvt"]);
    }

    [Test]
    public async Task Parts_ViewerReads_OperatorWrites()
    {
        var @operator = await ClientAsync(UserRole.Operator);
        var viewer = await ClientAsync(UserRole.Viewer);
        var part = await CreateAsync(@operator);

        (await viewer.GetJsonAsync<ClashPartSummary[]>("/api/clash/parts")).ShouldHaveSingleItem();
        (await viewer.PostJsonAsync("/api/clash/parts", new { name = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PutJsonAsync($"/api/clash/parts/{part.Id}", new { name = "y" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync($"/api/clash/parts/{part.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task UpdatePart_RenamesAndRejectsEmptyName()
    {
        var admin = await AdminAsync();
        var part = await CreateAsync(admin);

        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}", new { name = "Полковая К1", @object = "Полковая", part = "корпус 1" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}", new { name = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{part.Id}")).Name.ShouldBe("Полковая К1");
    }

    [Test]
    public async Task DeletePart_RemovesModelsLinks_KeepsModels()
    {
        var admin = await AdminAsync();
        var ar = await ModelAsync("POL_K1_AR.rvt");
        var part = await CreateAsync(admin);
        await SetModelsAsync(admin, part.Id, (ar.Id, null));

        (await admin.DeleteAsync($"/api/clash/parts/{part.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await admin.GetAsync($"/api/clash/parts/{part.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models")).ShouldHaveSingleItem().Id.ShouldBe(ar.Id);
    }
}
