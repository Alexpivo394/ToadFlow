using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Заявки на семейства: подача, очередь координатора, переписка, вложения, закрытие публикацией, «есть новое».</summary>
public class FamiliesRequestTests : ServerTest
{
    public FamiliesRequestTests() => Services = WithoutScanLoop;

    private FamilyLibrary? _library;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    private sealed record Created(Guid Id, int Number);

    private static async Task<Created> CreateAsync(HttpClient client, string title = "Клапан обратный DN50", RequestUrgency urgency = RequestUrgency.Normal)
    {
        var response = await client.PostJsonAsync("/api/families/requests", new NewRequest(title, "межфланцевый, Ду50", "ОВ", urgency, "Дом на Набережной"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync<Created>();
    }

    private static Task<RequestView> ViewAsync(HttpClient client, Guid id) => client.GetJsonAsync<RequestView>($"/api/families/requests/{id}");

    [Test]
    public async Task Create_DesignerSeesInMine_CoordinatorInQueue()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var other = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);

        var created = await CreateAsync(designer);
        var second = await CreateAsync(designer, "Решётка", RequestUrgency.Urgent);

        created.Number.ShouldBeGreaterThan(0);
        second.Number.ShouldBe(created.Number + 1);
        (await designer.GetJsonAsync<Page<RequestListItem>>("/api/families/requests")).Total.ShouldBe(2);
        (await other.GetJsonAsync<Page<RequestListItem>>("/api/families/requests")).Total.ShouldBe(0);
        var queue = await coordinator.GetJsonAsync<Page<RequestListItem>>("/api/families/requests?scope=all&state=New");
        queue.Items.Select(r => r.Title).ShouldBe(["Решётка", "Клапан обратный DN50"]); // срочная — выше
        var view = await ViewAsync(coordinator, created.Id);
        (view.Request.State, view.Request.AuthorName, view.Request.Section, view.Request.Project, view.Text)
            .ShouldBe((RequestState.New, "Тест Designer", "ОВ", "Дом на Набережной", "межфланцевый, Ду50"));
    }

