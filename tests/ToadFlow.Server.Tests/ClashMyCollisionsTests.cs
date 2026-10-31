using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>«Мои коллизии»: коллизии разделов профиля по всем утверждённым проверкам — одна выборка для веба и плагина.</summary>
public class ClashMyCollisionsTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashMyCollisionsTests() => Time = _time;

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

    /// <summary>Проектировщик с разделами; разделы задаёт координатор, поэтому — прямо в базе.</summary>
    private Task<HttpClient> DesignerAsync(params string[] disciplines) => ClientAsync(UserRole.Designer, disciplines);

    private static async Task<MyCollisionRow[]> MyAsync(HttpClient client, string query = "") =>
        (await client.GetJsonAsync<MyCollisionPage>($"/api/clash/my{query}")).Items.ToArray();

    [Test]
    public async Task My_ByDiscipline_BothSidesSeeIt()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1, disciplineA: "КР", disciplineB: "ОВК")], approve: true);

        var row = (await MyAsync(await DesignerAsync("ОВК"))).ShouldHaveSingleItem();
        (await MyAsync(await DesignerAsync("КР"))).ShouldHaveSingleItem();
        (await MyAsync(await DesignerAsync("АР"))).ShouldBeEmpty();

        (row.PartId, row.MineToFix, row.Collision.Number).ShouldBe((partId, true, 1));
        row.PartName.ShouldStartWith("Часть");
    }

    /// <summary>ОВК × ОВК — для ОВК одна строка; «Беру» ставит «обе стороны».</summary>
    [Test]
    public async Task My_SameDiscipline_OnceAndTakeBoth()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1, disciplineA: "ОВК", disciplineB: "ОВК")], approve: true);
        var ovk = await DesignerAsync("ОВК");
        var row = (await MyAsync(ovk)).ShouldHaveSingleItem();
        (await admin.PostJsonAsync($"/api/clash/collisions/{row.Collision.Id}/assignee", new { assignee = "SideA" })).EnsureSuccessStatusCode();

        (await ovk.PostAsync($"/api/clash/collisions/{row.Collision.Id}/take", null)).EnsureSuccessStatusCode();

        (await MyAsync(ovk)).ShouldHaveSingleItem().Collision.Assignee.ShouldBe(CollisionAssignee.Both);
        (await ovk.GetJsonAsync<MySummary>("/api/clash/my/summary")).ToFix.ShouldBe(1);
    }

    [Test]
    public async Task My_ToFixAndTaken_SplitByAssignee()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1, disciplineA: "КР", disciplineB: "ОВК"), Hit(2, disciplineA: "КР", disciplineB: "ОВК")], approve: true);
        var (ovk, kr) = (await DesignerAsync("ОВК"), await DesignerAsync("КР"));
        var second = (await MyAsync(kr)).Single(r => r.Collision.Number == 2);

        (await kr.PostAsync($"/api/clash/collisions/{second.Collision.Id}/take", null)).EnsureSuccessStatusCode();

        (await MyAsync(ovk)).ShouldHaveSingleItem().Collision.Number.ShouldBe(1); // по умолчанию — «Мне править»
        var taken = (await MyAsync(ovk, "?scope=taken")).ShouldHaveSingleItem();
        (taken.Collision.Number, taken.MineToFix, taken.Collision.Assignee).ShouldBe((2, false, CollisionAssignee.SideA));
        taken.TakenBy.ShouldNotBeNullOrWhiteSpace();
        taken.TakenAt.ShouldNotBeNull();
        (await MyAsync(ovk, "?scope=all")).Length.ShouldBe(2);
        (await MyAsync(kr)).Length.ShouldBe(2); // КР правит обе: №1 — обе стороны, №2 — взял сам
        var summary = await ovk.GetJsonAsync<MySummary>("/api/clash/my/summary");
        (summary.ToFix, summary.TakenByOther).ShouldBe((1, 1));
        summary.Disciplines.ShouldBe(["ОВК"]);
    }

    [Test]
    public async Task My_NoDisciplines_EmptyWithHint()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var designer = await ClientAsync(UserRole.Designer);

        var page = await designer.GetJsonAsync<MyCollisionPage>("/api/clash/my");
        var summary = await designer.GetJsonAsync<MySummary>("/api/clash/my/summary");

        (page.Total, page.Items.Count).ShouldBe((0, 0));
        summary.Disciplines.ShouldBeEmpty();
        (summary.ToFix, summary.TakenByOther, summary.NewSinceVisit).ShouldBe((0, 0, 0));
    }

    [Test]
    public async Task My_OpenByDefault_FakeByFilter()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2), Hit(3)], approve: true);
        var ov = await DesignerAsync("ОВ");
        var rows = await MyAsync(ov);
        (await admin.PostJsonAsync($"/api/clash/collisions/{rows[1].Collision.Id}/status", new { status = "Fake", note = "Гильза" })).EnsureSuccessStatusCode();
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 3).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Unchecked)));

        (await MyAsync(ov)).Select(r => r.Collision.Number).ShouldBe([1, 3]); // «не проверена» — тоже открытая
        (await MyAsync(ov, "?status=fake")).ShouldHaveSingleItem().Collision.Number.ShouldBe(2);
        (await MyAsync(ov, "?status=all")).Length.ShouldBe(3);
        (await MyAsync(ov, "?sort=depth&pageSize=1")).ShouldHaveSingleItem();
    }

    /// <summary>Плагин спрашивает коллизии открытой модели по её ключу.</summary>
    [Test]
    public async Task My_FilterByModel_ForPlugin()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1, modelA: "a", modelB: "b"), Hit(2, modelA: "c", modelB: "b")], approve: true);
        var ov = await DesignerAsync("ОВ");

        (await MyAsync(ov, "?model=c")).ShouldHaveSingleItem().Collision.Number.ShouldBe(2);
        (await MyAsync(ov, "?model=b")).Length.ShouldBe(2);
        (await MyAsync(ov, $"?partId={partId}&level=Этаж%201")).Length.ShouldBe(2);
        (await MyAsync(ov, $"?partId={Guid.NewGuid()}")).ShouldBeEmpty();
        (await MyAsync(ov, "?pair=АР%20×%20ОВ")).Length.ShouldBe(2);
        (await MyAsync(ov, "?object=" + Uri.EscapeDataString("Без проекта"))).Length.ShouldBe(2); // часть без объекта — как в списке проектов
        (await MyAsync(ov, "?object=DON")).ShouldBeEmpty();
    }

    /// <summary>
    ///     Новые считаются от прошлого захода: отметка сдвигается не чаще раза в 30 минут, поэтому открытие страницы их не
    ///     обнуляет. «Вернулась» после отметки — тоже новая.
    /// </summary>
    [Test]
    public async Task Visit_NewSinceCountsFromPrevious()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var ov = await DesignerAsync("ОВ");

        (await ov.PostAsync("/api/clash/my/visit", null)).EnsureSuccessStatusCode();
        var first = await ov.GetJsonAsync<MySummary>("/api/clash/my/summary");
        (first.NewSinceVisit, first.PreviousSeenAt).ShouldBe((1, null)); // захода не было — последние 7 дней

        _time.Advance(TimeSpan.FromMinutes(10));
        (await ov.PostAsync("/api/clash/my/visit", null)).EnsureSuccessStatusCode();
        (await ov.GetJsonAsync<MySummary>("/api/clash/my/summary")).PreviousSeenAt.ShouldBeNull(); // меньше 30 минут — отметка та же

        _time.Advance(TimeSpan.FromHours(1));
        (await ov.PostAsync("/api/clash/my/visit", null)).EnsureSuccessStatusCode();
        var second = await ov.GetJsonAsync<MySummary>("/api/clash/my/summary");
        second.PreviousSeenAt.ShouldNotBeNull();
        second.NewSinceVisit.ShouldBe(0);

        _time.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(admin, engine, partId, [Hit(2)], [new EngineGonePair("a", "ua-1", "b", "ub-1", "InGeometry", "InGeometry", null)]);
        _time.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(admin, engine, partId, [Hit(1), Hit(2)]);

        (await ov.GetJsonAsync<MySummary>("/api/clash/my/summary")).NewSinceVisit.ShouldBe(2);
        (await MyAsync(ov, "?since=visit")).Select(r => (r.Collision.Number, r.Collision.Status))
            .ShouldBe([(1, CollisionStatus.Returned), (2, CollisionStatus.Active)]);
    }

    [Test]
    public async Task Take_NotMyDiscipline_400()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var id = (await MyAsync(await DesignerAsync("ОВ"))).Single().Collision.Id;

        var response = await (await DesignerAsync("КР")).PostAsync($"/api/clash/collisions/{id}/take", null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await (await ClientAsync(UserRole.Viewer)).PostAsync($"/api/clash/collisions/{id}/take", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Take_WritesFeedWithName()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var ov = await DesignerAsync("ОВ");
        var me = await ov.GetJsonAsync<UserView>("/api/auth/me");
        var id = (await MyAsync(ov)).Single().Collision.Id;

        (await ov.PostAsync($"/api/clash/collisions/{id}/take", null)).EnsureSuccessStatusCode();

        var card = await ov.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");
        card.Collision.Assignee.ShouldBe(CollisionAssignee.SideB);
        var entry = card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Assignee));
        entry.Text.ShouldContain($"взял(а) {me.DisplayName}");
        entry.UserId.ShouldBe(me.Id);
    }

    [Test]
    public async Task Assignee_WithNote_FeedHasNote()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Hit(1)], approve: true);
        var ov = await DesignerAsync("ОВ");
        var id = (await MyAsync(ov)).Single().Collision.Id;

        (await ov.PostJsonAsync($"/api/clash/collisions/{id}/assignee", new { assignee = "SideA", note = "Стену двигает АР" })).EnsureSuccessStatusCode();

        var card = await ov.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");
        card.Collision.Assignee.ShouldBe(CollisionAssignee.SideA);
        card.Feed.Single(e => e.Kind == nameof(CollisionEventKind.Note)).Text.ShouldBe("Стену двигает АР");
        card.Feed[0].Kind.ShouldBe(nameof(CollisionEventKind.Note)); // примечание — после смены, сверху
        (await ov.PostJsonAsync($"/api/clash/collisions/{id}/assignee", new { assignee = "SideB" })).EnsureSuccessStatusCode(); // без примечания
        card = await ov.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}");
        card.Feed.Count(e => e.Kind == nameof(CollisionEventKind.Note)).ShouldBe(1);
    }
}
