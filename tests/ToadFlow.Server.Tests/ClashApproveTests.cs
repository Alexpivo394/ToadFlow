using System.Net;
using System.Text;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashApproveTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId);
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid partId, string? comment = null, bool run = false) =>
        client.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment, run });

    private static async Task<ClashConfiguration> DraftAsync(HttpClient client, Guid partId) =>
        ConfigurationJson.Deserialize((await client.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Configuration.GetRawText());

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid partId, ClashConfiguration config) =>
        client.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json"));

    private static ClashConfiguration Tolerance(ClashConfiguration config, double mm) =>
        config with { Matrix = config.Matrix.Select(c => c with { ToleranceMm = mm }).ToList() };

    [Test]
    public async Task Approve_CreatesVersion1_ThenVersion2()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;

        var first = await ApproveAsync(admin, partId, "первая");
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var v1 = await first.ReadJsonAsync<ApprovedVersionView>();
        (await SaveAsync(admin, partId, Tolerance(await DraftAsync(admin, partId), 50))).EnsureSuccessStatusCode();
        var v2 = await (await ApproveAsync(admin, partId)).ReadJsonAsync<ApprovedVersionView>();

        (v1.Version, v1.Comment, v1.RunId).ShouldBe((1, "первая", (Guid?)null));
        v1.UserName.ShouldNotBeNullOrWhiteSpace();
        v2.Version.ShouldBe(2);
        var versions = await admin.GetJsonAsync<ApprovedVersionView[]>($"/api/clash/parts/{partId}/versions");
        versions.Select(v => v.Version).ShouldBe([2, 1]);
        var stored = await admin.GetJsonAsync<ApprovedVersionDetails>($"/api/clash/parts/{partId}/versions/1");
        ConfigurationJson.Deserialize(stored.Configuration.GetRawText()).Matrix.ShouldAllBe(c => c.ToleranceMm == 20);
        var summary = (await admin.GetJsonAsync<ClashPartSummary[]>("/api/clash/parts")).Single(p => p.Id == partId);
        summary.ApprovedVersion.ShouldBe(2);
        await EventuallyAsync(async () => await DbAsync(db => db.SystemEvents.AnyAsync(e => e.Text.Contains("Утверждена версия 2"))));
    }

    [Test]
    public async Task Approve_WithProblems_400()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        var draft = await DraftAsync(admin, partId);
        (await SaveAsync(admin, partId, draft with { Matrix = draft.Matrix.Select(c => c with { Enabled = false }).ToList() })).EnsureSuccessStatusCode();

        var response = await ApproveAsync(admin, partId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("нет включённых проверок");
        (await DbAsync(db => db.Set<ClashPartVersion>().CountAsync())).ShouldBe(0);
    }

    [Test]
    public async Task Approve_NoChanges_400()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        (await ApproveAsync(admin, partId)).EnsureSuccessStatusCode();

        var response = await ApproveAsync(admin, partId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("изменений нет");
    }

    [Test]
    public async Task Approve_DraftEditAfter_DoesNotChangeApproved()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        (await ApproveAsync(admin, partId)).EnsureSuccessStatusCode();

        (await SaveAsync(admin, partId, Tolerance(await DraftAsync(admin, partId), 75))).EnsureSuccessStatusCode();

        var approved = await admin.GetJsonAsync<ApprovedVersionDetails>($"/api/clash/parts/{partId}/versions/1");
        ConfigurationJson.Deserialize(approved.Configuration.GetRawText()).Matrix.ShouldAllBe(c => c.ToleranceMm == 20);
        (await DbAsync(db => db.Set<ClashPart>().SingleAsync(p => p.Id == partId))).ApprovedVersion.ShouldBe(1);
    }

    [Test]
    public async Task ResetDraft_TakesApproved()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/reset", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ApproveAsync(admin, partId)).EnsureSuccessStatusCode();
        (await SaveAsync(admin, partId, Tolerance(await DraftAsync(admin, partId), 75))).EnsureSuccessStatusCode();

        var response = await admin.PostAsync($"/api/clash/parts/{partId}/draft/reset", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await DraftAsync(admin, partId)).Matrix.ShouldAllBe(c => c.ToleranceMm == 20);
        (await ApproveAsync(admin, partId)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // черновик снова равен утверждённой
    }

    [Test]
    public async Task Approve_Observer_403()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        var viewer = await ClientAsync(UserRole.Viewer);

        (await ApproveAsync(viewer, partId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/clash/parts/{partId}/draft/reset", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/clash/parts/{partId}/versions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
