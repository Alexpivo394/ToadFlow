using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Общий список коллизий: фасеты на текущий фильтр, сортировка по уровню, фильтры элемента и категории.</summary>
public class ClashCollisionListTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> SetupAsync(params EngineHit[] hits)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        var start = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, hits.Select(h => h with { FingerprintA = 1, FingerprintB = 2 }).ToList(), Stats, [], []))
            .EnsureSuccessStatusCode();
        return (admin, agent, partId);
    }

    private static EngineHit At(EngineHit hit, string? levelA, string? levelB) => hit with { LevelA = levelA, LevelB = levelB };

    private static async Task<int[]> NumbersAsync(HttpClient client, Guid partId, string query) =>
        (await client.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions{query}")).Items.Select(c => c.Number).ToArray();

    [Test]
    public async Task Facets_PairIgnoresOwnFilter()
    {
        var (admin, agent, partId) = await SetupAsync(
            At(Hit(1, disciplineA: "АР", disciplineB: "ОВ"), "Этаж 1", "Этаж 1"),
            At(Hit(2, disciplineA: "АР", disciplineB: "ОВ"), "Этаж 2", "Этаж 2"),
            At(Hit(3, disciplineA: "КР", disciplineB: "ОВ"), "Этаж 3", "Этаж 3"));
        await using var _ = agent;
        (await NumbersAsync(admin, partId, "")).Length.ShouldBe(3);

        var facets = await admin.GetJsonAsync<CollisionFacets>(
            $"/api/clash/parts/{partId}/approved/facets?pair={Uri.EscapeDataString("АР × ОВ")}");

        facets.Pairs.Select(p => (p.Value, p.Count)).OrderBy(p => p.Value).ShouldBe([("АР × ОВ", 2), ("КР × ОВ", 1)]);
        facets.Levels.Select(l => l.Value).Order().ShouldBe(["Этаж 1", "Этаж 2"]);
        facets.Models.Sum(m => m.Count).ShouldBeGreaterThan(0);
        facets.Categories.Select(c => c.Value).Order().ShouldBe(["OST_DuctCurves", "OST_Walls"]);
    }

    [Test]
    public async Task Facets_LevelBothSidesCountedOnce()
    {
        var (admin, agent, partId) = await SetupAsync(
            At(Hit(1), "Этаж 1", "Этаж 1"),
            At(Hit(2), "Этаж 1", "Этаж 2"),
            At(Hit(3), null, null));
        await using var _ = agent;

        var facets = await admin.GetJsonAsync<CollisionFacets>($"/api/clash/parts/{partId}/approved/facets");

        facets.Levels.Select(l => (l.Value, l.Count)).OrderBy(l => l.Value)
            .ShouldBe([(CollisionLevel.None, 1), ("Этаж 1", 2), ("Этаж 2", 1)]);
        facets.Categories.Single(c => c.Value == "OST_Walls").Count.ShouldBe(3);
    }

    [Test]
    public async Task List_SortLevel_ThenNumber()
    {
        var (admin, agent, partId) = await SetupAsync(
            At(Hit(1), "Этаж 2", "Этаж 2"),
            At(Hit(2), "Этаж 1", "Этаж 3"),
            At(Hit(3), "Этаж 1", "Этаж 1"),
            At(Hit(4), "Этаж 1", "Этаж 1"));
        await using var _ = agent;

        (await NumbersAsync(admin, partId, "?sort=level")).ShouldBe([3, 4, 2, 1]);
    }

    [Test]
    public async Task List_ElementFilter_EitherSide()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1), Hit(2), Hit(3) with { ModelA = "b", UniqueIdA = "ub-2" });
        await using var _ = agent;

        (await NumbersAsync(admin, partId, "?element=b:ub-2")).Order().ShouldBe([2, 3]);
        (await NumbersAsync(admin, partId, "?element=a:ua-1")).ShouldBe([1]);
        (await NumbersAsync(admin, partId, "?category=OST_DuctCurves")).Length.ShouldBe(3);
        (await NumbersAsync(admin, partId, "?category=OST_Doors")).ShouldBeEmpty();
    }

    [Test]
    public async Task My_Facets_OnlyMine()
    {
        var (admin, agent, partId) = await SetupAsync(
            At(Hit(1, disciplineA: "КР", disciplineB: "ОВК"), "Этаж 1", "Этаж 1"),
            At(Hit(2, disciplineA: "АР", disciplineB: "ВК"), "Этаж 2", "Этаж 2"));
        await using var _ = agent;
        var ovk = await ClientAsync(UserRole.Designer, "ОВК");

        var facets = await ovk.GetJsonAsync<CollisionFacets>("/api/clash/my/facets");

        facets.Pairs.ShouldHaveSingleItem().ShouldBe(new Alexpivo394.Revit.Clash.Rules.ValueCount("КР × ОВК", 1));
        facets.Levels.ShouldHaveSingleItem().Value.ShouldBe("Этаж 1");
        (await ovk.GetJsonAsync<MyCollisionPage>("/api/clash/my?sort=level&element=b:ub-1")).Items.ShouldHaveSingleItem();
        (await ovk.GetJsonAsync<MyCollisionPage>("/api/clash/my?element=b:ub-2")).Items.ShouldBeEmpty();
    }
}
