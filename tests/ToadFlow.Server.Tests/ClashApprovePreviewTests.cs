using System.Net;
using System.Text;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>
///     Предпросмотр утверждения: по последнему прогону черновика — сколько открытых коллизий уйдёт, сколько появится и сколько
///     останется, по парам разделов; плюс отличия черновика от утверждённой версии.
/// </summary>
public class ClashApprovePreviewTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    private static async Task ApproveRunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits)
    {
        var start = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, hits.Select(h => h with { FingerprintA = 1, FingerprintB = 2 }).ToList(), Stats, [], []))
            .EnsureSuccessStatusCode();
    }

    private static async Task DraftRunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits)
    {
        var run = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).ReadJsonAsync<DraftRunSummary>();
        (await engine.PostAsync("/api/engine/runs/claim", null)).EnsureSuccessStatusCode();
        (await PostResultAsync(engine, run.RunId, 0, true, hits, Stats)).EnsureSuccessStatusCode();
    }

    /// <summary>Допуск первой включённой клетки черновика.</summary>
    private static async Task<string> SetToleranceAsync(HttpClient admin, Guid partId, double mm)
    {
        var draft = await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft");
        var config = ConfigurationJson.Deserialize(draft.Configuration.GetRawText());
        var cell = config.Matrix.First(c => c.Enabled);
        var changed = config with { Matrix = [.. config.Matrix.Select(c => c == cell ? c with { ToleranceMm = mm } : c)] };
        (await admin.PutAsync($"/api/clash/parts/{partId}/draft", new StringContent(ConfigurationJson.Serialize(changed), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
        var (a, b) = (config.Groups.Single(g => g.Id == cell.GroupA).Name, config.Groups.Single(g => g.Id == cell.GroupB).Name);
        return $"Клетка {a} × {b}: допуск {cell.ToleranceMm:0.##} → {mm:0.##} мм";
    }

    private static Task<ApprovePreview> PreviewAsync(HttpClient admin, Guid partId) =>
        admin.GetJsonAsync<ApprovePreview>($"/api/clash/parts/{partId}/approve/preview");

    [Test]
    public async Task ApprovePreview_GoneNewSame_ByPair()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await ApproveRunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3)]);
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 3).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fake)));
        await SetToleranceAsync(admin, partId, 50);
        await DraftRunAsync(admin, engine, partId, [Hit(2), Hit(3), Hit(4), Hit(5, disciplineA: "КР", disciplineB: "ОВ")]);

        var preview = await PreviewAsync(admin, partId);

        (preview.NeverRun, preview.Stale, preview.FirstApproval).ShouldBe((false, false, false));
        preview.DraftRunAt.ShouldNotBeNull();
        preview.Pairs.OrderBy(p => p.Pair).ShouldBe([new ApprovePreviewPair("АР × ОВ", 1, 1, 1), new ApprovePreviewPair("КР × ОВ", 0, 1, 0)]);
        preview.Changes.ShouldHaveSingleItem().Kind.ShouldBe(ConfigChangeKind.CellTolerance);
    }

    [Test]
    public async Task ApprovePreview_DraftChangedAfterRun_Stale()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await ApproveRunAsync(admin, engine, partId, [Hit(1)]);
        await DraftRunAsync(admin, engine, partId, [Hit(1)]);

        var text = await SetToleranceAsync(admin, partId, 70);
        var preview = await PreviewAsync(admin, partId);

        preview.Stale.ShouldBeTrue();
        preview.Changes.Select(c => c.Text).ShouldBe([text]);
    }

    [Test]
    public async Task ApprovePreview_NeverRun()
    {
        var (admin, agent, partId, _) = await SetupAsync();
        await using var _a = agent;

        var preview = await PreviewAsync(admin, partId);

        (preview.NeverRun, preview.FirstApproval, preview.DraftRunAt).ShouldBe((true, true, null));
        preview.Pairs.ShouldBeEmpty();
    }

    [Test]
    public async Task ApprovePreview_First_OnlyNew()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await DraftRunAsync(admin, engine, partId, [Hit(1), Hit(2)]);

        var preview = await PreviewAsync(admin, partId);

        preview.FirstApproval.ShouldBeTrue();
        preview.Pairs.ShouldHaveSingleItem().ShouldBe(new ApprovePreviewPair("АР × ОВ", 0, 2, 0));
        preview.Changes.ShouldBeEmpty();
    }

    [Test]
    public async Task VersionChanges_V2AgainstV1()
    {
        var (admin, agent, partId, _) = await SetupAsync();
        await using var _a = agent;
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false })).EnsureSuccessStatusCode();
        var text = await SetToleranceAsync(admin, partId, 35);
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false })).EnsureSuccessStatusCode();

        (await admin.GetJsonAsync<ConfigChange[]>($"/api/clash/parts/{partId}/versions/2/changes")).Select(c => c.Text).ShouldBe([text]);
        (await admin.GetJsonAsync<ConfigChange[]>($"/api/clash/parts/{partId}/versions/1/changes")).ShouldBeEmpty();
        (await admin.GetAsync($"/api/clash/parts/{partId}/versions/9/changes")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
