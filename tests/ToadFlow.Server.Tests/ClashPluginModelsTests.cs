using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Plugin;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Плагин «Коллизии»: какая модель ToadFlow открыта в Revit — по пути центральной модели, иначе по имени файла.</summary>
public class ClashPluginModelsTests : ServerTest
{
    private async Task<ClashModel> ModelAsync(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var model = new ClashModel
        {
            Id = Guid.CreateVersion7(), Key = ClashStorage.KeyOf(path, name), Name = name, ModelPath = path, CreatedAt = DateTimeOffset.UtcNow
        };
        await DbAsync(async db =>
        {
            db.Set<ClashModel>().Add(model);
            await db.SaveChangesAsync();
        });
        return model;
    }

    private async Task<Guid> PartAsync(HttpClient admin, string name, bool approved, params (Guid Id, string? Override)[] models)
    {
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name })).ReadJsonAsync<ClashPartView>();
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}/models", models.Select(m => new { modelId = m.Id, disciplineOverride = m.Override }).ToArray()))
            .EnsureSuccessStatusCode();
        if (approved)
            await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == part.Id).ExecuteUpdateAsync(p => p.SetProperty(x => x.ApprovedVersion, 1)));
        return part.Id;
    }

    private static Task<ModelResolveView> ResolveAsync(HttpClient client, string? path, string? name) =>
        client.GetJsonAsync<ModelResolveView>($"/api/clash/models/resolve?path={Uri.EscapeDataString(path ?? "")}&name={Uri.EscapeDataString(name ?? "")}");

    [Test]
    public async Task Resolve_ByServerPath()
    {
        var model = await ModelAsync("RSN://srv/DON/DON_OVK.rvt");
        await ModelAsync("RSN://srv/DON/DON_KR.rvt");

        var result = await ResolveAsync(await AdminAsync(), @"RSN:\\srv\DON\DON_OVK.rvt", "DON_OVK.rvt");

        result.By.ShouldBe("path");
        var match = result.Matches.ShouldHaveSingleItem();
        (match.Id, match.Key, match.Name, match.ModelPath).ShouldBe((model.Id, model.Id.ToString("N"), "DON_OVK.rvt", "RSN://srv/DON/DON_OVK.rvt"));
    }

    [Test]
    public async Task Resolve_ByName_WhenPathUnknown()
    {
        var model = await ModelAsync("RSN://srv/DON/DON_OVK.rvt");

        var result = await ResolveAsync(await AdminAsync(), @"C:\Users\ivanov\Documents\DON_OVK_ivanov.rvt", "don_ovk.rvt");

        result.By.ShouldBe("name");
        result.Matches.ShouldHaveSingleItem().Id.ShouldBe(model.Id);
    }

    [Test]
    public async Task Resolve_TwoByName_ReturnsBoth()
    {
        var first = await ModelAsync("RSN://srv/DON/DON_OVK.rvt");
        var second = await ModelAsync(@"\\fs\projects\DON\DON_OVK.rvt");

        var result = await ResolveAsync(await AdminAsync(), null, "DON_OVK");

        result.By.ShouldBe("name");
        result.Matches.Select(m => m.Id).ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Test]
    public async Task Resolve_Nothing_ByNone()
    {
        await ModelAsync("RSN://srv/DON/DON_OVK.rvt");

        var result = await ResolveAsync(await AdminAsync(), "RSN://srv/DON/DON_AR.rvt", "DON_AR.rvt");

        result.By.ShouldBe("none");
        result.Matches.ShouldBeEmpty();
    }

    [Test]
    public async Task Resolve_PartsOnlyApproved_WithDiscipline()
    {
        var admin = await AdminAsync();
        var model = await ModelAsync("RSN://srv/DON/POL_K1_OV.rvt");
        var approved = await PartAsync(admin, "Утверждённая", true, (model.Id, "ОВК"));
        await PartAsync(admin, "Черновик", false, (model.Id, null));

        var match = (await ResolveAsync(admin, "RSN://srv/DON/POL_K1_OV.rvt", null)).Matches.ShouldHaveSingleItem();

        var part = match.Parts.ShouldHaveSingleItem();
        (part.Id, part.Name, part.LastApprovedRunAt).ShouldBe((approved, "Утверждённая", null));
        match.Discipline.ShouldBe("ОВК");
    }

    [Test]
    public async Task Resolve_WithoutParts_NoDiscipline()
    {
        await ModelAsync("RSN://srv/DON/POL_K1_OV.rvt");

        var match = (await ResolveAsync(await AdminAsync(), "RSN://srv/DON/POL_K1_OV.rvt", null)).Matches.ShouldHaveSingleItem();

        match.Parts.ShouldBeEmpty();
        match.Discipline.ShouldBeNull();
    }

    [Test]
    public async Task Resolve_DesignerAndToken_Allowed()
    {
        await ModelAsync("RSN://srv/DON/DON_OVK.rvt");
        var designer = await ClientAsync(UserRole.Designer);
        var created = await (await designer.PostJsonAsync("/api/auth/tokens", new { name = "ToadTools — PC" })).ReadJsonAsync<TokenCreatedView>();
        var token = Server.CreateClient();
        token.DefaultRequestHeaders.Add("X-ToadFlow-Token", created.Token);

        var response = await token.GetAsync("/api/clash/models/resolve?path=RSN://srv/DON/DON_OVK.rvt");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResolveAsync(designer, null, "DON_OVK.rvt")).Matches.ShouldHaveSingleItem();
    }
}
