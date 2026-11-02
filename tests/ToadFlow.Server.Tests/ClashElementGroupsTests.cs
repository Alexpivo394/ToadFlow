using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Группировка коллизий по элементу своей стороны: в проверке — по выбранному разделу, в «Моих» — по разделам пользователя.</summary>
public class ClashElementGroupsTests : ServerTest
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

    /// <summary>Коллизия с элементом B <c>b:ub-shared</c>.</summary>
    private static EngineHit Shared(EngineHit hit) => hit with { UniqueIdB = "ub-shared", ElementIdB = 777 };

    private static Task<ElementGroupPage> GroupsAsync(HttpClient client, Guid partId, string query) =>
        client.GetJsonAsync<ElementGroupPage>($"/api/clash/parts/{partId}/approved/collisions?group=element{query}");

    [Test]
    public async Task Groups_ByOwnSide_CountAndMaxDepth()
    {
        var (admin, agent, partId) = await SetupAsync(Shared(Hit(1, depth: 10)), Shared(Hit(2, depth: 30)), Shared(Hit(3, depth: 20)), Hit(4, depth: 50));
        await using var _ = agent;

        var page = await GroupsAsync(admin, partId, $"&side={Uri.EscapeDataString("ОВ")}");

        (page.Groups, page.Collisions).ShouldBe((2, 4));
        var first = page.Items[0];
        (first.Model, first.UniqueId, first.ElementId, first.Count, first.MaxDepthMm).ShouldBe(("b", "ub-shared", 777L, 3, 30f));
        (first.Discipline, first.Category, first.Family, first.Type, first.Level).ShouldBe(("ОВ", "OST_DuctCurves", "Воздуховод", "500x300", "Этаж 1"));
        (page.Items[1].UniqueId, page.Items[1].Count).ShouldBe(("ub-4", 1));
    }

    [Test]
    public async Task Groups_SameDisciplineBothSides_TwoGroups_CollisionsCountedOnce()
    {
        var (admin, agent, partId) = await SetupAsync(
            Hit(1, disciplineA: "ВК", disciplineB: "ВК"),
            Hit(2, disciplineA: "ВК", disciplineB: "ВК") with { UniqueIdA = "ua-1", ElementIdA = 1 });
        await using var _ = agent;

        var page = await GroupsAsync(admin, partId, $"&side={Uri.EscapeDataString("ВК")}");

        (page.Groups, page.Collisions).ShouldBe((3, 2));
        (page.Items[0].UniqueId, page.Items[0].Count).ShouldBe(("ua-1", 2));
    }

    [Test]
    public async Task Groups_Check_NoSide_400()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1));
        await using var _ = agent;

        var response = await admin.GetAsync($"/api/clash/parts/{partId}/approved/collisions?group=element");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Выберите раздел: группировать по его элементам");
    }

    [Test]
    public async Task Groups_My_UsesUserDisciplines()
    {
        var (_, agent, _) = await SetupAsync(
            Shared(Hit(1, disciplineA: "КР", disciplineB: "ОВК")),
            Shared(Hit(2, disciplineA: "КР", disciplineB: "ОВК")),
            Hit(3, disciplineA: "АР", disciplineB: "ВК"));
        await using var _a = agent;
        var ovk = await ClientAsync(UserRole.Designer, "ОВК");

        var page = await ovk.GetJsonAsync<ElementGroupPage>("/api/clash/my?group=element");

        (page.Groups, page.Collisions).ShouldBe((1, 2));
        var group = page.Items.ShouldHaveSingleItem();
        (group.Model, group.UniqueId, group.Count, group.Discipline).ShouldBe(("b", "ub-shared", 2, "ОВК"));
    }

    [Test]
    public async Task Groups_Paged_SingletonsLast()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1, depth: 90), Shared(Hit(2, depth: 10)), Shared(Hit(3, depth: 10)), Hit(4, depth: 80));
        await using var _ = agent;
        var side = $"&side={Uri.EscapeDataString("ОВ")}";

        var first = await GroupsAsync(admin, partId, side + "&pageSize=2&page=1");
        var second = await GroupsAsync(admin, partId, side + "&pageSize=2&page=2");

        (first.Groups, first.Collisions).ShouldBe((3, 4));
        first.Items.Select(g => g.UniqueId).ShouldBe(["ub-shared", "ub-1"]);
        second.Items.Select(g => g.UniqueId).ShouldBe(["ub-4"]);
    }
}
