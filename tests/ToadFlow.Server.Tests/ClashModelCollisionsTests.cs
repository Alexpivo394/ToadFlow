using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Plugin;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>
///     Плагин «Коллизии»: коллизии открытой модели по всем утверждённым частям. Своя сторона — по модели, а не по разделам
///     пользователя; «мне править» сначала, потом порядок отчёта.
/// </summary>
public class ClashModelCollisionsTests : ServerTest
{
    private sealed record Setup(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine, string Ar, string Ov, string Kr);

    private async Task<Setup> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt", "POL_K1_KR.rvt");
        var models = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        string Key(string name) => models.Single(m => m.Name == name).Id.ToString("N");
        return new Setup(admin, agent, partId, EngineClient(Server), Key("POL_K1_AR.rvt"), Key("POL_K1_OV.rvt"), Key("POL_K1_KR.rvt"));
    }

    private static async Task RunAsync(Setup setup, IReadOnlyList<EngineHit> hits, Guid? partId = null)
    {
        var part = partId ?? setup.PartId;
        var approved = await setup.Admin.PostJsonAsync($"/api/clash/parts/{part}/approve", new { comment = (string?)null, run = true });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        var runId = (await (await setup.Engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(setup.Engine, runId, 0, true, hits.Select(h => h with { FingerprintA = 1, FingerprintB = 2 }).ToList(), Stats, [], []))
            .EnsureSuccessStatusCode();
    }

    /// <summary>КР × ОВ: сторона A — КР, B — ОВ.</summary>
    private static EngineHit KrOv(Setup s, int i, string? level = "Этаж 1", double z = 0) =>
        Hit(i, s.Kr, s.Ov, "КР", "ОВ") with { LevelA = level, LevelB = level, Z = z };

    private static EngineHit ArOv(Setup s, int i, string? level = "Этаж 1", double z = 0) =>
        Hit(i, s.Ar, s.Ov, "АР", "ОВ") with { LevelA = level, LevelB = level, Z = z };

    private static Task<ModelCollisionPage> PageAsync(HttpClient client, string key, string query = "") =>
        client.GetJsonAsync<ModelCollisionPage>($"/api/clash/models/{key}/collisions{query}");

    private static Task AssignAsync(Setup s, Guid id, string assignee) =>
        s.Admin.PostJsonAsync($"/api/clash/collisions/{id}/assignee", new { assignee, note = "тест" }).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private static async Task<Guid> IdAsync(Setup s, int number) =>
        (await PageAsync(s.Admin, s.Ov, "?status=all&pageSize=500")).Items.Single(r => r.Collision.Number == number).Collision.Id;

    [Test]
    public async Task ModelCollisions_SideByModel()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1)]);

        var ov = (await PageAsync(s.Admin, s.Ov)).Items.ShouldHaveSingleItem();
        var kr = (await PageAsync(s.Admin, s.Kr)).Items.ShouldHaveSingleItem();

        (ov.Side, kr.Side).ShouldBe(("B", "A"));
        (ov.PartId, ov.MineToFix).ShouldBe((s.PartId, true));
        ov.PartName.ShouldStartWith("Часть");
        (await PageAsync(s.Admin, s.Ar)).Total.ShouldBe(0);
    }

    [Test]
    public async Task ModelCollisions_MineFirst_ThenPairLevelNumber()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1), KrOv(s, 2), KrOv(s, 3), ArOv(s, 4), KrOv(s, 5, "Этаж 2", 3000), KrOv(s, 6, "Этаж 0", -3000)]);
        await AssignAsync(s, await IdAsync(s, 1), "SideA"); // правит КР — для ОВ «другая сторона»
        await AssignAsync(s, await IdAsync(s, 2), "SideB"); // правит ОВ

        var page = await PageAsync(s.Admin, s.Ov);

        // «мои»: пара АР × ОВ раньше КР × ОВ (справочник), внутри пары — Этаж 0, Этаж 1, Этаж 2, потом №; «другая сторона» — последней
        page.Items.Select(r => r.Collision.Number).ShouldBe([4, 6, 2, 3, 5, 1]);
        page.Items.Select(r => r.MineToFix).ShouldBe([true, true, true, true, true, false]);
        (page.Total, page.Mine, page.Other).ShouldBe((6, 5, 1));
    }

    [Test]
    public async Task ModelCollisions_GroupFilter()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1), KrOv(s, 2), KrOv(s, 3)]);
        await AssignAsync(s, await IdAsync(s, 1), "SideA");

        var mine = await PageAsync(s.Admin, s.Ov, "?group=mine");
        var other = await PageAsync(s.Admin, s.Ov, "?group=other");

        mine.Items.Select(r => r.Collision.Number).ShouldBe([2, 3]);
        other.Items.Select(r => r.Collision.Number).ShouldBe([1]);
        (mine.Total, mine.Mine, mine.Other).ShouldBe((2, 2, 1));
        (other.Total, other.Mine, other.Other).ShouldBe((1, 2, 1));
        var taken = other.Items.Single();
        (taken.TakenBy, taken.TakenAt).ShouldNotBe((null, null));
        (await PageAsync(s.Admin, s.Kr, "?group=mine")).Items.Select(r => r.Collision.Number).ShouldBe([1, 2, 3]);
    }

    [Test]
    public async Task ModelCollisions_CoordinatorWithoutDisciplines_SeesModelSide()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1), KrOv(s, 2)]);
        await AssignAsync(s, await IdAsync(s, 1), "SideA");

        foreach (var client in new[] { await ClientAsync(UserRole.Operator), await ClientAsync(UserRole.Designer), await ClientAsync(UserRole.Viewer) })
        {
            var page = await PageAsync(client, s.Ov);
            page.Items.Select(r => (r.Collision.Number, r.MineToFix, r.Side)).ShouldBe([(2, true, "B"), (1, false, "B")]);
        }
    }

    [Test]
    public async Task ModelCollisions_SameModelBothSides_OneRowMine()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [Hit(1, s.Ov, s.Ov, "ОВ", "ОВ"), Hit(2, s.Ov, s.Ov, "ОВ", "ОВ")]);
        await AssignAsync(s, await IdAsync(s, 1), "SideA");
        await AssignAsync(s, await IdAsync(s, 2), "SideB");

        var page = await PageAsync(s.Admin, s.Ov);

        page.Items.Select(r => (r.Collision.Number, r.Side, r.MineToFix)).ShouldBe([(1, "AB", true), (2, "AB", true)]);
        (page.Total, page.Mine, page.Other).ShouldBe((2, 2, 0));
    }

    [Test]
    public async Task ModelCollisions_OnlyApprovedParts()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1)]);
        await DbAsync(db => db.Set<ClashPart>().Where(p => p.Id == s.PartId).ExecuteUpdateAsync(p => p.SetProperty(x => x.ApprovedVersion, (int?)null)));

        var page = await PageAsync(s.Admin, s.Ov);

        (page.Total, page.Items.Count, page.Mine, page.Other).ShouldBe((0, 0, 0, 0));
    }

    [Test]
    public async Task ModelCollisions_Facets_IgnoreOwnFilter()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1), KrOv(s, 2, "Этаж 2", 3000), ArOv(s, 3), ArOv(s, 4, null)]);
        var all = await PageAsync(s.Admin, s.Ov);
        var krOv = all.Facets.Pairs.Single(p => p.Label == "КР × ОВ").Value;

        var byPair = await PageAsync(s.Admin, s.Ov, "?pair=" + Uri.EscapeDataString(krOv));

        all.Facets.Pairs.Select(p => (p.Label, p.Count)).ShouldBe([("АР × ОВ", 2), ("КР × ОВ", 2)]);
        byPair.Items.Select(r => r.Collision.Number).ShouldBe([1, 2]);
        byPair.Facets.Pairs.Select(p => (p.Label, p.Count)).ShouldBe([("АР × ОВ", 2), ("КР × ОВ", 2)]);
        byPair.Facets.Levels.Select(l => (l.Value, l.Label, l.Count)).ShouldBe([("Этаж 1", "Этаж 1", 1), ("Этаж 2", "Этаж 2", 1)]);
        all.Facets.Levels.Select(l => (l.Value, l.Count)).ShouldBe([("Этаж 1", 2), ("Этаж 2", 1), (CollisionLevel.None, 1)]);
        all.Facets.Levels.Last().Label.ShouldBe("Без уровня");

        var byLevel = await PageAsync(s.Admin, s.Ov, "?level=" + Uri.EscapeDataString("Этаж 2"));
        byLevel.Items.Select(r => r.Collision.Number).ShouldBe([2]);
        byLevel.Facets.Levels.Select(l => l.Count).Sum().ShouldBe(4);
        byLevel.Facets.Pairs.Select(p => (p.Label, p.Count)).ShouldBe([("КР × ОВ", 1)]);
        (await PageAsync(s.Admin, s.Ov, "?level=-")).Items.Select(r => r.Collision.Number).ShouldBe([4]);
    }

    [Test]
    public async Task ModelCollisions_Search_ByNumberElementIdUniqueId()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1), KrOv(s, 2), KrOv(s, 3)]);

        (await PageAsync(s.Admin, s.Ov, "?q=2")).Items.Select(r => r.Collision.Number).ShouldBe([2]);
        (await PageAsync(s.Admin, s.Ov, "?q=100003")).Items.Select(r => r.Collision.Number).ShouldBe([3]);
        (await PageAsync(s.Admin, s.Ov, "?q=ua-1")).Items.Select(r => r.Collision.Number).ShouldBe([1]);
        (await PageAsync(s.Admin, s.Ov, "?q=ub-3")).Items.Select(r => r.Collision.Number).ShouldBe([3]);
    }

    [Test]
    public async Task ModelCollisions_StatusDefaultOpen_FakeOnRequest()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, [KrOv(s, 1), KrOv(s, 2)]);
        (await s.Admin.PostJsonAsync($"/api/clash/collisions/{await IdAsync(s, 2)}/status", new { status = "Fake", note = "Гильза" })).EnsureSuccessStatusCode();

        var open = await PageAsync(s.Admin, s.Ov);

        open.Items.Select(r => r.Collision.Number).ShouldBe([1]);
        (await PageAsync(s.Admin, s.Ov, "?status=fake")).Items.Select(r => r.Collision.Number).ShouldBe([2]);
        (await PageAsync(s.Admin, s.Ov, "?status=all")).Total.ShouldBe(2);
        open.Facets.Statuses.Select(f => (f.Value, f.Count)).ShouldBe([("open", 1), ("fake", 1), ("fixed", 0), ("all", 2)]);
    }

    [Test]
    public async Task ModelCollisions_Paging()
    {
        var s = await SetupAsync();
        await using var _ = s.Agent;
        await RunAsync(s, Enumerable.Range(1, 5).Select(i => KrOv(s, i)).ToList());

        var second = await PageAsync(s.Admin, s.Ov, "?page=2&pageSize=2");

        second.Total.ShouldBe(5);
        second.Items.Select(r => r.Collision.Number).ShouldBe([3, 4]);
    }

    [Test]
    public async Task ModelCollisions_UnknownKey_404()
    {
        var admin = await AdminAsync();

        (await admin.GetAsync($"/api/clash/models/{Guid.NewGuid():N}/collisions")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/clash/models/not-a-key/collisions")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
