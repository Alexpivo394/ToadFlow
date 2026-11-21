using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Analytics;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Аналитика семейств для руководителя и известные проблемы в карточке семейства.</summary>
public class FamiliesAnalyticsTests : ServerTest
{
    public FamiliesAnalyticsTests() => Services = WithoutScanLoop;

    private sealed record Created(Guid Id, int Number);

    private static async Task<Created> CreateAsync(HttpClient client, NewRequest body)
    {
        var response = await client.PostJsonAsync("/api/families/requests", body);
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync<Created>();
    }

    private static async Task OkAsync(Task<HttpResponseMessage> call) => (await call).EnsureSuccessStatusCode();

    [Test]
    public async Task Analytics_Weeks_Median_DeclinedShare()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var card = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var declined = await CreateAsync(author, new NewRequest("Клапан", null, null, null, null));
        var straight = await CreateAsync(author, new NewRequest("Решётка", null, null, null, null));
        await CreateAsync(author, new NewRequest("Отвод", null, null, null, null));
        foreach (var id in new[] { declined.Id, straight.Id }) await OkAsync(coordinator.PostAsync($"/api/families/requests/{id}/take", null));
        await OkAsync(coordinator.PostJsonAsync($"/api/families/requests/{declined.Id}/done", new RequestCard(card)));
        await OkAsync(author.PostJsonAsync($"/api/families/requests/{declined.Id}/decline", new RequestMessageRequest("не тот")));
        foreach (var id in new[] { declined.Id, straight.Id })
        {
            await OkAsync(coordinator.PostJsonAsync($"/api/families/requests/{id}/done", new RequestCard(card)));
            await OkAsync(author.PostAsync($"/api/families/requests/{id}/accept", null));
        }

        var analytics = await coordinator.GetJsonAsync<FamiliesAnalyticsView>("/api/families/analytics?days=28");

        analytics.Weeks.Count.ShouldBe(4);
        (analytics.Weeks[^1].Created, analytics.Weeks[^1].Closed).ShouldBe((3, 2));
        var common = analytics.ByDirection.Single();
        (common.Name, common.Open, common.Accepted, common.DeclinedShare).ShouldBe(("Общая очередь", 1, 2, 0.5));
        common.MedianHours.ShouldNotBeNull();
        analytics.ByAssignee.ShouldBeEmpty();
        (await author.GetAsync("/api/families/analytics")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Analytics_TopLoadedAndUnused()
    {
        var now = DateTimeOffset.UtcNow;
        var loaded = await DbAsync(db => SeedCardAsync(db, "Дверь", @"АР\Дверь.rfa", CardState.Approved, null, now.AddDays(-300)));
        var unused = await DbAsync(db => SeedCardAsync(db, "Окно", @"АР\Окно.rfa", CardState.Approved, null, now.AddDays(-300)));
        await DbAsync(db => SeedCardAsync(db, "Новое", @"АР\Новое.rfa", CardState.Approved, null, now));
        var designer = await ClientAsync(UserRole.Designer);
        await OkAsync(designer.PostJsonAsync($"/api/families/{loaded}/loads", new LoadReport(null, "ЖК Север")));

        var analytics = await (await ClientAsync(UserRole.Operator)).GetJsonAsync<FamiliesAnalyticsView>("/api/families/analytics");

        analytics.TopLoaded.Select(c => (c.CardId, c.Loads)).ShouldBe([(loaded, 1)]);
        analytics.Unused.Select(c => c.CardId).ShouldBe([unused]);
    }

    [Test]
    public async Task Xlsx_Opens()
    {
        var response = await (await ClientAsync(UserRole.Operator)).GetAsync("/api/families/analytics.xlsx");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        (await response.Content.ReadAsByteArrayAsync())[..2].ShouldBe("PK"u8.ToArray());
    }

    [Test]
    public async Task Card_ShowsFixRequestsAndProjects()
    {
        var card = await DbAsync(db => SeedCardAsync(db, "Дверь", @"АР\Дверь.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var designer = await ClientAsync(UserRole.Designer);
        var fix = await CreateAsync(designer, new NewRequest("Не тот уровень", null, null, null, null, RequestKind.Fix, TargetCardId: card));
        await OkAsync(designer.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(null, "ЖК Север")));
        await OkAsync(designer.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(null, "ЖК Север")));

        var view = await designer.GetJsonAsync<FamilyCardView>($"/api/families/{card}");

        view.OpenFixRequests.Select(r => (r.Id, r.Number, r.Title)).ShouldBe([(fix.Id, fix.Number, "Не тот уровень")]);
        view.LoadedProjects.ShouldBe(["ЖК Север"]);
    }
}
