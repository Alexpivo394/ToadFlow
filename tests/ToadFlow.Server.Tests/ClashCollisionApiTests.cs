using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Viewer;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashCollisionApiTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    /// <summary>Утверждённый прогон части сдаётся работником: коллизии, ушедшие пары и сопоставления.</summary>
    private static async Task<Guid> RunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits,
        IReadOnlyList<EngineGonePair>? gone = null, IReadOnlyList<EngineIdentityMatch>? matches = null, bool approve = false)
    {
        var start = approve
            ? await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })
            : await admin.PostAsync($"/api/clash/parts/{partId}/approved/run", null);
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var claim = await engine.PostAsync("/api/engine/runs/claim", null);
        var runId = (await claim.ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, gone ?? [], matches ?? [])).EnsureSuccessStatusCode();
        return runId;
    }

    private static EngineHit Fp(EngineHit hit) => hit with { FingerprintA = 1, FingerprintB = 2 };

    private static async Task<CollisionRow[]> ListAsync(HttpClient client, Guid partId, string query = "") =>
        (await client.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions{query}")).Items.ToArray();

    [Test]
    public async Task Summary_OpenByStatusAndCheck()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var check = (await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary")).Checks;
        await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2)), Fp(Hit(3))], approve: true);
        var key = (await DbAsync(db => db.Set<ClashPartVersion>().SingleAsync())).ConfigurationJson;
        var cell = ToadFlow.Server.Clash.Parts.DraftResultEndpoints.CheckKey(
            Alexpivo394.Revit.Clash.Rules.ConfigurationJson.Deserialize(key).Matrix.First(c => c.Enabled).GroupA,
            Alexpivo394.Revit.Clash.Rules.ConfigurationJson.Deserialize(key).Matrix.First(c => c.Enabled).GroupB);
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number <= 2).ExecuteUpdateAsync(c => c.SetProperty(x => x.Checks, new[] { cell })));
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 3).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fake)));

        var summary = await admin.GetJsonAsync<CollisionSummary>($"/api/clash/parts/{partId}/approved/summary");

        summary.Total.ShouldBe(2);
        summary.Statuses.Single(s => s.Status == CollisionStatus.New).Count.ShouldBe(2);
        summary.Statuses.Single(s => s.Status == CollisionStatus.Fake).Count.ShouldBe(1);
        summary.Checks.Single(c => c.Key == cell).Count.ShouldBe(2);
        summary.Checks.Count.ShouldBeGreaterThanOrEqualTo(1);
        var run = await admin.GetJsonAsync<ApprovedRunView>($"/api/clash/parts/{partId}/approved/run");
        (run.Version, run.Run.State, run.Summary!.Found).ShouldBe((1, EngineRunState.Succeeded, 3));
    }

    [Test]
    public async Task List_FilterByStatusAndCheck()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1)) with { Checks = ["x|y"] }, Fp(Hit(2)), Fp(Hit(3))], approve: true);
        await RunAsync(admin, engine, partId, [Fp(Hit(1)) with { Checks = ["x|y"] }, Fp(Hit(2))], [new EngineGonePair("a", "ua-3", "b", "ub-3", "Absent", "InGeometry", null)]);

        (await ListAsync(admin, partId)).Select(r => r.Number).ShouldBe([1, 2]); // по умолчанию — открытые
        (await ListAsync(admin, partId, "?status=Fixed")).ShouldHaveSingleItem().Number.ShouldBe(3);
        (await ListAsync(admin, partId, "?status=all")).Length.ShouldBe(3);
        (await ListAsync(admin, partId, "?check=x%7Cy")).ShouldHaveSingleItem().Number.ShouldBe(1);
        (await ListAsync(admin, partId, "?q=ua-2")).ShouldHaveSingleItem().Number.ShouldBe(2);
        (await ListAsync(admin, partId, "?q=3&status=all")).ShouldHaveSingleItem().UniqueIdA.ShouldBe("ua-3");
    }

    [Test]
    public async Task SetFake_WithoutNote_400()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var id = (await ListAsync(admin, partId)).Single().Id;

        var response = await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake", note = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("примечание");
    }

    [Test]
    public async Task SetFake_WithNote_FeedHasStatusAndNote()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var id = (await ListAsync(admin, partId)).Single().Id;

        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake", note = "Гильза есть" })).EnsureSuccessStatusCode();

        var card = await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");
        card.Collision.Status.ShouldBe(CollisionStatus.Fake);
        var status = card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Status));
        (status.FromStatus, status.ToStatus, status.Text).ShouldBe((CollisionStatus.New, CollisionStatus.Fake, "Гильза есть"));
        status.UserName.ShouldNotBeNullOrWhiteSpace();
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Active" })).EnsureSuccessStatusCode();
        (await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}")).Collision.Status.ShouldBe(CollisionStatus.Active); // найдена — активная
    }

    [Test]
    public async Task Assignee_WritesFeed()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var id = (await ListAsync(admin, partId)).Single().Id;

        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/assignee", new { assignee = "SideB" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/notes", new { text = "Переносим трубу" })).EnsureSuccessStatusCode();

        var card = await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");
        card.Collision.Assignee.ShouldBe(CollisionAssignee.SideB);
        card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Assignee)).Text.ShouldContain("сторона B");
        card.Feed[0].Text.ShouldBe("Переносим трубу"); // новые сверху
        (await ListAsync(admin, partId, "?assignee=SideB")).ShouldHaveSingleItem();
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/notes", new { text = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Merge_PredecessorKeepsNumberAndFeed()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var old = (await ListAsync(admin, partId)).Single();
        (await admin.PostJsonAsync($"/api/clash/collisions/{old.Id}/notes", new { text = "Разбирали" })).EnsureSuccessStatusCode();
        await RunAsync(admin, engine, partId, [Fp(Hit(1)) with { UniqueIdA = "ua-1-new" }], [new EngineGonePair("a", "ua-1", "b", "ub-1", "Absent", "InGeometry", null)],
            [new EngineIdentityMatch("a", "ua-1", "ua-1-new")]);
        var recreated = (await ListAsync(admin, partId)).Single();
        recreated.SuggestedPredecessorNumber.ShouldBe(old.Number);

        var response = await admin.PostJsonAsync($"/api/clash/collisions/{recreated.Id}/merge", new { predecessorId = old.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var all = await ListAsync(admin, partId, "?status=all");
        var merged = all.ShouldHaveSingleItem();
        (merged.Id, merged.Number, merged.UniqueIdA, merged.Status, merged.FoundNow).ShouldBe((old.Id, 1, "ua-1-new", CollisionStatus.Returned, true));
        var card = await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{old.Id}");
        card.Feed.ShouldContain(e => e.Text == "Разбирали");
        card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Merged)).Text.ShouldContain("ua-1 → ua-1-new");
    }

    [Test]
    public async Task Card_FeedIncludesDepthSamples()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        await RunAsync(admin, engine, partId, [Fp(Hit(1, depth: 55))]);
        var id = (await ListAsync(admin, partId)).Single().Id;

        var card = await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");

        card.Feed.Where(e => e.Kind == CollisionFeed.DepthKind).Select(e => e.DepthMm).ShouldBe([55f, 30f]);
        card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Found)).Version.ShouldBe(1);
    }

    [Test]
    public async Task CollisionScene_FromLastFoundRun()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models.Select(m => m.ModelId.ToString("N")).Order().ToArray();
        var hit = Fp(Hit(1)) with { ModelA = models[0], UniqueIdA = "wall-1", ModelB = models[1], UniqueIdB = "wall-1", X = 50, Y = 50, Z = 50 };
        var runId = await RunAsync(admin, engine, partId, [hit], approve: true);
        var id = (await ListAsync(admin, partId)).Single().Id;

        var scene = await admin.GetJsonAsync<ViewerSceneView>($"/api/clash/collisions/{id}/scene");
        var meshes = await admin.GetAsync($"/api/clash/collisions/{id}/meshes");

        (scene.RunId, scene.Number).ShouldBe((runId, 1));
        scene.Elements.Where(e => e.Role is "A" or "B").Select(e => e.UniqueId).ShouldBe(["wall-1", "wall-1"]);
        meshes.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/clash/runs/{runId}/elements/{models[0]}/wall-1")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task CollisionScene_SnapshotsPruned_410()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var id = (await ListAsync(admin, partId)).Single().Id;
        var run = await DbAsync(db => db.Set<EngineRun>().SingleAsync(r => r.Kind == EngineRunKind.Approved));
        using (var scope = Server.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
            foreach (var model in run.Models) Directory.Delete(storage.SnapshotFolder(model.ModelId, model.SnapshotId), true);
        }

        (await admin.GetAsync($"/api/clash/collisions/{id}/scene")).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Test]
    public async Task CollisionWrites_Observer_403()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var id = (await ListAsync(admin, partId)).Single().Id;
        var viewer = await ClientAsync(UserRole.Viewer);

        (await viewer.GetAsync($"/api/clash/collisions/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.PostJsonAsync($"/api/clash/collisions/{id}/notes", new { text = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake", note = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/clash/parts/{partId}/approved/run", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Merge_PredecessorFoundNow_Rejected()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        var rows = await ListAsync(admin, partId);

        var response = await admin.PostJsonAsync($"/api/clash/collisions/{rows[1].Id}/merge", new { predecessorId = rows[0].Id });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(admin, partId)).Length.ShouldBe(2);
    }

    /// <summary>Сверка держит блокировку части: ручная правка ждёт её конца и видит итог сверки, а не затирается им.</summary>
    [Test]
    [Arguments("status")]
    [Arguments("merge")]
    public async Task ManualChange_WaitsForPartLock(string action)
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        await RunAsync(admin, engine, partId, [Fp(Hit(1)) with { UniqueIdA = "ua-1-new" }], [new EngineGonePair("a", "ua-1", "b", "ub-1", "Absent", "InGeometry", null)]);
        var all = await ListAsync(admin, partId, "?status=all");
        var (old, recreated) = (all.Single(c => c.UniqueIdA == "ua-1"), all.Single(c => c.UniqueIdA == "ua-1-new"));

        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlow.Server.Data.ToadFlowDb>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM clash_parts WHERE id = {partId} FOR UPDATE");
        var change = action == "status"
            ? admin.PostJsonAsync($"/api/clash/collisions/{recreated.Id}/status", new { status = "Fake", note = "Гильза есть" })
            : admin.PostJsonAsync($"/api/clash/collisions/{recreated.Id}/merge", new { predecessorId = old.Id });
        await Task.Delay(500);

        change.IsCompleted.ShouldBeFalse();
        await transaction.CommitAsync();
        (await change).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
