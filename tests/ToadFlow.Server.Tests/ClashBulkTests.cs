using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Массовые действия над коллизиями: те же права и переходы, что поштучно, отказы — по коллизии, лента с общим Id операции.</summary>
public class ClashBulkTests : ServerTest
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

    private static async Task<Dictionary<int, Guid>> IdsAsync(HttpClient client, Guid partId) =>
        (await client.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all")).Items.ToDictionary(c => c.Number, c => c.Id);

    private static Task<HttpResponseMessage> BulkAsync(HttpClient client, object body) => client.PostJsonAsync("/api/clash/collisions/bulk", body);

    [Test]
    public async Task Bulk_Fake_RequiresNote()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1));
        await using var _ = agent;
        var ids = await IdsAsync(admin, partId);

        var response = await BulkAsync(admin, new { partId, action = "Fake", ids = ids.Values });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("примечание");
        (await IdsAsync(admin, partId)).Count.ShouldBe(1);
        (await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{ids[1]}")).Collision.Status.ShouldBe(CollisionStatus.New);
    }

    [Test]
    public async Task Bulk_Ids_AllDone_FeedHasBulkId()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1), Hit(2), Hit(3));
        await using var _ = agent;
        var ids = await IdsAsync(admin, partId);

        var result = await (await BulkAsync(admin, new { partId, action = "Fake", note = "Гильзы есть", ids = ids.Values })).ReadJsonAsync<BulkResult>();

        (result.Done, result.Refused.Count).ShouldBe((3, 0));
        var events = await DbAsync(db => db.Set<ClashCollisionEvent>().Where(e => e.Kind == CollisionEventKind.Status && e.UserId != null).ToListAsync());
        events.Select(e => e.CollisionId).Order().ShouldBe(ids.Values.Order());
        events.Select(e => e.BulkId).Distinct().ShouldHaveSingleItem().ShouldNotBeNull();
        events.ShouldAllBe(e => e.Text == "Гильзы есть" && e.ToStatus == CollisionStatus.Fake);
        var card = await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{ids[2]}");
        card.Collision.Status.ShouldBe(CollisionStatus.Fake);
        card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Status)).BulkId.ShouldBe(events[0].BulkId);
    }

    [Test]
    public async Task Bulk_Designer_ForeignRefused_OthersDone()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1, disciplineA: "АР", disciplineB: "ОВ"), Hit(2, disciplineA: "КР", disciplineB: "ВК"));
        await using var _ = agent;
        var ids = await IdsAsync(admin, partId);
        var designer = await ClientAsync(UserRole.Designer, "ОВ");

        var response = await BulkAsync(designer, new { partId, action = "FixedPending", ids = ids.Values });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.ReadJsonAsync<BulkResult>();
        result.Done.ShouldBe(1);
        var refusal = result.Refused.ShouldHaveSingleItem();
        refusal.Number.ShouldBe(2);
        refusal.Reason.ShouldContain("своего раздела");
        (await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{ids[1]}")).Collision.Status.ShouldBe(CollisionStatus.FixedPending);
        (await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{ids[2]}")).Collision.Status.ShouldBe(CollisionStatus.New);
    }

    [Test]
    public async Task Bulk_FilterCountChanged_Conflict()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1), Hit(2), Hit(3));
        await using var _ = agent;

        var response = await BulkAsync(admin, new { partId, action = "FixedPending", filter = new { pair = "АР × ОВ" }, expectedCount = 2 });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("count").GetInt32().ShouldBe(3);
        body.RootElement.GetProperty("error").GetString().ShouldBe("Коллизий по фильтру теперь 3 — проверьте и повторите");
        (await DbAsync(db => db.Set<ClashCollision>().CountAsync(c => c.Status == CollisionStatus.FixedPending))).ShouldBe(0);

        var ok = await (await BulkAsync(admin, new { partId, action = "FixedPending", filter = new { pair = "АР × ОВ" }, expectedCount = 3 }))
            .ReadJsonAsync<BulkResult>();
        ok.Done.ShouldBe(3);
    }

    [Test]
    public async Task Bulk_Over5000_400()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1));
        await using var _ = agent;

        var response = await BulkAsync(admin, new { partId, action = "Take", ids = Enumerable.Range(0, 5001).Select(_ => Guid.NewGuid()) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("За раз — не больше 5 000 коллизий");
        (await BulkAsync(admin, new { partId, action = "Take" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Bulk_FixedPending_ClosedRefused()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1), Hit(2));
        await using var _ = agent;
        var ids = await IdsAsync(admin, partId);
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 2).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fixed)));

        var result = await (await BulkAsync(admin, new { partId, action = "FixedPending", ids = ids.Values })).ReadJsonAsync<BulkResult>();

        result.Done.ShouldBe(1);
        result.Refused.ShouldHaveSingleItem().ShouldBe(new BulkRefusal(2, "Исправленной можно отметить только открытую коллизию"));
    }

    [Test]
    public async Task Single_Status_StillWorks()
    {
        var (admin, agent, partId) = await SetupAsync(Hit(1));
        await using var _ = agent;
        var id = (await IdsAsync(admin, partId))[1];

        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake", note = "Гильза" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/assignee", new { assignee = "SideA", note = "Вам" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/notes", new { text = "Смотрю" })).EnsureSuccessStatusCode();

        var card = await admin.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");
        (card.Collision.Status, card.Collision.Assignee).ShouldBe((CollisionStatus.Fake, CollisionAssignee.SideA));
        card.Feed.Count(e => e.Kind == nameof(CollisionEventKind.Note)).ShouldBe(2);
        card.Feed.ShouldAllBe(e => e.BulkId == null);
    }
}
