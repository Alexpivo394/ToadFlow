using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>«Проверки» — утверждённые части по проектам: их видит любая роль, в том числе проектировщик.</summary>
public class ClashChecksTests : ServerTest
{
    [Test]
    public async Task Checks_OnlyApprovedParts_WithOpenCount()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var approved = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var draftOnly = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var engine = EngineClient(Server);
        (await admin.PostJsonAsync($"/api/clash/parts/{approved}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, [Hit(1) with { FingerprintA = 1 }, Hit(2) with { FingerprintA = 1 }], Stats, [], []))
            .EnsureSuccessStatusCode();
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 2).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fake)));
        var designer = await ClientAsync(UserRole.Designer);

        var checks = await designer.GetJsonAsync<CheckView[]>("/api/clash/checks");

        var check = checks.ShouldHaveSingleItem();
        (check.PartId, check.Version, check.Open).ShouldBe((approved, 1, 1));
        check.LastRun!.Found.ShouldBe(2);
        check.LastRunAt.ShouldNotBeNull();
        check.Models.Select(m => m.Name).Order().ShouldBe(["POL_K1_AR.rvt", "POL_K1_OV.rvt"]);
        check.Models.Single(m => m.Name == "POL_K1_OV.rvt").Discipline.ShouldBe("ОВ");
        (await designer.GetJsonAsync<CheckView>($"/api/clash/checks/{approved}")).PartId.ShouldBe(approved);
        (await designer.GetAsync($"/api/clash/checks/{draftOnly}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
