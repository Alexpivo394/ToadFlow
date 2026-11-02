using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Data;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>«Новые для меня»: коллизия новая для человека, пока он не открыл её карточку после того, как она появилась или вернулась.</summary>
public class ClashSeenTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashSeenTests() => Time = _time;

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    private static async Task RunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits,
        IReadOnlyList<EngineGonePair>? gone = null, bool approve = false)
    {
        var start = approve
            ? await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })
            : await admin.PostAsync($"/api/clash/parts/{partId}/approved/run", null);
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, hits.Select(h => h with { FingerprintA = 1, FingerprintB = 2 }).ToList(), Stats,
            gone ?? [], [])).EnsureSuccessStatusCode();
    }

    private static async Task<int> NewForMeAsync(HttpClient client) => (await client.GetJsonAsync<MySummary>("/api/clash/my/summary")).NewForMe;

    private static async Task<MyCollisionRow[]> UnseenAsync(HttpClient client) =>
        (await client.GetJsonAsync<MyCollisionPage>("/api/clash/my?scope=all&since=unseen")).Items.ToArray();

    private static async Task SeenAsync(HttpClient client, Guid id) =>
        (await client.PostAsync($"/api/clash/collisions/{id}/seen", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    [Test]
    public async Task Seen_OpenCard_NoLongerNew()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var ov = await ClientAsync(UserRole.Designer, "ОВ");
        (await NewForMeAsync(ov)).ShouldBe(1);
        var row = (await UnseenAsync(ov)).ShouldHaveSingleItem();
        row.Unseen.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(1));
        await SeenAsync(ov, row.Collision.Id);

        (await NewForMeAsync(ov)).ShouldBe(0);
        (await UnseenAsync(ov)).ShouldBeEmpty();
        (await ov.GetJsonAsync<MyCollisionPage>("/api/clash/my")).Items.ShouldHaveSingleItem().Unseen.ShouldBeFalse();
        (await ov.PostAsync($"/api/clash/collisions/{Guid.NewGuid()}/seen", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Seen_ReturnedAfterOpen_NewAgain()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var ov = await ClientAsync(UserRole.Designer, "ОВ");
        _time.Advance(TimeSpan.FromMinutes(1));
        await SeenAsync(ov, (await UnseenAsync(ov)).Single().Collision.Id);

        _time.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(admin, engine, partId, [Hit(2)], [new EngineGonePair("a", "ua-1", "b", "ub-1", "InGeometry", "InGeometry", null)]);
        _time.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2)]);

        (await NewForMeAsync(ov)).ShouldBe(2);
        (await UnseenAsync(ov)).Select(r => (r.Collision.Number, r.Collision.Status)).OrderBy(r => r.Number)
            .ShouldBe([(1, CollisionStatus.Returned), (2, CollisionStatus.Active)]);
    }

    /// <summary>Координатор снял «фиктивную» — коллизия снова открыта и для проектировщика снова новая, как вернувшаяся после прогона.</summary>
    [Test]
    public async Task Seen_FakeRemovedByHand_NewAgain()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var ov = await ClientAsync(UserRole.Designer, "ОВ");
        _time.Advance(TimeSpan.FromMinutes(1));
        var id = (await UnseenAsync(ov)).Single().Collision.Id;
        await SeenAsync(ov, id);
        (await NewForMeAsync(ov)).ShouldBe(0);

        _time.Advance(TimeSpan.FromMinutes(5));
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Fake", note = "Гильза" })).EnsureSuccessStatusCode();
        _time.Advance(TimeSpan.FromMinutes(5));
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status = "Active" })).EnsureSuccessStatusCode();

        (await NewForMeAsync(ov)).ShouldBe(1);
    }

    [Test]
    public async Task Seen_PerUser()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var (first, second) = (await ClientAsync(UserRole.Designer, "ОВ"), await ClientAsync(UserRole.Designer, "ОВ"));
        _time.Advance(TimeSpan.FromMinutes(1));

        await SeenAsync(first, (await UnseenAsync(first)).Single().Collision.Id);

        (await NewForMeAsync(first)).ShouldBe(0);
        (await NewForMeAsync(second)).ShouldBe(1);
    }

    [Test]
    public async Task Seen_Bulk_NoFeed()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2)], approve: true);
        var ov = await ClientAsync(UserRole.Designer, "ОВ");
        var ids = (await UnseenAsync(ov)).Select(r => r.Collision.Id).ToList();
        _time.Advance(TimeSpan.FromMinutes(1));

        var result = await (await ov.PostJsonAsync("/api/clash/collisions/bulk", new { partId, action = "Seen", ids })).ReadJsonAsync<BulkResult>();

        (result.Done, result.Refused.Count).ShouldBe((2, 0));
        (await NewForMeAsync(ov)).ShouldBe(0);
        (await DbAsync(db => db.Set<ClashCollisionEvent>().CountAsync(e => e.UserId != null))).ShouldBe(0);
    }

    [Test]
    public async Task Seen_AfterMigration_ExistingOpenAreSeen()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3)], approve: true);
        var (ov, ar) = (await ClientAsync(UserRole.Designer, "ОВ"), await ClientAsync(UserRole.Designer, "АР"));
        var created = await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 1).Select(c => c.CreatedAt).SingleAsync());
        var returnedAt = created.AddMinutes(-30).AddDays(1);
        // Как до обновления: ни отметок, ни opened_at; №2 возвращалась, №3 закрыта.
        await DbAsync(async db =>
        {
            await db.Set<ClashCollision>().ExecuteUpdateAsync(c => c.SetProperty(x => x.OpenedAt, DateTimeOffset.UnixEpoch));
            await db.Set<ClashCollision>().Where(c => c.Number == 3).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fixed));
            var second = await db.Set<ClashCollision>().Where(c => c.Number == 2).Select(c => c.Id).SingleAsync();
            db.Set<ClashCollisionEvent>().Add(new ClashCollisionEvent
            {
                Id = Guid.CreateVersion7(), CollisionId = second, At = returnedAt, Kind = CollisionEventKind.Status, FromStatus = CollisionStatus.Fixed,
                ToStatus = CollisionStatus.Returned
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM clash_collision_seen");
        });

        await DbAsync(db => db.Database.ExecuteSqlRawAsync(ClashSeenBackfill.Sql));

        var opened = await DbAsync(db => db.Set<ClashCollision>().OrderBy(c => c.Number).Select(c => c.OpenedAt).ToListAsync());
        opened[0].ShouldBe(created, TimeSpan.FromMilliseconds(1));
        opened[1].ShouldBe(returnedAt, TimeSpan.FromMilliseconds(1));
        (await NewForMeAsync(ov)).ShouldBe(0);
        (await NewForMeAsync(ar)).ShouldBe(0);
        (await DbAsync(db => db.Set<ClashCollisionSeen>().CountAsync())).ShouldBe(4); // ОВ и АР — по две открытые
    }

    [Test]
    public async Task My_SummaryNewForMe()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1, disciplineA: "КР", disciplineB: "ОВК"), Hit(2, disciplineA: "АР", disciplineB: "ВК")], approve: true);
        var ovk = await ClientAsync(UserRole.Designer, "ОВК");

        var summary = await ovk.GetJsonAsync<MySummary>("/api/clash/my/summary");

        summary.NewForMe.ShouldBe(1);
        summary.NewSinceVisit.ShouldBe(1); // старое поле для плагина до 3.4.0 остаётся
    }
}
