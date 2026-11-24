using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Notifications;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Жизнь заявки: вопрос автору, приёмка («Подходит» / «Не то»), отзыв, смена направления, автоприёмка и просрочка.</summary>
public class FamiliesRequestWorkflowTests : ServerTest
{
    public FamiliesRequestWorkflowTests() => Services = WithoutScanLoop;

    private sealed record Created(Guid Id, int Number);

    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private static async Task<Created> CreateAsync(HttpClient client, string title = "Клапан обратный DN50", DateOnly? neededBy = null)
    {
        var response = await client.PostJsonAsync("/api/families/requests", new NewRequest(title, "Ду50", "ОВ", null, "ЖК Север", NeededBy: neededBy));
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync<Created>();
    }

    private static Task<RequestView> ViewAsync(HttpClient client, Guid id) => client.GetJsonAsync<RequestView>($"/api/families/requests/{id}");

    private static async Task OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private Task<Guid> CardAsync(string name = "Клапан обратный") =>
        DbAsync(db => SeedCardAsync(db, name, $@"ОВ\{name}.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));

    private async Task MaintainAsync(DateTimeOffset now)
    {
        await using var scope = Server.Services.CreateAsyncScope();
        await RequestMaintenance.RunOnceAsync(scope.ServiceProvider, now, CancellationToken.None);
    }

    /// <summary>
    ///     Сообщение и строка о событии одного действия пишутся с одним моментом, а Id версии 7 внутри миллисекунды случайны: событие
    ///     («автор ответил») всё равно идёт после сообщения.
    /// </summary>
    [Test]
    public async Task Messages_SameMoment_EventAfterMessage()
    {
        var author = await ClientAsync(UserRole.Designer);
        var created = await CreateAsync(author);
        var me = await IdAsync(author);
        var at = DateTimeOffset.UtcNow.AddMinutes(1);
        await DbAsync(async db =>
        {
            db.Set<FamilyRequestMessage>().AddRange(
                new FamilyRequestMessage { Id = Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"), RequestId = created.Id, AuthorUserId = me, Text = "Danfoss", At = at },
                new FamilyRequestMessage { Id = Guid.Parse("00000000-0000-7000-8000-000000000001"), RequestId = created.Id, AuthorUserId = me, Text = "автор ответил", IsEvent = true, At = at });
            await db.SaveChangesAsync();
        });

        var view = await ViewAsync(author, created.Id);

        view.Messages.TakeLast(2).Select(m => m.Text).ShouldBe(["Danfoss", "автор ответил"]);
    }

    [Test]
    public async Task Cycle_AskAnswerReviewDeclineAccept()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var card = await CardAsync();
        var created = await CreateAsync(author);
        var url = $"/api/families/requests/{created.Id}";

        await OkAsync(coordinator.PostJsonAsync($"{url}/take", new RequestTake(new DateOnly(2026, 12, 1))));
        (await ViewAsync(author, created.Id)).Request.PromisedBy.ShouldBe(new DateOnly(2026, 12, 1));

        await OkAsync(coordinator.PostJsonAsync($"{url}/ask", new RequestMessageRequest("Какой производитель?")));
        var asked = await ViewAsync(author, created.Id);
        (asked.Request.State, asked.Request.Turn).ShouldBe((RequestState.NeedInfo, "author"));
        asked.Messages.ShouldContain(m => !m.IsEvent && m.Text == "Какой производитель?");

        await OkAsync(author.PostJsonAsync($"{url}/messages", new RequestMessageRequest("Danfoss")));
        var answered = await ViewAsync(author, created.Id);
        (answered.Request.State, answered.Request.Turn).ShouldBe((RequestState.InWork, "staff"));
        answered.Messages.Last().Text.ShouldBe("автор ответил");

        await OkAsync(coordinator.PostJsonAsync($"{url}/done", new RequestCard(card)));
        var review = await ViewAsync(author, created.Id);
        (review.Request.State, review.Request.Resolution, review.Request.CardName, review.Request.Turn).ShouldBe((RequestState.Review, (RequestResolution?)RequestResolution.Made, "Клапан обратный", "author"));
        review.AutoAcceptAt.ShouldNotBeNull();

        (await author.PostJsonAsync($"{url}/decline", new RequestMessageRequest(" "))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await OkAsync(author.PostJsonAsync($"{url}/decline", new RequestMessageRequest("Не тот DN")));
        var declined = await ViewAsync(author, created.Id);
        (declined.Request.State, declined.Request.Resolution).ShouldBe((RequestState.InWork, (RequestResolution?)null));
        declined.Messages.ShouldContain(m => !m.IsEvent && m.Text == "Не тот DN");
        (await DbAsync(db => db.Set<FamilyRequest>().SingleAsync(r => r.Id == created.Id))).DeclineCount.ShouldBe(1);

        await OkAsync(coordinator.PostJsonAsync($"{url}/exists", new RequestCard(card)));
        await OkAsync(author.PostAsync($"{url}/accept", null));
        var accepted = await ViewAsync(author, created.Id);
        (accepted.Request.State, accepted.Request.Turn).ShouldBe((RequestState.Exists, (string?)null));
    }

    [Test]
    public async Task Accept_Decline_OnlyMembersAndOnlyOnReview()
    {
        var author = await ClientAsync(UserRole.Designer);
        var stranger = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author);
        var url = $"/api/families/requests/{created.Id}";

        (await author.PostAsync($"{url}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await OkAsync(coordinator.PostJsonAsync($"{url}/done", new RequestCard(await CardAsync())));
        (await stranger.PostAsync($"{url}/accept", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stranger.PostJsonAsync($"{url}/decline", new RequestMessageRequest("не то"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await OkAsync(stranger.PostAsync($"{url}/join", null));
        await OkAsync(stranger.PostAsync($"{url}/accept", null));
        (await ViewAsync(author, created.Id)).Request.State.ShouldBe(RequestState.Done);
    }

    [Test]
    public async Task Withdraw_AuthorOnly()
    {
        var author = await ClientAsync(UserRole.Designer);
        var other = await ClientAsync(UserRole.Designer);
        var created = await CreateAsync(author);
        var url = $"/api/families/requests/{created.Id}";
        await OkAsync(other.PostAsync($"{url}/join", null));

        (await other.PostAsync($"{url}/withdraw", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await OkAsync(author.PostAsync($"{url}/withdraw", null));

        var view = await ViewAsync(author, created.Id);
        (view.Request.State, view.Request.Turn).ShouldBe((RequestState.Withdrawn, (string?)null));
        view.Messages.Last().Text.ShouldBe("отозвал заявку");
    }

    [Test]
    public async Task Direction_ChangeByCoordinator()
    {
        var admin = await AdminAsync();
        var saved = await (await admin.PutJsonAsync("/api/families/directions", new[] { new DirectionInput("АР/КР", [], [], [], null) }))
            .ReadJsonAsync<List<DirectionView>>();
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author);

        await OkAsync(coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/direction", new RequestDirection(saved[0].Id)));

        var view = await ViewAsync(author, created.Id);
        view.Request.DirectionName.ShouldBe("АР/КР");
        view.Messages.Last().Text.ShouldBe("передал в направление «АР/КР»");
        (await author.PostJsonAsync($"/api/families/requests/{created.Id}/direction", new RequestDirection(null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Maintenance_AutoAcceptsAfterDays_RemindsDayBefore()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author);
        await OkAsync(coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/done", new RequestCard(await CardAsync())));
        var since = (await ViewAsync(author, created.Id)).Request.ReviewSince!.Value;

        await MaintainAsync(since.AddDays(3));
        (await author.GetJsonAsync<List<NotificationView>>("/api/notifications")).ShouldNotContain(n => n.Kind == NotificationKinds.RequestRemind);

        await MaintainAsync(since.AddDays(4.1));
        await MaintainAsync(since.AddDays(4.2));
        (await author.GetJsonAsync<List<NotificationView>>("/api/notifications")).Count(n => n.Kind == NotificationKinds.RequestRemind).ShouldBe(1);
        (await ViewAsync(author, created.Id)).Request.State.ShouldBe(RequestState.Review);

        await MaintainAsync(since.AddDays(5.1));
        var view = await ViewAsync(author, created.Id);
        (view.Request.State, view.Messages.Last().AuthorId).ShouldBe((RequestState.Done, (Guid?)null));
        view.Messages.Last().Text.ShouldStartWith("принята автоматически");
    }

    [Test]
    public async Task Maintenance_OverdueNotifiesOnce()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author, neededBy: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        await OkAsync(coordinator.PostAsync($"/api/families/requests/{created.Id}/take", null));

        await MaintainAsync(DateTimeOffset.UtcNow);
        await MaintainAsync(DateTimeOffset.UtcNow.AddHours(1));

        (await coordinator.GetJsonAsync<List<NotificationView>>("/api/notifications")).Count(n => n.Kind == NotificationKinds.RequestOverdue).ShouldBe(1);
    }

    [Test]
    public async Task Reopen_ClearsReview()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author);
        var url = $"/api/families/requests/{created.Id}";
        await OkAsync(coordinator.PostJsonAsync($"{url}/done", new RequestCard(await CardAsync())));
        await OkAsync(author.PostAsync($"{url}/accept", null));

        await OkAsync(coordinator.PostAsync($"{url}/reopen", null));

        var view = await ViewAsync(author, created.Id);
        (view.Request.State, view.Request.Resolution, view.Request.ReviewSince).ShouldBe((RequestState.InWork, (RequestResolution?)null, (DateTimeOffset?)null));
    }
}
