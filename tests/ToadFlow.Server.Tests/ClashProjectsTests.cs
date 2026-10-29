using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Projects;
using ToadFlow.Server.Naming;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashProjectsTests : ServerTest
{
    private async Task NamingAsync()
    {
        using var scope = Server.Services.CreateScope();
        var naming = NamingDefaults.Create();
        naming.Entries.Add(new NamingEntry(NamingFields.Object, "POL", "Полковая"));
        naming.Entries.Add(new NamingEntry(NamingFields.Object, "DON", "Донской"));
        await scope.ServiceProvider.GetRequiredService<NamingStore>().SaveAsync(naming);
    }

    private async Task<Guid> ModelAsync(string name)
    {
        using var scope = Server.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ClashStorage>().EnsureModelAsync(name, $"RSN://rs22/{name}", CancellationToken.None)).Id;
    }

    private async Task PublishAsync(FakeAgent agent, string name, DateTimeOffset extractedAt)
    {
        var published = await ClashFixtures.PublishAsync(Server, agent, name, extractedAt);
        await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(published.Result, TestJson.Options));
    }

    [Test]
    public async Task Projects_GroupedByObjectWithUnparsed()
    {
        await NamingAsync();
        var admin = await AdminAsync();
        await ModelAsync("POL_K1_AR.rvt");
        await ModelAsync("POL_K2_AR.rvt");
        await ModelAsync("ZZZ_K1_AR.rvt");
        (await admin.PostJsonAsync("/api/clash/parts", new { name = "Корпус 1", @object = "Полковая", part = "Корпус 1" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync("/api/clash/parts", new { name = "Сама по себе" })).EnsureSuccessStatusCode();

        var projects = await admin.GetJsonAsync<ClashProjectView[]>("/api/clash/projects");

        projects.Select(p => (p.Object, p.Name, p.Parts, p.Models)).ShouldBe([("Полковая", "Полковая", 1, 2), (null, "Без проекта", 1, 1)]);
    }

    [Test]
    public async Task ProjectModels_StaleAfter7Days()
    {
        await NamingAsync();
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        await PublishAsync(agent, "POL_K1_AR.rvt", DateTimeOffset.UtcNow.AddDays(-10));
        await PublishAsync(agent, "POL_K1_OV.rvt", DateTimeOffset.UtcNow.AddDays(-1));
        await ModelAsync("POL_K2_AR.rvt");
        await ModelAsync("DON_K1_AR.rvt");

        var models = await admin.GetJsonAsync<ProjectModelView[]>($"/api/clash/projects/models?object={Uri.EscapeDataString("Полковая")}");

        models.Select(m => (m.Name, m.Discipline, m.Stale)).ShouldBe(
            [("POL_K1_AR.rvt", "АР", true), ("POL_K1_OV.rvt", "ОВ", false), ("POL_K2_AR.rvt", "АР", true)]);
        models[0].Part.ShouldBe("корпус 1", StringCompareShould.IgnoreCase);
        models[0].SnapshotCount.ShouldBe(1);
        models[2].Latest.ShouldBeNull();
    }

    [Test]
    public async Task Parts_FilterByObject_WithWarnings()
    {
        await NamingAsync();
        var admin = await AdminAsync();
        var ar = await ModelAsync("POL_K1_AR.rvt");
        var don = await ModelAsync("DON_K1_AR.rvt");
        (await admin.PostJsonAsync("/api/clash/parts", new
        {
            name = "Корпус 1", @object = "Полковая", part = "Корпус 1", models = new[] { new { modelId = ar } }, start = new { kind = "disciplines" }
        })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync("/api/clash/parts", new { name = "Донской", @object = "Донской", models = new[] { new { modelId = don } } }))
            .EnsureSuccessStatusCode();

        var parts = await admin.GetJsonAsync<ClashPartSummary[]>($"/api/clash/parts?object={Uri.EscapeDataString("Полковая")}");

        var part = parts.ShouldHaveSingleItem();
        part.Name.ShouldBe("Корпус 1");
        part.Groups.ShouldBe(["АР"]);
        part.Warnings.ShouldBe(["АР: нет выгрузки"]);
        part.LastRun.ShouldBeNull();
        (await admin.GetJsonAsync<ClashPartSummary[]>("/api/clash/parts")).Length.ShouldBe(2);
    }
}