    [Test]
    public async Task Create_Viewer_Forbidden_EmptyTitle_BadRequest()
    {
        (await (await ClientAsync(UserRole.Viewer)).PostJsonAsync("/api/families/requests", new NewRequest("x", null, null, RequestUrgency.Normal, null)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await ClientAsync(UserRole.Designer)).PostJsonAsync("/api/families/requests", new NewRequest(" ", null, null, RequestUrgency.Normal, null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Take_AuthorSeesUpdateUntilOpened()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(designer);
        (await designer.GetJsonAsync<List<RequestListItem>>("/api/families/requests/updates")).ShouldBeEmpty();

        (await coordinator.PostAsync($"/api/families/requests/{created.Id}/take", null)).EnsureSuccessStatusCode();

        var updates = await designer.GetJsonAsync<List<RequestListItem>>("/api/families/requests/updates");
        updates.Select(u => (u.Id, u.State, u.AssigneeName, u.Unread)).ShouldBe([(created.Id, RequestState.InWork, "Тест Operator", true)]);
        (await designer.PostAsync($"/api/families/requests/{created.Id}/seen", null)).EnsureSuccessStatusCode();
        (await designer.GetJsonAsync<List<RequestListItem>>("/api/families/requests/updates")).ShouldBeEmpty();
        (await ViewAsync(designer, created.Id)).Messages.Single().Text.ShouldBe("взял в работу");
    }

    [Test]
    public async Task Join_ThenWrite_NonMemberDesignerCannotWrite()
    {
        var author = await ClientAsync(UserRole.Designer);
        var other = await ClientAsync(UserRole.Designer);
        var created = await CreateAsync(author);

        (await other.PostJsonAsync($"/api/families/requests/{created.Id}/messages", new RequestMessageRequest("и мне нужен"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await other.PostAsync($"/api/families/requests/{created.Id}/join", null)).EnsureSuccessStatusCode();
        (await other.PostJsonAsync($"/api/families/requests/{created.Id}/messages", new RequestMessageRequest("и мне нужен"))).EnsureSuccessStatusCode();

        var view = await ViewAsync(author, created.Id);
        view.Members.Count.ShouldBe(2);
        view.Messages.Last().Text.ShouldBe("и мне нужен");
        view.Request.Unread.ShouldBeTrue();
        (await other.GetJsonAsync<Page<RequestListItem>>("/api/families/requests")).Total.ShouldBe(1);
        (await author.PostAsync($"/api/families/requests/{created.Id}/leave", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Done_Exists_Reject_Reopen()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var card = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var first = await CreateAsync(designer);
        var second = await CreateAsync(designer, "Решётка");

        (await coordinator.PostJsonAsync($"/api/families/requests/{first.Id}/exists", new RequestCard(card))).EnsureSuccessStatusCode();
        (await coordinator.PostJsonAsync($"/api/families/requests/{second.Id}/reject", new RequestReject(" "))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await coordinator.PostJsonAsync($"/api/families/requests/{second.Id}/reject", new RequestReject("не наш раздел"))).EnsureSuccessStatusCode();
        (await designer.PostJsonAsync($"/api/families/requests/{second.Id}/reject", new RequestReject("x"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var exists = await ViewAsync(designer, first.Id);
        (exists.Request.State, exists.Request.CardId, exists.Request.CardName).ShouldBe((RequestState.Exists, (Guid?)card, "Клапан"));
        var rejected = await ViewAsync(designer, second.Id);
        (rejected.Request.State, rejected.Reason).ShouldBe((RequestState.Rejected, "не наш раздел"));

        (await coordinator.PostAsync($"/api/families/requests/{second.Id}/reopen", null)).EnsureSuccessStatusCode();
        (await ViewAsync(designer, second.Id)).Request.State.ShouldBe(RequestState.InWork);
    }

    [Test]
    public async Task Assign_OnlyToCoordinator()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(designer);
        var assignees = await coordinator.GetJsonAsync<List<RequestAssignee>>("/api/families/requests/assignees");
        var designerId = await DbAsync(db => db.Users.Where(u => u.Role == UserRole.Designer).Select(u => u.Id).FirstAsync());

        (await coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/assign", new RequestAssign(designerId))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var target = assignees.First(a => a.Name == "Тест Operator");
        (await coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/assign", new RequestAssign(target.Id))).EnsureSuccessStatusCode();

        (await ViewAsync(designer, created.Id)).Request.AssigneeId.ShouldBe(target.Id);
    }

    [Test]
    public async Task Publication_ClosesRequestWithCard()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(designer);

        (await coordinator.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\_На проверке\Клапан обратный.rfa", Hash("k"), null, "по заявке", created.Id))).EnsureSuccessStatusCode();
        (await ViewAsync(designer, created.Id)).Request.State.ShouldBe(RequestState.New); // файл ещё не увиден обходом
        await _library.ReportLastAsync(File(@"_На проверке\Клапан обратный.rfa", "k"));

        var view = await ViewAsync(designer, created.Id);
        (view.Request.State, view.Request.CardName, view.Request.AssigneeName, view.Request.Unread).ShouldBe((RequestState.Done, "Клапан обратный", "Тест Operator", true));
        view.Messages.Last().Text.ShouldContain("Клапан обратный");
    }

    [Test]
    public async Task Publication_ClosedRequest_Refused()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(await ClientAsync(UserRole.Designer));
        (await coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/reject", new RequestReject("нет"))).EnsureSuccessStatusCode();

        (await coordinator.PostJsonAsync("/api/families/publications", new PublishRequest($@"{Root}\ОВ\Новое.rfa", Hash("n"), null, null, created.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Similar_FindsFamiliesAndOpenRequests()
    {
        await DbAsync(db => SeedCardAsync(db, "Клапан обратный межфланцевый", @"ОВ\Клапан обратный межфланцевый.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        await DbAsync(db => SeedCardAsync(db, "Решётка", @"ОВ\Решётка.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var designer = await ClientAsync(UserRole.Designer);
        await CreateAsync(designer, "Клапан обратный DN50");
        await CreateAsync(designer, "Отвод");

        var similar = await designer.GetJsonAsync<SimilarResult>("/api/families/requests/similar?q=клапан обратный");

        similar.Families.Select(f => f.Name).ShouldBe(["Клапан обратный межфланцевый"]);
        similar.Requests.Select(r => r.Title).ShouldBe(["Клапан обратный DN50"]);
    }

    [Test]
    public async Task Files_UploadByMember_DownloadByAnyone()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var created = await CreateAsync(designer);
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent("каталог"u8.ToArray());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(bytes, "file", "Каталог ДУ50.pdf");

        (await designer.PostAsync($"/api/families/requests/{created.Id}/files", form)).EnsureSuccessStatusCode();
        (await (await ClientAsync(UserRole.Viewer)).PostAsync($"/api/families/requests/{created.Id}/files", new MultipartFormDataContent()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var file = (await ViewAsync(designer, created.Id)).Files.Single();
        (file.FileName, file.Size, file.UploadedByName).ShouldBe(("Каталог ДУ50.pdf", 14L, "Тест Designer"));
        var download = await (await ClientAsync(UserRole.Viewer)).GetAsync($"/api/families/requests/{created.Id}/files/{file.Id}");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe("каталог"u8.ToArray());
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
    }

    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private async Task<List<DirectionView>> DirectionsAsync(params DirectionInput[] directions)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/directions", directions);
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync<List<DirectionView>>();
    }

    private static async Task<Created> CreateFullAsync(HttpClient client, NewRequest body)
    {
        var response = await client.PostJsonAsync("/api/families/requests", body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<Created>();
    }

    private static NewRequest Body(string title, string? section = null, DateOnly? neededBy = null, bool? blocks = null, string? project = null) =>
        new(title, null, section, null, project, NeededBy: neededBy, BlocksIssue: blocks);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Test]
    public async Task Create_ResolvesDirectionAndKeepsLegacyUrgency()
    {
        await DirectionsAsync(new DirectionInput("ОВ/ВК", ["ОВ"], [], [], null));
        var designer = await ClientAsync(UserRole.Designer);

        var created = await CreateAsync(designer, urgency: RequestUrgency.Urgent);

        var view = await ViewAsync(designer, created.Id);
        (view.Request.DirectionName, view.Request.BlocksIssue, view.Request.Urgency, view.Request.Kind, view.Request.Turn)
            .ShouldBe(("ОВ/ВК", true, RequestUrgency.Urgent, RequestKind.New, "staff"));
    }

    [Test]
    public async Task Create_Fix_RequiresTargetCard()
    {
        var directions = await DirectionsAsync(new DirectionInput("АР/КР", ["Двери"], [], [], null));
        var card = await DbAsync(async db =>
        {
            var id = await SeedCardAsync(db, "Дверь ДГ", @"Двери\Дверь ДГ.rfa", CardState.Approved, null, DateTimeOffset.UtcNow);
            await db.Set<FamilyCard>().Where(c => c.Id == id).ExecuteUpdateAsync(c => c.SetProperty(x => x.Section, "Двери"));
            return id;
        });
        var designer = await ClientAsync(UserRole.Designer);

        (await designer.PostJsonAsync("/api/families/requests", new NewRequest("Не тот уровень", null, null, null, null, RequestKind.Fix)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var created = await CreateFullAsync(designer, new NewRequest("Не тот уровень", null, null, null, null, RequestKind.Fix, TargetCardId: card));

        var view = await ViewAsync(designer, created.Id);
        (view.Request.Kind, view.Request.TargetCardId, view.Request.TargetCardName, view.Request.Section, view.Request.DirectionId)
            .ShouldBe((RequestKind.Fix, (Guid?)card, "Дверь ДГ", "Двери", (Guid?)directions[0].Id));
    }

    [Test]
    public async Task List_Scopes()
    {
        var ovClient = await ClientAsync(UserRole.Operator);
        var arClient = await ClientAsync(UserRole.Operator);
        await DirectionsAsync(new DirectionInput("ОВ/ВК", ["ОВ"], [], [await IdAsync(ovClient)], null),
            new DirectionInput("АР/КР", ["АР"], [], [await IdAsync(arClient)], null));
        var designer = await ClientAsync(UserRole.Designer);
        var ov = await CreateFullAsync(designer, Body("Клапан", "ОВ"));
        var ar = await CreateFullAsync(designer, Body("Дверь", "АР"));
        var common = await CreateFullAsync(designer, Body("Что-то"));

        async Task<List<Guid>> ScopeAsync(HttpClient client, string scope) =>
            (await client.GetJsonAsync<Page<RequestListItem>>($"/api/families/requests?scope={scope}")).Items.Select(r => r.Id).ToList();

        (await ScopeAsync(ovClient, "direction")).ShouldBe([ov.Id, common.Id], ignoreOrder: true);
        (await ScopeAsync(arClient, "direction")).ShouldBe([ar.Id, common.Id], ignoreOrder: true);
        (await ovClient.PostAsync($"/api/families/requests/{ov.Id}/take", null)).EnsureSuccessStatusCode();
        (await ScopeAsync(ovClient, "assigned")).ShouldBe([ov.Id]);
        (await ScopeAsync(arClient, "unassigned")).ShouldBe([ar.Id, common.Id], ignoreOrder: true);
        (await ScopeAsync(designer, "mine")).Count.ShouldBe(3);
    }

    [Test]
    public async Task List_Order_BlocksThenOverdueThenDeadline()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var plain = await CreateFullAsync(designer, Body("без срока"));
        var later = await CreateFullAsync(designer, Body("через 10 дней", neededBy: Today.AddDays(10)));
        var soon = await CreateFullAsync(designer, Body("через 2 дня", neededBy: Today.AddDays(2)));
        var blocks = await CreateFullAsync(designer, Body("блокирует", blocks: true));
        var overdue = await CreateFullAsync(designer, Body("просрочена", neededBy: Today.AddDays(-1)));
        var coordinator = await ClientAsync(UserRole.Operator);

        var list = (await coordinator.GetJsonAsync<Page<RequestListItem>>("/api/families/requests?scope=all")).Items;

        list.Select(r => r.Id).ShouldBe([blocks.Id, overdue.Id, soon.Id, later.Id, plain.Id]);
        list.Single(r => r.Id == overdue.Id).Overdue.ShouldBeTrue();
        list.Single(r => r.Id == soon.Id).Overdue.ShouldBeFalse();
    }

    [Test]
    public async Task Edit_AuthorWhileOpen_OthersForbidden()
    {
        var author = await ClientAsync(UserRole.Designer);
        var created = await CreateAsync(author);

        (await author.PutJsonAsync($"/api/families/requests/{created.Id}",
            new RequestEdit("Клапан обратный DN65", "уточнил", "ОВ", "ЖК Север", Today.AddDays(5), true, 2024))).EnsureSuccessStatusCode();
        (await (await ClientAsync(UserRole.Designer)).PutJsonAsync($"/api/families/requests/{created.Id}",
            new RequestEdit("x", null, null, null, null, false, null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var view = await ViewAsync(author, created.Id);
        (view.Request.Title, view.Text, view.Request.Project, view.Request.NeededBy, view.Request.BlocksIssue, view.Request.RevitYear)
            .ShouldBe(("Клапан обратный DN65", "уточнил", "ЖК Север", (DateOnly?)Today.AddDays(5), true, (int?)2024));
        view.Messages.Last().Text.ShouldBe("изменил описание");
    }

    [Test]
    public async Task Stats_Counts()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var taken = await CreateFullAsync(designer, Body("взятая"));
        await CreateFullAsync(designer, Body("просроченная", neededBy: Today.AddDays(-3)));
        await CreateFullAsync(designer, Body("обычная"));
        (await coordinator.PostAsync($"/api/families/requests/{taken.Id}/take", null)).EnsureSuccessStatusCode();

        var stats = await coordinator.GetJsonAsync<RequestStats>("/api/families/requests/stats");

        (stats.Open, stats.Overdue, stats.Unassigned, stats.MineDirection).ShouldBe((3, 1, 2, 3));
        (await designer.GetAsync("/api/families/requests/stats")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Projects_Suggest()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await CreateFullAsync(designer, Body("а", project: "ЖК Север"));
        await CreateFullAsync(designer, Body("б", project: "ЖК Север"));
        await CreateFullAsync(designer, Body("в", project: "Дом на Набережной"));

        (await designer.GetJsonAsync<List<string>>("/api/families/requests/projects?q=сев")).ShouldBe(["ЖК Север"]);
        (await designer.GetJsonAsync<List<string>>("/api/families/requests/projects")).ShouldBe(["ЖК Север", "Дом на Набережной"]);
    }
}
